using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// EdgeCollider2D の折れ線から形 (円・長方形) の内側を切り取る。
// 切った結果が複数の鎖に分かれたら、同じ GameObject に EdgeCollider2D を足して受け持たせる
// (CompositeCollider2D は Android の libunity に無いので使わない。Collider2D.sharedMaterial も無いので写さない)。
internal static class EdgeCutter
{
    // 戻り値 = 切り取りが起きたか。removed には切り取った区間 (世界座標の線分の両端) を足す。
    // allow = 切り取る区間ごとの許可 (外壁の区間は残す等)。1 本の折れ線が部屋の外周を丸ごと持つ壁があるので、部品単位でなく区間単位で決める
    public static bool Cut(EdgeCollider2D col, Vector2 center, float radius, List<Vector2> removed = null)
        => Cut(col, new CircleShape(center, radius), removed);

    public static bool Cut(EdgeCollider2D col, CutShape shape, List<Vector2> removed = null, Func<Vector2, Vector2, bool> allow = null)
    {
        var t = col.transform;
        Vector2 offset = col.offset;
        var local = col.points;
        int n = local.Length;
        if (n < 2) return false;

        var world = new Vector2[n];
        for (int i = 0; i < n; i++) world[i] = t.TransformPoint(local[i] + offset);

        var chains = new List<List<Vector2>>();
        List<Vector2> cur = null;
        bool changed = false;

        for (int i = 0; i < n - 1; i++)
        {
            Vector2 a = world[i], b = world[i + 1];
            Vector2 d = b - a;

            // 形の内側にある区間 [s0, s1]
            if (!shape.Interval(a, b, out float s0, out float s1)) { s0 = 1f; s1 = 0f; }
            if (s0 < s1 && allow != null && !allow(a + d * s0, a + d * s1)) { s0 = 1f; s1 = 0f; }

            if (s0 >= s1)
            {
                // 線分は形に掛からない
                if (cur == null) { cur = new List<Vector2> { a }; chains.Add(cur); }
                cur.Add(b);
                continue;
            }

            changed = true;
            if (removed != null) { removed.Add(a + d * s0); removed.Add(a + d * s1); }

            // 形の手前の部分
            if (s0 > 0f)
            {
                if (cur == null) { cur = new List<Vector2> { a }; chains.Add(cur); }
                cur.Add(a + d * s0);
            }
            cur = null;

            // 形の先の部分
            if (s1 < 1f)
            {
                cur = new List<Vector2> { a + d * s1, b };
                chains.Add(cur);
            }
        }

        if (!changed) return false;

        chains.RemoveAll(ch => ch.Count < 2 || (ch.Count == 2 && (ch[0] - ch[1]).sqrMagnitude < 1e-6f));

        if (chains.Count == 0)
        {
            col.enabled = false;
            return true;
        }

        Apply(col, chains[0], t, offset);
        for (int k = 1; k < chains.Count; k++)
        {
            var extra = col.gameObject.AddComponent<EdgeCollider2D>();
            extra.edgeRadius = col.edgeRadius;
            extra.isTrigger = col.isTrigger;
            Apply(extra, chains[k], t, offset);
        }
        return true;
    }

    private static void Apply(EdgeCollider2D col, List<Vector2> worldChain, Transform t, Vector2 colliderOffset)
    {
        var pts = new Vector2[worldChain.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = (Vector2)t.InverseTransformPoint(worldChain[i]) - colliderOffset;
        col.offset = colliderOffset;
        col.points = pts;
    }
}
