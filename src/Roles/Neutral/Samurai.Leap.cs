using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 跳躍: 踏み込み (Shift・スマホは踏み込みのボタン) の向きの先に、歩いては行けない床があればそこへ跳ぶ。納めていても跳べる。
// 行き先になるのは 2 種類だけ (同じ高さの壁は越えない = 壁抜けにしない):
//   1. はしごの反対の端・昇降床の向こう岸・ジップラインの反対の端 (近くの端から向きの合う反対側へ)
//   2. 歩いてつながっていない別の床 (崖の上と下など)。向きの先を辿り、自分の床を出てから最初に出会う別の島。
//      途中で自分と同じ島に戻ったら、それは部屋の間の壁なので跳ばない。
//      歩ける所の地図は中身の詰まった当たり判定 (鉱山の岩など) の内側も床に数えるので、着地点が当たり判定に埋まる島は飛ばして先を探す
// 跳ぶ間だけ自分の足元の当たり判定を切って一直線に動き、着いたら行き先へ位置を合わせる (間に床の無い所へ落ちて止まらないように)。
// 位置は本編の同期と Teleport で全員に届くので、電文は跳ぶ姿 (秒・高さ・向き) だけ
public sealed partial class Samurai
{
    private const float LeapNear = 1.8f;   // はしご・昇降床の端からこれ以内なら、その反対側へ跳べる
    private const float LeapCos = 0.8f;    // 向きと行き先のずれの許し (約 37 度)
    private const float LeapMax = 9f;      // はしご・昇降床の反対側までの長さの上限
    private const float ScanMax = 10f;     // 別の床を探す長さ (Fungle の崖は上と下の床が 6〜9 離れている)
    internal const float LandClear = 0.22f; // 着地点に要る体の半径
    private const float ZipMax = 25f;      // ジップラインの両端の長さの上限 (Fungle は約 18)
    internal const int LandMask = 1 << 9 | 1 << 12; // 船の層と家具の層

    // 跳べる組 (端・反対の端・長さの上限)。船ごとに 1 回作る
    private static readonly List<(Vector2 A, Vector2 B, float Max)> Links = new();
    private static int _linksGen = -1;

    // 自分の跳躍 (自分の端末だけ)
    private static bool _leapOn;
    internal static bool LeapActive => _leapOn;
    private static float _leapStart, _leapAir;
    private static Vector2 _leapFrom, _leapTo;
    private static PlayerControl _leapPc;
    private static bool _leapCol; // 当たり判定を切ったか (戻すのはこちらが切った時だけ)
    internal static int Leaps, LeapMisses;
    internal static string LastLeap = "-", LastMiss = "-";

    private static float AirTime(float dist) => 0.32f + 0.045f * dist;
    private static float LeapHeight(float dist) => FxMath.Min(0.35f + 0.06f * dist, 0.9f);

    private static void EnsureLinks()
    {
        var ship = ShipStatus.Instance;
        if (!ship || _linksGen == GameClock.ShipGen) return;
        _linksGen = GameClock.ShipGen;
        Links.Clear();
        foreach (var (top, bottom) in ShipLadders.Of(ship)) AddLink(top, bottom, LeapMax);
        foreach (var mp in ship.GetComponentsInChildren<MovingPlatformBehaviour>(true))
        {
            if (!mp) continue;
            var parent = mp.transform.parent;
            Vector3 l = parent ? parent.TransformPoint(mp.LeftUsePosition) : mp.LeftUsePosition;
            Vector3 r = parent ? parent.TransformPoint(mp.RightUsePosition) : mp.RightUsePosition;
            AddLink(l, r, LeapMax);
        }
        foreach (var z in ship.GetComponentsInChildren<ZiplineBehaviour>(true))
            if (z && z.landingPositionTop && z.landingPositionBottom) AddLink(z.landingPositionTop.position, z.landingPositionBottom.position, ZipMax);
    }

    // 両端を歩ける床へ寄せて、両向きを足す
    private static void AddLink(Vector2 a, Vector2 b, float max)
    {
        if (SolidMap.NearestFloor(a.x, a.y, 0.8f, LandClear, out var fa)) a = fa;
        if (SolidMap.NearestFloor(b.x, b.y, 0.8f, LandClear, out var fb)) b = fb;
        Links.Add((a, b, max));
        Links.Add((b, a, max));
    }

