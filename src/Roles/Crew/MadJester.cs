// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/MadJester.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// マッドてるてる (クルーの姿・インポスターの味方): 決めた数のタスクを終えてから追放されると、インポスター陣営の勝ちで試合が終わる
public sealed class MadJester : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("MadJester.Name");
    public override string Reading => "まっどてるてる";
    public override Text Blurb => new("MadJester.Blurb");
    public override string Origin => "SNR";
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt Vent = new("MadJester.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("MadJester.ImpostorVision", false);
    public static readonly BoolOpt KnowImpostors = new("MadJester.KnowImpostors", false);
    public static readonly IntOpt NeededTasks = new("MadJester.NeededTasks", 6, 0, 30) { When = () => KnowImpostors };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    public static readonly BoolOpt WinOnExiled = new("MadJester.WinOnExiled", true);
    public static readonly IntOpt WinTasks = new("MadJester.WinTasks", 5, 0, 30) { When = () => WinOnExiled };

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        if (WinOnExiled && PortKit.TasksDone(Player) >= WinTasks) GameEnd.WinTeam(Team.Impostor);
    }
}
