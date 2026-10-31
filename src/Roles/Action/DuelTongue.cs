using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライと立ち合っているインポスターの手 = 舌の一撃。立ち合いの間だけキルボタンが舌になり、相手のサムライへ届き Reach まで伸ばせる。
// ・押すとホストが段取りを始め、開いて伸びきった所 (Open + Extend) で当たりを決める。相手が回避で飛び退いていれば外れ
// ・サムライの振りが伸びている途中の舌に向いて来たら舌を斬られる (その振りはインポスターに当たらない)
// ・サムライの振りが始まってから押すと、舌は出さず口で刀を受けて弾く (Samurai.Tongue.cs で斬りを少し保留して待つ)
// 待ち時間はホストが数える。本編のキルの待ち時間は立ち合いの間は裏で数え続け、終わったら戻す
internal static class DuelTongue
{
    internal const float Reach = 3.0f;
    internal const int Damage = 35;
    internal const float Cooldown = 1.6f;
    internal const float HitWidth = 0.45f;     // 伸びきった舌の線から相手の足元までの離れ
    // 押してからこの間に向いて来た振りは舌を斬る (口が開くのを見てから振って間に合うよう、開く溜めを長く取ってある)
    internal const float CutFrom = 0.05f, CutTo = ImpostorMaw.Open + ImpostorMaw.Extend + 0.05f;
    internal const float GuardLead = 0.15f;    // 振りの始めよりこれだけ前の押しまでは「受けた」とみなす (相手の振りは届いてから見えるので読みで押す・口を見てから振った時は斬れる側)
    internal const float Stagger = 0.5f;       // 舌を斬られたインポスターが何もできない秒
    private const float ResolveAt = ImpostorMaw.Open + ImpostorMaw.Extend;
    private const float CooldownSlack = 0.15f;
    private const byte None = 255;

    private enum Kind : byte { Open = 1, Hit = 2, Miss = 3, Cut = 4, Guard = 5, Kill = 6 }

    // ホスト: インポスターごとの伸ばしている舌
    private struct Lash
    {
        public bool On;
        public byte S;
        public float T0, Ang;
    }

    private static readonly Lash[] Lashes = new Lash[256];
    private static readonly float[] PressAt = new float[256];
    private static readonly float[] ReadyAt = new float[256];
    private static readonly float[] StunUntil = new float[256];
    private static readonly float[] KillAt = new float[256]; // 全員: 舌で倒した時刻 (本編のキルで跳ばされた体を戻す)
    private static int _lashes;
    internal static bool Finishing; // ホスト: 舌の止めのキルを確かめている間 (サムライ側の「本編のキルを舌にする」を素通りさせる)
    internal static int Presses, Hits, Misses, CutCount, GuardCount, Refused;

    // 自分の端末: 本編のキルの待ち時間の控え
    private static bool _shadowOn;
    private static float _shadowLeft, _shadowAt;
    private static Lifespan _match;

    // サムライがいる試合で (全員の端末で)。試合ごとに 1 回だけ
    internal static void Use()
    {
        var match = RoleState.Match;
        if (_match == match && !match.IsDead) return;
        _match = match;
        Reset();
        ImpostorMaw.Clear();
        ImpostorMaw.Preload();
        Duel.OnJoin += OnDuelJoin;
        Duel.OnLeave += OnDuelLeave;
        Events<MeetingStartEvent>.Subscribe(_ => { Reset(); ImpostorMaw.Clear(); }, match, owner: "DuelTongue");
        match.OnRelease(() =>
        {
            Duel.OnJoin -= OnDuelJoin;
            Duel.OnLeave -= OnDuelLeave;
            RestoreKillTimer();
            Reset();
            ImpostorMaw.Clear();
        });
    }

    private static void Reset()
    {
        Array.Clear(Lashes);
        Array.Clear(PressAt);
        Array.Clear(ReadyAt);
        Array.Clear(StunUntil);
        _lashes = 0;
    }

    private static PlayerControl Pc(byte id) => GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;

    private static bool Alive(PlayerControl p) => p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

    private static bool IsImpostor(PlayerControl p) => p.Data.Role && p.Data.Role.IsImpostor;

