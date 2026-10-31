// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/HauntedWolf.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// 狼憑き (クルー): 能力は無く、死ぬまで自分の役職に気付かない。シェリフなど陣営を見分ける役職からはインポスターに見える
public sealed class HauntedWolf : RoleBase, PortKit.IAppearsImpostor
{
    public override Team Team => Team.Crew;
    public override string Color => "#7B6C3E";
    public override Text Name => new("HauntedWolf.Name");
    public override string Reading => "おおかみつき";
    public override Text Blurb => new("HauntedWolf.Blurb");
    public override string Origin => "SNR";
    public override bool HiddenWhileAlive => true;
}
