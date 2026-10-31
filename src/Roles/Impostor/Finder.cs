// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Finder.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// ファインダー (インポスター): 決めた数だけキルすると、マッドメイト (インポスターと勝つ人) が誰か分かる
public sealed class Finder : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Finder.Name");
    public override string Reading => "ふぁいんだー";
    public override Text Blurb => new("Finder.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt KillCooldown = new("Finder.KillCooldown", 25f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt RevealKills = new("Finder.RevealKills", 2, 1, 10);

    private int _kills;

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (++_kills == RevealKills) PortKit.Notice(new Text("Finder.Revealed").ToString());
    }

    public override string NameMark(PlayerControl seen) =>
        _kills >= RevealKills && PortKit.IsMad(seen) ? "<color=#FF1919>▲</color>" : null;
}
