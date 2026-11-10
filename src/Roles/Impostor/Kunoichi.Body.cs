using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// クナイは場を飛ぶ物 (Bodies) として、刀・弾・爆発・叩く・火に反応する (金属の小さな物 = 普通の振りで斬れる)。
// クナイは全員の端末にそれぞれ飛んでいて、斬った・撃ち落とした端末がほかの端末へ消えたことを配る。
// 頑丈な役職 (サムライ) に当たったかは、刀の向きと振りが正しく分かるその人の端末 (練習の相手はホスト) が決める
public sealed partial class Kunoichi
{
    private static readonly BodyKind KunaiKind = new("KunoichiKunai", BodyMaterial.Metal, BodySize.Small, 1f);
    private static float KunaiRadius => 0.2f * SizeScale;

    private byte _seq;
    private readonly Dictionary<byte, (SpriteRenderer sr, Body body)> _kunai = new();

    private void AddKunai(byte seq, SpriteRenderer sr, bool thrower)
    {
        EndKunai(seq);
        var body = Bodies.Add(new Body
        {
            Kind = KunaiKind,
            Tf = sr.transform,
            Radius = KunaiRadius,
            OffY = -KunaiHand.FlyHeight, // 絵は手の高さを飛ぶが、当たりは足元の高さの線 (飛ぶ間の当たりの判定と同じ高さ)
            Judges = () => true, // どの端末も自分の手元のクナイを落とす (落とした事は Owns の端末が配る)
            OnStrike = (b, r, s) => OnKunaiStrike(seq, b, r, s, thrower),
        });
        _kunai[seq] = (sr, body);
    }

    // ---- 手に持ったクナイ ----
    // 構えている間、握ったクナイも場の物として刀・弾・爆発・叩く・火に当たる (投げたクナイと同じ金属の小さな物)。
    // 斬られる・壊れると手から弾き飛ばされ、KnockSeconds の間は握り直せない (投げられない)
    private static readonly BodyKind HeldKind = new("KunoichiHeldKunai", BodyMaterial.Metal, BodySize.Small, 1f);
    private const float KnockSeconds = 1.5f;
    private Body _heldBody;
    private float _knockedUntil = -1f; // 自分の端末: これまで握り直せない

    private bool Knocked => Time.time < _knockedUntil;

    // 構えた / しまった (全員の端末で・手の絵と同じ時に)
    private void HeldBody(bool on)
    {
        if (!on)
        {
            if (_heldBody != null) { Bodies.Remove(_heldBody); _heldBody = null; }
            return;
        }
        if (_heldBody != null) return;
        var tf = KunaiHand.KunaiTransform(Player);
        if (!tf) return;
        var p = Player;
        _heldBody = Bodies.Add(new Body
        {
            Kind = HeldKind,
            Tf = tf,
            Radius = KunaiRadius,
            Judges = () => KunaiHand.Holding(p), // 手が見えている (握っている) 間だけ当たる・どの端末も手元の当たりを決める
            OnStrike = OnHeldStrike,
        });
    }