    // dir の向きの跳び先。無ければ false
    private static bool FindLeap(PlayerControl p, Vector2 dir, out Vector2 to)
    {
        to = default;
        float len = FxMath.Sqrt(dir.x * dir.x + dir.y * dir.y);
        if (len < 1e-3f) return false;
        float dx = dir.x / len, dy = dir.y / len;
        Vector2 me = p.GetTruePosition();
        EnsureLinks();
        float best = float.MaxValue;
        foreach (var (a, b, max) in Links)
        {
            float ax = a.x - me.x, ay = a.y - me.y;
            float da = ax * ax + ay * ay;
            if (da > LeapNear * LeapNear || da >= best) continue;
            float vx = b.x - me.x, vy = b.y - me.y, vl = FxMath.Sqrt(vx * vx + vy * vy);
            if (vl < 1f || vl > max || (vx * dx + vy * dy) / vl < LeapCos) continue;
            best = da;
            to = b;
        }
        if (best < float.MaxValue) return true;
        return FindLedge(me, dx, dy, out to);
    }

    // 向きの先の別の島 (崖の上と下など)
    private static bool FindLedge(Vector2 me, float dx, float dy, out Vector2 to)
    {
        to = default;
        int home = SolidMap.IslandNear(me.x, me.y, 1f);
        if (home == 0) { LastMiss = "no floor here"; return false; }
        bool left = false;
        for (float d = 0.1f; d <= ScanMax; d += 0.1f)
        {
            int k = SolidMap.IslandAt(me.x + dx * d, me.y + dy * d);
            if (k == home)
            {
                // 自分の床を出た後に自分の床へ戻った = 同じ高さの壁を挟んだだけ
                if (left) { LastMiss = $"same floor again at {d:0.0}"; return false; }
                continue;
            }
            left = true;
            if (k == 0) continue;
            // 縁から少し内側を着地点にする
            float ex = me.x + dx * (d + 0.35f), ey = me.y + dy * (d + 0.35f);
            if (!SolidMap.NearestFloor(ex, ey, 0.6f, LandClear, out var at) || SolidMap.IslandAt(at.x, at.y) != k) { LastMiss = $"no room to land at {d:0.0}"; return false; }
            if (Buried(at))
            {
                // 当たり判定の中の島 = 歩いては入れない所。その島を抜けるまで進めて、先の島を探し直す
                LastMiss = $"buried at {d:0.0}";
                while (d <= ScanMax && SolidMap.IslandAt(me.x + dx * d, me.y + dy * d) == k) d += 0.1f;
                d -= 0.1f;
                continue;
            }
            to = at;
            return true;
        }
        LastMiss = $"nothing within {ScanMax:0} (home={home})";
        return false;
    }

    // 着地点の体が壁や家具の当たり判定に重なるか (歩ける所の地図に載らない詰まった当たり判定の中を弾く)
    internal static bool Buried(Vector2 at)
    {
        foreach (var col in Physics2D.OverlapCircleAll(at, LandClear, LandMask))
            if (col && !col.isTrigger) return true;
        return false;
    }

    // 踏み込みの代わりに跳べたら true
    private bool TryLeap(Vector2 dir)
    {
        var p = Player;
        if (!p || p.inVent || p.onLadder || p.inMovingPlat || p.walkingToVent || _leapOn) return false;
        if (!FindLeap(p, dir, out var to)) { LeapMisses++; return false; }
        var col = p.Collider;
        if (!col) return false;
        UseKi(DashCost);
        Vector2 me = p.GetTruePosition();
        // 足元 (GetTruePosition) と体の根 (transform) の差の分だけ、行き先の根をずらす
        Vector3 root = p.transform.position;
        float ox = root.x - me.x, oy = root.y - me.y;
        float dist = FxMath.Sqrt((to.x - me.x) * (to.x - me.x) + (to.y - me.y) * (to.y - me.y));
        _leapFrom = FxMath.V2(root.x, root.y);
        _leapTo = FxMath.V2(to.x + ox, to.y + oy);
        _leapAir = AirTime(dist);
        _leapStart = Time.time;
        _leapPc = p;
        _leapCol = col.enabled;
        if (_leapCol) col.enabled = false;
        _leapOn = true;
        Leaps++;
        int sign = to.x >= me.x ? 1 : 0;
        float h = LeapHeight(dist);
        LastLeap = $"from={me.x:0.00},{me.y:0.00} to={to.x:0.00},{to.y:0.00} dist={dist:0.00} air={_leapAir:0.00} h={h:0.00}";
        _blade.Leap(_leapAir, h, sign == 1 ? 1 : -1);
        LeapCall.Send((ushort)(Math.Clamp((int)(_leapAir * 1000f), 1, 4000) | (int)(h * 100f) << 12 | sign << 20));
        return true;
    }

