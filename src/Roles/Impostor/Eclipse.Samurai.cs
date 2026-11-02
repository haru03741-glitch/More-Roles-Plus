using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 隕石とサムライ。円の中のサムライが着弾の直前 (CutFrom〜CutTo 秒前) に刀を振っていれば、隕石は空中で二つに割れて
// 左右へ落ち、爆発しない (円の中の誰も倒れず、壁も壊れない)。斬ったかはそのサムライの端末 (ダミーはホスト) が決めて全員へ送る。
// 円の中に生きたサムライが居る間は、その知らせを着弾から最長 Grace 秒待つ (倒す・爆発の音・見た目・地形のどれも)。
// 斬れなかったサムライは即死せず、体力を削られて吹き飛び、エクリプスとの立ち合いに入る。転がっている最中は当たらない
public sealed partial class Eclipse
{
    private const float CutFrom = 0.30f, CutTo = 0.04f; // 着弾の何秒前から何秒前までの振りで斬れるか
    private const float Grace = 0.25f;                  // 斬ったかの知らせを待つ、着弾からの最長
    private const float CutSlack = 0.6f;                // 受け手が斬った人の位置を確かめる時の円の余裕 (動いた分と届くまでの遅れ)
    private const int ToughDamage = 50;                 // 斬れなかったサムライの体力の減り (ロケランの爆発と同じ)

    internal enum Fate : byte { Falling, Hold, Cut, Land }

    private sealed class Strike
    {
        public byte Ecl;
        public float X, Y, R, ImpactAt;
        public bool Cut, Landed;
        public int Key;
    }

    private static readonly List<Strike> Strikes = new();
    private static int _nextKey;

    private static Strike AddStrike(byte ecl, Vector2 at, float size, float impact)
    {
        var st = new Strike { Ecl = ecl, X = at.x, Y = at.y, R = size, ImpactAt = Time.time + impact, Key = ++_nextKey };
        Strikes.Add(st);
        return st;
    }

    // 見た目の側から: この隕石は今どうなっているか
    internal static Fate FateOf(int key)
    {
        foreach (var st in Strikes)
        {
            if (st.Key != key) continue;
            if (st.Cut) return Fate.Cut;
            if (st.Landed) return Fate.Land;
            return Time.time >= st.ImpactAt ? Fate.Hold : Fate.Falling;
        }
        return Fate.Land;
    }

    // 着弾の時刻に (全員の端末): 斬られていなければ爆発させる。円の中にサムライが居れば斬ったかの知らせを少し待つ
    private static void Settle(Strike st, Eclipse e)
    {
        if (st.Cut || st.Landed) return;
        if (MeetingHud.Instance || !ShipStatus.Instance) { st.Landed = true; return; }
        if (Time.time < st.ImpactAt + Grace && SamuraiInside(st))
        {
            PortKit.After(0.05f, () => Settle(st, e));
            return;
        }
        st.Landed = true;
        var at = FxMath.V2(st.X, st.Y);
        PortSound.Play("eclipse_boom", () => PortSound.Fallback("eclipse_boom"), 1f, at, SoundReach);
        if (AmongUsClient.Instance.AmHost) HostBlast(e.Player, at, st.R);
    }

    private static bool SamuraiInside(Strike st)
    {
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.inVent || RoleState.Of(p) is not Neutral.Samurai) continue;
            Vector2 u = p.GetTruePosition();
            float dx = u.x - st.X, dy = u.y - st.Y;
            if (dx * dx + dy * dy <= st.R * st.R) return true;
        }
        return false;
    }

    // 毎フレーム (全員の端末・隕石が落ちている間だけ): 斬る窓の中なら、この端末が判定するサムライの振りを見る
    internal static void TickStrikes()
    {
        if (Strikes.Count == 0) return;
        float now = Time.time;
        for (int i = Strikes.Count - 1; i >= 0; i--)
        {
            var st = Strikes[i];
            if (now > st.ImpactAt + 3f) { Strikes.RemoveAt(i); continue; }
            if (st.Cut || st.Landed) continue;
            float lead = st.ImpactAt - now;
            if (lead > CutFrom || lead < CutTo) continue;
            var lp = PlayerControl.LocalPlayer;
            if (lp && RoleState.Of(lp) is Neutral.Samurai mine && TryCut(st, mine)) continue;
            if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) continue;
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p && p.isDummy && RoleState.Of(p) is Neutral.Samurai bot && TryCut(st, bot)) break;
        }
    }

    private static bool TryCut(Strike st, Neutral.Samurai s)
    {
        var p = s.Player;
        if (!s.JudgesBullets || !PortKit.Alive(p) || p.inVent || !s.CutsNow) return false;
        Vector2 u = p.GetTruePosition();
        float dx = u.x - st.X, dy = u.y - st.Y;
        if (dx * dx + dy * dy > st.R * st.R) return false;
        CutCall.SendAs(p, (st.Ecl, st.X, st.Y));
        ApplyCut(st, p);
        return true;
    }

    private static void ApplyCut(Strike st, PlayerControl by)
    {
        st.Cut = true;
        Plugin.Logger.LogInfo($"eclipse: meteor cut by {by.PlayerId} at ({st.X:0.00},{st.Y:0.00})");
        EclipseFx.CutStrike(st.Key, by);
    }

    // 送り主 = 斬ったサムライ (ダミーならホストが代わりに送る)。届いた側でも、生きたサムライが円の近くに居て、その隕石がまだ落ちていない時だけ受ける
    private static readonly RemoteCall<(byte ecl, float x, float y)> CutCall = new("Eclipse.Cut", Route.Anyone,
        (w, m) => { w.Write(m.ecl); w.Write(m.x); w.Write(m.y); }, r => (r.ReadByte(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) =>
        {
            if (!PortKit.Alive(sender) || RoleState.Of(sender) is not Neutral.Samurai) return;
            Strike best = null;
            float bestD = 0.25f;
            foreach (var st in Strikes)
            {
                if (st.Ecl != m.ecl || st.Cut || st.Landed) continue;
                float ex = st.X - m.x, ey = st.Y - m.y, d = ex * ex + ey * ey;
                if (d < bestD) { bestD = d; best = st; }
            }
            if (best == null || Time.time < best.ImpactAt - CutFrom - 0.5f) return;
            Vector2 u = sender.GetTruePosition();
            float dx = u.x - best.X, dy = u.y - best.Y, reach = best.R + CutSlack;
            if (dx * dx + dy * dy > reach * reach) return;
            ApplyCut(best, sender);
        });

    // ホスト: 円の中のサムライに斬られずに落ちた (転がっている最中なら当たらない)
    private static void HostHitSamurai(PlayerControl invoker, PlayerControl p)
    {
        if (DuelDodge.HostSafe(p.PlayerId)) return;
        Duel.HostSmash(invoker, p, ToughDamage);
    }
}
