using AmongUs.GameOptions;
using InnerNet;

namespace MoreRolesPlus.Options;

// ロビーで本編のモード (通常 / かくれんぼ) を切り替える。本編はモードを部屋の作成時にしか選べず、
// 試合を回す GameManager もモードごとに別の物 (NormalGameManager / HideAndSeekManager) なので、
// ホストがモードを替えたら今の GameManager を消して新しいモードの物を出し直し、設定を配り直す。
internal static class GameModeSwitch
{
    // MRP の役職・勝ち負け・配り方は通常モードの時だけ動かす。
    // GameOptionsManager.CurrentGameOptions は読むだけで SwitchGameMode を呼ぶことがあるので、フィールドを直に見る
    public static bool IsNormal
    {
        get
        {
            var m = GameOptionsManager.Instance;
            return m == null || m.currentGameMode == GameModes.Normal;
        }
    }

    public static GameModes Current => GameOptionsManager.Instance?.currentGameMode ?? GameModes.Normal;

    // 開始できる最少人数。通常は 1 人から練習として始められる。かくれんぼは鬼と隠れる側が要る (1 人だと本編のイントロが鬼を探して落ちる)
    public static int MinPlayers => IsNormal ? 1 : 2;

    // 設定画面を閉じる演出の間は開けないので、消えたフレームで開き直す
    private static long _reopenUntil;

    public static void ReopenSettings() => _reopenUntil = System.Environment.TickCount64 + 3000;

    public static void Tick()
    {
        if (_reopenUntil == 0) return;
        if (System.Environment.TickCount64 > _reopenUntil || !LobbyBehaviour.Instance) { _reopenUntil = 0; return; }
        if (GameSettingMenu.Instance) return;
        _reopenUntil = 0;
        var start = Vanilla.StartManager;
        if (start) start.ClickEdit();
    }

    // ホストのロビーでだけ。結果を 1 行で返す
    public static string Switch(GameModes mode)
    {
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost) return "host only";
        if (!LobbyBehaviour.Instance || client.GameState != InnerNetClient.GameStates.Joined) return "lobby only";
        // 開始の数えの間に入れ替えると、開始の処理が古い GameManager を掴んだまま進む
        var start = Vanilla.StartManager;
        if (start && start.startState != GameStartManager.StartingStates.NotStarting) return "starting";
        var opts = GameOptionsManager.Instance;
        var prev = opts.currentGameMode;
        if (prev == mode) return $"already {mode}";

        try { return Respawn(client, mode); }
        catch (System.Exception e)
        {
            // 途中で落ちたら元のモードの GameManager を出し直す (GameManager の無いロビーは開始できない)
            Plugin.Logger.LogError($"game mode switch failed: {e}");
            try { return $"failed, restored: {Respawn(client, prev)}"; }
            catch (System.Exception e2) { return $"failed: {e2.Message}"; }
        }
    }

    private static string Respawn(AmongUsClient client, GameModes mode)
    {
        GameOptionsManager.Instance.SwitchGameMode(mode);
        var old = GameManager.Instance;
        if (old)
        {
            client.Despawn(old);
            // 消した物の破棄はフレームの終わりなので、空けておかないと新しい物が自分を Instance にできない
            GameManager.Instance = null;
        }
        var gm = GameManagerCreator.CreateGameManager(mode);
        if (!gm) throw new System.InvalidOperationException("CreateGameManager returned null");
        client.Spawn(gm, -2, SpawnFlags.None);
        gm.LogicOptions?.SyncOptions();
        var start = Vanilla.StartManager;
        if (start) start.MinPlayers = MinPlayers;
        return $"switched to {mode} (gm={gm.GetIl2CppType().Name} netId={gm.NetId} instance={(GameManager.Instance == gm ? "new" : "other")})";
    }
}

// 客の手元: ホストが出し直した GameManager は古い物の消去と同じフレームに届く。読み込み (Deserialize) の前に
// 手元のモードを届いた物に合わせ、まだ消えていない古い物を Instance から外す (外さないと新しい物の部品が揃わない)。
// モード合わせはロビーの外でも行う (かくれんぼの部屋の後で通常の部屋に入った時に、手元のモードが残らないように)
[HarmonyLib.HarmonyPatch(typeof(GameManager), nameof(GameManager.Awake))]
internal static class GameManagerAwakeModePatch
{
    private static void Prefix(GameManager __instance)
    {
        var mode = __instance.TryCast<HideAndSeekManager>() != null ? GameModes.HideNSeek : GameModes.Normal;
        var opts = GameOptionsManager.Instance;
        if (opts != null && opts.currentGameMode != mode) opts.SwitchGameMode(mode);
        if (!LobbyBehaviour.Instance) return;
        var cur = GameManager.Instance;
        if (cur && cur.Pointer != __instance.Pointer) GameManager.Instance = null;
    }
}

// モードを替えた時、消した GameManager の OnDestroy が後から来ても新しい方の Instance を消させない (ホストも客も)
[HarmonyLib.HarmonyPatch(typeof(GameManager), nameof(GameManager.OnDestroy))]
internal static class GameManagerDestroyKeepPatch
{
    private static void Prefix(GameManager __instance, out GameManager __state)
    {
        var cur = GameManager.Instance;
        __state = cur && cur.Pointer != __instance.Pointer ? cur : null;
    }

    private static void Postfix(GameManager __state)
    {
        if (__state && GameManager.Instance != __state) GameManager.Instance = __state;
    }
}
