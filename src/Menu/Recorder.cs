using System;
using System.IO;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Native;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 録画 (PC と Android)。ゲームの画面とゲームの音だけを MP4 にする (ほかのアプリや Windows・端末の画面・音は入らない)。
// - 画面: PC = 一番最後に描くカメラの最後に「描いた画面を RT へ縮めて写す」+「C++ の部品を呼ぶ」を積み、部品が描画のスレッドで
//   読み出して書き出す (Direct3D 11 と Media Foundation)。
//   Android = ゲームの Unity は描いた画面を読む口が削られていて描画も別のスレッドなので、端末の画面録画 (MediaProjection) を使う。
//   部品が H.264 の符号器の入口をランチャーへ渡し、ランチャーが許可の画面を出して画面をそこへ映す (Vulkan の端末でも録れる)。
//   音の速さを読む口も削られているので、Android は始める前に音の出口へ届く量を 1 秒数えて AAC の決まった速さに丸める
// - 音: ゲームの音の出口 (AudioListener) に部品を付けて、鳴っている音をそのまま受け取る。
//   ボイスチャットの声を入れない時は、声の鳴らし手を出口の部品の手前を通らないようにする (bypassListenerEffects。自分には今まで通り聞こえる)
// 保存先は PC = ビデオのフォルダーの MoreRolesPlus / Android = 端末の Movies/MoreRolesPlus (書けなければアプリの中)
internal static unsafe class Recorder
{
    internal static ConfigEntry<bool> RecordVoice { get; private set; }
    internal static ConfigEntry<int> Quality { get; private set; }

    private static readonly (int H, int Fps, int Bitrate, string Name)[] Qualities =
    {
        (720, 30, 6_000_000, "720p 30fps"),
        (1080, 30, 10_000_000, "1080p 30fps"),
        (1080, 60, 16_000_000, "1080p 60fps"),
    };

    internal static void Bind(ConfigFile cfg)
    {
        RecordVoice = cfg.Bind("Client", "RecordVoice", true, "録画にボイスチャットの声も入れる");
        Quality = cfg.Bind("Client", "RecordQuality", Android ? 0 : 1, "録画の画質 (0 = 720p30 / 1 = 1080p30 / 2 = 1080p60)");
    }

    internal static void NextQuality()
    {
        if (Active) return; // 録画中は変えない (次の録画から)
        Quality.Value = (Quality.Value + 1) % Qualities.Length;
    }

    internal static string QualityLabel() => Qualities[Math.Clamp(Quality.Value, 0, Qualities.Length - 1)].Name;

    private static void* _h;
    private static readonly object AudioLock = new();
#if ANDROID
    private static int _w, _h2; // 録画の大きさ
    private static int _rate; // 測った音の速さ (一度測れば使い回す)
    private static volatile bool _measuring;
    private static long _rateFrames, _rateFirst, _rateLast;
    private static float _measureUntil;
    private static readonly int[] AacRates = { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 };
#else
    private static RenderTexture _rt;
    private static CommandBuffer _cmd;
#endif
    private static Camera _cam;
    private static AudioListener _listener;
    private static float _since, _nextCheck;
    private static string _path;
    private static int _channels;
    private static readonly bool Android = OperatingSystem.IsAndroid();
    // 写した画面の上下を入れ替えて読むか (D3D11 では入れ替えない: 写した RT は上から並ぶ。GL は下から並ぶので入れ替える)
    internal static bool Flip = Android;

    internal static bool Active => _h != null;

    // ボイスチャットの声を録画に入れないか (声の鳴らし手が見る)
    internal static bool HideVoice => Active && !RecordVoice.Value;

    internal static string StateLabel()
    {
        if (!Active) return new Text("Recorder.Off");
        int s = (int)(Time.realtimeSinceStartup - _since);
        return new Text("Recorder.On").With("time", $"{s / 60}:{s % 60:00}");
    }

    private static string _folder;

    internal static string Folder => _folder ??= PickFolder();

    // Android は端末の Movies (ギャラリーから見られる) に書けるか試し、だめならアプリの中
    private static string PickFolder()
    {
        if (!Android) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MoreRolesPlus");
        try
        {
            string dir = "/storage/emulated/0/Movies/MoreRolesPlus";
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogInfo($"recorder: Movies not writable ({e.Message})");
            return Path.Combine(Application.persistentDataPath, "Recordings");
        }
    }

