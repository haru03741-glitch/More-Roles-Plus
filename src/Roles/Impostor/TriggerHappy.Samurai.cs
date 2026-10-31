using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 機関銃の弾とサムライ。サムライは弾で一撃では倒れず、立ち合いの体力が削れる。刀で斬る・受け流す・跳ね返すができる (Neutral/Samurai.Bullets.cs)。
// 当たったかは撃たれたサムライ本人の端末 (練習の相手はホスト) が、その端末で飛ばしている弾で決め、0.2 秒ごとに数をまとめてホストへ送る。
// ホストは体力を減らし、受け流した火花と跳ね返した弾を全員に見せる。溜め切った居合を当てられると機関銃が切れて乱射が終わる
public sealed partial class TriggerHappy
{
    private const float GrazeEvery = 0.2f;
    private const int GrazeMax = 16;          // 1 通に載せる 1 種類の数の上限 (1 tick に 1 人 1 発 × 0.2 秒 = 6 発に、遅れて固まる分の余裕)
    private const int ShowMax = 6;            // 1 通で見せる火花・跳ね返る弾の上限

    private static bool Tough(PlayerControl p) => RoleState.Of(p) is Neutral.Samurai;

    // この端末が当たりを決めるサムライ (自分か、ホストなら練習の相手)
    private readonly List<(Neutral.Samurai s, float x, float y)> _judged = new();
    // ほかの端末が決めるサムライ (この端末では弾を止めて見せるだけ・数えない。火花は決めた端末から届く)
    private readonly List<(float x, float y)> _shields = new();

    // 当たりを決めたサムライごとの数 (まとめて送る)
    private sealed class Tally { public byte Id; public int Hits, Parries, Cuts, Reflects; }
    private readonly List<Tally> _tallies = new();
    private float _grazeTimer;

    private int _judgedAge;
    private readonly List<byte> _stopped = new();   // この tick に弾を止めたサムライ (1 tick に 1 人 1 発まで・他の人の当たりと同じ)

