// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/SatsumaAndImo.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// さつまといも (クルー / マッド): 会議が終わるたびに、クルーとマッドメイト (インポスターと勝つ) が入れ替わる
public sealed class SatsumaAndImo : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#990044";
    public override Text Name => new("SatsumaAndImo.Name");
    public override string Reading => "さつまといも";
    public override Text Blurb => new("SatsumaAndImo.Blurb");
    public override string Origin => "SNR";

    private bool _mad;

    public override Team? WinsWithTeam => _mad ? Team.Impostor : null;

    // 会議の終わりは全員の端末で起きるので、どの端末でも同じように切り替わる
    private void OnMeetingEnd(MeetingEndEvent e) => _mad = !_mad;

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == PlayerId ? (_mad ? " <color=#FF1919>(M)</color>" : " <color=#8CFFFF>(C)</color>") : null;
}
