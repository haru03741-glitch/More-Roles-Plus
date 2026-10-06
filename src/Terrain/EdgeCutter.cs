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

    // keep = 切り取らない矩形 (家具の保護範囲。壁の絵を抜かない所は当たり判定も残す)
    // dryRun = 切り取る区間を allow に見せるだけで壁は変えない (全部の可否を切る前の地形で決めてから切るため)
    public static bool Cut(EdgeCollider2D col, CutShape shape, List<Vector2> removed = null, Func<Vector2, Vector2, bool> allow = null,
        List<Rect> keep = null, bool dryRun = false)
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
        var cuts = new List<(float s0, float s1)>();

        for (int i = 0; i < n - 1; i++)
        {
            Vector2 a = world[i], b = world[i + 1];
            Vector2 d = b - a;

            // 形の内側にある区間 [s0, s1] から、残す矩形に掛かる所を引いた区間の列
            cuts.Clear();
            if (shape.Interval(a, b, out float s0, out float s1) && s0 < s1)
            {
                cuts.Add((s0, s1));
                if (keep != null)
                {
                    foreach (var r in keep) Subtract(cuts, a, d, r);
                    // 家具の範囲を引いて残った細い切れ目は切らない (人は通れず、縁のぼかしで絵も抜けないので見た目と食い違うだけ)
                    bool trimmed = cuts.Count != 1 || cuts[0].s0 != s0 || cuts[0].s1 != s1;
                    float len = d.magnitude;
                    if (trimmed) cuts.RemoveAll(c => (c.s1 - c.s0) * len < MinSliver);
                }
                if (allow != null) cuts.RemoveAll(c => !allow(a + d * c.s0, a + d * c.s1));
            }
            if (dryRun) continue;

            if (cuts.Count == 0)
            {
                // 線分は形に掛からない
                if (cur == null) { cur = new List<Vector2> { a }; chains.Add(cur); }
                cur.Add(b);
                continue;
            }

            changed = true;
            float from = 0f; // 残す部分の始まり
            foreach (var (c0, c1) in cuts)
            {
                if (removed != null) { removed.Add(a + d * c0); removed.Add(a + d * c1); }
                // 切り取る区間の手前の部分
                if (c0 > from)
                {
                    if (cur == null) { cur = new List<Vector2> { a + d * from }; chains.Add(cur); }
                    cur.Add(a + d * c0);
                }
                cur = null;
                from = c1;
            }
            // 最後の区間の先の部分 (次の線分へ続く)
            if (from < 1f)
            {
                cur = new List<Vector2> { a + d * from, b };
                chains.Add(cur);
            }
        }

        if (!changed || dryRun) return false;

        // 頂点ちょうどで切ると、切った点と頂点が重なる (重なるかどうかは計算の末尾の差で端末ごとに分かれる) ので 1 つにまとめる
        foreach (var ch in chains)
            for (int k = ch.Count - 1; k > 0; k--)
            {
                Vector2 p = ch[k], q = ch[k - 1];
                if ((p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y) < SamePoint * SamePoint) ch.RemoveAt(k == ch.Count - 1 ? k - 1 : k);
            }

        chains.RemoveAll(ch => ch.Count < 2 || (ch.Count == 2 && (ch[0] - ch[1]).sqrMagnitude < 1e-6f));

        if (chains.Count == 0)
        {
            TerrainDigest.Removed(world[0], world[n - 1]);
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

    private const float MinSliver = 0.1f;
    private const float SamePoint = 0.002f; // これより近い隣り合う点は同じ点とみなす

    // 区間の列 (昇順・重なり無し) から、線分 a + d*s が矩形 r の内側にある範囲を引く
    private static void Subtract(List<(float s0, float s1)> cuts, Vector2 a, Vector2 d, Rect r)
    {
        float k0 = 0f, k1 = 1f;
        if (!Slab(a.x, d.x, r.xMin, r.xMax, ref k0, ref k1) || !Slab(a.y, d.y, r.yMin, r.yMax, ref k0, ref k1)) return;
        for (int i = cuts.Count - 1; i >= 0; i--)
        {
            var (c0, c1) = cuts[i];
            if (k1 <= c0 || k0 >= c1) continue;
            cuts.RemoveAt(i);
            if (k1 < c1) cuts.Insert(i, (k1, c1));
            if (k0 > c0) cuts.Insert(i, (c0, k0));
        }
    }

    private static bool Slab(float p, float dp, float lo, float hi, ref float k0, ref float k1)
    {
        if (MathF.Abs(dp) < 1e-9f) return p >= lo && p <= hi;
        float t0 = (lo - p) / dp, t1 = (hi - p) / dp;
        if (t0 > t1) (t0, t1) = (t1, t0);
        k0 = Math.Max(k0, t0);
        k1 = Math.Min(k1, t1);
        return k0 < k1;
    }

    private static void Apply(EdgeCollider2D col, List<Vector2> worldChain, Transform t, Vector2 colliderOffset)
    {
        TerrainDigest.Chain(worldChain);
        var pts = new Vector2[worldChain.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = (Vector2)t.InverseTransformPoint(worldChain[i]) - colliderOffset;
        col.offset = colliderOffset;
        col.points = pts;
    }
}
