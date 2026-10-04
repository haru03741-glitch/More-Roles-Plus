using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 崩れた塊が跳ね返る壁の線 (動きの層の当たり判定の輪郭) を、破壊 1 回につき 1 度だけ managed の配列へ写す。
// 演出の毎フレームの計算は Physics2D に触らず、この配列だけを見る。
// 家具・ドアの当たり判定も入れる (壊さない物でも塊は当たる)。箱・多角形・円は置き換えずに輪郭を読むだけ
internal static class WallSegments
{
    private const int ShipLayer = 9;

    // c から r 以内に掛かる壁の線分 (x1, y1, x2, y2 の並び)
    public static float[] Snapshot(Vector2 c, float r)
    {
        var o = new List<float>();
        foreach (var col in Physics2D.OverlapCircleAll(c, r, 1 << ShipLayer))
        {
            if (!col || col.isTrigger || !col.enabled) continue;
            var t = col.transform;
            var edge = col.TryCast<EdgeCollider2D>();
            if (edge)
            {
                var pts = edge.points;
                if (pts.Length < 2) continue;
                Vector2 off = edge.offset;
                Vector2 prev = t.TransformPoint(pts[0] + off);
                for (int i = 1; i < pts.Length; i++)
                {
                    Vector2 cur = t.TransformPoint(pts[i] + off);
                    Add(o, prev, cur, c, r);
                    prev = cur;
                }
                continue;
            }
            var box = col.TryCast<BoxCollider2D>();
            if (box)
            {
                Vector2 h = box.size * 0.5f, off = box.offset;
                Loop(o, t, off, new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) }, c, r);
                continue;
            }
            var poly = col.TryCast<PolygonCollider2D>();
            if (poly)
            {
                for (int p = 0; p < poly.pathCount; p++)
                {
                    var path = poly.GetPath(p);
                    var pts = new Vector2[path.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = path[i];
                    Loop(o, t, poly.offset, pts, c, r);
                }
                continue;
            }
            var circle = col.TryCast<CircleCollider2D>();
            if (circle)
            {
                var pts = new Vector2[8];
                for (int i = 0; i < 8; i++)
                {
                    float a = i * MathF.PI / 4f;
                    pts[i] = new Vector2(MathF.Cos(a) * circle.radius, MathF.Sin(a) * circle.radius);
                }
                Loop(o, t, circle.offset, pts, c, r);
            }
        }
        return o.ToArray();
    }

    private static void Loop(List<float> o, Transform t, Vector2 off, Vector2[] pts, Vector2 c, float r)
    {
        if (pts.Length < 2) return;
        Vector2 first = t.TransformPoint(pts[0] + off), prev = first;
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 cur = t.TransformPoint(pts[i] + off);
            Add(o, prev, cur, c, r);
            prev = cur;
        }
        Add(o, prev, first, c, r);
    }

    // 線分の囲みが円の囲みに掛かるものだけ
    private static void Add(List<float> o, Vector2 a, Vector2 b, Vector2 c, float r)
    {
        if (Math.Max(a.x, b.x) < c.x - r || Math.Min(a.x, b.x) > c.x + r ||
            Math.Max(a.y, b.y) < c.y - r || Math.Min(a.y, b.y) > c.y + r) return;
        o.Add(a.x); o.Add(a.y); o.Add(b.x); o.Add(b.y);
    }
}
