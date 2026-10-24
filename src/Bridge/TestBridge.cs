// Based on https://github.com/waffle-ful/Aeterna-End-K-not (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 開発用のコマンドの登録と実行 (既定 OFF、config の EnableTestBridge でのみ有効)。
// 開発者コンソールと dev のチャットコマンドがここを通る。処理は全部メインスレッドで行う。
// 実行の口を足す時は partial の実装側に書く (実装が無いビルドでは partial void の呼び出しごと消える)。
//
// 機能側のコマンドは Register で足す (このファイルを触らずに `hole 1 2 0.5` のような検証口を増やせる)。
public static partial class TestBridge
{
    public delegate void Handler(string args, Action<string> reply);

    private static readonly Dictionary<string, (Handler handler, string help)> Commands = new(StringComparer.OrdinalIgnoreCase);

    private static bool _inited;
    private static string _dir, _screensDir;

    private static bool _phaseErrorLogged;
    internal static long _menuSeenMs;

    public static string Dir { get { EnsureInit(); return _dir; } }

    public static void Register(string name, string help, Handler handler) => Commands[name] = (handler, help);

    public static bool Has(string name) => Commands.ContainsKey(name);

    public static void Out(string line) => WriteOut(line);

    private static bool Enabled => Plugin.EnableTestBridge is { Value: true };

    static partial void InitHost();
    static partial void TickHost(long now);
    static partial void WriteOut(string line);
    static partial void WriteStateHost();

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

            BridgeLog.Install();
            BuiltinCommands.RegisterAll();
            Burst.Register();
            Perf.Register();
            PatchProf.Register();
            StructCheck.Register();
            Dev.TpBurst.Register();
            Dev.PosTrace.Register();
            LobbyCommands.Register();
            RoleCommands.Register();
            UiCommands.Register();
            Roles.RewindCinematic.Register();
            Rules.RulesProbe.Register();
            Terrain.TerrainProbe.Register();
            InitHost();
        }
        catch { _dir = null; }
    }

    public static void Tick()
    {
        if (!Enabled) return;

        EnsureInit();
        if (_dir == null) return;

        TickHost(Environment.TickCount64);
        Terrain.ViewCheck.Tick();
    }

    // 1 行のコマンドを実行する (開発者コンソールと dev のチャットコマンドで共用)
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

    internal static void WriteState() => WriteStateHost();

    // ── 出力 ──────────────────────────────────────────────────────────────

    public static string ScreensDir { get { EnsureInit(); return _screensDir; } }

    // コマンドの返事以外の知らせ (連写の終わり等)
    internal static void Log(string line) => WriteOut(line);

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
