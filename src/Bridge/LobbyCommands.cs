using AmongUs.Data;
using InnerNet;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 複数の AU を LAN 部屋でつないで同期を確かめるためのブリッジコマンド
internal static class LobbyCommands
{
    // テスト中は試合の終了判定を止める (2 人だとインポスター 1 対クルー 1 で始まった瞬間に終わるため)
    internal static bool NoGameEnd; // 試合の終了判定を止める (Roles/GameEnd.cs の CheckEndCriteriaPatch が見る)

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

        // 版の違う端末どうし (PC と Android でビルド番号が違う) は LAN の部屋一覧に出ないので、公式サーバーの部屋で試す
        TestBridge.Register("region", "[番号] 接続先の地域を一覧 / 切り替える", (args, reply) =>
        {
            var sm = ServerManager.Instance;
            var regions = sm.AvailableRegions;
            if (int.TryParse(args.Trim(), out int idx) && idx >= 0 && idx < regions.Length) sm.SetRegion(regions[idx]);
            for (int i = 0; i < regions.Length; i++) reply($"REGION {i} {regions[i].Name}{(regions[i].Name == sm.CurrentRegion.Name ? " *" : "")}");
            reply($"OK region now={sm.CurrentRegion.Name}");
        });

        TestBridge.Register("hostonline", "メニューから公式サーバーの部屋作成画面を開く (数秒置いて confirmcreate)", (_, reply) =>
        {
            var mm = Object.FindObjectOfType<MainMenuManager>();
            if (!mm || TestBridge.Phase() != "Menu") { reply($"ERR hostonline not at main menu (phase={TestBridge.Phase()})"); return; }
            AmongUsClient.Instance.NetworkMode = NetworkModes.OnlineGame;
            // クイックチャット限定のアカウント (テスト端末に多い) も入れるよう、部屋をクイックチャット用で立てる
            DataManager.Settings.Multiplayer.ChatMode = QuickChatModes.QuickChatOnly;
            mm.OpenCreateGame();
            reply("OK hostonline create dialog requested (follow: confirmcreate)");
        });

        TestBridge.Register("findgame", "[early [ミリ秒]] メニューから公式サーバーの部屋検索を開く (early = ログインの終わりを待たず、メニューが出てからその時間が経っていれば押す)", (args, reply) =>
        {
            var mm = Object.FindObjectOfType<MainMenuManager>();
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            bool early = parts.Length > 0 && parts[0] == "early";
            string phase = TestBridge.Phase(); // メニューを最初に見た時刻もここで記録される
            if (!mm || (!early && phase != "Menu")) { reply($"ERR findgame not at main menu (phase={phase})"); return; }
            if (early && parts.Length > 1 && long.TryParse(parts[1], out long delayMs))
            {
                long seen = System.Environment.TickCount64 - TestBridge._menuSeenMs;
                if (TestBridge._menuSeenMs == 0 || seen < delayMs) { reply($"ERR findgame menu seen {seen}ms ago (< {delayMs})"); return; }
            }
            AmongUsClient.Instance.NetworkMode = NetworkModes.OnlineGame;
            DataManager.Settings.Multiplayer.ChatMode = QuickChatModes.QuickChatOnly;
            SceneChanger.ChangeScene("FindAGame");
            reply("OK findgame requested (follow: state で scene=FindAGame)");
        });

        TestBridge.Register("confirmcreate", "開いた部屋作成画面で作成を押す (follow: wait phase=Lobby 90)", (_, reply) =>
        {
            var cgo = Object.FindObjectOfType<CreateGameOptions>();
            if (!cgo || !cgo.isActiveAndEnabled) { reply("ERR confirmcreate dialog not open"); return; }
            cgo.Confirm();
            reply("OK confirmcreate requested");
        });

        TestBridge.Register("joincode", "<部屋コード> 公式サーバーの部屋にコードで入る (follow: wait phase=Lobby 60)", (args, reply) =>
        {
            if (TestBridge.Phase() != "Menu") { reply($"ERR joincode not at menu (phase={TestBridge.Phase()})"); return; }
            int id = GameCode.GameNameToInt(args.Trim().ToUpperInvariant());
            if (id == -1) { reply($"ERR joincode bad code '{args}'"); return; }
            AmongUsClient.Instance.NetworkMode = NetworkModes.OnlineGame;
            AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CoFindGameInfoFromCodeAndJoin(id));
            reply($"OK joincode {args.Trim().ToUpperInvariant()} requested");
        });

        TestBridge.Register("playagain", "終了画面の「もう一度プレイ」を押す (follow: wait phase=Lobby 60)", (_, reply) =>
        {
            var nav = Object.FindObjectOfType<EndGameNavigation>();
            if (!nav) { reply("ERR playagain not on the end screen"); return; }
            nav.NextGame();
            reply("OK playagain requested");
        });

        TestBridge.Register("lobbycode", "今いる部屋のコード", (_, reply) =>
            reply($"OK lobbycode {GameCode.IntToGameName(AmongUsClient.Instance.GameId)}"));

        TestBridge.Register("startgame", "[マップ番号] ホストが部屋の試合を始める (人数の下限を外す。follow: wait phase=InGame 60)", (args, reply) =>
        {
            var gsm = Vanilla.StartManager;
            if (!gsm || !AmongUsClient.Instance.AmHost) { reply("ERR startgame not host in lobby"); return; }
            if (byte.TryParse(args, out byte map))
                GameOptionsManager.Instance.CurrentGameOptions.SetByte(AmongUs.GameOptions.ByteOptionNames.MapId, map);
            gsm.MinPlayers = 1;
            gsm.BeginGame();
            reply($"OK startgame map={(args.Length > 0 ? args : "current")} players={GameData.Instance.PlayerCount}");
        });

        TestBridge.Register("pressstart", "ロビーの開始ボタンを押す (人数の下限は外さない・人が押したのと同じ。follow: wait phase=InGame 60)", (_, reply) =>
        {
            var gsm = Vanilla.StartManager;
            if (!gsm || !AmongUsClient.Instance.AmHost) { reply("ERR pressstart not host in lobby"); return; }
            gsm.StartButton.OnClick.Invoke();
            reply($"OK pressstart min={gsm.MinPlayers} players={GameData.Instance.PlayerCount}");
        });

        TestBridge.Register("vrow", "<impostors> [+N|-N] 本編のゲーム設定の行 (いまはインポスター数) を ± で押して、値と幅を返す (設定画面のゲーム設定タブを開いておく)", (args, reply) =>
        {
            var menu = GameSettingMenu.Instance;
            if (!menu || !menu.GameSettingsTab || menu.GameSettingsTab.Children == null) { reply("ERR vrow open the game settings tab first"); return; }
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int times = parts.Length > 1 && int.TryParse(parts[1], out int t) ? t : 0;
            foreach (var b in menu.GameSettingsTab.Children)
            {
                var num = b.TryCast<NumberOption>();
                if (num == null || num.Title != StringNames.GameNumImpostors) continue;
                for (int i = 0; i < System.Math.Abs(times); i++) { if (times > 0) num.Increase(); else num.Decrease(); }
                reply($"OK vrow value={num.Value} range={num.ValidRange.min}..{num.ValidRange.max} opt={GameOptionsManager.Instance.CurrentGameOptions.NumImpostors}");
                return;
            }
            reply("ERR vrow row not found");
        });

        TestBridge.Register("noend", "<on|off> 本編の試合の終わりを全部止める (MRP の勝ちと forcewin は通る。テスト用)", (args, reply) =>
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
