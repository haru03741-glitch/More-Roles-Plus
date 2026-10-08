using System;
using AmongUs.GameOptions;
using HarmonyLib;
using MoreRolesPlus.Net;

namespace MoreRolesPlus.Options;

// インポスターの人数の上限。本編は 3 人まで (人数ごとの上限の表 + 設定画面の幅 1〜3) だが、
// 公式サーバー以外では「クルーの方が多い」範囲まで増やせるようにする (15 人なら 7 人)。
// - 本編は人数ごとの上限を静的な表 (NormalGameOptionsV12.MaxImpostors) から引くので、表の中身を差し替える。
//   上限を計算する関数が呼び出し元に埋め込まれていても、表を読む限り効く。
// - 公式サーバーでは本編の表へ戻し、3 人を超える設定は 3 人に戻す (超えた設定で部屋を立てない)。
internal static class ImpostorCap
{
    public const int VanillaMax = 3;
    private const int TableSize = 128;

    private static int[] _vanillaMax, _vanillaMinPlayers;
    private static bool _widened;

    // 人数 players のときに置けるインポスターの最大数 (公式サーバー以外)。クルーが必ず 1 人多く残る
    public static int CapFor(int players) => Math.Max(1, (players - 1) / 2);

    // 今の接続先での最大数 (設定画面の幅)。maxPlayers = 部屋の定員
    public static int MaxFor(int maxPlayers) => ServerKind.IsOfficial() ? VanillaMax : Math.Max(VanillaMax, CapFor(maxPlayers));

    // 接続先に合わせて本編の表を差し替える / 戻す。ロビーに入った時・設定画面を開いた時・役職を配る直前に呼ぶ
    public static void Refresh()
    {
        try
        {
            bool widen = !ServerKind.IsOfficial();
            if (_vanillaMax == null)
            {
                _vanillaMax = NormalGameOptionsV12.MaxImpostors;
                _vanillaMinPlayers = NormalGameOptionsV12.MinPlayers;
            }
            if (widen == _widened) return;
            _widened = widen;
            if (widen)
            {
                var max = new int[TableSize];
                for (int n = 0; n < TableSize; n++)
                    max[n] = Math.Max(n < _vanillaMax.Length ? _vanillaMax[n] : 0, CapFor(n));
                // MinPlayers はインポスターの人数ごとの最少人数 (表の外を引くと本編が例外を出す)
                var min = new int[TableSize];
                for (int i = 0; i < TableSize; i++)
                    min[i] = i < _vanillaMinPlayers.Length ? _vanillaMinPlayers[i] : Math.Max(_vanillaMinPlayers[^1], i * 2 + 1);
                NormalGameOptionsV12.MaxImpostors = max;
                NormalGameOptionsV12.MinPlayers = min;
            }
            else
            {
                NormalGameOptionsV12.MaxImpostors = _vanillaMax;
                NormalGameOptionsV12.MinPlayers = _vanillaMinPlayers;
                ClampToVanilla(GameOptionsManager.Instance?.CurrentGameOptions);
            }
            Plugin.Logger.LogInfo($"impostor cap: {(widen ? "widened" : "vanilla")}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"impostor cap: {e}"); }
    }

    // 公式サーバーへ持ち込まないように 3 人へ戻す (戻したら true)
    public static bool ClampToVanilla(IGameOptions opts)
    {
        if (opts == null || opts.NumImpostors <= VanillaMax) return false;
        opts.SetInt(Int32OptionNames.NumImpostors, VanillaMax);
        return true;
    }

    // 部屋の定員。設定画面の値・試合の設定・部屋を立てた時の値が食い違うことがある (LAN の部屋で 10 と 15) ので大きい方、今いる人数も下回らない
    public static int RoomSize(IGameOptions opts)
    {
        int n = opts != null ? opts.MaxPlayers : 0;
        var gm = GameManager.Instance;
        if (gm && gm.LogicOptions != null) n = Math.Max(n, gm.LogicOptions.MaxPlayers);
        var host = GameOptionsManager.Instance?.GameHostOptions;
        if (host != null) n = Math.Max(n, host.MaxPlayers);
        if (GameData.Instance) n = Math.Max(n, GameData.Instance.PlayerCount);
        return n;
    }

    // 試合の人数で実際に置く数 (本編の計算と同じ形: 設定値を 1〜上限に収める)
    public static int Adjusted(IGameOptions opts, int players)
    {
        int cap = ServerKind.IsOfficial() ? Math.Min(VanillaMax, VanillaTable(players)) : CapFor(players);
        return Math.Clamp(opts.NumImpostors, 1, Math.Max(1, cap));
    }

    private static int VanillaTable(int players)
    {
        var t = _vanillaMax;
        if (t == null || t.Length == 0) return VanillaMax;
        return t[Math.Clamp(players, 0, t.Length - 1)];
    }
}

// 人数の調整 (イントロの「インポスターは N 人」と役職の配り方が使う)。公式サーバーでは本編のまま
[HarmonyPatch(typeof(IGameOptionsExtensions), nameof(IGameOptionsExtensions.GetAdjustedNumImpostors))]
internal static class AdjustedImpostorsPatch
{
    public static bool Prefix(IGameOptions gameOptions, int playerCount, ref int __result)
    {
        if (gameOptions == null || ServerKind.IsOfficial()) return true;
        __result = ImpostorCap.Adjusted(gameOptions, playerCount);
        return false;
    }
}

[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class ImpostorCapSelectPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix()
    {
        ImpostorCap.Refresh();
        var opts = GameOptionsManager.Instance.CurrentGameOptions;
        Plugin.Logger.LogInfo($"impostor cap: setting={opts.NumImpostors} players={GameData.Instance.PlayerCount} adjusted={opts.GetAdjustedNumImpostors(GameData.Instance.PlayerCount)}");
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class ImpostorCapLobbyPatch
{
    public static void Postfix() => ImpostorCap.Refresh();
}

// ゲーム設定の「インポスター」の行の幅を接続先に合わせる
[HarmonyPatch(typeof(NumberOption), nameof(NumberOption.Initialize))]
internal static class ImpostorCapRowPatch
{
    public static void Postfix(NumberOption __instance)
    {
        if (__instance.Title != StringNames.GameNumImpostors) return;
        ImpostorCap.Refresh();
        var opts = GameOptionsManager.Instance.CurrentGameOptions;
        int max = ImpostorCap.MaxFor(ImpostorCap.RoomSize(opts));
        __instance.ValidRange = new FloatRange(1f, max);
        float v = Math.Clamp(__instance.Value, 1f, max);
        if (v != __instance.Value)
        {
            __instance.Value = v;
            opts.SetInt(Int32OptionNames.NumImpostors, (int)v);
        }
    }
}

// 公式サーバーで部屋を立てる時は 3 人を超える設定を持ち込まない (地域の分からない時は戻さない)
[HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Confirm))]
internal static class ImpostorCapCreatePatch
{
    public static void Prefix()
    {
        if (!ServerKind.IsOfficial(ignoreNetworkMode: true, unknownIsOfficial: false)) return;
        var m = GameOptionsManager.Instance;
        if (m == null) return;
        ImpostorCap.ClampToVanilla(m.GameHostOptions);
        ImpostorCap.ClampToVanilla(m.CurrentGameOptions);
    }
}
