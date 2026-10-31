// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Moira.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// モイラ (第三陣営): 会議中に 2 人を選ぶと、会議の終わりにその 2 人の役職を入れ替える。
// 決まった回数を使い切って試合の終わりまで生きていれば、勝った陣営から勝ちを奪う
public sealed class Moira : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#9370DB";
    public override Text Name => new("Moira.Name");
    public override string Reading => "もいら";
    public override Text Blurb => new("Moira.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Moira.Pick", "snr_fate") };
    public static readonly IntOpt Swaps = new("Moira.Swaps", 1, 1, 5);

    private byte _a = 255, _b = 255;
    private int _done;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Moira.Pick"), "snr_fate")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => Swaps * 2,
            CanTarget = (_, t) => t.PlayerId != _a,
            OnUsed = (_, target) =>
            {
                if (_a == 255) _a = target.PlayerId;
                else _b = target.PlayerId;
            },
        });
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        byte a = _a, b = _b;
        _a = _b = 255;
        if (a == 255 || b == 255) return;
        _done++;
        if (!AmongUsClient.Instance.AmHost) return;
        var pa = PortKit.ById(a);
        var pb = PortKit.ById(b);
        var ra = RoleState.Of(pa);
        var rb = RoleState.Of(pb);
        if (ra == null || rb == null) return;
        var protoA = Options.Registry.Roles.Find(r => r.GetType() == ra.GetType());
        var protoB = Options.Registry.Roles.Find(r => r.GetType() == rb.GetType());
        if (protoA == null || protoB == null) return;
        PortKit.After(0.5f, () =>
        {
            RoleState.Change(pa, protoB);
            RoleState.Change(pb, protoA);
        });
    }

    public override bool StealsWin(GameResult result) => PortKit.Alive(Player) && _done >= Swaps;

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _a || seen.PlayerId == _b ? "<color=#9370DB>⇄</color>" : null;
}
