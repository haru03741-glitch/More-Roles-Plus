using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 箱・多角形・円の壁を、同じ輪郭の閉じた折れ線 (EdgeCollider2D) に置き換える。
// 壁の当たり判定は外側からぶつかるだけなので、輪郭だけでも動きと視界は変わらない。
// 置き換えるのは損傷が初めて触れた壁だけ (何も壊れない試合では何も変えない)。
internal static class WallOutline
{
    private const int CircleSegments = 16;

    // 置き換えた折れ線を返す。置き換えられない形 (カプセル等) や EdgeCollider2D 自身は空
    public static List<EdgeCollider2D> Convert(Collider2D col)
    {
        var result = new List<EdgeCollider2D>();
        var go = col.gameObject;

        var box = col.TryCast<BoxCollider2D>();
        if (box)
        {
            Vector2 h = box.size * 0.5f;
            AddLoop(go, col, box.offset, new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) }, result);
        }

        var poly = col.TryCast<PolygonCollider2D>();
        if (poly)
        {
            for (int p = 0; p < poly.pathCount; p++)
            {
                var path = poly.GetPath(p);
                var pts = new Vector2[path.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = path[i];
                AddLoop(go, col, poly.offset, pts, result);
            }
        }

        var circle = col.TryCast<CircleCollider2D>();
        if (circle)
        {
            var pts = new Vector2[CircleSegments];
            for (int i = 0; i < CircleSegments; i++)
            {
                float a = i * MathF.PI * 2f / CircleSegments;
                pts[i] = new Vector2(MathF.Cos(a), MathF.Sin(a)) * circle.radius;
            }
            AddLoop(go, col, circle.offset, pts, result);
        }

        if (result.Count > 0) col.enabled = false;
        return result;
    }

    private static void AddLoop(GameObject go, Collider2D src, Vector2 offset, Vector2[] pts, List<EdgeCollider2D> result)
    {
        if (pts.Length < 2) return;
        var loop = new Vector2[pts.Length + 1];
        Array.Copy(pts, loop, pts.Length);
        loop[pts.Length] = pts[0];
        var e = go.AddComponent<EdgeCollider2D>();
        e.offset = offset;
        e.points = loop;
        e.isTrigger = src.isTrigger;
        result.Add(e);
    }
}
