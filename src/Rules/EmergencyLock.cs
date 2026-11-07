using HarmonyLib;

namespace MoreRolesPlus.Rules;

// 役職・アドオンで緊急ボタンを押せない人 (ゲスト・プロセキューターの設定)。押せない人には画面に「ロックされている」と出す
internal static class EmergencyLock
{
    internal static bool Locked(PlayerControl p) =>
        p && (Roles.Crew.Prosecutor.BlocksEmergency(p) || Roles.Addons.Guest.Has(p));
}

[HarmonyPatch(typeof(EmergencyMinigame), nameof(EmergencyMinigame.Update))]
internal static class EmergencyLockPatch
{
    public static void Postfix(EmergencyMinigame __instance)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!EmergencyLock.Locked(lp)) return;
        __instance.StatusText.text = new Text("EmergencyLock.Locked").ToString();
        __instance.NumberText.text = string.Empty;
        __instance.ClosedLid.gameObject.SetActive(true);
        __instance.OpenLid.gameObject.SetActive(false);
        __instance.ButtonActive = false;
    }
}

// 押せない人の通報 (緊急の会議だけ) をホストが受けない
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
internal static class EmergencyLockHostPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target) =>
        !(target == null && AmongUsClient.Instance.AmHost && ShipStatus.Instance && EmergencyLock.Locked(__instance));
}