    internal static void OpenFolder()
    {
        if (Android)
        {
            // 端末のファイルの画面は開けないので、場所を知らせる (Android 10 からは録り終えた動画を端末の「動画」へ移す)
            string where = OperatingSystem.IsAndroidVersionAtLeast(29) ? "Movies/MoreRolesPlus" : Folder;
            Options.SettingNotice.Toast(new Text("Recorder.Where").With("path", where));
            return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Plugin.Logger.LogError($"recorder folder: {e}"); }
    }

    internal static void Toggle()
    {
        if (Active) Stop(false);
        else Start();
    }

    internal static string Start()
    {
        if (Active) return "already";
#if !ANDROID
        var api = SystemInfo.graphicsDeviceType;
        if (Android ? api != GraphicsDeviceType.OpenGLES3 : api != GraphicsDeviceType.Direct3D11)
            return Fail(new Text("Recorder.NotSupported").With("api", api.ToString()));
#endif
#if ANDROID
        if (_rate == 0) return MeasureRate();
        int rate = _rate;
#else
        int rate = AudioSettings.outputSampleRate;
        if (rate != 44100 && rate != 48000) return Fail($"{rate}Hz");
#endif
        var q = Qualities[Math.Clamp(Quality.Value, 0, Qualities.Length - 1)];
        int sw = Math.Max(Screen.width, 16), sh = Math.Max(Screen.height, 16);
        // Android の符号器は 16 の倍数でないと受け付けない物がある
        int align = Android ? 15 : 1;
        int h = Math.Min(q.H, sh) & ~align;
        int w = (int)Math.Round((double)sw * h / sh) & ~align;
        try
        {
            Directory.CreateDirectory(Folder);
            _path = Path.Combine(Folder, $"MRP_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
#if !ANDROID
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.BGRA32) { name = "MrpRecord" };
            _rt.Create();
#endif
            fixed (char* p = _path)
                _h = MrpNative.RecStart(p, w, h, q.Fps, q.Bitrate, rate, 2);
            if (_h == null) throw new Exception("start failed");
#if ANDROID
            _w = w;
            _h2 = h;
#else
            MrpNative.RecTexture(_h, (void*)_rt.GetNativeTexturePtr(), Flip ? 1 : 0);
            _cmd = new CommandBuffer { name = "MrpRecord" };
            _cmd.Blit(Target(BuiltinRenderTextureType.CurrentActive, 0), Target(BuiltinRenderTextureType.RenderTexture, _rt.GetInstanceID()));
            _cmd.IssuePluginEvent((IntPtr)MrpNative.RecRenderEvent(), 1);
#endif
            _since = Time.realtimeSinceStartup;
            _nextCheck = 0f;
            Attach();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"recorder: {e}");
            if (_h != null)
            {
                void* started = _h;
                Detach();
                lock (AudioLock) _h = null;
                MrpNative.RecStop(started, 1);
                try { File.Delete(_path); } catch { }
            }
            Cleanup();
            return Fail(e.Message);
        }
        Plugin.Logger.LogInfo($"recorder: start {w}x{h} {q.Fps}fps {rate}Hz {_path}");
        Options.SettingNotice.Toast(Android ? new Text("Recorder.StartedTouch") : new Text("Recorder.Started").With("key", Keybinds.Key(Keybinds.Slot.Record).ToString()));
        return "OK " + _path;
    }

#if !ANDROID
    // RenderTargetIdentifier を中身から作る (ゲームの Unity は組み込みの種類から作るコンストラクターが削られている。中身は Unity のコンストラクターと同じ)
    private static RenderTargetIdentifier Target(BuiltinRenderTextureType type, int instanceId) => new()
    {
        m_Type = type,
        m_NameID = -1,
        m_InstanceID = instanceId,
        m_BufferPointer = IntPtr.Zero,
        m_MipLevel = 0,
        m_CubeFace = CubemapFace.Unknown,
        m_DepthSlice = 0,
    };
#endif

