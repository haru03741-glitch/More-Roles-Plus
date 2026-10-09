using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壁に貼られていた絵だけの飾り (季節の飾りなど)。壁が抜けた時に、絵の範囲の半分ほどが抜けた所にある物を隠す
// (壁の絵は抜けるのに飾りだけが宙に残らないように)。穴が開いた時に 1 回だけ動く。
// 候補 (船の小さな絵とその範囲) は船ごとに 1 回だけ集める (穴を開けるたびに船じゅうを読まない)
internal static class WallDecor
{
    private const float MaxArea = 2f; // これより大きい絵は飾りでなく部屋や家具の絵
    private static readonly List<(SpriteRenderer R, Vector2 Min, Vector2 Size)> Decor = new();
    private static int _gen = -1;

    // 中心 c・半径 r に掛かる飾りのうち、gone(点) が絵の範囲の 3×3 の点のうち 4 つ以上で真の物を隠し、隠した数を返す
    internal static int Hide(Vector2 c, float r, Func<Vector2, bool> gone)
    {
        if (!ShipStatus.Instance) return 0;
        if (_gen != GameClock.ShipGen)
        {
            _gen = GameClock.ShipGen;
            Decor.Clear();
            foreach (var s in ShipStatus.Instance.GetComponentsInChildren<SpriteRenderer>(false))
            {
                if (!s.enabled || !s.sprite) continue;
                var b = s.bounds;
                var bs = b.size;
                if (bs.x * bs.y > MaxArea || s.name.StartsWith("Mrp", StringComparison.Ordinal)) continue;
                var bm = b.min;
                Decor.Add((s, new Vector2(bm.x, bm.y), new Vector2(bs.x, bs.y)));
            }
        }
        int n = 0;
        for (int d = Decor.Count - 1; d >= 0; d--)
        {
            var (sr, mn, sz) = Decor[d];
            float nx = Math.Clamp(c.x, mn.x, mn.x + sz.x) - c.x, ny = Math.Clamp(c.y, mn.y, mn.y + sz.y) - c.y;
            if (nx * nx + ny * ny > r * r) continue;
            int inside = 0;
            for (int i = 0; i < 9 && inside < 4; i++)
                if (gone(FxMath.V2(mn.x + sz.x * (0.17f + 0.33f * (i % 3)), mn.y + sz.y * (0.17f + 0.33f * (i / 3))))) inside++;
            if (inside < 4) continue;
            Decor.RemoveAt(d);
            // 部品を見るのは抜けた所の物だけ (船じゅうの絵で部品を引くと重い)
            if (!sr || !sr.enabled || !PropSim.IsDecor(sr)) continue;
            sr.enabled = false;
            n++;
        }
        return n;
    }
}
