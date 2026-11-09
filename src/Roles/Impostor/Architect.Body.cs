using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 設計台は場に置かれた物 (Bodies) として、刀・弾・火に反応する (木の大きな物 = 強い振りで真っ二つ・火でよく燃える)。
// ハンマーと爆発は台の足元の四角で自分で当たりを見ているので、ここでは見ない。
// 壊れたかはホストが決めて全員へ配る (叩いて壊れた時と同じ流れ)
public sealed partial class Architect
{
    private static readonly BodyKind DeskKind = new("ArchitectDesk", BodyMaterial.Wood, BodySize.Large, 9f);
    private const float DeskRadius = 0.6f;
    private const float DeskMid = 0.3f; // 床の点から当たりの中心まで (脚の四角の奥と板の高さの間)

    private readonly Dictionary<ArchitectDeskArt.Handle, Body> _deskBodies = new();

    private void AddBody(ArchitectDeskArt.Handle h)
    {
        DropBody(h);
        byte owner = PlayerId;
        _deskBodies[h] = Bodies.Add(new Body
        {
            Kind = DeskKind,
            Fixed = true,
            X = h.X,
            Y = h.Y + DeskMid,
            Radius = DeskRadius,
            Ignore = Bodies.Mask(StrikeElement.Blunt, StrikeElement.Blast),
            Judges = () => true,
            OnStrike = (b, r, s) => OnDeskStrike(owner, h, b, r, s),
        });
    }

    private void DropBody(ArchitectDeskArt.Handle h)
    {
        if (h == null || !_deskBodies.Remove(h, out var b)) return;
        Bodies.Remove(b);
    }

    private void DropBodies()
    {
        foreach (var b in _deskBodies.Values) Bodies.Remove(b);
        _deskBodies.Clear();
    }

    private static bool OnDeskStrike(byte owner, ArchitectDeskArt.Handle h, Body b, StrikeResult r, Strike s)
    {
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            // 火は燃えている絵が出ているので、刀と弾だけ当たった所に火花を出す
            if (s.Element != StrikeElement.Heat) Bodies.Spark(b.X, b.Y, s, s.Element == StrikeElement.Bullet);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut) return false;
        Bodies.Spark(b.X, b.Y, s, false);
        if (!Bodies.Reports(s) || RoleState.Of(owner) is not Architect a) return true;
        int desk = a._desks.IndexOf(h);
        if (desk < 0) return true;
        if (AmongUsClient.Instance.AmHost) a.HostSmash(PortKit.ById(s.By), (byte)desk, s.Element);
        else SmashCall.Send((owner, (byte)desk, (byte)s.Element));
        return true;
    }

    // 刀・弾で壊した人の端末から (火はホストが自分で決める)
    private static readonly RemoteCall<(byte Owner, byte Desk, byte Element)> SmashCall = new("Architect.Smash", Route.ToHost,
        (w, v) => { w.Write(v.Owner); w.Write(v.Desk); w.Write(v.Element); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (sender, v) => { if (Bodies.ClientReport((StrikeElement)v.Element)) (RoleState.Of(v.Owner) as Architect)?.HostSmash(sender, v.Desk, (StrikeElement)v.Element); }, perSecond: 4);

    private const float SlashReach = 3f, ShotReach = 15f;

    // ホスト: 壊した人が本当に届く所にいれば、叩いて壊した時と同じく全員へ配る (ハンマーの振りは見せない)
    private void HostSmash(PlayerControl by, byte desk, StrikeElement e)
    {
        if (desk >= _desks.Count || _desks[desk].Broken || !GameClock.ShipAlive || MeetingHud.Instance) return;
        var d = _desks[desk];
        byte hitter = LocalId;
        if ((e == StrikeElement.Slash || e == StrikeElement.Bullet) && !Bodies.FromTest(by))
        {
            if (!PortKit.Alive(by)) return;
            if (e == StrikeElement.Slash && RoleState.Of(by) is not Neutral.Samurai) return;
            if (e == StrikeElement.Bullet && !Bodies.Shoots(by)) return;
            float reach = e == StrikeElement.Slash ? SlashReach : ShotReach;
            if (Vector2.Distance(by.GetTruePosition(), FxMath.V2(d.X, d.Y)) > reach) return;
            hitter = by.PlayerId;
        }
        else if (e != StrikeElement.Heat && e != StrikeElement.Slash && e != StrikeElement.Bullet) return;
        SmashedCall.Send((PlayerId, desk, hitter));
        ShowHit(desk, hitter, DeskHits, hammer: false);
    }

    private static readonly RemoteCall<(byte Owner, byte Desk, byte Hitter)> SmashedCall = new("Architect.Smashed", Route.HostToAll,
        (w, v) => { w.Write(v.Owner); w.Write(v.Desk); w.Write(v.Hitter); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, v) => (RoleState.Of(v.Owner) as Architect)?.ShowHit(v.Desk, v.Hitter, DeskHits, hammer: false));
}
