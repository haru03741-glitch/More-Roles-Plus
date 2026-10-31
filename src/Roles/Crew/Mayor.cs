// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Mayor.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// メイヤー (クルー): 会議で何票分も入れられる
public sealed class Mayor : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#008080";
    public override Text Name => new("Mayor.Name");
    public override string Reading => "めいやー";
    public override Text Blurb => new("Mayor.Blurb");
    public override string Origin => "SNR";

    public static readonly IntOpt Votes = new("Mayor.Votes", 2, 1, 10);

    public override void ModifyVoteWeight(ref int weight) => weight = Votes;
}
