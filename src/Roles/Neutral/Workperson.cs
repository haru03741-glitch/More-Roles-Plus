// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Workperson.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// 仕事人 (第三陣営): 自分のタスクを全部終えたら一人勝ち
public sealed class Workperson : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#D2B48C";
    public override Text Name => new("Workperson.Name");
    public override string Reading => "しごとにん";
    public override Text Blurb => new("Workperson.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt NeedAlive = new("Workperson.NeedAlive", false);
    public static readonly BoolOpt Vent = new("Workperson.CanVent", false);

    public override bool? CanVent => Vent;

    // タスクを終えた本人の端末でもホストの端末でも起きる。勝ちを決めるのはホスト
    [HostOnly, OnlyMine]
    private void OnTask(TaskCompletedEvent e)
    {
        if (NeedAlive && !PortKit.Alive(Player)) return;
        if (PortKit.AllTasksDone(Player)) GameEnd.Win(this);
    }
}
