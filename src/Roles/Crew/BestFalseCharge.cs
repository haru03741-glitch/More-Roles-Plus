// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/BestFalseCharge.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// ベスト冤罪ヤー (クルー): 能力は無く、死ぬまで自分の役職に気付かない。最初の会議が終わると死ぬ
public sealed class BestFalseCharge : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFFFFF";
    public override Text Name => new("BestFalseCharge.Name");
    public override string Reading => "べすとえんざいやー";
    public override Text Blurb => new("BestFalseCharge.Blurb");
    public override string Origin => "SNR";
    public override bool HiddenWhileAlive => true;

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (PortKit.Alive(Player)) PortKit.Suicide(body: false);
    }
}
