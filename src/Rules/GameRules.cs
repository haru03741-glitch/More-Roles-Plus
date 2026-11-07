// Based on https://github.com/tukasa0001/TownOfHost, https://github.com/EnhancedNetwork/TownofHost-Enhanced
// and https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System;
using HarmonyLib;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Rules;

// 本編の「ゲーム設定」タブの項目の終わりに 1 行ずつ足すルール (GameTabRows)。
// - クルー: 全員がベントを使える (各端末が自分の本編の役職にベントを許す)
// - 会議: 会議を無くす (緊急ボタンも死体の通報も。押す側で止め、ホストも断る)・スキップできない・
//   投票しなかった人は自分に入れた扱い (MeetingVotes)
// - タスク: タスクで勝てなくする (いつも / クルーが全員死んだ時だけ)
// - 終わり方: 人数 (キル・追放) やサボタージュで勝ち負けを決めない・死んでも生き返る (タブの終わりの組)
[Settings(Tab.General, "GameRules.Title", order: -55)]
public static class GameRules
{
    public static readonly BoolOpt EveryoneVents = new("GameRules.EveryoneVents", false) { OffTab = true };
    public static readonly BoolOpt NoMeetings = new("GameRules.NoMeetings", false) { OffTab = true };
    public static readonly BoolOpt NoSkip = new("GameRules.NoSkip", false) { OffTab = true };
    public static readonly BoolOpt SelfVoteIfNone = new("GameRules.SelfVoteIfNone", false) { OffTab = true };
    public static readonly ChoiceOpt TaskWin = new("GameRules.TaskWin", 0,
        new Text("GameRules.TaskWinOn"), new Text("GameRules.TaskWinNever"), new Text("GameRules.TaskWinNotIfCrewDead")) { OffTab = true };

    // 試合の終わり方を止める (ホストの終わりの判定で見る)。タスクの勝ちは上の TaskWin
    public static readonly BoolOpt NoCountWin = new("GameRules.NoCountWin", false) { OffTab = true };
    public static readonly BoolOpt NoSabotageWin = new("GameRules.NoSabotageWin", false) { OffTab = true };
    // 死んでも数秒で生き返る (ホストが生き返らせる・Respawn)
    public static readonly BoolOpt Respawn = new("GameRules.Respawn", false) { OffTab = true };
    public static readonly IntOpt RespawnSeconds = new("GameRules.RespawnSeconds", 5, 1, 60, 1, "s") { OffTab = true, Parent = Respawn };

    internal static readonly Opt[] EndRows = { NoCountWin, NoSabotageWin, Respawn, RespawnSeconds };

    // 本編の終わりの判定の代わりに MRP の判定で見る必要があるルールが入っているか
    internal static bool EndRuleOn => TaskWin.Value != 0 || NoCountWin || NoSabotageWin;

    // 会議が無いか (設定か、サドンデスの間)
    internal static bool MeetingsOff => NoMeetings || SuddenDeath.Active;

    // タスクで勝つのを止めているか (ホストの終わりの判定で見る)。サドンデスではいつも止める
    internal static bool TaskWinBlocked()
    {
        if (SuddenDeath.Active) return true;
        switch (TaskWin.Value)
        {
            case 1: return true;
            case 2: return !AnyCrewAlive();
            default: return false;
        }
    }

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
        if (p && Roles.RoleState.Of(p) is { CanVent: not null } mrp) { ApplyRoleVent(mrp); return; }
        if (!(EveryoneVents || SuddenDeath.Active && SuddenDeath.Vent) || !p || !p.AmOwner || p.Data == null) return;
        var role = p.Data.Role;
        if (!role || role.CanVent || RoleManager.IsGhostRole(role.Role)) return;
        role.CanVent = true;
        var hud = Vanilla.Hud;
        if (hud && hud.ImpostorVentButton && ShipStatus.Instance) hud.ImpostorVentButton.ToggleVisible(true);
    }

    // MRP の役職が決めたベントの可否を本編の役職へ写す (役職が付いた時と、本編の役職が後から届いた時)
    internal static void ApplyRoleVent(Roles.RoleBase mrp)
    {
        var p = mrp.Player;
        if (mrp.CanVent is not bool can || !p || p.Data == null) return;
        var role = p.Data.Role;
        if (!role || RoleManager.IsGhostRole(role.Role)) return;
        role.CanVent = can;
        if (!p.AmOwner) return;
        var hud = Vanilla.Hud;
        if (hud && hud.ImpostorVentButton && ShipStatus.Instance) hud.ImpostorVentButton.ToggleVisible(can && !p.Data.IsDead);
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

// タスクで勝てなくするルールの間と、配られたタスクが 1 つも無い試合 (クルーがいない) では、本編のタスクの勝ちの判定に「まだ」と答える
// (本編はタスクの総数 0 を「全部済んだ」と数える)。
// 本編は通報の時にもこれを見て、済んでいれば会議を開かずにタスクの勝ちで終わらせようとする (止めると会議が開けないまま残る)
[HarmonyPatch(typeof(GameManager), nameof(GameManager.CheckTaskCompletion))]
internal static class TaskWinRulePatch
{
    public static bool Prefix(ref bool __result)
    {
        if (!Options.GameModeSwitch.IsNormal) return true;
        var gd = GameData.Instance;
        if (!GameRules.TaskWinBlocked() && !(gd && gd.TotalTasks <= 0)) return true;
        __result = false;
        return false;
    }
}

// 会議を無くす: 緊急ボタンの画面はすぐ閉じる・通報は送らない (自分) / 受けない (ホスト)
[HarmonyPatch(typeof(EmergencyMinigame), nameof(EmergencyMinigame.Begin))]
internal static class NoMeetingsButtonPatch
{
    public static void Postfix(EmergencyMinigame __instance)
    {
        if (GameRules.MeetingsOff && ShipStatus.Instance) __instance.Close();
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdReportDeadBody))]
internal static class NoMeetingsReportPatch
{
    public static bool Prefix() => !(GameRules.MeetingsOff && ShipStatus.Instance);
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
internal static class NoMeetingsHostPatch
{
    public static bool Prefix() => !(GameRules.MeetingsOff && ShipStatus.Instance && AmongUsClient.Instance.AmHost);
}

// 通報のボタンを出さない (HUD を出し入れした時。本編は両方の入口から出し直す)
[HarmonyPatch(typeof(HudManager))]
internal static class NoMeetingsHudPatch
{
    [HarmonyPatch(nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool)), HarmonyPostfix]
    public static void Full(HudManager __instance) => Hide(__instance);

    [HarmonyPatch(nameof(HudManager.SetHudActive), typeof(bool)), HarmonyPostfix]
    public static void Short(HudManager __instance) => Hide(__instance);

    // 本編が後から出し直すことがある (役職が届いた時など) ので、毎 FixedUpdate も見る
    internal static void Tick()
    {
        if (!GameRules.MeetingsOff) return; // 会議ありの試合ではボタンを読まない
        var hud = Vanilla.Hud;
        if (hud && hud.ReportButton && hud.ReportButton.gameObject.activeSelf) Hide(hud);
    }

    private static void Hide(HudManager hud)
    {
        if (GameRules.MeetingsOff && ShipStatus.Instance && hud.ReportButton) hud.ReportButton.gameObject.SetActive(false);
    }
}