#if ANDROID
    // 音の出口に受け口を付けて、届く量を数え始める (1 秒後に Tick が丸めて録画を始める)
    private static string MeasureRate()
    {
        if (_measuring) return "OK measuring";
        _rateFrames = 0;
        _rateFirst = _rateLast = 0;
        _measureUntil = Time.realtimeSinceStartup + 1f;
        _measuring = true;
        AttachTap();
        return "OK measuring";
    }

    private static void FinishMeasure()
    {
        _measuring = false;
        // 音のスレッドが書いた値 (ランチャーは 64bit だけなので long は途中の値にならない)
        long ticks = _rateLast - _rateFirst;
        double r = ticks > 0 ? _rateFrames * (double)System.Diagnostics.Stopwatch.Frequency / ticks : 0;
        Plugin.Logger.LogInfo($"recorder: audio rate ~{r:0} Hz");
        // AAC が受ける速さのうち一番近い物 (端末によっては 24000 などで鳴らしている)
        int best = 0;
        foreach (int c in AacRates)
            if (best == 0 || Math.Abs(r - c) < Math.Abs(r - best)) best = c;
        if (Math.Abs(r - best) > best * 0.1)
        {
            Fail($"{r:0}Hz");
            return;
        }
        _rate = best;
        Start();
    }
#endif

    private static string Fail(string why)
    {
        string msg = new Text("Recorder.Failed").With("why", why);
        Plugin.Logger.LogWarning($"recorder: {msg}");
        Options.SettingNotice.Toast(msg);
        return msg;
    }

    // failed = 録れなかった (保存の知らせを出さず、中身の無いファイルを消す)
    internal static string Stop(bool wait, bool failed = false)
    {
        if (!Active) return "not recording";
        string path = _path;
        void* h = _h;
        Detach();
        lock (AudioLock) _h = null; // 音のスレッドがもう渡さないように
        int res = MrpNative.RecStop(h, wait ? 1 : 0);
        Cleanup();
        Plugin.Logger.LogInfo($"recorder: stop {path} res={res}");
        if (failed)
        {
            try { File.Delete(path); } catch { }
            return $"OK stopped {path} res={res}";
        }
        Options.SettingNotice.Toast(new Text("Recorder.Saved").With("name", Path.GetFileName(path)));
        return $"OK saved {path} res={res}";
    }

    // 部品が返す失敗の種類を一言に
    private static string Why(int error) => error switch
    {
#if ANDROID
        7 => new Text("Recorder.Denied"),
        9 => new Text("Recorder.OldLauncher"),
#endif
        _ => $"0x{error:X8}",
    };

    private static void Cleanup()
    {
#if !ANDROID
        if (_rt)
        {
            _rt.Release();
            Object.Destroy(_rt);
        }
        _rt = null;
        _cmd = null;
#endif
    }

    // 毎フレーム。一番最後に描くカメラと音の出口は場面が変わると作り直されるので、ときどき付け直す
    internal static void Tick()
    {
        try
        {
#if ANDROID
            if (_measuring && Time.realtimeSinceStartup >= _measureUntil) FinishMeasure();
#endif
            if (!Active) return;
            if (Time.realtimeSinceStartup >= _nextCheck)
            {
                _nextCheck = Time.realtimeSinceStartup + 0.5f;
                Attach();
                var info = stackalloc int[6];
                MrpNative.RecInfo(_h, info);
                if (info[0] == 3)
                {
                    // 端末の側 (通知や画面録画の停止) で止められた: ここまでを保存する
                    Stop(false);
                    return;
                }
                if (info[0] < 0)
                {
                    Plugin.Logger.LogError($"recorder: failed hr=0x{info[5]:X8} frames={info[1]}");
                    Stop(false, failed: info[1] == 0);
                    Fail(Why(info[5]));
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"recorder: {e}"); }
    }

    private static void Attach()
    {
        Camera last = null;
        foreach (var c in Camera.allCameras)
        {
            if (!c || !c.enabled || c.targetTexture) continue;
            if (!last || c.depth >= last.depth) last = c;
        }
        if (last != _cam)
        {
#if ANDROID
            _cam = last; // 画面は端末の画面録画から受ける (カメラには何も積まない)
#else
            if (_cam && _cmd != null) _cam.RemoveCommandBuffer(CameraEvent.AfterEverything, _cmd);
            _cam = last;
            if (_cam && _cmd != null) _cam.AddCommandBuffer(CameraEvent.AfterEverything, _cmd);
#endif
        }
        AttachTap();
    }

    private static void AttachTap()
    {
        var l = _listener ? _listener : Object.FindObjectOfType<AudioListener>();
        if (l != _listener || l && !l.GetComponent<RecordTap>())
        {
            _listener = l;
            if (l && !l.GetComponent<RecordTap>()) l.gameObject.AddComponent<RecordTap>();
        }
    }

    private static void Detach()
    {
#if !ANDROID
        if (_cam && _cmd != null) _cam.RemoveCommandBuffer(CameraEvent.AfterEverything, _cmd);
#endif
        _cam = null;
        _listener = null;
    }

    private static short[] _pcm = new short[8192];
    private static double _energy; // 確かめ用: 受けた音の 2 乗の和と数 (info で読むと 0 に戻す)
    private static long _energyN;

    // 音のスレッドから (鳴っている音はそのまま返す)
    internal static void OnAudio(Il2CppStructArray<float> data, int channels)
    {
#if ANDROID
        if (_measuring && data != null && channels > 0)
        {
            // 初めの呼び出しの時刻から数える (付けてから届くまでの遅れと、初めの塊は入れない)
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_rateFirst == 0) _rateFirst = now;
            else _rateFrames += data.Length / channels;
            _rateLast = now;
            return;
        }
#endif
        if (_h == null || data == null || channels <= 0) return;
        lock (AudioLock)
        {
            if (_h == null) return;
            int frames = data.Length / channels;
            if (_pcm.Length < frames * 2) _pcm = new short[frames * 2];
            _channels = channels;
            for (int i = 0; i < frames; i++)
            {
                float l = data[i * channels];
                float r = channels > 1 ? data[i * channels + 1] : l;
                _energy += l * l;
                _pcm[i * 2] = (short)(Math.Clamp(l, -1f, 1f) * 32767f);
                _pcm[i * 2 + 1] = (short)(Math.Clamp(r, -1f, 1f) * 32767f);
            }
            _energyN += frames;
            fixed (short* p = _pcm) MrpNative.RecAudio(_h, p, frames);
        }
    }

    // ゲームを閉じる時は書き終えるまで待つ (待たないと MP4 が壊れる)
    internal static void OnQuit()
    {
        if (Active) Stop(true);
    }

    // 確かめ用 (ブリッジ): rec [start | stop | info | peek | flip]
    internal static string Command(string[] a)
    {
        string head = a.Length > 0 ? a[0] : "";
        if (head == "start") return Start();
        if (head == "stop") return Stop(a.Length > 1 && a[1] == "wait");
        if (head == "flip") { Flip = !Flip; return $"OK flip={Flip} (次の録画から)"; }
        if (head == "voice") { RecordVoice.Value = !RecordVoice.Value; return $"OK voice={RecordVoice.Value}"; }
        if (head == "peek") return Peek();
        if (!Active) return $"OK idle quality={QualityLabel()} voice={RecordVoice.Value}";
        var info = stackalloc int[6];
        MrpNative.RecInfo(_h, info);
        double rms;
        lock (AudioLock)
        {
            rms = _energyN > 0 ? Math.Sqrt(_energy / _energyN) : 0;
            _energy = 0;
            _energyN = 0;
        }
        return $"OK rec rms={rms:0.00000} state={info[0]} frames={info[1]} dropped={info[2]} queuedMB={info[3]} audioPeak={info[4]} ch={_channels} hr=0x{info[5]:X8} cam={(_cam ? _cam.name : "-")} size={Size()} path={_path}";
    }

