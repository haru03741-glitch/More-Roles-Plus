// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Bait.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ベイト (クルー): キルされると、少し後にキルした人が自分の死体を通報してしまう
public sealed class Bait : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#00F7FF";
    public override Text Name => new("Bait.Name");
    public override string Reading => "べいと";
    public override Text Blurb => new("Bait.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt ReportTime = new("Bait.ReportTime", 0.5f, 0.5f, 10f, 0.1f, "s");
    public static readonly BoolOpt WarnKiller = new("Bait.WarnKiller", true);
    public static readonly BoolOpt RandomDelay = new("Bait.RandomDelay", false);
    public static readonly IntOpt DelayVariation = new("Bait.DelayVariation", 2, 0, 30, 1, "s") { When = () => RandomDelay };

    // キルした人の端末で、その人に通報させる
    [OnlyMine]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        var killer = e.Killer;
        if (!killer || killer.PlayerId == PlayerId || !killer.AmOwner) return;
        if (WarnKiller) PortKit.Flash(new Color(0f, 0.97f, 1f, 0.5f));
        float delay = ReportTime + (RandomDelay ? Random.Range(0f, DelayVariation) : 0f);
        var body = Player.Data;
        PortKit.After(delay, () =>
        {
            if (!PortKit.Alive(killer) || MeetingHud.Instance || ExileController.Instance) return;
            killer.CmdReportDeadBody(body);
        });
    }
}
