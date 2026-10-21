using System.Net;
using System.Net.Sockets;
using HarmonyLib;
using InnerNet;

namespace MoreRolesPlus.Net;

// 同じ PC で Among Us を 2 つ動かした時の待ち受けの取り合いを避ける。
// 本編のフリープレイは 127.0.0.1:22023、LAN の部屋は全体 (0.0.0.0):22023 で待ち受ける。Windows ではこの 2 つを同時に掴めて、
// 127.0.0.1 宛ては 127.0.0.1 だけで待つ側へ届く。そのため LAN の部屋の自分の接続が別の AU のフリープレイへ入り、
// 「ホストを待っています」のまま止まる (部屋を立てた後にフリープレイが始まっても同じ)。
// - フリープレイ: 22023 が使われていたら空いている番号で立て、自分の接続も同じ番号へ向ける
// - LAN: 自分の PC の部屋へは 127.0.0.1 でなく LAN のアドレスでつなぐ (全体で待つ部屋にだけ届く)
internal static class LocalPorts
{
    // 部屋 (StartAsLocalServer) と接続 (SetEndpoint) は本編の呼ぶ順に頼らず、決めてからしばらくは同じ番号を返す
    // (片方だけ・2 回呼ばれても揃う)。次のフリープレイは時間が過ぎてから来るので決め直す
    private const long KeepMs = 1000;
    private static long _decidedAt; // 0 = まだ決めていない

    internal static ushort LastFreePlayPort { get; private set; } = Constants.GamePlayPort;

    internal static ushort FreePlayPort()
    {
        long now = System.Environment.TickCount64;
        if (_decidedAt != 0 && now - _decidedAt < KeepMs) return LastFreePlayPort;
        ushort p = Constants.GamePlayPort;
        if (!UdpPortFree(p)) p = FreeUdpPort();
        if (p != Constants.GamePlayPort) Plugin.Logger.LogInfo($"[LocalPorts] port {Constants.GamePlayPort} is in use, free play uses {p}");
        _decidedAt = now;
        LastFreePlayPort = p;
        return p;
    }

    // Android は 1 台で 1 つしか動かないので触らない
    internal static bool Active => !System.OperatingSystem.IsAndroid();

    // 外へ出る時に使う自分のアドレス (UDP の Connect は送らずに経路だけ決める)。LAN が無ければ null
    internal static string LanAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address;
            return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) ? null : ip.ToString();
        }
        catch (SocketException) { return null; }
    }

    // フリープレイは 127.0.0.1 で待ち受け、LAN の部屋は全体で待ち受けるので、両方で掴めるかを見る
    private static bool UdpPortFree(ushort port) => UdpBindable(IPAddress.Loopback, port) && UdpBindable(IPAddress.Any, port);

    private static bool UdpBindable(IPAddress addr, ushort port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.ExclusiveAddressUse = true;
            s.Bind(new IPEndPoint(addr, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    // OS に空き番号を選ばせる (閉じてから本編が掴むまでの短い間に取られることは考えない)
    private static ushort FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return (ushort)((IPEndPoint)s.LocalEndPoint!).Port;
    }
}

[HarmonyPatch(typeof(InnerNetServer), nameof(InnerNetServer.StartAsLocalServer))]
internal static class LocalPortsServerPatch
{
    public static void Prefix(InnerNetServer __instance)
    {
        var c = AmongUsClient.Instance;
        if (!LocalPorts.Active || !c) return;
        // 部屋は使い回されて番号が残るので、フリープレイ以外では本編の番号へ戻す
        __instance.Port = c.NetworkMode == NetworkModes.FreePlay ? LocalPorts.FreePlayPort() : Constants.GamePlayPort;
        Plugin.Logger.LogInfo($"[LocalPorts] local server mode={c.NetworkMode} port={__instance.Port}");
    }
}

// LAN の部屋はこちらで立つ (全体で待ち受ける)。フリープレイで変えた番号が残っているので本編の番号へ戻す
[HarmonyPatch(typeof(InnerNetServer), nameof(InnerNetServer.StartAsServer))]
internal static class LocalPortsLanServerPatch
{
    public static void Prefix(InnerNetServer __instance)
    {
        if (!LocalPorts.Active) return;
        __instance.Port = Constants.GamePlayPort;
        Plugin.Logger.LogInfo($"[LocalPorts] lan server port={__instance.Port}");
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SetEndpoint))]
internal static class LocalPortsClientPatch
{
    public static void Prefix(InnerNetClient __instance, ref string addr, ref ushort port)
    {
        if (!LocalPorts.Active || port != Constants.GamePlayPort || addr != "127.0.0.1") return;
        if (__instance.NetworkMode == NetworkModes.FreePlay) port = LocalPorts.FreePlayPort();
        else if (__instance.NetworkMode == NetworkModes.LocalGame && LocalPorts.LanAddress() is { } lan)
        {
            Plugin.Logger.LogInfo($"[LocalPorts] local game via {lan} instead of 127.0.0.1");
            addr = lan;
        }
    }
}
