// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Mafia.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// マフィア (インポスター): ほかのインポスターが生きている間はキルできない。生きているインポスターがマフィアだけになるとキルできる
public sealed class Mafia : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Mafia.Name");
    public override string Reading => "まふぃあ";
    public override Text Blurb => new("Mafia.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt OwnCooldown = new("Mafia.OwnCooldown", false);
    public static readonly FloatOpt KillCooldown = new("Mafia.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s") { When = () => OwnCooldown };

    public override void ModifyKillCooldown(ref float seconds)
    {
        if (OwnCooldown) seconds = KillCooldown;
    }

    internal static bool CanKillNow()
    {
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && PortKit.IsImpostor(p) && RoleState.Of(p) is not Mafia) return false;
        return true;
    }

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId && !CanKillNow()) e.Cancel("mafia waits");
    }
}
