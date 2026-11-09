using MoreRolesPlus.Net;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ポータルの装置は場に置かれた物 (Bodies) として、刀・弾・火に反応する (金属の中くらいの物 = 強い振りで真っ二つ・弾と火には強い)。
// ハンマーと爆発は装置の円で自分で当たりを見ているので、ここでは見ない。
// 壊れたかはホストが決めて全員へ配る (叩いて壊れた時と同じ流れで、2 つとも閉じる)
public sealed partial class PortalMaker
{
    private static readonly BodyKind MachineKind = new("PortalMachine", BodyMaterial.Metal, BodySize.Medium, 9f);

    private readonly Body[] _bodies = new Body[2];

    private void AddBody(int i)
    {
        Bodies.Remove(_bodies[i]);
        byte owner = PlayerId;
        _bodies[i] = Bodies.Add(new Body
        {
            Kind = MachineKind,
            Fixed = true,
            X = _x[i],
            Y = _y[i] + PortalArt.MachineRise,
            Radius = MachineR,
            Ignore = Bodies.Mask(StrikeElement.Blunt, StrikeElement.Blast),
            Judges = () => true,
            OnStrike = (b, r, s) => OnMachineStrike(owner, b, r, s),
        });
    }

    private void DropBodies()
    {
        for (int i = 0; i < 2; i++) { Bodies.Remove(_bodies[i]); _bodies[i] = null; }
    }

    private static bool OnMachineStrike(byte owner, Body b, StrikeResult r, Strike s)
    {
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            if (s.Element != StrikeElement.Heat) Bodies.Spark(b.X, b.Y, s);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut) return false;
        Bodies.Spark(b.X, b.Y, s);
        if (!Bodies.Reports(s)) return true;
        if (AmongUsClient.Instance.AmHost) (RoleState.Of(owner) as PortalMaker)?.HostSmash(PortKit.ById(s.By), s.Element);
        else SmashCall.Send((owner, (byte)s.Element));
        return true;
    }

    // 刀・弾で壊した人の端末から (火はホストが自分で決める)
    private static readonly RemoteCall<(byte Owner, byte Element)> SmashCall = new("PortalMaker.Smash", Route.ToHost,
        (w, v) => { w.Write(v.Owner); w.Write(v.Element); }, r => (r.ReadByte(), r.ReadByte()),
        (sender, v) => { if (Bodies.ClientReport((StrikeElement)v.Element)) (RoleState.Of(v.Owner) as PortalMaker)?.HostSmash(sender, (StrikeElement)v.Element); }, perSecond: 4);

    private const float SlashReach = 3f, ShotReach = 15f;

    // ホスト: 壊した人が本当に届く所にいれば、叩いて壊した時と同じく全員へ配る (ハンマーの振りは見せない)
    private void HostSmash(PlayerControl by, StrikeElement e)
    {
        if (_count == 0 || !GameClock.ShipAlive || MeetingHud.Instance) return;
        var lp = PlayerControl.LocalPlayer;
        byte hitter = lp ? lp.PlayerId : (byte)255;
        if ((e == StrikeElement.Slash || e == StrikeElement.Bullet) && !Bodies.FromTest(by))
        {
            if (!PortKit.Alive(by)) return;
            if (e == StrikeElement.Slash && RoleState.Of(by) is not Neutral.Samurai) return;
            if (e == StrikeElement.Bullet && !Bodies.Shoots(by)) return;
            Vector2 p = by.GetTruePosition();
            if (NearestMachine(p.x, p.y, out _) > (e == StrikeElement.Slash ? SlashReach : ShotReach)) return;
            hitter = by.PlayerId;
        }
        else if (e != StrikeElement.Heat && e != StrikeElement.Slash && e != StrikeElement.Bullet) return;
        HitsCall.Send(PlayerId | hitter << 8 | HammerHits << 16 | 1 << 24);
        ShowHit(hitter, HammerHits, hammer: false);
    }
}
