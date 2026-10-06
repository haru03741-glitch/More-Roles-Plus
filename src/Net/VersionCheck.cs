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
        Plugin.Logger.LogInfo($"hello -> host: {Plugin.Version} fp={Registry.Fingerprint:X8}");
        var w = Rpc.Start(Rpc.Hello);
        w.Write(Plugin.Version);
        w.Write(Registry.Fingerprint);
        Rpc.Finish(w);
    }

    public static void Receive(PlayerControl sender, MessageReader r)
    {
        string version = r.ReadString();
        uint fp = r.ReadUInt32();
        if (!AmongUsClient.Instance.AmHost) return;
        bool same = version == Plugin.Version && fp == Registry.Fingerprint;
        Seen[sender.OwnerId] = same;
        SeenVersion[sender.OwnerId] = version;
        Plugin.Logger.LogInfo($"hello from client {sender.OwnerId}: {version} fp={fp:X8} same={same}");
        if (same) OptionSync.SendAll(sender.OwnerId);
    }

    public static bool IsSame(int clientId) => Seen.TryGetValue(clientId, out bool s) && s;

    // 部屋に入る (自分が立てた時も) たびに、前の部屋の照合結果と送った記録を捨てる
    public static void OnJoined()
    {
        Seen.Clear();
        SeenVersion.Clear();
        _helloSentFor = System.IntPtr.Zero;
    }

    public static void ForgetClient(int clientId) { Seen.Remove(clientId); SeenVersion.Remove(clientId); }

    // 始められない人の一覧 (空なら始めてよい)
    public static List<string> Blockers()
    {
        var list = new List<string>();
        int host = AmongUsClient.Instance.HostId;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.OwnerId == host || p.notRealPlayer) continue;
            if (IsSame(p.OwnerId)) continue;
            string name = p.Data != null ? p.Data.PlayerName : $"#{p.PlayerId}";
            list.Add(SeenVersion.TryGetValue(p.OwnerId, out string v)
                ? new Text($"{name} (版が違う: {v})", $"{name} (different version: {v})")
                : new Text($"{name} (MRP が入っていない)", $"{name} (no MRP)"));
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
        var blockers = VersionCheck.Blockers();
        if (blockers.Count == 0) return true;
        string msg = new Text("全員が同じ版の More Roles Plus を入れるまで始められません:\n", "Everyone needs the same More Roles Plus version:\n")
                     + string.Join("\n", blockers);
        if (HudManager.InstanceExists) HudManager.Instance.Chat.AddChat(PlayerControl.LocalPlayer, msg, false);
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
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class ForgetLeftPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData data)
    {
        if (data != null) VersionCheck.ForgetClient(data.Id);
    }
}
