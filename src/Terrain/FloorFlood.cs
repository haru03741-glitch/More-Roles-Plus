using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 歩ける所の地図 (SolidMap) の上を、種の点から距離の近い順に塗り広げる (粉塵・水たまり)。
// 壁で止まり、出入口や開けた穴から流れ込む。塗る量 (升の数) は半径の円の面積で、狭い所ほど遠くまで流れる (半径の reachCap 倍まで)。
// 地図と破壊の結果だけで決まるので、全員の手元で同じ形になる
internal static class FloorFlood
{
    internal const int CostStraight = 5, CostDiagonal = 7; // 升 1 つの距離 = 5

    internal class Area
    {
        public int X0, Y0, W, H;
        public ushort[] Dist;    // 種からの距離 (升 1 つ = 5)。ushort.MaxValue = 届いていない
        public float Reach;      // 届いた一番遠い所 (単位)
        public int Cells;
        public float Cx, Cy;
    }

    internal static T Fill<T>(Vector2 seed, float radius, float reachCap, float spread, bool nearestOnly) where T : Area, new()
    {
        bool walls = SolidMap.Valid;
        float ppu = walls ? SolidMap.Ppu : 1f / DamageMap.TexelSize;
        Vector2 org = walls ? SolidMap.Origin : Vector2.zero;
        int cap = (int)(radius * reachCap * ppu) + 2;
        int sx = (int)MathF.Floor((seed.x - org.x) * ppu), sy = (int)MathF.Floor((seed.y - org.y) * ppu);
        int x0 = sx - cap, y0 = sy - cap, x1 = sx + cap, y1 = sy + cap;
        if (walls)
        {
            x0 = Math.Max(x0, 1); y0 = Math.Max(y0, 1);
            x1 = Math.Min(x1, SolidMap.W - 2); y1 = Math.Min(y1, SolidMap.H - 2);
            if (x1 < x0 || y1 < y0) return null;
        }
        int w = x1 - x0 + 1, h = y1 - y0 + 1;

        // 通れる升 = 歩ける升で、周り 8 升も歩ける (壁の線の継ぎ目の細い隙間から漏れないように 1 升削る)。
        // 塗りが触る升は範囲の一部なので、触った時に調べて覚える (0 = まだ・1 = 通れる・2 = 通れない)
        var pass = new byte[w * h];
        bool Pass(int k)
        {
            if (pass[k] == 0) pass[k] = !walls || Eroded(x0 + k % w, y0 + k / w) ? (byte)1 : (byte)2;
            return pass[k] == 1;
        }

        // 種: 範囲の中の通れる升を、種の点からの直線距離で始める (打撃は一番近い 1 升だけ = 叩いた側)
        var dist = new ushort[w * h];
        Array.Fill(dist, ushort.MaxValue);
        var queue = new PriorityQueue<int, int>();
        int search = (int)(spread * ppu);
        int lx = sx - x0, ly = sy - y0;
        int best = int.MaxValue, nearest = -1;
        for (int dy = -search; dy <= search; dy++)
        for (int dx = -search; dx <= search; dx++)
        {
            int x = lx + dx, y = ly + dy;
            int d2 = dx * dx + dy * dy;
            if (d2 > search * search || x < 0 || y < 0 || x >= w || y >= h || !Pass(y * w + x)) continue;
            if (d2 < best) { best = d2; nearest = y * w + x; }
            if (nearestOnly) continue;
            int k = y * w + x;
            dist[k] = (ushort)(MathF.Sqrt(d2) * CostStraight);
            queue.Enqueue(k, dist[k]);
        }
        if (nearest < 0) return null;
        if (nearestOnly) { dist[nearest] = 0; queue.Enqueue(nearest, 0); }

        int target = (int)(MathF.PI * radius * radius * ppu * ppu);
        int maxCost = Math.Min(ushort.MaxValue - 1, (int)(radius * reachCap * ppu * CostStraight));
        int popped = 0, far = 0;
        while (queue.TryDequeue(out int k, out int d))
        {
            if (d != dist[k]) continue;
            popped++;
            far = d;
            if (popped >= target) break;
            int x = k % w, y = k / w;
            for (int n = 0; n < 8; n++)
            {
                int nx = x + Dx[n], ny = y + Dy[n];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int nk = ny * w + nx;
                if (!Pass(nk)) continue;
                int nd = d + (n < 4 ? CostStraight : CostDiagonal);
                if (nd > maxCost || nd >= dist[nk]) continue;
                dist[nk] = (ushort)nd;
                queue.Enqueue(nk, nd);
            }
        }
        // 打ち切った後も表に残った (まだ確定していない) 升は、届いた一番遠い所より先なら消す
        for (int i = 0; i < dist.Length; i++)
            if (dist[i] != ushort.MaxValue && dist[i] > far) dist[i] = ushort.MaxValue;

        float reach = MathF.Max(far / (float)CostStraight / ppu, 0.3f);
        return new T
        {
            X0 = x0, Y0 = y0, W = w, H = h, Dist = dist, Reach = reach, Cells = popped,
            Cx = org.x + (sx + 0.5f) / ppu, Cy = org.y + (sy + 0.5f) / ppu,
        };
    }

    private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static bool Eroded(int x, int y)
    {
        int w = SolidMap.W;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (!SolidMap.OpenCell((y + dy) * w + x + dx)) return false;
        return true;
    }
}
