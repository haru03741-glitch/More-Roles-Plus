using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// EdgeCollider2D の折れ線から円の内側を切り取る。
// 切った結果が複数の鎖に分かれたら、同じ GameObject に EdgeCollider2D を足して受け持たせる
// (CompositeCollider2D は Android の libunity に無いので使わない)。
internal static class EdgeCutter
{
    // 戻り値 = 切り取りが起きたか
    public static bool Cut(EdgeCollider2D col, Vector2 center, float radius)
    {
        var t = col.transform;
        Vector2 offset = col.offset;
        var local = col.points;
        int n = local.Length;
        if (n < 2) return false;

        var world = new Vector2[n];
        for (int i = 0; i < n; i++) world[i] = t.TransformPoint(local[i] + offset);

        float r2 = radius * radius;
        var chains = new List<List<Vector2>>();
        List<Vector2> cur = null;
        bool changed = false;

        for (int i = 0; i < n - 1; i++)
        {
            Vector2 a = world[i], b = world[i + 1];
            Vector2 d = b - a;
            float len2 = d.sqrMagnitude;

            // 線分 a + s*d と円の交点の s (0..1)
            float s0 = 1f, s1 = 0f; // 内側区間 [s0, s1]。空なら s0 > s1
            if (len2 > 1e-12f)
            {
                Vector2 f = a - center;
                float bq = Vector2.Dot(f, d);
                float c = f.sqrMagnitude - r2;
                float disc = bq * bq - len2 * c;
                if (disc > 0f)
                {
                    float sq = Mathf.Sqrt(disc);
                    s0 = Mathf.Max((-bq - sq) / len2, 0f);
                    s1 = Mathf.Min((-bq + sq) / len2, 1f);
                }
            }

            if (s0 >= s1)
            {
                // 線分は円に掛からない
                if (cur == null) { cur = new List<Vector2> { a }; chains.Add(cur); }
                cur.Add(b);
                continue;
            }

            changed = true;

            // 円の手前の部分
            if (s0 > 0f)
            {
                if (cur == null) { cur = new List<Vector2> { a }; chains.Add(cur); }
                cur.Add(a + d * s0);
            }
            cur = null;

            // 円の先の部分
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
            extra.sharedMaterial = col.sharedMaterial;
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