    private bool OnHeldStrike(Body b, StrikeResult r, Strike s)
    {
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            Spark(b.X, b.Y, s);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut) return false;
        Knock(b.X, b.Y, s.Dx, s.Dy);
        if (Owns(s, IsLocal)) KnockCall.Send((PlayerId, b.X, b.Y, s.Dx, s.Dy));
        return true;
    }

    private static readonly RemoteCall<(byte k, float x, float y, float dx, float dy)> KnockCall = new("Kunoichi.Knock", Route.Anyone,
        (w, m) => { w.Write(m.k); w.Write(m.x); w.Write(m.y); w.Write(m.dx); w.Write(m.dy); },
        r => (r.ReadByte(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        (_, m) => { if (RoleState.Of(m.k) is Kunoichi k) k.Knock(m.x, m.y, m.dx, m.dy); }, perSecond: 6);

    // 全員の端末で: 握ったクナイが弾き飛ばされた (火花と金属の音・手が空になり、少しして握り直す)
    private void Knock(float x, float y, float dx, float dy)
    {
        if (IsLocal && Knocked) return; // 自分の手元の判定と振った人からの知らせで二度弾かない
        Spark(x, y, new Strike(StrikeElement.Slash, 1f, 1, x, y, dx, dy, PlayerId));
        KunaiHand.Knock(Player, KnockSeconds);
        if (IsLocal) _knockedUntil = Time.time + KnockSeconds;
    }

    private void DropKunai(byte seq)
    {
        if (!_kunai.TryGetValue(seq, out var k)) return;
        Bodies.Remove(k.body);
        _kunai.Remove(seq);
    }

    // クナイを消す (この端末に無ければ何もしない)
    private void EndKunai(byte seq)
    {
        if (!_kunai.TryGetValue(seq, out var k)) return;
        DropKunai(seq);
        if (k.sr) Object.Destroy(k.sr.gameObject);
    }

    private bool OnKunaiStrike(byte seq, Body b, StrikeResult r, Strike s, bool thrower)
    {
        float x = b.X, y = b.Y;
        if (r == StrikeResult.Damage || r == StrikeResult.Glance)
        {
            Spark(x, y, s);
            return true;
        }
        if (r != StrikeResult.Break && r != StrikeResult.Cut) return false;
        Spark(x, y, s);
        EndKunai(seq);
        if (Owns(s, thrower)) GoneCall.Send((PlayerId, seq));
        return true;
    }

    // 落とした事を配る端末: 刀と弾は振った・撃った人の端末 (練習の相手はホスト)・それ以外 (爆発など) は投げた本人
    private static bool Owns(in Strike s, bool thrower)
    {
        if (s.Element != StrikeElement.Slash && s.Element != StrikeElement.Bullet) return thrower;
        var by = PortKit.ById(s.By);
        if (!by) return false;
        return by.AmOwner || (by.isDummy && AmongUsClient.Instance && AmongUsClient.Instance.AmHost);
    }

    private static void Spark(float x, float y, in Strike s)
    {
        float dx = s.Dx, dy = s.Dy;
        if (dx == 0f && dy == 0f) { dx = x - s.X; dy = y - s.Y; }
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.0001f) { dx = 0f; dy = -1f; } else { dx /= d; dy /= d; }
        GatlingFx.Deflect(x, y, dx, dy);
        GunSound.Deflect(x, y, false);
    }

    // 頑丈な役職への当たりを決めるのはこの端末か
    private static bool DecidesFor(PlayerControl p) =>
        p.AmOwner || (p.isDummy && AmongUsClient.Instance && AmongUsClient.Instance.AmHost);

    // 頑丈な役職の体に届いたクナイをどう受けたか (サムライは弾と同じ: 踏み込み = 素通り・振り = 斬る・抜いて正面 = 受け流す)
    private static Neutral.Samurai.BulletFate ToughCatch(PlayerControl p, Vector2 dir) =>
        RoleState.Of(p) is Neutral.Samurai s ? s.TakeBullet(dir.x, dir.y) : Neutral.Samurai.BulletFate.Hit;

    // この端末が当たりを決める頑丈な役職が居るか (投げた本人でない端末で、クナイごとに全員を見ないため)
    private static bool AnyToughHere()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && Duel.Tough(lp)) return true;
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.isDummy && Duel.Tough(p)) return true;
        return false;
    }

    private static readonly RemoteCall<(byte k, byte seq)> GoneCall = new("Kunoichi.Gone", Route.Anyone,
        (w, m) => { w.Write(m.k); w.Write(m.seq); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => { if (RoleState.Of(m.k) is Kunoichi k) k.EndKunai(m.seq); }, perSecond: 12);

    // 頑丈な役職の端末 (練習の相手はホスト) から: クナイが自分に当たった
    private static readonly RemoteCall<(byte k, byte t)> ToughHitCall = new("Kunoichi.ToughHit", Route.ToHost,
        (w, m) => { w.Write(m.k); w.Write(m.t); }, r => (r.ReadByte(), r.ReadByte()),
        (sender, m) =>
        {
            var t = PortKit.ById(m.t);
            if (!t || !Duel.Tough(t) || !sender) return;
            if (sender.PlayerId != m.t && !t.isDummy) return; // 自分への当たりか、ホストが動かす練習の相手だけ
            if (RoleState.Of(m.k) is Kunoichi k && PortKit.Alive(k.Player)) k.HostHit(m.t);
        }, perSecond: 12);
}
