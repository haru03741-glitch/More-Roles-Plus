using System;
using HarmonyLib;
using Hazel;

namespace MoreRolesPlus.Net;

// MRP の電文の番号表と受け口。番号は本編の RpcCalls と重ならない高い所を使う。
// 新しい電文を足す時は、ここに番号を足して LastId を合わせ、下の switch に受け先を書く。
internal static class Rpc
{
    public const byte Hello = 210;     // 全員 → ホスト: 版と指紋
    public const byte Options = 211;   // ホスト → 客: 設定値
    public const byte Roles = 212;     // ホスト → 全員: 役職の割り当て
    public const byte Terrain = 213;   // 地形の破壊
    public const byte Win = 214;       // ホスト → 客: 試合の勝者 (第三陣営がいる試合だけ)
    public const byte LastId = Win;    // 受け口で拾う番号の上限 (番号を足したらここも)

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
        if (callId < Rpc.Hello || callId > Rpc.LastId) return true;
        try
        {
            switch (callId)
            {
                case Rpc.Hello: VersionCheck.Receive(__instance, reader); break;
                case Rpc.Options: OptionSync.Receive(__instance, reader); break;
                case Rpc.Roles: MoreRolesPlus.Roles.RoleAssigner.Receive(__instance, reader); break;
                case Rpc.Terrain: Terrain.TerrainSync.Receive(__instance, reader); break;
                case Rpc.Win: MoreRolesPlus.Roles.GameEnd.Receive(__instance, reader); break;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"rpc {callId}: {ex}"); }
        return false;
    }
}