#if ANDROID
    private static string Size() => $"{_w}x{_h2}";
#else
    private static string Size() => _rt ? $"{_rt.width}x{_rt.height}" : "-";
#endif

    // 最後に読んだ画面を PNG にする (上下と色の並びの確かめ)
    private static string Peek()
    {
#if ANDROID
        if (!Active) return "ERR not recording";
        int w = _w, h = _h2;
#else
        if (!Active || !_rt) return "ERR not recording";
        int w = _rt.width, h = _rt.height;
#endif
        var buf = new byte[w * h * 4];
        int n;
        fixed (byte* p = buf) n = MrpNative.RecPeek(_h, p, buf.Length);
        if (n == 0) return "ERR no frame yet";
        var tex = new Texture2D(w, h, TextureFormat.BGRA32, false);
        // 部品の画面は上から下・PNG にする Texture2D は下から上なので、行を逆に入れる
        var flipped = new Il2CppStructArray<byte>((long)buf.Length);
        int row = w * 4;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < row; x++)
                flipped[(h - 1 - y) * row + x] = buf[y * row + x];
        tex.LoadRawTextureData(flipped);
        tex.Apply();
        string path = Path.Combine(Folder, "peek.png");
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.Destroy(tex);
        return "OK " + path;
    }
}

// ゲームの音の出口に付ける部品 (鳴っている音を録画へ渡す・音は変えない)
public class RecordTap : MonoBehaviour
{
    public RecordTap(IntPtr ptr) : base(ptr) { }

    private void OnAudioFilterRead(Il2CppStructArray<float> data, int channels) => Recorder.OnAudio(data, channels);
}
