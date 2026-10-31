// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Celebrity.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// スター (クルー): 能力は無いが、全員の画面で名前に星が付き、スターだと皆に分かる
public sealed class Celebrity : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("Celebrity.Name");
    public override string Reading => "すたー";
    public override Text Blurb => new("Celebrity.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt OnlyWhileAlive = new("Celebrity.OnlyWhileAlive", true);

    public override string PublicMark => !OnlyWhileAlive || PortKit.Alive(Player) ? "<color=#FFFF00>★</color>" : null;
}
