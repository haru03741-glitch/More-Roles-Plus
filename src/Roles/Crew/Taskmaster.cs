// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Taskmaster.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// タスクマスター (クルー): タスクを開くだけで、そのタスクが終わる
public sealed class Taskmaster : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFD700";
    public override Text Name => new("Taskmaster.Name");
    public override string Reading => "たすくますたー";
    public override Text Blurb => new("Taskmaster.Blurb");
    public override string Origin => "SNR";
}

// タスクの画面が開いた時。自分がタスクマスターなら、その場で終えて閉じる
[HarmonyLib.HarmonyPatch(typeof(Minigame), nameof(Minigame.Begin))]
internal static class TaskmasterBeginPatch
{
    public static void Postfix(Minigame __instance)
    {
        if (RoleState.Local is not Taskmaster || !__instance) return;
        var task = __instance.MyNormTask;
        var lp = PlayerControl.LocalPlayer;
        if (task == null || task.IsComplete || !lp) return;
        lp.RpcCompleteTask(task.Id);
        __instance.Close();
    }
}
