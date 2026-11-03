package dev.allofus.fusioncore;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.provider.MediaStore;
import android.util.Log;
import android.view.Surface;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

import dev.allofus.fusioncore.bridge.UnityActivityHost;

/**
 * Screen recording for the mod (the game's Unity build has no way to read the rendered frame on
 * Android). The mod's native recorder makes an H.264 encoder whose input is a {@link Surface} and
 * calls {@link #start}; this asks the system for screen capture consent, keeps a foreground
 * service running as Android requires, and mirrors the screen into that surface through a
 * virtual display. Audio does not come from here: the mod hands the game's own sound to the same
 * native recorder.
 *
 * <p>Called from native code on a background thread, so every UI step is posted to the main
 * thread and the outcome is read back through {@link #state}.
 */
public final class ScreenRecorder {
    private static final String TAG = "FusionCore";
    private static final String CHANNEL = "mrp_screen_record";
    private static final int NOTIFICATION_ID = 41;

    /** 0 = idle, 1 = waiting for consent, 2 = capturing, -1 = refused, -2 = failed, -3 = stopped by the system. */
    private static volatile int state;
    // Bumped by every start, so a stop that is still queued for an earlier recording leaves a new one alone.
    private static volatile int generation;
    private static Surface surface;
    private static int width, height, density;
    private static MediaProjection projection;
    private static VirtualDisplay display;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    private ScreenRecorder() {
    }

    public static int state() {
        return state;
    }

    /** Begins asking for consent; capture goes into {@code target} once granted. */
    public static void start(Surface target, int w, int h) {
        generation++;
        surface = target;
        width = w;
        height = h;
        state = 1;
        MAIN.post(() -> {
            Activity game = UnityActivityHost.gameActivity();
            if (game == null) {
                Log.w(TAG, "screen record: no game activity");
                state = -2;
                return;
            }
            density = game.getResources().getDisplayMetrics().densityDpi;
            try {
                game.startActivity(new Intent(game, ConsentActivity.class));
            } catch (RuntimeException e) {
                Log.w(TAG, "screen record: consent screen", e);
                state = -2;
            }
        });
    }

    /** Stops mirroring and lets the system know capture has ended. Safe to call in any state. */
    public static void stop() {
        int g = generation;
        MAIN.post(() -> {
            if (g == generation) release();
        });
    }

    private static void release() {
        if (display != null) {
            display.release();
            display = null;
        }
        MediaProjection p = projection;
        projection = null;
        if (p != null) p.stop();
        surface = null;
        Context app = appContext();
        if (app != null) app.stopService(new Intent(app, CaptureService.class));
        if (state > 0) state = 0;
    }

