using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 複数の AU を LAN 部屋でつないで同期を確かめるためのブリッジコマンド
internal static class LobbyCommands
{
    // テスト中は試合の終了判定を止める (2 人だとインポスター 1 対クルー 1 で始まった瞬間に終わるため)
    internal static bool NoGameEnd;

    public static void Register()
    {
        TestBridge.Register("hostlocal", "メニューから LAN 部屋を立てる (follow: wait phase=Lobby 60)", (_, reply) =>
        {
            string phase = TestBridge.Phase();
            if (phase is not ("Menu" or "Boot")) { reply($"ERR hostlocal not at menu (phase={phase})"); return; }
            var btn = Object.FindObjectOfType<HostLocalGameButton>(true);
            if (!btn) { reply("ERR hostlocal HostLocalGameButton not found"); return; }
            Activate(btn.transform);
            btn.NetworkMode = NetworkModes.LocalGame;
            btn.OnClick();
            reply("OK hostlocal requested");
        });

        TestBridge.Register("joinlocal", "[アドレス] LAN 部屋に入る。部屋一覧に見つかった部屋を優先 (無ければ既定 127.0.0.1 へ直接。follow: wait phase=Lobby 60)", (args, reply) =>
        {
            string phase = TestBridge.Phase();
            if (phase is not ("Menu" or "Boot")) { reply($"ERR joinlocal not at menu (phase={phase})"); return; }
            // LAN の部屋一覧 (ローカル画面) に見つかった部屋のボタンがあればそれを押す。本編と同じ経路で入れる
            foreach (var found in Object.FindObjectsOfType<JoinGameButton>())
            {
                if (!found.isActiveAndEnabled || found.NetworkMode != NetworkModes.LocalGame || string.IsNullOrEmpty(found.netAddress)) continue;
                if (!string.IsNullOrWhiteSpace(args) && found.netAddress != args.Trim()) continue;
                found.OnClick();
                reply($"OK joinlocal listed {found.netAddress} requested");
                return;
            }
            var btn = Object.FindObjectOfType<JoinGameButton>(true);
            if (!btn) { reply("ERR joinlocal JoinGameButton not found"); return; }
            Activate(btn.transform);
            btn.NetworkMode = NetworkModes.LocalGame;
            btn.netAddress = string.IsNullOrWhiteSpace(args) ? "127.0.0.1" : args.Trim();
            btn.StartCoroutine(btn.JoinLocalGame());
            reply($"OK joinlocal {btn.netAddress} requested");
        });

        TestBridge.Register("startgame", "[マップ番号] ホストが部屋の試合を始める (人数の下限を外す。follow: wait phase=InGame 60)", (args, reply) =>
        {
            var gsm = GameStartManager.Instance;
            if (!gsm || !AmongUsClient.Instance.AmHost) { reply("ERR startgame not host in lobby"); return; }
            if (byte.TryParse(args, out byte map))
                GameOptionsManager.Instance.CurrentGameOptions.SetByte(AmongUs.GameOptions.ByteOptionNames.MapId, map);
            gsm.MinPlayers = 1;
            gsm.BeginGame();
            reply($"OK startgame map={(args.Length > 0 ? args : "current")} players={GameData.Instance.PlayerCount}");
        });

        TestBridge.Register("noend", "<on|off> 試合の終了判定を止める (テスト用)", (args, reply) =>
        {
            NoGameEnd = args.Trim() != "off";
            reply($"OK noend {(NoGameEnd ? "on" : "off")}");
        });

        TestBridge.Register("netinfo", "通信の状態 (ホストか・接続・同期で適用した件数)", (_, reply) =>
        {
            var c = AmongUsClient.Instance;
            reply($"OK netinfo mode={c.NetworkMode} amHost={c.AmHost} connected={c.AmConnected} clientId={c.ClientId} hostId={c.HostId} applied={Terrain.TerrainSync.Applied}");
        });
    }

    private static void Activate(Transform t)
    {
        for (; t; t = t.parent)
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
    }
}

[HarmonyPatch(typeof(LogicGameFlowNormal), nameof(LogicGameFlowNormal.CheckEndCriteria))]
internal static class NoGameEndPatch
{
    public static bool Prefix() => !LobbyCommands.NoGameEnd;
}