    // 値 = 宙にいる ms (12 bit) | 高さ (0.01 単位・8 bit) << 12 | 右へ跳ぶか << 20
    private static readonly RemoteCall<int> LeapCall = new("Samurai.Leap", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadInt32(),
        (sender, v) => (RoleState.Of(sender) as Samurai)?._blade.Leap((v & 0xFFF) / 1000f, ((v >> 12) & 0xFF) / 100f, (v >> 20 & 1) != 0 ? 1 : -1),
        perSecond: 6);

    // 本編の FixedUpdate の後置きから (自分の端末)。跳んでいる間の速さ。終わったら行き先へ合わせる
    internal static bool Leaping(PlayerPhysics ph, out Vector2 vel)
    {
        vel = default;
        if (!_leapOn) return false;
        var pc = ph.myPlayer;
        if (!pc || pc != _leapPc) return false;
        float t = Time.time - _leapStart - SamuraiBlade.LeapCrouch;
        if (t < 0f) return true; // 屈む間は止まる
        float dt = Time.fixedDeltaTime;
        float u = (t + dt) / _leapAir;
        if (u >= 1f + dt / _leapAir)
        {
            EndLeap(true);
            return true;
        }
        u = FxMath.Clamp01(u);
        float wx = _leapFrom.x + (_leapTo.x - _leapFrom.x) * u, wy = _leapFrom.y + (_leapTo.y - _leapFrom.y) * u;
        Vector2 cur = ph.body.position;
        vel = FxMath.V2((wx - cur.x) / dt, (wy - cur.y) / dt);
        return true;
    }

    // land = 行き先へ着いた (位置を合わせる)・false = 会議や死で打ち切った
    private static void EndLeap(bool land)
    {
        if (!_leapOn) return;
        _leapOn = false;
        var pc = _leapPc;
        _leapPc = null;
        if (!pc) return;
        if (_leapCol && pc.Collider) pc.Collider.enabled = true;
        if (land) Teleport.Self(_leapTo);
    }

    // FixedUpdate ごと (自分の端末)。会議・死・ベントで打ち切る
    private void TickLeap(PlayerControl p)
    {
        if (!_leapOn) return;
        if (_leapPc != p || p.Data == null || p.Data.IsDead || MeetingHud.Instance || p.inVent) EndLeap(false);
        // 着くはずの時を大きく過ぎた = 床の無い所で当たり判定を戻して詰まらないよう、行き先へ合わせる
        else if (Time.time - _leapStart > _leapAir + 2f) EndLeap(true);
    }

    // 役職が外れた・試合が替わった時に (自分の端末)
    private static void ReleaseLeap() => EndLeap(false);

    // 確かめ用
    internal bool LeapForTest(float dx, float dy) => Player && TryLeap(FxMath.V2(dx, dy));

    internal static string DescribeLeap()
    {
        EnsureLinks();
        var sb = new System.Text.StringBuilder();
        sb.Append($"leaps={Leaps} misses={LeapMisses} on={_leapOn} last[{LastLeap}] miss[{LastMiss}] links={Links.Count / 2}");
        for (int i = 0; i < Links.Count; i += 2)
            sb.Append($" {Links[i].A.x:0.00},{Links[i].A.y:0.00}<->{Links[i].B.x:0.00},{Links[i].B.y:0.00}");
        return sb.ToString();
    }
}
