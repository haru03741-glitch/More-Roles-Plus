// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Datahacker.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// データハッカー (クルー): 決めた数のタスクを終えると、敵 (インポスターとキルできる第三陣営) が誰か分かる。
// ただしその少し前から、敵の画面では自分がデータハッカーだと分かってしまう
public sealed class Datahacker : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#00FF7F";
    public override Text Name => new("Datahacker.Name");
    public override string Reading => "でーたはっかー";
    public override Text Blurb => new("Datahacker.Blurb");
    public override string Origin => "SNR";
    public static readonly IntOpt NeededTasks = new("Datahacker.NeededTasks", 5, 1, 30);
    public static readonly IntOpt ExposeTasks = new("Datahacker.ExposeTasks", 3, 0, 30);

    private static bool Enemy(PlayerControl p) => PortKit.IsImpostor(p) || (PortKit.IsNeutral(p) && PortKit.CanKillAnyone(p));

    public override string NameMark(PlayerControl seen) =>
        PortKit.TasksDone(Player) >= NeededTasks && Enemy(seen) ? "<color=#FF1919>◎</color>" : null;

    public override string MarkSeenBy(PlayerControl viewer) =>
        PortKit.TasksDone(Player) >= ExposeTasks && Enemy(viewer) ? "<color=#00FF7F>◎</color>" : null;
}
