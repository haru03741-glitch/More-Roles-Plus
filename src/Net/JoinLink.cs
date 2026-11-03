using System;
using System.IO;
using InnerNet;
using UnityEngine;

namespace MoreRolesPlus.Net;

// Discord のマッチメイキングの「参加する」から来た部屋コード。インストーラー (mrp://join/…) が
// BepInEx/config/mrp-join.txt に「コード\n書いた時刻 (Unix 秒)」を置くので、メインメニューに居る時に
// MRP のサーバーへ切り替えてその部屋に入る。古い紙 (5 分より前) は捨てる。
internal static class JoinLink
{
    private const string FileName = "mrp-join.txt";
    private const long MaxAgeSeconds = 300; // スマホは起動に時間がかかるので長めに
    private const float CheckEvery = 0.5f;

    private static float _next;
    private static string _pending;
    private static float _pendingSince;

    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, FileName);

#if !ANDROID
    // インストーラーを入れていない PC でもリンク (mrp://join/…) で入れるように、mrp:// をこのゲームに結び付ける (利用者ごと・管理者は要らない)。
    // 受け手は BepInEx/config に置く小さな PowerShell: 紙を書き、ゲームが動いていれば前へ、閉じていれば起動する。
    // インストーラーが結び付けていて、その exe がまだあれば触らない
    private const string ScriptName = "mrp-join.ps1";
    private static bool _registered;

    internal static void RegisterProtocol()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            string current;
            using (var cmd = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\mrp\shell\open\command"))
                current = cmd?.GetValue("") as string;
            if (current != null && current.Contains("--join"))
            {
                string exe = current.StartsWith("\"") ? current[1..current.IndexOf('"', 1)] : current.Split(' ')[0];
                if (File.Exists(exe)) return;
            }
            string script = Path.Combine(BepInEx.Paths.ConfigPath, ScriptName);
            File.WriteAllText(script, Script());
            string command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" \"%1\"";
            if (current == command) return;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\mrp");
            if (key == null) return;
            key.SetValue("", "URL:More Roles Plus");
            key.SetValue("URL Protocol", "");
            using (var c = key.CreateSubKey(@"shell\open\command")) c?.SetValue("", command);
            Plugin.Logger.LogInfo("join link: registered mrp:// for this game");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"join link: register: {e.Message}"); }
    }

    private static string Script()
    {
        static string Q(string v) => "'" + v.Replace("'", "''") + "'";
        string game = BepInEx.Paths.GameRootPath;
        string exe = Path.Combine(game, "Among Us.exe");
        bool steam = game.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0;
        // Steam 版は Steam から起動する (exe を直に開くと Steam が起動し直すので同じ事)
        string start = steam ? "Start-Process 'steam://rungameid/945360'" : $"Start-Process -FilePath {Q(exe)} -WorkingDirectory {Q(game)}";
        return string.Join("\r\n",
            "# More Roles Plus: opens mrp://join/<code> in the game (written by the mod on every start)",
            "param([string]$Link)",
            "$m = [regex]::Match($Link, '^(?:mrp:(?://)?join/)?([A-Za-z]{4}|[A-Za-z]{6})/?$')",
            "if (-not $m.Success) { exit 1 }",
            "$code = $m.Groups[1].Value.ToUpperInvariant()",
            "$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()",
            $"[IO.File]::WriteAllText({Q(Path.Combine(BepInEx.Paths.ConfigPath, FileName))}, \"$code`n$now\")",
            "$p = Get-Process -Name 'Among Us' -ErrorAction SilentlyContinue | Select-Object -First 1",
            "if ($p) { (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null }",
            "else { " + start + " }",
            "");
    }
#endif

    // コピーした物から部屋コードを読む: 参加リンク (…/mrp/join/<コード>・mrp://join/<コード>) か、コードそのもの。無ければ null
    internal static string Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"(?:/join/|^)([A-Za-z]{6}|[A-Za-z]{4})/?(?:[?#].*)?$");
        if (!m.Success) return null;
        string code = m.Groups[1].Value.ToUpperInvariant();
        return GameCode.GameNameToInt(code) == -1 ? null : code;
    }

    // ホーム画面の「リンクで参加」から: リンクを開いた時と同じ道で入る (MRP のサーバーへ切り替える・非公開の部屋も入れる)
    internal static void Request(string code)
    {
        _pending = code;
        _pendingSince = Time.realtimeSinceStartup;
        _next = 0f;
    }

    public static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _next) return;
        _next = now + CheckEvery;
        if (_pending == null) Read();
        if (_pending == null) return;
        if (now - _pendingSince > MaxAgeSeconds) { _pending = null; return; }

        var c = AmongUsClient.Instance;
        if (!c) return;
        if (c.GameState != InnerNetClient.GameStates.NotJoined || LobbyBehaviour.Instance)
        {
            // 部屋の中では入らない (知らせて捨てる)
            Tell(new Text("JoinLink.InRoom").With("code", _pending));
            _pending = null;
            return;
        }
        // ログインが終わってメインメニューが出てから
        var eos = EOSManager.Instance;
        if (!eos || !eos.loginFlowFinished || !UnityEngine.Object.FindObjectOfType<MainMenuManager>()) return;
        string code = _pending;
        _pending = null;
        int id = GameCode.GameNameToInt(code);
        if (id == -1) return;
        if (!MrpRegion.Select()) Plugin.Logger.LogWarning("join link: MRP region not found, joining on the current region");
        Plugin.Logger.LogInfo($"join link: joining {code}");
        c.NetworkMode = NetworkModes.OnlineGame;
        c.StartCoroutine(c.CoFindGameInfoFromCodeAndJoin(id));
    }

    private static void Read()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path);
            File.Delete(path);
            if (lines.Length < 2 || !long.TryParse(lines[1].Trim(), out long at)) return;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at > MaxAgeSeconds) return;
            string code = lines[0].Trim().ToUpperInvariant();
            if (code.Length is not (4 or 6)) return;
            _pending = code;
            _pendingSince = Time.realtimeSinceStartup;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"join link: {e.Message}"); }
    }

    private static void Tell(string text)
    {
        var hud = Vanilla.Hud;
        if (hud && hud.Notifier) hud.Notifier.AddDisconnectMessage(text);
    }
}
