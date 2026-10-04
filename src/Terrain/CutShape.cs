using System;
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
}
