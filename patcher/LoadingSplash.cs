using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;

namespace MoreRolesPlus.BootAccel
{
    // 起動中の画面 (MoreRolesPlus.Splash.exe) を出し、BepInEx のログから今の段階を送る。
    //   1 環境とBepInExの初期化   ゲームが起きてからこの patcher が動くまで
    //   2 パッチャーの読み込みと適用  ここから「Chainloader initialized」まで
    //   3 MOD の読み込みと適用     「Chainloader startup complete」まで (「Loading [名前]」で 1 つずつ数える)
    //   4 ゲームの開始           メニューが出た (MRP の「BOOT load=… menu=…」の行) まで。ログインの待ちは待たない (メニューはもう見えて触れる)
    // 終わったら段階ごとの時間を 1 行ログに残す (どこで遅いか後から分かるように)。
    // 画面が無い・作れない・BepInEx/cache/morerolesplus-splash.disabled がある時は何もしない (起動は止めない)
    [PatcherPluginInfo("morerolesplus.splash", "More Roles Plus Loading Window", "1.0.0")]
    public class LoadingSplashPatcher : BasePatcher
    {
        public override void Initialize() => LoadingSplash.Begin();

        public override void Finalizer() { }
    }

    internal static class LoadingSplash
    {
        private const string ExeName = "MoreRolesPlus.Splash.exe";
        private const string DisableFileName = "morerolesplus-splash.disabled";

        private static Process _proc;
        private static StreamWriter _pipe;
        private static readonly object Gate = new object();
        private static Listener _listener;
        private static readonly double[] StageAt = new double[6];
        private static int _stage;
        private static DateTime _gameStart;

        internal static void Begin()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                string cacheDir = null;
                try { cacheDir = BepInEx.Paths.CachePath; } catch { }
                if (cacheDir != null && File.Exists(Path.Combine(cacheDir, DisableFileName))) return;

                string dir = Path.GetDirectoryName(typeof(LoadingSplash).Assembly.Location) ?? "";
                string exe = Path.Combine(dir, ExeName);
                if (!File.Exists(exe)) return;

                var self = Process.GetCurrentProcess();
                try { _gameStart = self.StartTime; } catch { _gameStart = DateTime.Now; }
                string gameExe = "";
                try { gameExe = self.MainModule?.FileName ?? ""; } catch { }
                string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

                var psi = new ProcessStartInfo(exe, $"{self.Id} {lang} \"{gameExe}\" \"{cacheDir ?? ""}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                };
                // ゲームの起動の仕組み (Doorstop) の環境変数は渡さない
                foreach (var key in new System.Collections.Generic.List<string>(psi.Environment.Keys))
                    if (key.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase)) psi.Environment.Remove(key);
                _proc = Process.Start(psi);
                if (_proc == null) return;
                _pipe = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                Mark(1);
                Mark(2); // この patcher が動いた = BepInEx の準備は終わった
                _listener = new Listener();
                Logger.Listeners.Add(_listener);
            }
            catch (Exception e)
            {
                try { Logger.CreateLogSource("Splash").LogWarning("splash: " + e.Message); } catch { }
                Stop();
            }
        }

        private static double Now => (DateTime.Now - _gameStart).TotalSeconds;

        private static void Mark(int stage)
        {
            lock (Gate)
            {
                while (_stage < stage)
                {
                    _stage++;
                    StageAt[_stage] = _stage == 1 ? 0 : Now;
                }
            }
            if (stage >= 2 && stage <= 4) Send("STAGE " + stage + " " + Now.ToString("0.000", CultureInfo.InvariantCulture)); // 時刻はゲームの側で計る (画面が受け取るのは遅れることがある)
        }

        internal static void Send(string line)
        {
            lock (Gate)
            {
                if (_pipe == null) return;
                try { _pipe.WriteLine(line.Replace('\r', ' ').Replace('\n', ' ')); }
                catch { CloseSilently(); } // 画面が閉じられた (「Among Usを開く」等)。ゲームはそのまま
            }
        }

        private static void Finish()
        {
            if (_finished) return;
            Mark(5);
            Send("DONE " + Now.ToString("0.000", CultureInfo.InvariantCulture));
            _finished = true;
            // どこに時間がかかったか
            try
            {
                var sb = new StringBuilder("boot stages:");
                string[] names = { "", "bepinex", "patchers", "plugins", "game" };
                for (int i = 1; i <= 4; i++)
                    sb.Append(' ').Append(names[i]).Append('=').Append((StageAt[i + 1] - StageAt[i]).ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
                Logger.CreateLogSource("Splash").LogInfo(sb.ToString());
            }
            catch { }
            Stop();
        }

        // ログの受け口は外さない (ログを配っている最中に外すと配る側の一覧が壊れる)。終わった印で何もしなくなる
        private static bool _finished;

        private static void Stop()
        {
            _finished = true;
            lock (Gate) CloseSilently();
        }

        private static void CloseSilently()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private static readonly Regex Count = new Regex(@"^(\d+) plugins? to load", RegexOptions.Compiled);
        private static readonly Regex Loading = new Regex(@"^Loading \[(.+?)\]", RegexOptions.Compiled);

        private sealed class Listener : ILogListener
        {
            public LogLevel LogLevelFilter => LogLevel.All;

            public void LogEvent(object sender, LogEventArgs e)
            {
                if (_finished) return;
                try
                {
                    string msg = e.Data?.ToString() ?? "";
                    string src = e.Source?.SourceName ?? "";
                    if (msg.StartsWith("Chainloader initialized", StringComparison.Ordinal)) Mark(3);
                    else if (msg.StartsWith("Chainloader startup complete", StringComparison.Ordinal)) Mark(4);
                    else if (src == "More Roles Plus" && msg.StartsWith("BOOT load=", StringComparison.Ordinal)) { Finish(); return; }
                    else
                    {
                        var m = Count.Match(msg);
                        if (m.Success) Send("COUNT " + m.Groups[1].Value);
                        m = Loading.Match(msg);
                        if (m.Success) Send("PLUGIN " + m.Groups[1].Value);
                    }
                    int nl = msg.IndexOf('\n');
                    if (nl >= 0) msg = msg.Substring(0, nl);
                    if (msg.Length > 160) msg = msg.Substring(0, 160) + "…";
                    Send("LOG " + (src.Length > 0 ? "[" + src.Trim() + "] " : "") + msg);
                }
                catch { }
            }

            public void Dispose() { }
        }
    }
}
