using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ランタンは場に置かれた物 (Bodies) として、刀と火に反応する (金属の小さな物 = 普通の振りで斬れる)。
// 爆発・打撃・弾は地形の壊れた知らせで全員の端末が自分で倒しているので、ここでは見ない。
// 斬られて倒れたかはホストが決めて全員へ配る
public sealed partial class Phosphorus
{
    private static readonly BodyKind LanternKind = new("PhosphorusLantern", BodyMaterial.Metal, BodySize.Small, 3f);
    private const float LanternRadius = 0.25f;

    private void AddBody(Lantern l)
    {
        DropBody(l);
        byte owner = PlayerId;
        l.Hit = Bodies.Add(new Body
        {
            Kind = LanternKind,
            Fixed = true,
            X = l.X,
            Y = l.Y + FlameH,
            Radius = LanternRadius,
            Ignore = Bodies.Mask(StrikeElement.Blunt, StrikeElement.Blast) | Bodies.Mask(StrikeElement.Bullet),
            Judges = () => true,
            OnStrike = (b, r, s) => OnLanternStrike(owner, l, b, r, s),
        });
    }

    private static void DropBody(Lantern l)
    {
        if (l.Hit == null) return;
        Bodies.Remove(l.Hit);
        l.Hit = null;
    }

    private static bool OnLanternStrike(byte owner, Lantern l, Body b, StrikeResult r, Strike s)
    {
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            if (s.Element != StrikeElement.Heat) Bodies.Spark(b.X, b.Y, s);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut) return false;
        if (s.Element != StrikeElement.Heat) Bodies.Spark(b.X, b.Y, s, false);
        if (!Bodies.Reports(s) || RoleState.Of(owner) is not Phosphorus ph) return true;
        int i = ph._lanterns.IndexOf(l);
        if (i < 0) return true;
        if (AmongUsClient.Instance.AmHost) ph.HostSmash(PortKit.ById(s.By), (byte)i, s.Element);
        else SmashCall.Send((owner, (byte)i, (byte)s.Element));
        return true;
    }

    // 刀で倒した人の端末から (火はホストが自分で決める)
    private static readonly RemoteCall<(byte Owner, byte Index, byte Element)> SmashCall = new("Phosphorus.Smash", Route.ToHost,
        (w, v) => { w.Write(v.Owner); w.Write(v.Index); w.Write(v.Element); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (sender, v) => { if ((StrikeElement)v.Element == StrikeElement.Slash) (RoleState.Of(v.Owner) as Phosphorus)?.HostSmash(sender, v.Index, StrikeElement.Slash); }, perSecond: 4);

    private const float SlashReach = 3f;

    private void HostSmash(PlayerControl by, byte index, StrikeElement e)
    {
        if (index >= _lanterns.Count || _lanterns[index].Broken || !GameClock.ShipAlive || MeetingHud.Instance) return;
        var l = _lanterns[index];
        float awayX = 0f;
        if (e == StrikeElement.Slash && !Bodies.FromTest(by))
        {
            if (!PortKit.Alive(by) || RoleState.Of(by) is not Neutral.Samurai) return;
            Vector2 p = by.GetTruePosition();
            if (Vector2.Distance(p, FxMath.V2(l.X, l.Y)) > SlashReach) return;
            awayX = l.X - p.x;
        }
        else if (e != StrikeElement.Heat && e != StrikeElement.Slash) return;
        SmashedCall.Send((PlayerId, index, awayX));
        Smashed(index, awayX);
    }

    private static readonly RemoteCall<(byte Owner, byte Index, float AwayX)> SmashedCall = new("Phosphorus.Smashed", Route.HostToAll,
        (w, v) => { w.Write(v.Owner); w.Write(v.Index); w.Write(v.AwayX); }, r => (r.ReadByte(), r.ReadByte(), r.ReadSingle()),
        (_, v) => (RoleState.Of(v.Owner) as Phosphorus)?.Smashed(v.Index, v.AwayX));

    private void Smashed(byte index, float awayX)
    {
        if (index >= _lanterns.Count) return;
        var l = _lanterns[index];
        if (!l.Go || l.Broken) return;
        Break(l, awayX);
    }
}
