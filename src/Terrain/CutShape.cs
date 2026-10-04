using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壁を切り取る形。壁の線分との交わり (内側の区間) と、損傷マスク用の符号付き距離を返す。
internal abstract class CutShape
{
    // 線分 a→b のうち形の内側にある区間 [s0, s1] (0..1)。交わらなければ false
    public abstract bool Interval(Vector2 a, Vector2 b, out float s0, out float s1);

    // 形の縁までの距離 (内側は負)
    public abstract float SignedDistance(float x, float y);

    // 形を囲む円 (処理範囲の絞り込み用)
    public abstract Vector2 Center { get; }
    public abstract float BoundRadius { get; }

    // 縁を反時計回りに step 間隔で並べた閉じた点列 (最初の点は繰り返さない)
    public abstract List<Vector2> Outline(float step);

    protected static void AddEdge(List<Vector2> pts, Vector2 a, Vector2 b, float step)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        int k = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy) / step));
        for (int i = 0; i < k; i++) pts.Add(new Vector2(a.x + dx * i / k, a.y + dy * i / k));
    }
}

internal sealed class CircleShape : CutShape
{
    private readonly Vector2 _c;
    private readonly float _r;

    public CircleShape(Vector2 center, float radius) { _c = center; _r = radius; }

    public override Vector2 Center => _c;
    public override float BoundRadius => _r;

    public override bool Interval(Vector2 a, Vector2 b, out float s0, out float s1)
    {
        s0 = 1f; s1 = 0f;
        Vector2 d = b - a;
        float len2 = d.sqrMagnitude;
        if (len2 < 1e-12f) return false;
        Vector2 f = a - _c;
        float bq = Vector2.Dot(f, d);
        float c = f.sqrMagnitude - _r * _r;
        float disc = bq * bq - len2 * c;
        if (disc <= 0f) return false;
        float sq = MathF.Sqrt(disc);
        s0 = Math.Max((-bq - sq) / len2, 0f);
        s1 = Math.Min((-bq + sq) / len2, 1f);
        return s0 < s1;
    }

    public override float SignedDistance(float x, float y)
    {
        float dx = x - _c.x, dy = y - _c.y;
        return MathF.Sqrt(dx * dx + dy * dy) - _r;
    }

    public override List<Vector2> Outline(float step)
    {
        int k = Math.Max(8, (int)MathF.Ceiling(2f * MathF.PI * _r / step));
        var pts = new List<Vector2>(k);
        for (int i = 0; i < k; i++)
        {
            float a = 2f * MathF.PI * i / k;
            pts.Add(new Vector2(_c.x + MathF.Cos(a) * _r, _c.y + MathF.Sin(a) * _r));
        }
        return pts;
    }
}

// 向きのある長方形 (ハンマーで叩いた壁の区間など)。tangent 方向に長さ length、normal 方向に深さ depth
internal sealed class RectShape : CutShape
{
    private readonly Vector2 _c, _t, _n;
    private readonly float _hl, _hd;

    public RectShape(Vector2 center, Vector2 tangent, float length, float depth)
    {
        _c = center;
        _t = tangent.normalized;
        _n = new Vector2(-_t.y, _t.x);
        _hl = length * 0.5f;
        _hd = depth * 0.5f;
    }

    public override Vector2 Center => _c;
    public override float BoundRadius => MathF.Sqrt(_hl * _hl + _hd * _hd);

    // 長方形の局所座標 (u = 接線方向, v = 法線方向) で 2 つの帯に挟まれた区間を求める
    public override bool Interval(Vector2 a, Vector2 b, out float s0, out float s1)
    {
        s0 = 0f; s1 = 1f;
        Vector2 pa = a - _c, d = b - a;
        if (!Slab(Vector2.Dot(pa, _t), Vector2.Dot(d, _t), _hl, ref s0, ref s1)) return false;
        if (!Slab(Vector2.Dot(pa, _n), Vector2.Dot(d, _n), _hd, ref s0, ref s1)) return false;
        return s0 < s1;
    }

    private static bool Slab(float p, float dp, float half, ref float s0, ref float s1)
    {
        if (MathF.Abs(dp) < 1e-9f) return MathF.Abs(p) <= half;
        float t0 = (-half - p) / dp, t1 = (half - p) / dp;
        if (t0 > t1) (t0, t1) = (t1, t0);
        s0 = Math.Max(s0, t0);
        s1 = Math.Min(s1, t1);
        return s0 < s1;
    }

    public override float SignedDistance(float x, float y)
    {
        var p = new Vector2(x, y) - _c;
        float qu = MathF.Abs(Vector2.Dot(p, _t)) - _hl;
        float qv = MathF.Abs(Vector2.Dot(p, _n)) - _hd;
        float ou = Math.Max(qu, 0f), ov = Math.Max(qv, 0f);
        return MathF.Sqrt(ou * ou + ov * ov) + Math.Min(Math.Max(qu, qv), 0f);
    }

    public override List<Vector2> Outline(float step)
    {
        Vector2 u = _t * _hl, v = _n * _hd;
        var c = new[] { _c - u - v, _c + u - v, _c + u + v, _c - u + v };
        var pts = new List<Vector2>();
        for (int i = 0; i < 4; i++) AddEdge(pts, c[i], c[(i + 1) % 4], step);
        return pts;
    }
}

