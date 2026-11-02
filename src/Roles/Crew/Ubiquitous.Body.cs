using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ドローンは場に置かれた物 (Bodies) として、弾・刀・爆発・叩く・水に反応する。
// 飛んでいるドローンは本人の端末が決める (ほかの端末の当たりは Ubiquitous.Share.cs)。止まったドローンは全員の端末にあり、本人 (抜けていればホスト) が決めて配る。
// 当たっただけなら火花と金属の音・耐久が尽きる/斬られる/水に浸かると止まって落ちる
public sealed partial class Ubiquitous
{
    // 飛んでいる時は人の腰より上 (泳ぐ深さで浸かる)・止まっている時は床の少し上
    private static readonly BodyKind FlyingKind = new("UbiquitousDrone", BodyMaterial.Electronic, BodySize.Small, 15f, 0.4f);
    private static readonly BodyKind IdleKind = new("UbiquitousDroneIdle", BodyMaterial.Electronic, BodySize.Small, 15f, 0.1f);
    private const float HitRadius = 0.25f;

    private Body _hitBody;

    private void AddFlyingBody()
    {
        _hitBody = Bodies.Add(new Body
        {
            Kind = FlyingKind,
            Tf = _drone.transform,
            Radius = HitRadius,
            Judges = () => true,
            OnStrike = OnFlyingStrike,
        });
    }

    private void RemoveFlyingBody()
    {
        Bodies.Remove(_hitBody);
        _hitBody = null;
    }

    private bool OnFlyingStrike(Body b, StrikeResult r, Strike s)
    {
        float x = b.X, y = b.Y;
        if (s.Element == StrikeElement.Bullet) SendGuard(x, y);
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            Spark(x, y, s);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut && r != StrikeResult.Douse) return false;
        Plugin.Logger.LogInfo($"ubiquitous: drone down by {s.Element} ({r})");
        WreckFlying(r, s);
        DestroyDrone(true);
        return true;
    }

    private static void Spark(float x, float y, in Strike s)
    {
        float dx = s.Dx, dy = s.Dy;
        if (dx == 0f && dy == 0f) { dx = x - s.X; dy = y - s.Y; }
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.0001f) { dx = 0f; dy = -1f; } else { dx /= d; dy /= d; }
        GatlingFx.Deflect(x, y, dx, dy);
        // 弾は跳ね返る音・ハンマーは殻を叩く音 (爆発と火はそれぞれの音が鳴っている)
        if (s.Element == StrikeElement.Bullet) GunSound.Deflect(x, y, false);
        else if (s.Element == StrikeElement.Blunt) Hearing.At("ubiq_drone_hit", x, y, HumRange, 0.8f, null, Ears.All);
    }

    // ---- 止まったドローン ----

    private static Body IdleBody(IdleDrone d)
    {
        byte owner = d.Owner;
        return Bodies.Add(new Body
        {
            Kind = IdleKind,
            Tf = d.Go.transform,
            Radius = HitRadius,
            Judges = () => JudgesIdle(owner),
            OnStrike = (b, r, s) => OnIdleStrike(d, b, r, s),
        });
    }

    // 止まったドローンを決める端末 = 本人 (抜けていればホスト)
    private static bool JudgesIdle(byte owner)
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == owner) return true;
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(owner) : null;
        // 抜けた人と練習の相手 (ホストが動かす) の物はホストが決める
        bool host = info == null || info.Disconnected || !info.Object || info.Object.isDummy;
        return host && AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
    }

    // 確かめ用: その人の止まったドローンをそこに置く (この端末だけ・会議を挟まずに止まったドローンへの反応を見る)
    internal static bool DevIdle(PlayerControl owner, Vector2 at)
    {
        if (!owner || RoleState.Of(owner) is not Ubiquitous) return false;
        AddIdle(owner, at, (byte)DroneArt.YawIndex(-FxMath.PI * 0.5f));
        return true;
    }

    private static bool OnIdleStrike(IdleDrone d, Body b, StrikeResult r, Strike s)
    {
        float x = b.X, y = b.Y;
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            Spark(x, y, s);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut && r != StrikeResult.Douse) return false;
        byte fate = (byte)FateOf(r, s), angle = StrikeAngle(s, x, y);
        IdleGoneCall.Send((d.Owner, FxMath.V2(x, y), fate, angle));
        DropIdle(d.Owner, FxMath.V2(x, y), fate, angle);
        return true;
    }

    private static readonly RemoteCall<(byte owner, Vector2 at, byte fate, byte angle)> IdleGoneCall = new("Ubiquitous.IdleGone", Route.Anyone,
        (w, v) => { w.Write(v.owner); w.Write(v.at.x); w.Write(v.at.y); w.Write(v.fate); w.Write(v.angle); },
        r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadByte(), r.ReadByte()),
        (sender, v) => { if (sender && float.IsFinite(v.at.x) && float.IsFinite(v.at.y)) DropIdle(v.owner, v.at, v.fate, v.angle); });

    // 止まったドローンが壊れた (全員の端末): 一番近い物を消して、その場に残骸
    private static void DropIdle(byte owner, Vector2 at, byte fate, byte angle)
    {
        int best = -1;
        float bd = 0.36f;
        for (int i = 0; i < Idle.Count; i++)
        {
            var d = Idle[i];
            if (d.Owner != owner || !d.Go) continue;
            Vector2 p = d.Go.transform.position;
            float dx = p.x - at.x, dy = p.y - at.y, dd = dx * dx + dy * dy;
            if (dd < bd) { bd = dd; best = i; }
        }
        if (best < 0) return;
        var g = Idle[best];
        Bodies.Remove(g.Body);
        Vector2 gp = g.Go.transform.position;
        if (fate <= (byte)WreckFate.Douse) SpawnWreck(gp, IdleLift, (WreckFate)fate, g.Yaw, angle);
        Object.Destroy(g.Go);
        Idle.RemoveAt(best);
        Plugin.Logger.LogInfo($"ubiquitous: idle drone of {owner} broken");
    }
}
