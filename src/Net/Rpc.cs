using System;
using HarmonyLib;
using Hazel;

namespace MoreRolesPlus.Net;

// 本編の RPC 番号のうち MRP が使う 2 つ。本編の RpcCalls と重ならない高い所を使う。
// MRP の電文は全部 Bus の中に入れて送る (足し方は RemoteCall)。
// Hello だけは版が違う相手とも読み合える必要があるので、Bus に入れず番号を固定する。
internal static class Rpc
{
    public const byte Hello = 210; // 全員 → ホスト: 版と指紋
    public const byte Bus = 211;   // MRP の電文の束 ([指紋][電文]...)

    public static MessageWriter Start(byte id, int target = -1) =>
        AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, id, SendOption.Reliable, target);

    public static void Finish(MessageWriter w) => AmongUsClient.Instance.FinishRpcImmediately(w);

    public static bool FromHost(PlayerControl sender) => sender && sender.OwnerId == AmongUsClient.Instance.HostId;
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class RpcPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] byte callId, [HarmonyArgument(1)] MessageReader reader)
    {
        if (callId != Rpc.Hello && callId != Rpc.Bus) return true;
        try
        {
            if (callId == Rpc.Hello) VersionCheck.Receive(__instance, reader);
            else Remote.Receive(__instance, reader);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"rpc {callId}: {ex}"); }
        return false;
    }
}
