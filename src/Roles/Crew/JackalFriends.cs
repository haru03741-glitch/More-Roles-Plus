// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/JackalFriends.cs (GPL-3.0)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Neutral;

namespace MoreRolesPlus.Roles.Crew;

// ジャッカルフレンズ (クルーの姿・ジャッカルの味方): ジャッカルが勝つと一緒に勝つ。生死は問わない
public sealed class JackalFriends : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => Jackal.PackColor;
    public override Text Name => new("JackalFriends.Name");
    public override string Reading => "じゃっかるふれんず";
    public override Text Blurb => new("JackalFriends.Blurb");
    public override string Origin => "SNR";
    public override string WinsWithFaction => Jackal.Pack;

    public static readonly BoolOpt KnowJackal = new("JackalFriends.KnowJackal", false);
    public static readonly IntOpt NeededTasks = new("JackalFriends.NeededTasks", 6, 0, 30) { When = () => KnowJackal };
    public static readonly BoolOpt Vent = new("JackalFriends.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("JackalFriends.ImpostorVision", false);

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowJackal && PortKit.TasksDone(Player) >= NeededTasks && RoleState.Of(seen) is { Faction: Jackal.Pack }
            ? $"<color={Jackal.PackColor}>◆</color>" : null;
}
