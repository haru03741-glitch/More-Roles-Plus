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

    public static void Apply()
    {
        var client = AmongUsClient.Instance;
        CurrentModRegistration.ModRegistrationGuidString =
            client && client.NetworkMode == NetworkModes.OnlineGame ? Guid : string.Empty;
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HostGame))]
    private static class HostGamePatch { public static void Prefix() => Apply(); }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.JoinGame))]
    private static class JoinGamePatch { public static void Prefix() => Apply(); }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.RequestGameList))]
    private static class RequestGameListPatch { public static void Prefix() => Apply(); }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.GetConnectionData))]
    private static class GetConnectionDataPatch { public static void Prefix() => Apply(); }

    [HarmonyPatch(typeof(InnerNetServer), nameof(InnerNetServer.StartAsLocalServer))]
    private static class StartAsLocalServerPatch { public static void Prefix() => CurrentModRegistration.ModRegistrationGuidString = string.Empty; }
}
