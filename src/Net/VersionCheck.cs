using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Net;

// 全員が同じ版の MRP を入れているかの確認。
// 各自が自分のキャラが出た時に版と指紋を送り、ホストが覚えておく。
// 同じ版の人には設定値を送り、入っていない人・版が違う人がいる間はゲームを始めさせない。
internal static class VersionCheck
{
    // clientId → 同じ版か
    private static readonly Dictionary<int, bool> Seen = new();
    private static readonly Dictionary<int, string> SeenVersion = new();
    private static readonly Dictionary<int, int> SeenAbi = new();
    // clientId → 入ってきた時刻。版の知らせは入ってから 1 秒あまり後に届くので、その前に始めようとした時は待つ
    private static readonly Dictionary<int, long> JoinedAt = new();
    private const long CheckingMs = 10000;
    private static long _startPendingUntil; // 確認が済み次第始める (0 = 待っていない)

    // 自分のキャラが出た合図から少し待って送る (Start はコルーチンで、合図の時点ではまだ送れないことがある)
    private static bool _helloPending;
    private static long _helloAtMs;
    private static System.IntPtr _helloSentFor; // 同じ自分のキャラには 1 回だけ送る

    public static void ScheduleHello()
    {
        _helloPending = true;
        _helloAtMs = System.Environment.TickCount64 + 1000;
    }

    public static void Tick()
    {
        if (!_helloPending || System.Environment.TickCount64 < _helloAtMs) return;
        _helloPending = false;
        var client = AmongUsClient.Instance;
        var me = PlayerControl.LocalPlayer;
        if (!client || client.AmHost || !me || me.Pointer == _helloSentFor) return;
        _helloSentFor = me.Pointer;
        SendHello();
    }

    public static void SendHello()
    {
        int abi = Native.MrpNative.Abi;
        Plugin.Logger.LogInfo($"hello -> host: {Plugin.Version} fp={Registry.Fingerprint:X8} abi={abi}");
        var w = Rpc.Start(Rpc.Hello);
        w.Write(Plugin.Version);
        w.Write(Registry.Fingerprint);
        w.Write(abi);
        Rpc.Finish(w);
    }

    public static void Receive(PlayerControl sender, MessageReader r)
    {
        string version = r.ReadString();
        uint fp = r.ReadUInt32();
        int abi = r.BytesRemaining >= 4 ? r.ReadInt32() : -1; // 古い版は送ってこない
        if (!AmongUsClient.Instance.AmHost) return;
        bool same = version == Plugin.Version && fp == Registry.Fingerprint && abi == Native.MrpNative.Abi;
        Seen[sender.OwnerId] = same;
        SeenVersion[sender.OwnerId] = version;
        SeenAbi[sender.OwnerId] = abi;
        Plugin.Logger.LogInfo($"hello from client {sender.OwnerId}: {version} fp={fp:X8} abi={abi} same={same}");
        if (same) OptionSync.SendAll(sender.OwnerId);
        if (_startPendingUntil != 0 && Blockers(out _).Count == 0 && _startPendingUntil != 0) // Blockers が期限切れを消す
        {
            _startPendingUntil = 0;
            // 数えが終わって読み込み中にもう一度始めると、ロビーの部品が壊れて毎フレーム例外が出る
            if (Vanilla.StartManager is { } gsm && !ShipStatus.Instance
                && gsm.startState == GameStartManager.StartingStates.NotStarting
                && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Joined)
                gsm.BeginGame();
        }
    }

    public static void OnClientJoined(int clientId) => JoinedAt[clientId] = System.Environment.TickCount64;

    // 客の側: 版の知らせはホストだけが覚えているので、ホストが抜けて替わったら新しいホストへ送り直す
    private static int _host = -1;

    public static void RememberHost() => _host = AmongUsClient.Instance.HostId;

    public static void CheckHost(int leftId)
    {
        var client = AmongUsClient.Instance;
        bool changed = _host != -1 && (leftId == _host || client.HostId != _host);
        _host = client.HostId;
        if (!changed || client.AmHost) return;
        _helloSentFor = System.IntPtr.Zero;
        ScheduleHello();
    }

