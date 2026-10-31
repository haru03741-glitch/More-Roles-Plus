// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/NiceRedRidingHood.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ナイス赤ずきん (クルー): キルされて死体が残った後、自分をキルした人が死ぬと、次の会議の後に生き返る。
// 生き返れる回数は設定で決める
public sealed class NiceRedRidingHood : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF4040";
    public override Text Name => new("NiceRedRidingHood.Name");
    public override string Reading => "ないすあかずきん";
    public override Text Blurb => new("NiceRedRidingHood.Blurb");
    public override string Origin => "SNR";
    public static readonly IntOpt Revives = new("NiceRedRidingHood.Revives", 1, 1, 10);

    private int _used;
    private byte _killer = 255;
    private bool _ready;

    // 全員の端末で同じように覚える (生き返らせるのはホスト)
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Target.PlayerId == PlayerId)
        {
            _killer = e.Killer && e.Killer.PlayerId != PlayerId ? e.Killer.PlayerId : (byte)255;
            _ready = false;
        }
        else if (e.Target.PlayerId == _killer) _ready = true;
    }

    private void OnExiled(PlayerExiledEvent e)
    {
        if (e.Player.PlayerId == _killer) _ready = true;
    }

    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (!_ready || _used >= Revives || PortKit.Alive(Player)) return;
        _ready = false;
        _killer = 255;
        _used++;
        PortKit.HostRevive(Player);
    }
}
