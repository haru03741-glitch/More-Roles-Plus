// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Tasker.cs (GPL-3.0)
using HarmonyLib;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// タスカー (インポスター): 本物のタスクができ、生きたまま全部終えるとインポスターの勝ち。
// 設定でキルできなくしたり、タスク中はキルの待ち時間が進まないようにできる
public sealed class Tasker : RoleBase, ITaskDoer
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Tasker.Name");
    public override string Reading => "たすかー";
    public override Text Blurb => new("Tasker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Tasker.Tasks") };

    public static readonly BoolOpt EnableIndividualTasks = new("Tasker.EnableIndividualTasks", true);
    public static readonly IntOpt CommonTasks = new("Tasker.CommonTasks", 1, 0, 10) { When = () => EnableIndividualTasks };
    public static readonly IntOpt ShortTasks = new("Tasker.ShortTasks", 1, 0, 20) { When = () => EnableIndividualTasks };
    public static readonly IntOpt LongTasks = new("Tasker.LongTasks", 1, 0, 10) { When = () => EnableIndividualTasks };
    public static readonly BoolOpt TaskerCanKill = new("Tasker.CanKill", true);
    public static readonly BoolOpt KillCoolDuringTask = new("Tasker.KillCoolDuringTask", true) { When = () => TaskerCanKill };

    public override void OnAssigned()
    {
        if (!IsLocal) return;
        PortKit.After(1f, () =>
        {
            if (RoleState.Of(PlayerControl.LocalPlayer) is not Tasker) return;
            if (EnableIndividualTasks && CommonTasks + ShortTasks + LongTasks > 0) SnrKit.AssignOwnTasks(CommonTasks, ShortTasks, LongTasks);
            else SnrKit.AssignOwnVanillaTasks();
        });
    }

    // キルできない設定: 本編のキルボタンを隠す
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (TaskerCanKill) return;
        var hud = Vanilla.Hud;
        if (hud && hud.KillButton && hud.KillButton.gameObject.activeSelf) hud.KillButton.gameObject.SetActive(false);
    }

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!TaskerCanKill && e.Killer && e.Killer.PlayerId == PlayerId) e.Cancel("tasker cannot kill");
    }

    // 生きたまま全部のタスクを終えたらインポスターの勝ち
    [HostOnly]
    private void OnTask(TaskCompletedEvent e)
    {
        if (!e.Player || e.Player.PlayerId != PlayerId || !PortKit.Alive(Player)) return;
        if (PortKit.AllTasksDone(Player)) GameEnd.WinTeam(Team.Impostor);
    }

    // タスクの画面を開いている間はキルの待ち時間を止める (設定で止めない時を除く)
    internal static bool HoldKillTimer(PlayerControl p)
    {
        if (!p || !p.AmOwner || KillCoolDuringTask) return false;
        if (RoleState.Of(p) is not Tasker || !PortKit.Alive(p)) return false;
        if (p.CanMove || !Minigame.Instance) return false;
        var task = Minigame.Instance.MyNormTask;
        return task && task.Owner && task.Owner.AmOwner;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
internal static class TaskerKillTimerPatch
{
    private static float? _saved;

    [HarmonyPriority(Priority.First)]
    public static void Prefix(PlayerControl __instance) => _saved = Tasker.HoldKillTimer(__instance) ? __instance.killTimer : null;

    [HarmonyPriority(Priority.Last)]
    public static void Postfix(PlayerControl __instance)
    {
        if (_saved is not float t) return;
        _saved = null;
        if (!__instance.AmOwner) return;
        __instance.killTimer = t;
        var hud = Vanilla.Hud;
        float max = GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown);
        if (hud && hud.KillButton) hud.KillButton.SetCoolDown(t, max);
    }
}

// タスカーには途中のタスクの矢印を出す (インポスターは本編では出ない)
[HarmonyPatch(typeof(NormalPlayerTask), nameof(NormalPlayerTask.FixedUpdate))]
internal static class TaskerTaskArrowPatch
{
    public static void Postfix(NormalPlayerTask __instance)
    {
        if (!__instance || !__instance.Arrow) return;
        if (__instance.IsComplete)
        {
            if (__instance.Arrow.isActiveAndEnabled) __instance.Arrow.gameObject.SetActive(false);
            return;
        }
        if (RoleState.Local is not Tasker || __instance.TaskStep <= 0) return;
        __instance.Arrow.gameObject.SetActive(true);
    }
}
