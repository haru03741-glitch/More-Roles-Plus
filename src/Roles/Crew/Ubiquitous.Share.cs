using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 飛んでいるドローンをほかの人の端末にも当たりとして置く (弾がドローンで止まるように)。
// 既定: 本人の端末で弾が当たった時だけ、その場所を全員へ送る。受けた端末は見えない当たりを少しの間だけそこに置き、弾はそこで火花を出して止まる
// (送りが届くまでの最初の数発は抜ける)。
// 設定「飛ぶドローンを全員に見せる」: 本人が位置を 0.2 秒ごとに送り、ほかの人の端末にもドローンの絵と当たりを置く
public sealed partial class Ubiquitous
{
    public static readonly BoolOpt ShowFlying = new("Ubiquitous.ShowFlying", false);

    private const float GuardEvery = 0.15f;  // 弾が当たった場所を送る間
    private const float GuardLife = 0.5f;    // 受けた端末で当たりを置いておく間
    private const float ShareEvery = 0.2f;   // 見せる時の位置を送る間
    private const float ShareLost = 1.5f;    // 見せているドローンの位置がこれだけ届かなければ消す

    private float _nextGuard, _nextShare;
    private bool _shared;                    // 見せる位置を送っている

    // ほかの人の端末: 弾を止める見えない当たり (1 人 1 つ。届くたびに場所と期限を書き換える)
    private static readonly Dictionary<byte, Body> Guards = new();

    // ほかの人の端末: 見せているドローン
    private GameObject _seen;
    private Body _seenBody;
    private float _seenX, _seenY, _seenLast;

    // ---- 本人の端末 ----

    // 飛んでいるドローンに弾が当たった
    private void SendGuard(float x, float y)
    {
        if (ShowFlying) return; // 見せている時はほかの端末にも当たりがある
        float now = Time.time;
        if (now < _nextGuard) return;
        _nextGuard = now + GuardEvery;
        GuardCall.Send(FxMath.V2(x, y));
    }

    // 飛んでいる間の毎 tick (本人の端末)
    private void ShareTick()
    {
        if (!ShowFlying || !_drone) return;
        float now = Time.time;
        if (now < _nextShare) return;
        _nextShare = now + ShareEvery;
        Vector3 p = _drone.transform.position;
        _shared = true;
        SeenCall.Send((FxMath.V2(p.x, p.y), (byte)DroneArt.YawIndex(_yaw), true));
    }

    // ドローンを消した (本人の端末)
    private void ShareGone()
    {
        if (!_shared) return;
        _shared = false;
        SeenCall.Send((default, 0, false));
    }

    private static readonly RemoteCall<Vector2> GuardCall = new("Ubiquitous.Guard", Route.Anyone,
        (w, v) => { w.Write(v.x); w.Write(v.y); },
        r => new Vector2(r.ReadSingle(), r.ReadSingle()),
        (sender, at) => PlaceGuard(sender, at));

    private static readonly RemoteCall<(Vector2 at, byte yaw, bool on)> SeenCall = new("Ubiquitous.Seen", Route.Anyone,
        (w, v) => { w.Write(v.on); if (v.on) { w.Write(v.at.x); w.Write(v.at.y); w.Write(v.yaw); } },
        r => r.ReadBoolean() ? (new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadByte(), true) : (default, (byte)0, false),
        (sender, v) => { if (RoleState.Of(sender) is Ubiquitous u && !u.IsLocal) u.Seen(v.at, v.yaw, v.on); },
        Delivery.Queued);

    // ---- ほかの人の端末 ----

    private static void PlaceGuard(PlayerControl owner, Vector2 at)
    {
        if (!owner || owner.AmOwner || RoleState.Of(owner) is not Ubiquitous) return;
        if (Guards.TryGetValue(owner.PlayerId, out var g) && g != null && Bodies.Contains(g))
        {
            g.X = at.x;
            g.Y = at.y;
            g.Gone = false;
            g.SeenFrame = -1;
            g.Until = Time.time + GuardLife;
            return;
        }
        Guards[owner.PlayerId] = Bodies.Add(new Body
        {
            Kind = FlyingKind,
            Fixed = true,
            X = at.x,
            Y = at.y,
            Radius = HitRadius,
            Until = Time.time + GuardLife,
            BulletsOnly = true,
            OnStrike = OnSeenStrike,
        });
    }

    private void Seen(Vector2 at, byte yaw, bool on)
    {
        if (!on) { DropSeen(); return; }
        _seenX = at.x;
        _seenY = at.y;
        _seenLast = Time.time;
        if (_seen) return;
        _seen = Lifespan.Bind(MakeDrone(at, true, yaw));
        FxRoot.TrackWorld(_seen);
        _seenBody = Bodies.Add(new Body
        {
            Kind = FlyingKind,
            Tf = _seen.transform,
            Radius = HitRadius,
            OnStrike = OnSeenStrike,
        });
    }

    // 見せているドローンを届いた位置へ寄せる (ほかの人の端末の毎 tick)
    private void SeenTick()
    {
        if (!_seen) return;
        if (RewindPlayback.Active) { _seenLast = Time.time; return; } // 逆再生の間は本人が送らないので、届かないのを消える合図にしない
        if (Time.time - _seenLast > ShareLost || !PortKit.Alive(Player)) { DropSeen(); return; }
        var tf = _seen.transform;
        Vector3 p = tf.position;
        float k = FxMath.Clamp01(Time.fixedDeltaTime / ShareEvery);
        tf.position = FxMath.V3(p.x + (_seenX - p.x) * k, p.y + (_seenY - p.y) * k, p.z);
    }

    private void DropSeen()
    {
        Bodies.Remove(_seenBody);
        _seenBody = null;
        if (_seen) Object.Destroy(_seen);
        _seen = null;
    }

    // ほかの人の端末の当たり: 結果は本人の端末が決めるので、当たった火花だけ出す
    private static bool OnSeenStrike(Body b, StrikeResult r, Strike s)
    {
        if (r == StrikeResult.None || r == StrikeResult.Pass) return false;
        if (s.Element == StrikeElement.Bullet) Spark(b.X, b.Y, s);
        return true;
    }
}
