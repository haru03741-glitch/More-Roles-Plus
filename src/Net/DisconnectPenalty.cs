// https://github.com/Rabek009/MoreGamemodes Patches/ClientPatch.cs (GPL-3.0)
using AmongUs.Data;
using HarmonyLib;

namespace MoreRolesPlus.Net;

// 試合の途中で抜けると付く「N 分間は他のゲームに参加できません」を消す。
// この待ち時間は端末に保存した点数から本編が自分で計算しているだけで、サーバーは見ていない。
// 部屋を作る画面は点数を直接見るので、オンラインの確認の直前だけでなく、抜けた後に必ず戻るメニューでも 0 に戻す。
[HarmonyPatch]
internal static class DisconnectPenaltyPatch
{
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CheckOnlinePermissions))]
    [HarmonyPrefix]
    public static void BeforeOnlineCheck() => Clear();

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    public static void AfterMenu() => Clear();

    private static void Clear() => DataManager.Player.Ban.banPoints = 0f;
}
