using System;
using System.IO;
using InnerNet;
using UnityEngine;

namespace MoreRolesPlus.Net;

// Discord のマッチメイキングの「参加する」から来た部屋コード。インストーラー (mrp://join/…) が
// BepInEx/config/mrp-join.txt に「コード\n書いた時刻 (Unix 秒)」を置くので、メインメニューに居る時に
// MRP のサーバーへ切り替えてその部屋に入る。古い紙 (3 分より前) は捨てる。
internal static class JoinLink
{
    private const string FileName = "mrp-join.txt";
    private const long MaxAgeSeconds = 180;
    private const float CheckEvery = 0.5f;

    private static float _next;
    private static string _pending;
    private static float _pendingSince;

    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, FileName);

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
