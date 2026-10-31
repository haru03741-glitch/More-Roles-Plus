// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Teruteru.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// てるてる (第三陣営): 会議で追放されたら一人勝ち。設定でタスクを終えている事も条件にできる
public sealed class Teruteru : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#FFA500";
    public override Text Name => new("Teruteru.Name");
    public override string Reading => "てるてる";
    public override Text Blurb => new("Teruteru.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt Vent = new("Teruteru.CanVent", false);
    public static readonly BoolOpt NeedTasks = new("Teruteru.NeedTasks", false);
    public static readonly IntOpt NeededTasks = new("Teruteru.NeededTasks", 3, 1, 15) { When = () => NeedTasks };

    public override bool? CanVent => Vent;

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        if (NeedTasks && PortKit.TasksDone(Player) < NeededTasks) return;
        GameEnd.Win(this);
    }
}