    // このインポスターが舌で戦えるサムライ。同じ組のサムライのうち一番近い人。
    // 組の外のインポスターは、舌の届く所で戦っている (組に空きのある) サムライへ乱入できる
    internal static PlayerControl FoeOf(PlayerControl p)
    {
        if (!Duel.Any || !p) return null;
        byte me = p.PlayerId, g = Duel.GroupOf(me);
        // 組の外の人は毎 FixedUpdate 呼ばれるので、乱入の相手探しは 0.1 秒に 1 回 (覚えた答えを返す)
        long tick = Environment.TickCount64;
        if (g == 0 && me == _outsideFor && tick < _outsideUntil) return _outside && Alive(_outside) ? _outside : null;
        if (!Alive(p) || !IsImpostor(p)) return null;
        Vector2 a = p.GetTruePosition();
        PlayerControl best = null;
        float bestD = float.MaxValue;
        if (g != 0)
        {
            int n = Duel.CountOf(g);
            for (int i = 0; i < n; i++) Closer(Duel.MemberOf(g, i), me, a, ref best, ref bestD);
            return best;
        }
        // 組の外: 戦っている全部の組から (組は数えるほどしか無い)
        for (byte k = 1; k <= Duel.MaxGroups; k++)
        {
            int n = Duel.CountOf(k);
            if (n == 0 || n >= Duel.MaxMembers) continue;
            for (int i = 0; i < n; i++) Closer(Duel.MemberOf(k, i), me, a, ref best, ref bestD);
        }
        _outside = bestD <= Reach * Reach ? best : null;
        _outsideFor = me;
        _outsideUntil = tick + 100;
        return _outside;
    }

    private static PlayerControl _outside;
    private static byte _outsideFor = None;
    private static long _outsideUntil;

    private static void Closer(byte id, byte me, Vector2 a, ref PlayerControl best, ref float bestD)
    {
        if (id == me || RoleState.Of(id) is not Samurai) return;
        var s = Pc(id);
        if (!Alive(s)) return;
        Vector2 b = s.GetTruePosition();
        float dx = b.x - a.x, dy = b.y - a.y, d = dx * dx + dy * dy;
        if (d >= bestD) return;
        bestD = d;
        best = s;
    }

    // このインポスターがこのサムライと舌で戦えるか (同じ組・または乱入できる)
    internal static bool Engages(PlayerControl imp, byte samurai)
    {
        if (!Alive(imp) || !IsImpostor(imp) || RoleState.Of(samurai) is not Samurai || !Alive(Pc(samurai))) return false;
        if (Duel.SameGroup(imp.PlayerId, samurai)) return true;
        return !Duel.InDuel(imp.PlayerId) && FoeOf(imp) is { } f && f.PlayerId == samurai;
    }

    private static float AngleTo(PlayerControl a, PlayerControl b)
    {
        Vector2 x = a.GetTruePosition(), y = b.GetTruePosition();
        return FxMath.Atan2(y.y - x.y, y.x - x.x) * FxMath.Rad2Deg;
    }

    // ---- 押した人 (自分の端末) ----

