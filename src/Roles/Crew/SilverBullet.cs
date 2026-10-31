// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/SilverBullet.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// シルバーバレット (クルー): ベントに「分析」をすると、前のターンにそのベントが何回使われたかが分かる
public sealed class SilverBullet : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#C0C0C0";
    public override Text Name => new("SilverBullet.Name");
    public override string Reading => "しるばーばれっと";
    public override Text Blurb => new("SilverBullet.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("SilverBullet.Analyze", "snr_analyze") };
    public static readonly FloatOpt AnalyzeCooldown = new("SilverBullet.Cooldown", 20f, 0f, 60f, 2.5f, "s");

    private readonly System.Collections.Generic.Dictionary<int, int> _now = new(), _last = new();

    // 全員の端末で同じように数える (この端末の役職の分だけ使う)
    [LocalOnly]
    private void OnVent(VentEnteredEvent e) => _now[e.VentId] = (_now.TryGetValue(e.VentId, out var n) ? n : 0) + 1;

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        _last.Clear();
        foreach (var kv in _now) _last[kv.Key] = kv.Value;
        _now.Clear();
    }

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("SilverBullet.Analyze"), "snr_analyze")
        {
            Cooldown = () => AnalyzeCooldown,
            OnUse = () =>
            {
                var vent = PortKit.NearestVent(Player.GetTruePosition(), 1.2f);
                if (!vent) return false;
                int n = _last.TryGetValue(vent.Id, out var c) ? c : 0;
                PortKit.Notice(new Text("SilverBullet.Result").With("n", n).ToString());
                return true;
            },
        });
    }
}
