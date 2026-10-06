using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 外部ツールからゲームを遠隔テストするための観測・操作口 (既定 OFF、config の EnableTestBridge でのみ有効)。
// <Desktop>/MRP_Logs/bridge/ の bridge-cmd.txt を 100ms 間隔で読み、1 tick に 1 行ずつ実行して
// 結果を bridge-out.log へ追記する。状態は bridge-state.json、スクショは Screens/ へ書く。
// 入出力はテキストファイルだけなので、操作する側の言語は問わない。処理は全部メインスレッドで行う。
//
// 機能側のコマンドは Register で足す (このファイルを触らずに `hole 1 2 0.5` のような検証口を増やせる)。
public static class TestBridge
{
    private const int MaxBatchLines = 20;
    private const long MaxOutFileBytes = 2 * 1024 * 1024;
    private const int CmdPollIntervalMs = 100;
    private const int StateIntervalMs = 1000;

    public delegate void Handler(string args, Action<string> reply);

    private static readonly Dictionary<string, (Handler handler, string help)> Commands = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Pending = new();

    private static bool _inited;
    private static string _dir, _cmdPath, _outPath, _statePath, _screensDir;
    private static long _lastPollMs, _lastStateMs;

    private static WaitState _wait;
    private static bool _phaseErrorLogged;
    internal static long _menuSeenMs;

    public static string Dir { get { EnsureInit(); return _dir; } }

    public static void Register(string name, string help, Handler handler) => Commands[name] = (handler, help);

    public static bool Has(string name) => Commands.ContainsKey(name);

    public static void Out(string line) => WriteOut(line);

    private static bool Enabled => Plugin.EnableTestBridge is { Value: true };

