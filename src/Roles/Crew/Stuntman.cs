// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Stuntman.cs, Roles/Ability/StuntmanAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// スタントマン (クルー): 決まった回数までキルを自動で防ぐ。防いだことに本人は気付かない
public sealed class Stuntman : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#ADFF2F";
    public override Text Name => new("Stuntman.Name");
    public override string Reading => "すたんとまん";
    public override Text Blurb => new("Stuntman.Blurb");
    public override string Origin => "SNR";

    public static readonly IntOpt Uses = new("Stuntman.Uses", 1, 1, 10);

    private int _used;

    [HostOnly, OnlyMine]
    private void OnTargeted(BeforeMurderEvent e)
    {
        if (e.Canceled || _used >= Uses || e.Killer == null || e.Killer.PlayerId == PlayerId) return;
        _used++;
        e.Cancel("stuntman");
        PortKit.HostBlocked(e.Killer, e.Target);
    }
}