// 凸多角形 (反時計回り)。向きのある爆発の円錐・斜めに叩いた壁の平行四辺形に使う
internal sealed class ConvexShape : CutShape
{
    private readonly Vector2[] _v;
    private readonly Vector2[] _n; // 辺 i (v[i]→v[i+1]) の外向き法線
    private readonly Vector2 _c;
    private readonly float _r;

    public ConvexShape(Vector2[] ccw)
    {
        _v = ccw;
        int k = ccw.Length;
        _n = new Vector2[k];
        for (int i = 0; i < k; i++)
        {
            Vector2 e = ccw[(i + 1) % k] - ccw[i];
            float l = MathF.Sqrt(e.x * e.x + e.y * e.y);
            _n[i] = l > 1e-9f ? new Vector2(e.y / l, -e.x / l) : Vector2.zero;
        }
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in ccw)
        {
            minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
            minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
        }
        _c = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
        foreach (var p in ccw)
        {
            float dx = p.x - _c.x, dy = p.y - _c.y;
            _r = Math.Max(_r, MathF.Sqrt(dx * dx + dy * dy));
        }
    }

    public override Vector2 Center => _c;
    public override float BoundRadius => _r;

    // 辺ごとの半平面で区間を絞る
    public override bool Interval(Vector2 a, Vector2 b, out float s0, out float s1)
    {
        s0 = 0f; s1 = 1f;
        Vector2 d = b - a;
        for (int i = 0; i < _v.Length; i++)
        {
            Vector2 n = _n[i];
            float num = (a.x - _v[i].x) * n.x + (a.y - _v[i].y) * n.y; // 正 = 外側
            float den = d.x * n.x + d.y * n.y;
            if (MathF.Abs(den) < 1e-9f)
            {
                if (num > 0f) return false;
                continue;
            }
            float t = -num / den;
            if (den < 0f) s0 = Math.Max(s0, t); else s1 = Math.Min(s1, t);
            if (s0 >= s1) return false;
        }
        return s0 < s1;
    }

    // 内側 = 辺の直線までの符号付き距離の最大 (凸なので正確) / 外側 = 辺 (線分) までの最短距離
    public override float SignedDistance(float x, float y)
    {
        float inside = float.MinValue;
        bool outside = false;
        for (int i = 0; i < _v.Length; i++)
        {
            float s = (x - _v[i].x) * _n[i].x + (y - _v[i].y) * _n[i].y;
            if (s > 0f) outside = true;
            if (s > inside) inside = s;
        }
        if (!outside) return inside;
        float best = float.MaxValue;
        int k = _v.Length;
        for (int i = 0; i < k; i++)
        {
            Vector2 a = _v[i], b = _v[(i + 1) % k];
            float sx = b.x - a.x, sy = b.y - a.y, l2 = sx * sx + sy * sy;
            float t = l2 > 0 ? Math.Clamp(((x - a.x) * sx + (y - a.y) * sy) / l2, 0f, 1f) : 0f;
            float dx = a.x + sx * t - x, dy = a.y + sy * t - y;
            best = Math.Min(best, dx * dx + dy * dy);
        }
        return MathF.Sqrt(best);
    }

    public override List<Vector2> Outline(float step)
    {
        var pts = new List<Vector2>();
        for (int i = 0; i < _v.Length; i++) AddEdge(pts, _v[i], _v[(i + 1) % _v.Length], step);
        return pts;
    }

    // 向きのある爆発: 爆心の円 (後ろは縮む) と、向きの先へ伸びた先端を包む凸の涙形。
    // 円は Segments 点で近似 (16px/単位のマスクなら半径 3 でも誤差 1px 未満)
    private const int Segments = 12;

    public static ConvexShape Cone(Vector2 center, float radius, Vector2 dir, float stretch, float shrink)
    {
        float back = radius * (1f - shrink);
        Vector2 tip = center + dir * (radius * (1f + stretch));
        float a0 = MathF.Atan2(dir.y, dir.x);
        // 先端から円への接点より後ろの円弧だけ残すと凸になる (接点の角度 = acos(back / 先端までの距離))
        float dist = radius * (1f + stretch);
        float half = MathF.Acos(Math.Clamp(back / dist, -1f, 1f));
        var pts = new Vector2[Segments + 2];
        pts[0] = tip;
        for (int i = 0; i <= Segments; i++)
        {
            float a = a0 + half + (MathF.PI * 2f - 2f * half) * i / Segments;
            pts[i + 1] = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * back;
        }
        return new ConvexShape(pts);
    }

    // 斜めに抜く平行四辺形: 壁に沿って length、axis (振った向き) に沿って壁を貫く。
    // start = 叩いた面の上の中心、axis は壁の奥へ向かう単位ベクトル、run = axis に沿った長さ
    public static ConvexShape Slanted(Vector2 start, Vector2 tangent, Vector2 axis, float length, float run)
    {
        Vector2 h = tangent * (length * 0.5f);
        Vector2 e = axis * run;
        var pts = new[] { start - h, start + h, start + h + e, start - h + e };
        // 反時計回りに揃える (符号付き面積が負なら逆順)
        float area = 0f;
        for (int i = 0; i < 4; i++) { var p = pts[i]; var q = pts[(i + 1) % 4]; area += p.x * q.y - q.x * p.y; }
        if (area < 0f) Array.Reverse(pts);
        return new ConvexShape(pts);
    }
}