    /**
     * Moves a finished recording into the shared Movies/MoreRolesPlus folder so the gallery and
     * file apps can see it (Android 10 and later; earlier versions keep the file where it is).
     * Returns true when the file was moved.
     */
    public static boolean publish(String path) {
        if (Build.VERSION.SDK_INT < 29) return false;
        Context app = appContext();
        if (app == null) return false;
        File src = new File(path);
        ContentResolver cr = app.getContentResolver();
        ContentValues v = new ContentValues();
        v.put(MediaStore.MediaColumns.DISPLAY_NAME, src.getName());
        v.put(MediaStore.MediaColumns.MIME_TYPE, "video/mp4");
        v.put(MediaStore.MediaColumns.RELATIVE_PATH, "Movies/MoreRolesPlus");
        v.put(MediaStore.MediaColumns.IS_PENDING, 1);
        Uri uri = null;
        try {
            uri = cr.insert(MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), v);
            if (uri == null) return false;
            try (InputStream in = new FileInputStream(src); OutputStream out = cr.openOutputStream(uri)) {
                if (out == null) throw new java.io.IOException("no output");
                byte[] buf = new byte[1 << 16];
                int n;
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            }
            v.clear();
            v.put(MediaStore.MediaColumns.IS_PENDING, 0);
            cr.update(uri, v, null, null);
            if (!src.delete()) Log.w(TAG, "screen record: could not remove " + path);
            return true;
        } catch (Exception e) {
            Log.w(TAG, "screen record: publish", e);
            if (uri != null) {
                try {
                    cr.delete(uri, null, null);
                } catch (RuntimeException ignored) {
                }
            }
            return false;
        }
    }

    private static Context appContext() {
        Activity game = UnityActivityHost.gameActivity();
        return game != null ? game.getApplicationContext() : null;
    }

    // Runs on the main thread from the service once it is in the foreground.
    private static void begin(Context ctx, int resultCode, Intent data) {
        if (surface == null || state != 1) {
            ctx.stopService(new Intent(ctx, CaptureService.class));
            return;
        }
        try {
            MediaProjectionManager mpm = (MediaProjectionManager) ctx.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            MediaProjection p = mpm.getMediaProjection(resultCode, data);
            if (p == null) throw new IllegalStateException("no projection");
            p.registerCallback(new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    if (projection == p) {
                        projection = null;
                        if (display != null) {
                            display.release();
                            display = null;
                        }
                        if (state == 2) state = -3;
                    }
                }
            }, MAIN);
            projection = p;
            display = p.createVirtualDisplay("MoreRolesPlus", width, height, density,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, surface, null, MAIN);
            state = 2;
        } catch (RuntimeException e) {
            Log.w(TAG, "screen record: start", e);
            state = -2;
            release();
        }
    }

    /** Transparent screen that shows the system's capture consent dialog and passes the answer on. */
    public static class ConsentActivity extends Activity {
        private static final int REQUEST = 1;

        @Override
        protected void onCreate(Bundle savedInstanceState) {
            super.onCreate(savedInstanceState);
            if (savedInstanceState != null) return; // the dialog is still up after a configuration change
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            try {
                startActivityForResult(mpm.createScreenCaptureIntent(), REQUEST);
            } catch (RuntimeException e) {
                Log.w(TAG, "screen record: consent", e);
                state = -2;
                finish();
            }
        }

        @Override
        protected void onActivityResult(int requestCode, int resultCode, Intent data) {
            super.onActivityResult(requestCode, resultCode, data);
            if (requestCode == REQUEST) {
                if (resultCode == RESULT_OK && data != null && state == 1) {
                    Intent svc = new Intent(this, CaptureService.class)
                            .putExtra("code", resultCode)
                            .putExtra("data", data);
                    try {
                        if (Build.VERSION.SDK_INT >= 26) startForegroundService(svc);
                        else startService(svc);
                    } catch (RuntimeException e) {
                        Log.w(TAG, "screen record: service", e);
                        state = -2;
                    }
                } else if (state == 1) {
                    state = -1;
                }
            }
            finish();
            overridePendingTransition(0, 0);
        }
    }

    /** Android only hands out a screen capture while a foreground service of this type is running. */
    public static class CaptureService extends Service {
        @Override
        public int onStartCommand(Intent intent, int flags, int startId) {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null) {
                nm.createNotificationChannel(new NotificationChannel(CHANNEL, getString(R.string.screen_record_channel),
                        NotificationManager.IMPORTANCE_LOW));
            }
            Notification.Builder b = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
            Notification n = b.setSmallIcon(android.R.drawable.presence_video_online)
                    .setContentTitle(getString(R.string.screen_record_notice))
                    .setOngoing(true)
                    .build();
            try {
                if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
                else startForeground(NOTIFICATION_ID, n);
            } catch (RuntimeException e) {
                Log.w(TAG, "screen record: foreground", e);
                state = -2;
                stopSelf();
                return START_NOT_STICKY;
            }
            Intent data = intent == null ? null
                    : Build.VERSION.SDK_INT >= 33 ? intent.getParcelableExtra("data", Intent.class)
                    : intent.getParcelableExtra("data");
            int code = intent != null ? intent.getIntExtra("code", 0) : 0;
            if (data == null) {
                state = -2;
                stopSelf();
                return START_NOT_STICKY;
            }
            begin(this, code, data);
            return START_NOT_STICKY;
        }

        @Override
        public void onDestroy() {
            if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE);
            super.onDestroy();
        }

        @Override
        public IBinder onBind(Intent intent) {
            return null;
        }
    }
}
