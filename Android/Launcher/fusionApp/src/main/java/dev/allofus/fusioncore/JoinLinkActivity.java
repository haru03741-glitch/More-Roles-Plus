package dev.allofus.fusioncore;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.ComponentName;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.util.Log;
import android.widget.Toast;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import dev.allofus.fusioncore.tools.Utilities;

/**
 * Receives {@code mrp://join/<code>} from the room link page (the lobby's share button / Discord).
 * Same hand-off as the Windows installer: the room code and the time are written to
 * {@code BepInEx/config/mrp-join.txt}, which the mod picks up on the main menu and joins on the
 * MRP server. A running game is brought to the front; otherwise the launcher starts it.
 */
public class JoinLinkActivity extends Activity {
    private static final String TAG = "FusionCore";
    private static final String FILE_NAME = "mrp-join.txt";
    private static final Pattern CODE = Pattern.compile("^/?([A-Za-z]{4}|[A-Za-z]{6})/?$");

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        String code = parse(getIntent() != null ? getIntent().getData() : null);
        if (code == null || !write(code)) {
            Toast.makeText(this, R.string.join_link_failed, Toast.LENGTH_LONG).show();
            finish();
            return;
        }
        Toast.makeText(this, getString(R.string.join_link_joining, code), Toast.LENGTH_SHORT).show();
        if (BootstrapActivity.isGameLoaded()) {
            // The game already runs in this process: bring its task back; the mod reads the file.
            if (!moveGameToFront()) Log.w(TAG, "join link: game task not found");
            finish();
            return;
        }
        Intent start = new Intent(this, SelectorActivity.class);
        start.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        startActivity(start);
        finish();
    }

    // mrp://join/ABCDEF → "ABCDEF" (anything else → null)
    static String parse(Uri uri) {
        if (uri == null || !"mrp".equalsIgnoreCase(uri.getScheme()) || !"join".equalsIgnoreCase(uri.getHost())) {
            return null;
        }
        String path = uri.getPath();
        Matcher m = CODE.matcher(path != null ? path : "");
        return m.matches() ? m.group(1).toUpperCase(Locale.ROOT) : null;
    }

    private boolean write(String code) {
        try {
            Utilities.initStorage(this);
            File root = Utilities.getExternalFusionCoreDirectory(SelectorActivity.TARGET_PACKAGE);
            File config = new File(new File(root, "BepInEx"), "config");
            if (!config.isDirectory() && !config.mkdirs()) return false;
            long now = System.currentTimeMillis() / 1000L;
            try (FileOutputStream out = new FileOutputStream(new File(config, FILE_NAME))) {
                out.write((code + "\n" + now + "\n").getBytes(StandardCharsets.UTF_8));
            }
            Log.i(TAG, "join link: wrote " + code);
            return true;
        } catch (Exception e) {
            Log.w(TAG, "join link: " + e);
            return false;
        }
    }

    private boolean moveGameToFront() {
        ActivityManager am = getSystemService(ActivityManager.class);
        if (am == null) return false;
        String stub = StubActivity.class.getName();
        List<ActivityManager.AppTask> tasks = am.getAppTasks();
        for (ActivityManager.AppTask task : tasks) {
            try {
                ComponentName base = task.getTaskInfo().baseActivity;
                ComponentName top = task.getTaskInfo().topActivity;
                if (base != null && stub.equals(base.getClassName()) || top != null && stub.equals(top.getClassName())) {
                    task.moveToFront();
                    return true;
                }
            } catch (Exception ignored) {
            }
        }
        return false;
    }
}