    // 3 tick に 1 回だけ作り直す (位置のずれは 0.1 秒ぶん)
    private void CacheJudged()
    {
        _stopped.Clear();
        if (Neutral.Samurai.Count == 0) { _judged.Clear(); _shields.Clear(); return; }
        if (++_judgedAge < 3 && _judged.Count + _shields.Count > 0) return;
        _judgedAge = 0;
        _judged.Clear();
        _shields.Clear();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.inVent || p.PlayerId == PlayerId || RoleState.Of(p) is not Neutral.Samurai s) continue;
            if (!FriendlyFire && SameSide(p)) continue;
            var pos = p.GetTruePosition();
            if (s.JudgesBullets) _judged.Add((s, pos.x, pos.y + 0.3f));
            else _shields.Add((pos.x, pos.y + 0.3f));
        }
    }

    // ほかの端末が決めるサムライの体に入った弾は止める (撃った人の画面で弾が体を抜けて見えないように)
    private bool Shielded(Shot s)
    {
        float r = Radius * BulletSize + 0.3f, r2 = r * r;
        for (int k = 0; k < _shields.Count; k++)
        {
            float dx = _shields[k].x - s.X, dy = _shields[k].y - s.Y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    // 弾がこの端末の決めるサムライに届いたか。止まる弾なら true (素通りは false)
    private bool TrySamurai(Shot s)
    {
        float r = Radius * BulletSize + 0.3f, r2 = r * r;
        for (int k = 0; k < _judged.Count; k++)
        {
            var (sam, px, py) = _judged[k];
            if (_stopped.Contains(sam.PlayerId)) continue;
            float dx = px - s.X, dy = py - s.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;
            if (!PierceWalls && d2 > 0.0001f)
            {
                float d = FxMath.Sqrt(d2);
                if (PhysicsHelpers.AnyNonTriggersBetween(FxMath.V2(s.X, s.Y), FxMath.V2(dx / d, dy / d), d, Constants.ShipAndObjectsMask)) continue;
            }
            var fate = sam.TakeBullet(s.Dx, s.Dy);
            if (fate == Neutral.Samurai.BulletFate.Pass) return false;
            var t = TallyOf(sam.PlayerId);
            switch (fate)
            {
                case Neutral.Samurai.BulletFate.Hit: t.Hits++; break;
                case Neutral.Samurai.BulletFate.Parry: t.Parries++; break;
                case Neutral.Samurai.BulletFate.Cut: t.Cuts++; break;
                default: t.Reflects++; break;
            }
            if (fate != Neutral.Samurai.BulletFate.Hit) ShowDeflect(sam, Player, s.Dx, s.Dy, fate == Neutral.Samurai.BulletFate.Reflect);
            _stopped.Add(sam.PlayerId);
            return true;
        }
        return false;
    }

    private Tally TallyOf(byte id)
    {
        foreach (var t in _tallies) if (t.Id == id) return t;
        var n = new Tally { Id = id };
        _tallies.Add(n);
        return n;
    }

    private void FlushGraze(float dt)
    {
        if (_tallies.Count == 0) return;
        if ((_grazeTimer += dt) < GrazeEvery) return;
        _grazeTimer = 0f;
        foreach (var t in _tallies)
        {
            if (t.Hits + t.Parries + t.Cuts + t.Reflects == 0) continue;
            GrazeCall.Send(new Graze(PlayerId, t.Id, Cap(t.Hits), Cap(t.Parries), Cap(t.Cuts), Cap(t.Reflects)));
        }
        _tallies.Clear();
    }

    private static byte Cap(int n) => (byte)System.Math.Min(n, GrazeMax);

    private readonly struct Graze
    {
        public readonly byte Shooter, Victim, Hits, Parries, Cuts, Reflects;
        public Graze(byte shooter, byte victim, byte hits, byte parries, byte cuts, byte reflects)
        {
            Shooter = shooter; Victim = victim; Hits = hits; Parries = parries; Cuts = cuts; Reflects = reflects;
        }
    }

    private static readonly RemoteCall<Graze> GrazeCall = new("TriggerHappy.Graze", Route.ToHost,
        (w, g) => { w.Write(g.Shooter); w.Write(g.Victim); w.Write(g.Hits); w.Write(g.Parries); w.Write(g.Cuts); w.Write(g.Reflects); },
        r => new Graze(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        HostGraze, perSecond: 12);

    // ホスト: 撃たれたサムライの端末から届いた数を当てる
    private static void HostGraze(PlayerControl sender, Graze g)
    {
        if (MeetingHud.Instance || !sender) return;
        if (PortKit.ById(g.Shooter) is not { } shooter || RoleState.Of(shooter) is not TriggerHappy th) return;
        if (!(th._active || Time.time < th._activeUntil + 1f) || !PortKit.Alive(shooter)) return;
        if (PortKit.ById(g.Victim) is not { } v || !PortKit.Alive(v) || RoleState.Of(v) is not Neutral.Samurai sam) return;
        // 当たりを決めてよいのは撃たれた本人か、練習の相手ならホスト
        if (sender != v && !(v.isDummy && sender.AmOwner)) return;
        if (!FriendlyFire && th.SameSide(v)) return;
        Vector2 a = shooter.GetTruePosition(), b = v.GetTruePosition();
        float dx = b.x - a.x, dy = b.y - a.y, reach = SprayRange * 2f + 2f;
        if (dx * dx + dy * dy > reach * reach) return;
        int hits = System.Math.Min((int)g.Hits, GrazeMax), parries = System.Math.Min((int)g.Parries, GrazeMax), cuts = System.Math.Min((int)g.Cuts, GrazeMax), reflects = System.Math.Min((int)g.Reflects, GrazeMax);
        Duel.HostJoin(shooter.PlayerId, v.PlayerId);
        if (parries > 0) sam.HostSpendParry(parries);
        if (hits > 0) HostBulletDamage(shooter, v, Neutral.Samurai.BulletDamage * hits);
        // 跳ね返しは段 2 以上の居合を振った直後の申告だけ受ける (それ以外は斬っただけにする)
        if (reflects > 0 && !sam.HostMayReflect) { cuts += reflects; reflects = 0; }
        if (reflects > 0 && PortKit.Alive(shooter) && PortKit.Alive(v)) sam.HostReflect(shooter, Neutral.Samurai.ReflectDamage * reflects);
        int shown = parries + cuts + reflects;
        if (shown == 0) return;
        var m = (v.PlayerId, shooter.PlayerId, (byte)System.Math.Min(parries + cuts, ShowMax), (byte)System.Math.Min(reflects, ShowMax));
        DeflectCall.Send(m);
        // ホストの手元で決めた (練習の相手) なら、もう見せている
        if (!sender.AmOwner) ShowDeflects(m.Item1, m.Item2, m.Item3, m.Item4);
    }

    // ホスト: 弾が体に当たった。倒す分だけキルとして確かめる (盾などに止められたら体力 1 で残す)
    private static void HostBulletDamage(PlayerControl shooter, PlayerControl v, int dmg)
    {
        byte id = v.PlayerId;
        int left = Duel.HpOf(id);
        if (left - dmg <= 0)
        {
            DuelTongue.Finishing = true;
            try { if (BeforeMurderEvent.Ask(shooter, v) != null) dmg = left - 1; }
            finally { DuelTongue.Finishing = false; }
        }
        if (!Duel.HostDamage(shooter.PlayerId, id, dmg, start: dmg < left)) return;
        shooter.RpcMurderPlayer(v, true);
        Duel.HostLeave(id);
    }

    // 受け流した火花と跳ね返る弾 (当たりを決めた端末以外へ)
    private static readonly RemoteCall<(byte victim, byte shooter, byte n, byte reflects)> DeflectCall = new("TriggerHappy.Deflect", Route.HostToAll,
        (w, m) => { w.Write(m.victim); w.Write(m.shooter); w.Write(m.n); w.Write(m.reflects); },
        r => (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp && lp.PlayerId == m.victim) return;
            ShowDeflects(m.victim, m.shooter, m.n, m.reflects);
        });

    private static void ShowDeflects(byte victim, byte shooter, int n, int reflects)
    {
        var v = PortKit.ById(victim);
        var s = PortKit.ById(shooter);
        if (!v || !s || RoleState.Of(v) is not Neutral.Samurai sam) return;
        var a = s.transform.position;
        var b = v.transform.position;
        float dx = b.x - a.x, dy = b.y - a.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) return;
        dx /= d;
        dy /= d;
        for (int i = 0; i < n + reflects; i++)
        {
            float j = FxMath.Range(-0.15f, 0.15f);
            ShowDeflect(sam, s, dx - dy * j, dy + dx * j, i >= n);
        }
    }

    // 刀で受けた火花と音・跳ね返した弾は撃った人へ飛ぶ (見た目だけ)
    private static void ShowDeflect(Neutral.Samurai sam, PlayerControl shooter, float dx, float dy, bool reflect)
    {
        var p = sam.Player;
        if (!p || !p.Visible) return;
        var tip = sam.BladeTip(dx, dy);
        GatlingFx.Deflect(tip.x, tip.y, dx, dy);
        GunSound.Deflect(tip.x, tip.y, reflect);
        if (!reflect || !shooter) return;
        var c = shooter.transform.position;
        float rx = c.x - tip.x, ry = c.y + 0.1f - tip.y, rd = FxMath.Sqrt(rx * rx + ry * ry);
        if (rd < 0.05f) return;
        GatlingFx.Shoot(tip.x, tip.y, rx / rd, ry / rd, Speed, rd, false, BulletSize, FxMath.Range(0f, 0.15f));
    }

    // 確かめ用: この人 (練習の相手でも) に機関銃を構えさせる / しまわせる (ホストの手元だけ)
    internal static bool ArmForTest(PlayerControl p, bool on)
    {
        if (RoleState.Of(p) is not TriggerHappy th) return false;
        th.SetActive(on);
        return true;
    }

    internal bool ActiveForTest => _active;

    // ---- 機関銃を斬られる ----

    // ホスト: 溜め切った居合が機関銃の人に当たった。構えていれば機関銃が切れて乱射が終わる
    internal void HostBreakGun()
    {
        if (!_active) return;
        BreakCall.Send(PlayerId);
        Break();
    }

    private static readonly RemoteCall<byte> BreakCall = new("TriggerHappy.Break", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (_, id) => (RoleState.Of(id) as TriggerHappy)?.Break());

    private void Break()
    {
        if (_gun != null)
        {
            GatlingFx.BreakGun(_gun);
            _gun = null;
        }
        if (IsLocal && _active && _spray != null) _spray.EndEffect();
        else if (_active) SetActive(false);
    }
}
