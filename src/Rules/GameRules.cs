// Based on https://github.com/tukasa0001/TownOfHost, https://github.com/EnhancedNetwork/TownofHost-Enhanced
// and https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System;
using HarmonyLib;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Rules;

// 本編の「ゲーム設定」タブの項目の終わりに 1 行ずつ足すルール (GameTabRows)。
// - クルー: 全員がベントを使える (各端末が自分の本編の役職にベントを許す)
// - 会議: 会議を無くす (緊急ボタンも死体の通報も。押す側で止め、ホストも断る)
// - タスク: タスクで勝てなくする (いつも / クルーが全員死んだ時だけ)
[Settings(Tab.General, "GameRules.Title", order: -55)]
public static class GameRules
{
    public static readonly BoolOpt EveryoneVents = new("GameRules.EveryoneVents", false) { OffTab = true };
    public static readonly BoolOpt NoMeetings = new("GameRules.NoMeetings", false) { OffTab = true };
    public static readonly ChoiceOpt TaskWin = new("GameRules.TaskWin", 0,
        new Text("GameRules.TaskWinOn"), new Text("GameRules.TaskWinNever"), new Text("GameRules.TaskWinNotIfCrewDead")) { OffTab = true };

    // タスクで勝つのを止めているか (ホストの終わりの判定で見る)
    internal static bool TaskWinBlocked()
    {
        switch (TaskWin.Value)
        {
            case 1: return true;
            case 2: return !AnyCrewAlive();
            default: return false;
        }
    }

    internal static bool TaskWinRuleOn => TaskWin.Value != 0;

    private static bool AnyCrewAlive()
    {
        var gd = GameData.Instance;
        if (!gd) return true;
        var all = gd.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected || p.IsDead) continue;
            if (p.Role && p.Role.IsImpostor) continue;
            if (RoleState.Of(p.PlayerId) is { Team: Team.Neutral }) continue;
            return true;
        }
        return false;
    }

    // 自分の本編の役職にベントを許す (役職が付いた時)
    internal static void OnRoleSet(PlayerControl p)
    {
        if (!EveryoneVents || !p || !p.AmOwner || p.Data == null) return;
        var role = p.Data.Role;
        if (!role || role.CanVent || RoleManager.IsGhostRole(role.Role)) return;
        role.CanVent = true;
        var hud = Vanilla.Hud;
        if (hud && hud.ImpostorVentButton && ShipStatus.Instance) hud.ImpostorVentButton.ToggleVisible(true);
    }
}

[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SetRole))]
internal static class EveryoneVentsPatch
{
    public static void Postfix([HarmonyArgument(0)] PlayerControl targetPlayer)
    {
        try { GameRules.OnRoleSet(targetPlayer); }
        catch (Exception e) { Plugin.Logger.LogError($"everyone vents: {e}"); }
    }
}

// 会議を無くす: 緊急ボタンの画面はすぐ閉じる・通報は送らない (自分) / 受けない (ホスト)
[HarmonyPatch(typeof(EmergencyMinigame), nameof(EmergencyMinigame.Begin))]
internal static class NoMeetingsButtonPatch
{
    public static void Postfix(EmergencyMinigame __instance)
    {
        if (GameRules.NoMeetings && ShipStatus.Instance) __instance.Close();
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
internal static class NoMeetingsReportPatch
{
    public static bool Prefix() => !(GameRules.NoMeetings && ShipStatus.Instance);
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
internal static class NoMeetingsHostPatch
{
    public static bool Prefix() => !(GameRules.NoMeetings && ShipStatus.Instance && AmongUsClient.Instance.AmHost);
}

// 通報のボタンを出さない (HUD を出し入れした時。本編は両方の入口から出し直す)
[HarmonyPatch(typeof(HudManager))]
internal static class NoMeetingsHudPatch
{
    [HarmonyPatch(nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool)), HarmonyPostfix]
    public static void Full(HudManager __instance) => Hide(__instance);

    [HarmonyPatch(nameof(HudManager.SetHudActive), typeof(bool)), HarmonyPostfix]
    public static void Short(HudManager __instance) => Hide(__instance);

    private static void Hide(HudManager hud)
    {
        if (GameRules.NoMeetings && ShipStatus.Instance && hud.ReportButton) hud.ReportButton.gameObject.SetActive(false);
    }
}
