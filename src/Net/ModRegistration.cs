// Ported from https://github.com/ykundesu/SuperNewRoles Patches/ModRegistrationPatch.cs (GPL-3.0)
using HarmonyLib;
using InnerNet;

namespace MoreRolesPlus.Net;

// 公式サーバーに「全員が同じ mod を入れる部屋」として登録する。登録した部屋は mod の無い人が入れず、
// 部屋検索でも同じ mod の部屋だけが出る。ローカルの部屋で GUID を付けると入れなくなるので、
// オンラインの時だけ付けて、それ以外は外す。
// 版番号の印 (ModdedVersion.cs) とは別の仕組みで、両方を同時に使ってよい。
internal static class ModRegistration
{
    // 一度決めたら変えない (変えると前の版と同じ部屋に入れなくなる)
    public const string Guid = "169375b5-e08c-4ca8-bf96-0c8cde15ddf7";

    // テスト用 (ブリッジの modreg 0)。付けない時の動きと比べる
    public static bool Off;

    public static void Apply()
    {
        var client = AmongUsClient.Instance;
        CurrentModRegistration.ModRegistrationGuidString =
            !Off && client && client.NetworkMode == NetworkModes.OnlineGame ? Guid : string.Empty;
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HostGame))]
    private static class HostGamePatch { public static void Prefix() => Apply(); }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.JoinGame))]
    private static class JoinGamePatch { public static void Prefix() => Apply(); }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.RequestGameList))]
    private static class RequestGameListPatch { public static void Prefix() => Apply(); }

    // 「ゲームを探す」画面は開いた時に GUID を読んで検索条件を作る (上の RequestGameList は通らない)。
    // この画面はまだオンラインになっていない (NetworkMode が違う) ので、Apply だと GUID が外れる。
    // GUID が空のまま作られた mod の条件は中身が null になり、Impostor は「読めない」と 400 を返して一覧が出ない
    [HarmonyPatch(typeof(FindAGameManager), nameof(FindAGameManager.Start))]
    private static class FindAGameStartPatch { public static void Prefix() => ForSearch(); }

    [HarmonyPatch(typeof(CurrentModRegistration), nameof(CurrentModRegistration.UpdateFilterSetWithModRegistrationSettings))]
    private static class FilterSetPatch
    {
        public static void Prefix() => ForSearch();

        // それでも中身の無い mod の条件が残ったら外す (送ると一覧が 400 で読めない)
        public static void Postfix(AmongUs.Matchmaking.GameFilterSet __0)
        {
            try
            {
                var filters = __0?.Filters;
                if (filters == null) return;
                for (int i = filters.Count - 1; i >= 0; i--)
                {
                    var f = filters[i];
                    if (f != null && f.OptionType == "mod" && f.SubFilter == null) filters.RemoveAt(i);
                }
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"modreg: filter fix failed: {e.Message}"); }
        }
    }

    // 部屋探しは必ずオンラインなので、同じ mod の部屋だけを探す
    private static void ForSearch() => CurrentModRegistration.ModRegistrationGuidString = Off ? string.Empty : Guid;

#if !ANDROID
    // Android では this を渡さずに呼ばれる関数で、パッチの入口が例外を出して本編の接続の挨拶が壊れる
    // (公式サーバーに「版が古い」と切られる)。上の 3 つで GUID は付くので Android では当てない。
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.GetConnectionData))]
    private static class GetConnectionDataPatch { public static void Prefix() => Apply(); }
#endif

    [HarmonyPatch(typeof(InnerNetServer), nameof(InnerNetServer.StartAsLocalServer))]
    private static class StartAsLocalServerPatch { public static void Prefix() => CurrentModRegistration.ModRegistrationGuidString = string.Empty; }
}