    // 本編がキルボタンの狙いを決め直す時。立ち合いの間は相手のサムライだけ (舌の届く所にいる時)
    internal static bool OverrideTarget(ref PlayerControl target)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !Duel.Any) return false; // どこにも組が無ければ比べるだけ (毎 FixedUpdate)
        var foe = FoeOf(lp);
        if (!foe) return false;
        Vector2 a = lp.GetTruePosition(), b = foe.GetTruePosition();
        float dx = b.x - a.x, dy = b.y - a.y;
        target = dx * dx + dy * dy <= Reach * Reach ? foe : null;
        return true;
    }

    // キルボタンが押された。舌にした時は false (本編のキルへ流さない)
    internal static bool OnClick(KillButton button)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !Duel.Any || !FoeOf(lp)) return true;
        if (!button.isActiveAndEnabled || button.isCoolingDown || !lp.CanMove || !button.currentTarget) return false;
        PressCall.Send(0);
        lp.SetKillTimer(Cooldown);
        return false;
    }

    private static void OnDuelJoin(byte id, byte g, bool fresh)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.PlayerId != id || !FoeOf(lp)) return;
        // 本編のキルの待ち時間は控えて裏で数え、舌はすぐ使える
        if (!_shadowOn)
        {
            _shadowOn = true;
            _shadowLeft = lp.killTimer;
            _shadowAt = Time.time;
        }
        lp.SetKillTimer(0f);
    }

    private static void OnDuelLeave(byte id, byte g)
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == id && !Duel.InDuel(id)) RestoreKillTimer();
    }

    private static void RestoreKillTimer()
    {
        if (!_shadowOn) return;
        _shadowOn = false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) return;
        // 舌の止めで本編のキルの待ち時間が満ちていれば、そちらを残す
        lp.SetKillTimer(FxMath.Max(_shadowLeft - (Time.time - _shadowAt), lp.killTimer > Cooldown ? lp.killTimer : 0f));
    }

    // ---- ホスト ----

    private static readonly RemoteCall<byte> PressCall = new("Duel.Tongue", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, _) => HostPress(sender), perSecond: 8);

    // インポスターがキルボタンを押した (ボットと本編のキルの経路からも)。舌を出したか弾いたら true
    internal static bool HostPress(PlayerControl imp)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || AmongUsClient.Instance.IsGameOver || MeetingHud.Instance) return false;
        var s = FoeOf(imp);
        if (!s || imp.inVent || imp.inMovingPlat) { Refused++; return false; }
        byte i = imp.PlayerId;
        float now = Time.time;
        if (now < ReadyAt[i] - CooldownSlack || now < StunUntil[i] || Lashes[i].On) { Refused++; return false; }
        // 組の外からの乱入: 舌を出せる時だけ組へ入れる (満員になっていたら押せない)
        if (!Duel.SameGroup(i, s.PlayerId) && !Duel.HostJoin(i, s.PlayerId)) { Refused++; return false; }
        Presses++;
        PressAt[i] = now;
        ReadyAt[i] = now + Cooldown;
        // 振りかかって来ている刀があれば口で受ける (舌は出さない)
        if (RoleState.Of(s) is Samurai ss && ss.HostTakeHeldOn(i))
        {
            HostGuard(i, s.PlayerId, ss);
            return true;
        }
        Lashes[i] = new Lash { On = true, S = s.PlayerId, T0 = now, Ang = AngleTo(imp, s) };
        _lashes++;
        Broadcast(Kind.Open, i, s.PlayerId, AngleByte(Lashes[i].Ang));
        return true;
    }

    // ホスト: サムライの振り (a0 から sweep 度・thrust = 突き) がこのサムライへ伸ばしている舌を斬るか、舌を出しかけたインポスターに受けられるか。
    // 斬ったか受けられたインポスターを返す (その振りはそのインポスターに当てない)
    internal static byte HostStroke(Samurai sam, Vector2 me, float a0, float sweep, bool thrust, float secs)
    {
        if (_lashes == 0) return None;
        float now = Time.time;
        byte sid = sam.PlayerId;
        for (int i = 0; i < 256; i++)
        {
            ref var l = ref Lashes[i];
            if (!l.On || l.S != sid) continue;
            var imp = Pc((byte)i);
            if (!Alive(imp)) continue;
            Vector2 at = imp.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y;
            float dist = FxMath.Sqrt(dx * dx + dy * dy);
            if (dist > Reach + 0.8f || !Aimed(dx, dy, a0, sweep, thrust)) continue;
            // 振りが始まった後に押していた = 振りを見て受けた
            if (l.T0 >= now - secs - GuardLead)
            {
                l.On = false;
                _lashes--;
                HostGuard((byte)i, sid, sam);
                return (byte)i;
            }
            float age = now - l.T0;
            if (age < CutFrom || age > CutTo) continue;
            l.On = false;
            _lashes--;
            StunUntil[i] = now + Stagger;
            CutCount++;
            // 斬れた所 = 舌の線の上でサムライに一番近い所 (口から)
            float ar = l.Ang * FxMath.Deg2Rad;
            float along = (me.x - at.x) * FxMath.Cos(ar) + (me.y - at.y) * FxMath.Sin(ar);
            Broadcast(Kind.Cut, (byte)i, sid, LenByte(FxMath.Clamp(along - 0.6f, 0.2f, Reach)));
            return (byte)i;
        }
        return None;
    }

    private static bool Aimed(float dx, float dy, float a0, float sweep, bool thrust)
    {
        float ta = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
        if (thrust)
        {
            float off = (ta - a0) % 360f;
            if (off < 0f) off += 360f;
            return off <= 25f || off >= 335f;
        }
        float d = (sweep >= 0f ? ta - a0 : a0 - ta) % 360f;
        if (d < 0f) d += 360f;
        return d <= FxMath.Abs(sweep) + 30f || d >= 330f;
    }

    private static void HostGuard(byte i, byte sid, Samurai sam)
    {
        GuardCount++;
        sam.HostStun();
        Broadcast(Kind.Guard, i, sid, 0);
    }

    // ホスト: 伸びきった舌の当たりを決める (毎フレーム・伸ばしている舌が無ければ件数の比較だけ)
    private static void HostTick()
    {
        if (_lashes == 0) return;
        float now = Time.time;
        for (int i = 0; i < 256 && _lashes > 0; i++)
        {
            ref var l = ref Lashes[i];
            if (!l.On || now - l.T0 < ResolveAt) continue;
            l.On = false;
            _lashes--;
            var imp = Pc((byte)i);
            var s = Pc(l.S);
            // 押してから立ち合いが終わっていたら当てない (当てると新しい立ち合いが始まる)
            if (!Alive(s) || MeetingHud.Instance || !Duel.SameGroup((byte)i, l.S)) continue;
            Vector2 a = imp.GetTruePosition(), b = s.GetTruePosition();
            float ar = l.Ang * FxMath.Deg2Rad, dx = b.x - a.x, dy = b.y - a.y;
            float along = dx * FxMath.Cos(ar) + dy * FxMath.Sin(ar), side = FxMath.Abs(dx * FxMath.Sin(ar) - dy * FxMath.Cos(ar));
            bool hit = along > 0f && along <= Reach + 0.3f && side <= HitWidth && !DuelDodge.HostSafe(l.S)
                       && !PhysicsHelpers.AnythingBetween(a, b, Constants.ShipAndObjectsMask, false);
            if (!hit)
            {
                Misses++;
                Broadcast(Kind.Miss, (byte)i, l.S, LenByte(Reach));
                continue;
            }
            Hits++;
            // 倒す一撃だけ本編のキルにする (他の確かめに止められたら体力 1 で残す)
            int left = Duel.HpOf(l.S), dmg = Damage;
            if (left - dmg <= 0)
            {
                Finishing = true;
                try { if (BeforeMurderEvent.Ask(imp, s) != null) dmg = left - 1; }
                finally { Finishing = false; }
            }
            if (left - dmg <= 0)
            {
                Broadcast(Kind.Kill, (byte)i, l.S, LenByte(along));
                imp.RpcMurderPlayer(s, true);
                Duel.HostLeave(l.S);
                continue;
            }
            Broadcast(Kind.Hit, (byte)i, l.S, LenByte(along));
            Duel.HostDamage((byte)i, l.S, dmg);
        }
    }

    // 毎フレーム (Plugin から)
    internal static void Tick()
    {
        ImpostorMaw.Tick();
        if (_lashes > 0 && AmongUsClient.Instance && AmongUsClient.Instance.AmHost) HostTick();
    }

    // ---- 全員の端末 ----

    // 値 = 種類 | インポスター << 8 | サムライ << 16 | 角度か長さ << 24
    private static readonly RemoteCall<int> StateCall = new("Duel.TongueState", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(), (_, v) => Apply(v));

    private static byte AngleByte(float deg) => (byte)(((int)MathF.Round((deg % 360f + 360f) % 360f / 360f * 256f)) & 0xFF);

    private static byte LenByte(float len) => (byte)Math.Clamp((int)MathF.Round(len * 50f), 0, 255);

    private static void Broadcast(Kind k, byte i, byte s, byte q)
    {
        int v = (byte)k | i << 8 | s << 16 | q << 24;
        StateCall.Send(v);
        Apply(v); // 配る電文はホストの手元では動かない
    }

    private static void Apply(int v)
    {
        var k = (Kind)(v & 0xFF);
        byte i = (byte)(v >> 8), sid = (byte)(v >> 16), q = (byte)(v >> 24);
        var imp = Pc(i);
        var s = Pc(sid);
        if (!imp) return;
        switch (k)
        {
            case Kind.Open:
                ImpostorMaw.Strike(imp, q * (360f / 256f), s);
                break;
            case Kind.Hit:
                ImpostorMaw.Resolve(i, q / 50f, true);
                break;
            case Kind.Kill:
                KillAt[i] = Time.time;
                ImpostorMaw.Resolve(i, q / 50f, true);
                break;
            case Kind.Miss:
                ImpostorMaw.Resolve(i, q / 50f, false);
                break;
            case Kind.Cut:
                ImpostorMaw.Cut(i, q / 50f);
                if (s) SamuraiSound.Hit(imp, false);
                break;
            case Kind.Guard:
                if (s) ImpostorMaw.Guard(imp, AngleTo(imp, s));
                if (s) SamuraiSound.Clash(imp, s);
                (RoleState.Of(sid) as Samurai)?.OnGuarded(i);
                break;
        }
    }

    // 舌のキルで本編が倒した人の所へ跳ばした体を戻すか (全員の端末)
    internal static bool JustKilled(byte imp) => Time.time - KillAt[imp] < 2f;

    internal static string Describe()
    {
        var lp = PlayerControl.LocalPlayer;
        var foe = lp ? FoeOf(lp) : null;
        return $"foe={(foe ? foe.PlayerId : -1)} lashes={_lashes} presses={Presses} hits={Hits} misses={Misses} cuts={CutCount} guards={GuardCount} refused={Refused} shadow={(_shadowOn ? _shadowLeft : -1f):0.0} maw[{ImpostorMaw.Describe()}]";
    }

    // 確かめ用: id の人がキルボタンを押した扱い (ホストのみ)
    internal static bool ForTest(byte id)
    {
        var p = Pc(id);
        return p && HostPress(p);
    }
}
