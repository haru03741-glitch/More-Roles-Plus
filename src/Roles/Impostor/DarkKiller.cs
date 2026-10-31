// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/DarkKiller.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// ダークキラー (インポスター): キルのクールダウンが短い代わりに、停電中しかキルできない
public sealed class DarkKiller : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("DarkKiller.Name");
    public override string Reading => "だーくきらー";
    public override Text Blurb => new("DarkKiller.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt KillCooldown = new("DarkKiller.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt Vent = new("DarkKiller.CanVent", true);

    public override bool? CanVent => Vent;

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId && !PortKit.LightsOut()) e.Cancel("not dark");
    }
}