    private static void EnsureInit()
    {
        if (_inited) return;
        _inited = true;

        try
        {
            // Android はランチャーが渡すアプリ専用の書き込み先 (無ければ persistentDataPath)
            string basePath = OperatingSystem.IsAndroid()
                ? Environment.GetEnvironmentVariable("FUSION_APP_DATA_DIR") is { Length: > 0 } appDir ? appDir : Application.persistentDataPath
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            // 同じ PC で 2 つ起動する時 (同期テスト) は環境変数で置き場を分ける
            _dir = Environment.GetEnvironmentVariable("MRP_BRIDGE_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(basePath, "MRP_Logs", "bridge");
            _screensDir = Path.Combine(_dir, "Screens");
            Directory.CreateDirectory(_screensDir);

            _cmdPath = Path.Combine(_dir, "bridge-cmd.txt");
            _outPath = Path.Combine(_dir, "bridge-out.log");
            _statePath = Path.Combine(_dir, "bridge-state.json");

            BridgeLog.Install();
            BuiltinCommands.RegisterAll();
            Burst.Register();
            Perf.Register();
            LobbyCommands.Register();
            RoleCommands.Register();
            Terrain.TerrainProbe.Register();
            WriteOut($"BRIDGE up {Plugin.Version}");
        }
        catch { _dir = null; }
    }

    public static void Tick()
    {
        if (!Enabled) return;

        EnsureInit();
        if (_dir == null) return;

        long now = Environment.TickCount64;

        if (now - _lastPollMs >= CmdPollIntervalMs)
        {
            _lastPollMs = now;
            ReadCmdFile();
        }

        if (_wait != null) EvaluateWait(now);
        else if (Pending.Count > 0) Execute(Pending.Dequeue());

        if (now - _lastStateMs >= StateIntervalMs)
        {
            _lastStateMs = now;
            WriteState();
        }
    }

    private static void ReadCmdFile()
    {
        if (!File.Exists(_cmdPath)) return;

        var lines = new List<string>();
        try
        {
            using (var fs = new FileStream(_cmdPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
            }

            File.Delete(_cmdPath);
        }
        catch { return; } // 書き込み途中などでロックされている = 次回読む

        foreach (string raw in lines)
        {
            string d = raw.Trim();
            if (d.Length == 0 || d.StartsWith('#')) continue;

            if (_wait != null && d.Equals("wait cancel", StringComparison.OrdinalIgnoreCase))
            {
                _wait = null;
                WriteOut("OK wait cancelled");
                continue;
            }

            if (Pending.Count >= MaxBatchLines) { WriteOut($"ERR queue full, dropped: {d}"); continue; }
            Pending.Enqueue(d);
        }
    }

    private static void Execute(string directive)
    {
        WriteOut($"> {directive}");

        int sp = directive.IndexOf(' ');
        string name = sp < 0 ? directive : directive[..sp];
        string args = sp < 0 ? string.Empty : directive[(sp + 1)..].Trim();

        if (name.Equals("wait", StringComparison.OrdinalIgnoreCase)) { StartWait(args); return; }

        Run(directive, WriteOut);
        if (name.Equals("help", StringComparison.OrdinalIgnoreCase))
            WriteOut("HELP wait phase=<Boot|Menu|Lobby|InGame|Meeting> [秒] | wait marker <正規表現> [秒] | wait cancel");
    }

    // 1 行のコマンドを実行する (ブリッジと開発者コンソールで共用。wait はブリッジだけ)
    public static void Run(string line, Action<string> reply)
    {
        line = line.Trim();
        int sp = line.IndexOf(' ');
        string name = sp < 0 ? line : line[..sp];
        string args = sp < 0 ? string.Empty : line[(sp + 1)..].Trim();

        if (name.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kv in Commands)
                if (args.Length == 0 || kv.Key.Contains(args, StringComparison.OrdinalIgnoreCase)) reply($"HELP {kv.Key} {kv.Value.help}");
            reply("OK help");
            return;
        }

        if (!Commands.TryGetValue(name, out var cmd)) { reply($"ERR unknown command '{name}' (help で一覧)"); return; }

        try { cmd.handler(args, reply); }
        catch (Exception e)
        {
            reply($"ERR {name} {e.GetType().Name}: {e.Message}");
            BridgeLog.RecordError("bridge", e.ToString());
        }
    }

    // ── wait ──────────────────────────────────────────────────────────────

    private sealed class WaitState
    {
        public string Phase;
        public Regex Marker;
        public long DeadlineMs;
        public long Since;
        public string Text;
    }

    private static void StartWait(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { WriteOut("ERR wait needs phase=<X> or marker <regex>"); return; }

        var w = new WaitState { Text = args, Since = BridgeLog.Sequence };
        int secIndex;

        if (parts[0].StartsWith("phase=", StringComparison.OrdinalIgnoreCase))
        {
            w.Phase = parts[0][6..];
            secIndex = 1;
        }
        else if (parts[0].Equals("marker", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
        {
            try { w.Marker = new Regex(parts[1]); }
            catch (Exception e) { WriteOut($"ERR wait bad regex: {e.Message}"); return; }
            secIndex = 2;
        }
        else { WriteOut("ERR wait needs phase=<X> or marker <regex>"); return; }

        int sec = parts.Length > secIndex && int.TryParse(parts[secIndex], out int s) ? Math.Clamp(s, 1, 600) : 30;
        w.DeadlineMs = Environment.TickCount64 + sec * 1000L;
        _wait = w;
    }

    private static void EvaluateWait(long now)
    {
        bool hit = _wait.Phase != null
            ? string.Equals(Phase(), _wait.Phase, StringComparison.OrdinalIgnoreCase)
            : BridgeLog.AnyMatchSince(_wait.Since, _wait.Marker);

        if (hit) { WriteOut($"OK wait {_wait.Text}"); _wait = null; return; }
        if (now >= _wait.DeadlineMs) { WriteOut($"ERR wait timeout {_wait.Text} (phase={Phase()})"); _wait = null; }
    }

    // ── 状態 ──────────────────────────────────────────────────────────────

    public static string Phase()
    {
        try
        {
            if (MeetingHud.Instance) return "Meeting";
            if (ShipStatus.Instance) return "InGame";
            if (LobbyBehaviour.Instance) return "Lobby";
            if (AmongUsClient.Instance && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.NotJoined) return "Joining";
            return MenuReady() ? "Menu" : "Boot";
        }
        catch (Exception e)
        {
            if (!_phaseErrorLogged) { _phaseErrorLogged = true; BridgeLog.RecordError("bridge", "Phase: " + e); }
            return "Unknown";
        }
    }

    // メニュー表示直後はログインやメニューの初期化が終わっておらず、ボタンを押すと例外で無視される
    public static bool MenuReady()
    {
        // Time.timeSinceLevelLoad は Android の libunity に無いので、メニューを最初に見た時刻から自前で数える
        if (!UnityEngine.Object.FindObjectOfType<MainMenuManager>()) { _menuSeenMs = 0; return false; }
        long now = Environment.TickCount64;
        if (_menuSeenMs == 0) _menuSeenMs = now;
        if (now - _menuSeenMs < 3000) return false;
        var eos = EOSManager.Instance;
        return !eos || eos.loginFlowFinished;
    }

    internal static void WriteState()
    {
        try
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"ts\":\"").Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"phase\":\"").Append(Phase()).Append('"');
            sb.Append(",\"scene\":").Append(JStr(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name));
            sb.Append(",\"errorsTotal\":").Append(BridgeLog.ErrorsTotal);
            sb.Append(",\"fps\":").Append(F(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f)));

            var map = ShipStatus.Instance;
            if (map) sb.Append(",\"map\":").Append(JStr(map.name));

            var lp = PlayerControl.LocalPlayer;
            if (lp)
            {
                Vector2 p = lp.GetTruePosition();
                sb.Append(",\"local\":{\"id\":").Append(lp.PlayerId)
                  .Append(",\"x\":").Append(F(p.x)).Append(",\"y\":").Append(F(p.y)).Append('}');
            }

            sb.Append(",\"players\":[");
            bool first = true;
            foreach (var pc in PlayerControl.AllPlayerControls)
            {
                if (!pc) continue;
                if (!first) sb.Append(',');
                first = false;
                Vector2 p = pc.GetTruePosition();
                sb.Append("{\"id\":").Append(pc.PlayerId)
                  .Append(",\"name\":").Append(JStr(pc.Data != null ? pc.Data.PlayerName : ""))
                  .Append(",\"x\":").Append(F(p.x)).Append(",\"y\":").Append(F(p.y)).Append('}');
            }
            sb.Append("]}");

            string tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, _statePath, true);
        }
        catch { }
    }

    // ── 出力 ──────────────────────────────────────────────────────────────

    public static string ScreensDir { get { EnsureInit(); return _screensDir; } }

    private static long _bytesSinceRotateCheck = MaxOutFileBytes;

    // コマンドの返事以外の知らせ (連写の終わり等) を bridge-out.log へ
    internal static void Log(string line) => WriteOut(line);

    private static void WriteOut(string line)
    {
        if (_outPath == null) return;

        try
        {
            string payload = $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n";

            _bytesSinceRotateCheck += payload.Length;
            if (_bytesSinceRotateCheck >= 64 * 1024)
            {
                _bytesSinceRotateCheck = 0;
                if (File.Exists(_outPath) && new FileInfo(_outPath).Length > MaxOutFileBytes)
                {
                    string prev = Path.Combine(_dir, "bridge-out.prev.log");
                    try { if (File.Exists(prev)) File.Delete(prev); File.Move(_outPath, prev); } catch { }
                }
            }

            File.AppendAllText(_outPath, payload);
        }
        catch { }
    }

    internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string JStr(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

// BepInEx のログを横取りして、エラー件数と直近のログ行を手元に持つ (grep / errors / wait marker 用)
internal sealed class BridgeLog : ILogListener
{
    private const int RingCap = 2000;
    private const int ErrorCap = 200;

    private static readonly Queue<(long seq, string line)> Ring = new();
    private static readonly Queue<string> Errors = new();
    private static bool _installed;

    public static long Sequence { get; private set; }
    public static int ErrorsTotal { get; private set; }

    public LogLevel LogLevelFilter => LogLevel.All;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        BepInEx.Logging.Logger.Listeners.Add(new BridgeLog());
    }

    public void LogEvent(object sender, LogEventArgs e)
    {
        string text = $"[{e.Level}:{e.Source.SourceName}] {e.Data}";
        lock (Ring)
        {
            Sequence++;
            Ring.Enqueue((Sequence, text));
            while (Ring.Count > RingCap) Ring.Dequeue();
        }

        if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0) RecordError(e.Source.SourceName, e.Data?.ToString());
    }

    public static void RecordError(string tag, string text)
    {
        lock (Errors)
        {
            ErrorsTotal++;
            Errors.Enqueue($"[{DateTime.Now:HH:mm:ss}] {tag}: {text}");
            while (Errors.Count > ErrorCap) Errors.Dequeue();
        }
    }

    public static List<string> Grep(Regex re, int max)
    {
        var hits = new List<string>();
        lock (Ring)
            foreach (var (_, line) in Ring)
                if (re.IsMatch(line)) hits.Add(line);
        return hits.Count > max ? hits.GetRange(hits.Count - max, max) : hits;
    }

    public static bool AnyMatchSince(long since, Regex re)
    {
        lock (Ring)
            foreach (var (seq, line) in Ring)
                if (seq > since && re.IsMatch(line)) return true;
        return false;
    }

    public static List<string> RecentErrors(int max)
    {
        lock (Errors)
        {
            var all = new List<string>(Errors);
            return all.Count > max ? all.GetRange(all.Count - max, max) : all;
        }
    }

    public void Dispose() { }
}
