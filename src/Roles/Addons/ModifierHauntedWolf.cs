// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/ModifierHauntedWolf.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// モディファイア狼憑き (アドオン・クルーの役職だけ): 陣営を見分ける役職 (シェリフなど) からインポスターに見える。本人は気付かない
public sealed class ModifierHauntedWolf : AddonBase, PortKit.IAppearsImpostor
{
    public override string Color => "#8CFFFF";
    public override Text Name => new("ModifierHauntedWolf.Name");
    public override string Reading => "もでぃふぁいあおおかみつき";
    public override Text Blurb => new("ModifierHauntedWolf.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt OnMadAndFriends = new("ModifierHauntedWolf.OnMadAndFriends", true);

    public override bool CanAttach(Team team, RoleBase role) =>
        team == Team.Crew && (OnMadAndFriends || role is not { WinsWithTeam: not null } && role is not { WinsWithFaction: not null });
}
