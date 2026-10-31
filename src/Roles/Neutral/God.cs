// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/God.cs (GPL-3.0)
using HarmonyLib;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// 神 (第三陣営): 全員の役職が見える (会議中は死んだ人も)。試合の終わりに生きていれば (タスクが要る設定ならタスクも終えていれば)、
// 勝った陣営から勝ちを奪う。設定で投票先が見える。設定でサボタージュ (原子炉・酸素 / 通信 / 停電) を直せず、通報も緊急会議もできない
public sealed class God : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#FFFF00";
    public override Text Name => new("God.Name");
    public override string Reading => "かみ";
    public override Text Blurb => new("God.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt SeeVote = new("God.SeeVote", false);
    public static readonly BoolOpt NeedTasks = new("God.NeedTasks", false);
    public static readonly IntOpt CommonTasks = new("God.CommonTasks", 1, 0, 10) { When = () => NeedTasks };
    public static readonly IntOpt ShortTasks = new("God.ShortTasks", 1, 0, 20) { When = () => NeedTasks };
    public static readonly IntOpt LongTasks = new("God.LongTasks", 1, 0, 10) { When = () => NeedTasks };
    public static readonly BoolOpt NoFixReactor = new("God.CannotFixReactor", true);
    public static readonly BoolOpt NoFixComms = new("God.CannotFixComms", true);
    public static readonly BoolOpt NoFixLights = new("God.CannotFixLights", true);
    public static readonly BoolOpt NoReport = new("God.CannotUseReport", true);
    public static readonly BoolOpt NoEmergency = new("God.CannotUseEmergency", true);

    public override bool TasksCount => false;

    internal static bool LocalNoReport => NoReport && RoleState.Local is God;
    internal static bool LocalNoEmergency => NoEmergency && RoleState.Local is God;

    public override void OnAssigned()
    {
        if (!IsLocal) return;
        PortKit.After(0.5f, () =>
        {
            if (RoleState.Local != this) return;
            if (NeedTasks) SnrKit.AssignOwnTasks(CommonTasks, ShortTasks, LongTasks);
            else SnrKit.AssignOwnTasks(0, 0, 0);
        });
    }

    public override bool StealsWin(GameResult result) =>
        PortKit.Alive(Player) && (!NeedTasks || PortKit.AllTasksDone(Player));

    // 生きている人の役職 (会議中は死んだ人も)
    public override string NameMark(PlayerControl seen)
    {
        if (seen.PlayerId == PlayerId || (!PortKit.Alive(seen) && !MeetingHud.Instance)) return null;
        var r = RoleState.Of(seen);
        return r == null ? null : $" <size=70%><color={r.Color}>{r.Name}</color></size>";
    }

    // 直せないサボタージュか
    internal static bool Blocked(TaskTypes t) =>
        (NoFixReactor && t is TaskTypes.ResetReactor or TaskTypes.ResetSeismic or TaskTypes.StopCharles or TaskTypes.RestoreOxy)
        || (NoFixComms && t is TaskTypes.FixComms)
        || (NoFixLights && t is TaskTypes.FixLights);
}

// 神の画面では、直せない設定のサボタージュの操作盤を使えない
[HarmonyPatch(typeof(Console), nameof(Console.CanUse))]
internal static class GodConsolePatch
{
    public static void Postfix(Console __instance, ref float __result, [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse)
    {
        if (RoleState.Local is not God || __instance.TaskTypes == null) return;
        foreach (var t in __instance.TaskTypes)
        {
            if (!God.Blocked(t)) continue;
            canUse = couldUse = false;
            __result = float.MaxValue;
            return;
        }
    }
}