    // 新しいホストの側: 前のホストが受けた知らせは引き継げないので、今いる人は入ってきたばかりと同じに待つ
    public static void OnBecomeHost()
    {
        Seen.Clear();
        SeenVersion.Clear();
        SeenAbi.Clear();
        _startPendingUntil = 0;
        long now = System.Environment.TickCount64;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p) JoinedAt[p.OwnerId] = now;
    }

    // 始めようとした時に確認中の人しか残っていなければ、確認が済み次第始める
    public static void HoldStart() => _startPendingUntil = System.Environment.TickCount64 + CheckingMs;
    public static void CancelHeldStart() => _startPendingUntil = 0;

    public static bool IsSame(int clientId) => Seen.TryGetValue(clientId, out bool s) && s;

    // 部屋に入る (自分が立てた時も) たびに、前の部屋の照合結果と送った記録を捨てる
    public static void OnJoined()
    {
        Seen.Clear();
        SeenVersion.Clear();
        SeenAbi.Clear();
        JoinedAt.Clear();
        _startPendingUntil = 0;
        _host = -1;
        _helloSentFor = System.IntPtr.Zero;
    }

    public static void ForgetClient(int clientId) { Seen.Remove(clientId); SeenVersion.Remove(clientId); SeenAbi.Remove(clientId); JoinedAt.Remove(clientId); }

    // 始められない人の一覧 (空なら始めてよい)。checking = そのうち入ってきたばかりで版の知らせを待っている人の数
    public static List<string> Blockers(out int checking)
    {
        var list = new List<string>();
        checking = 0;
        long now = System.Environment.TickCount64;
        if (_startPendingUntil != 0 && now > _startPendingUntil) _startPendingUntil = 0;
        int host = AmongUsClient.Instance.HostId;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.OwnerId == host || p.notRealPlayer || p.isDummy) continue; // ダミーはホストの端末で動く
            if (IsSame(p.OwnerId)) continue;
            string name = p.Data != null ? p.Data.PlayerName : $"#{p.PlayerId}";
            if (!SeenVersion.TryGetValue(p.OwnerId, out string v))
            {
                if (JoinedAt.TryGetValue(p.OwnerId, out long at) && now - at < CheckingMs)
                {
                    checking++;
                    list.Add(new Text("VersionCheck.Checking").With("name", name));
                }
                else list.Add(new Text("VersionCheck.NoMRP").With("name", name));
            }
            else if (v == Plugin.Version && SeenAbi.TryGetValue(p.OwnerId, out int abi) && abi != Native.MrpNative.Abi)
                list.Add(new Text("VersionCheck.DifferentNativeModule").With("name", name));
            else
                list.Add(new Text("VersionCheck.DifferentVersion").With("name", name).With("version", v));
        }
        return list;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start))]
internal static class HelloOnSpawnPatch
{
    // Start はコルーチンなので、この時点では自分のキャラかどうかもまだ決まっていないことがある。
    // 誰かが出たら合図だけ出し、送るかどうかは少し後で決める
    public static void Postfix()
    {
        if (AmongUsClient.Instance && !AmongUsClient.Instance.AmHost) VersionCheck.ScheduleHello();
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
internal static class BlockStartPatch
{
    public static bool Prefix()
    {
        if (!AmongUsClient.Instance.AmHost) return true;
        var blockers = VersionCheck.Blockers(out int checking);
        if (blockers.Count == 0) { VersionCheck.CancelHeldStart(); return true; }
        if (checking == blockers.Count)
        {
            VersionCheck.HoldStart();
            if (Vanilla.Chat is { } c)
                c.AddChat(PlayerControl.LocalPlayer, new Text("VersionCheck.WaitingForNewPlayers"), false);
            return false;
        }
        string msg = new Text("VersionCheck.Mismatch")
                     + string.Join("\n", blockers);
        if (Vanilla.Chat is { } chat) chat.AddChat(PlayerControl.LocalPlayer, msg, false);
        return false;
    }
}

[HarmonyPatch(typeof(AmongUsClient), "OnGameJoined")]
internal static class ResetOnJoinPatch
{
    public static void Prefix()
    {
        VersionCheck.OnJoined();
        Roles.PracticeMatch.OnJoined();
        Commands.ChatGuard.Reset();
    }

    public static void Postfix() => VersionCheck.RememberHost();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class JoinedAtPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData data)
    {
        if (data != null) VersionCheck.OnClientJoined(data.Id);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class ForgetLeftPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData data)
    {
        if (data != null) VersionCheck.ForgetClient(data.Id);
        VersionCheck.CheckHost(data != null ? data.Id : -1);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnBecomeHost))]
internal static class BecomeHostPatch
{
    public static void Postfix() => VersionCheck.OnBecomeHost();
}
