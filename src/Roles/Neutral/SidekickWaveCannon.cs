// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/SidekickWaveCannon.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 波動砲サイドキック (第三陣営): 波動砲ジャッカルに仲間にされた人。キルはできず、ジャッカルの仲間と一緒に勝つ。
// 生きている波動砲ジャッカルがいなくなると、波動砲ジャッカルに上がる。最初から配られることはない
public sealed class SidekickWaveCannon : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#00B4EB";
    public override Text Name => new("SidekickWaveCannon.Name");
    public override string Reading => "はどうほうさいどきっく";
    public override Text Blurb => new("SidekickWaveCannon.Blurb");
    public override string Origin => "SNR";
    public override string Faction => Jackal.Pack;
    public override bool MadeByOthers => true;

    public static readonly BoolOpt Vent = new("SidekickWaveCannon.CanVent", true);

    public override bool? CanVent => Vent;

    private void OnMurdered(PlayerMurderedEvent e) => Promote();

    private void OnMeetingEnd(MeetingEndEvent e) => Promote();

    private void Promote()
    {
        foreach (var r in RoleState.All)
            if (r is WaveCannonJackal && PortKit.Alive(r.Player)) return;
        if (!PortKit.Alive(Player)) return;
        if (WaveCannonJackal.Promoted.Count == 0) RoleState.Match.OnRelease(WaveCannonJackal.Promoted.Clear);
        WaveCannonJackal.Promoted.Add(PlayerId);
        if (AmongUsClient.Instance.AmHost) RoleState.Change(Player, RoleState.Proto<WaveCannonJackal>());
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId != PlayerId && Jackal.InPack(seen) ? $"<color={Jackal.PackColor}>◆</color>" : null;
}
