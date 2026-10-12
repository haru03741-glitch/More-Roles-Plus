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
        int search = (int)(spread * ppu);
        int lx = sx - x0, ly = sy - y0;
        int target = (int)(MathF.PI * radius * radius * ppu * ppu);
        int maxCost = Math.Min(ushort.MaxValue - 1, (int)(radius * reachCap * ppu * CostStraight));

        // 答えは届いた升を囲む矩形 (bx by bw bh) に切り出した表
        var dist = FillNative(walls, x0, y0, w, h, lx, ly, search, nearestOnly, target, maxCost, out int bx, out int by, out int bw, out int bh, out int popped, out int far);
        if (Native.MrpNative.Verify)
        {
            var cs = new ushort[w * h];
            bool ok = FillCs(walls, x0, y0, w, h, lx, ly, search, nearestOnly, target, maxCost, cs, out int csPopped, out int csFar);
            long diff = ok != (dist != null) ? 1000000 : 0;
            if (dist != null && ok)
            {
                if (csPopped != popped || csFar != far) diff += 1000000;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool inBox = x >= bx && x < bx + bw && y >= by && y < by + bh;
                    if (cs[y * w + x] != (inBox ? dist[(y - by) * bw + x - bx] : ushort.MaxValue)) diff++;
                }
            }
            VerifyFills++;
            VerifyDiff += diff;
        }
        if (dist == null) return null;

        float reach = MathF.Max(far / (float)CostStraight / ppu, 0.3f);
        return new T
        {
            X0 = x0 + bx, Y0 = y0 + by, W = bw, H = bh, Dist = dist, Reach = reach, Cells = popped,
            Cx = org.x + (sx + 0.5f) / ppu, Cy = org.y + (sy + 0.5f) / ppu,
        };
    }

    // 答え合わせ (native verify): C++ と同じ塗り広げを C# でもやって距離の表を比べる
    internal static long VerifyFills, VerifyDiff;
    // C++ の置き場 (メインスレッドだけ)。距離と通れるかは呼ぶ前も後もまっさら (届いていない・まだ)
    private static int[] _nxt = Array.Empty<int>(), _prv = Array.Empty<int>(), _touched = Array.Empty<int>();
    private static byte[] _pass = Array.Empty<byte>();
    private static ushort[] _dist = Array.Empty<ushort>(), _out = Array.Empty<ushort>();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct FloodArgs
    {
        public byte* Open; public int MapW;
        public int X0, Y0, W, H;
        public int Lx, Ly, Search, NearestOnly;
        public int Target, MaxCost;
        public ushort* Dist;
        public byte* Pass;
        public int* Nxt; public int* Prv;
        public int* Touched;
        public ushort* Out;
        public int Bx, By, Bw, Bh;
        public int Popped, Far, Found;
    }

    private static unsafe ushort[] FillNative(bool walls, int x0, int y0, int w, int h, int lx, int ly, int search, bool nearestOnly,
        int target, int maxCost, out int bx, out int by, out int bw, out int bh, out int popped, out int far)
    {
        int n = w * h;
        if (_nxt.Length < n)
        {
            _nxt = new int[n]; _prv = new int[n]; _touched = new int[n]; _out = new ushort[n]; _pass = new byte[n];
            _dist = new ushort[n]; Array.Fill(_dist, ushort.MaxValue);
        }
        byte[] open = walls ? SolidMap.OpenCells : null;
        FloodArgs a;
        fixed (byte* o = open) fixed (ushort* d = _dist) fixed (byte* ps = _pass) fixed (int* nx = _nxt) fixed (int* pv = _prv)
        fixed (int* tc = _touched) fixed (ushort* ot = _out)
        {
            a = new FloodArgs
            {
                Open = o, MapW = walls ? SolidMap.W : 0, X0 = x0, Y0 = y0, W = w, H = h,
                Lx = lx, Ly = ly, Search = search, NearestOnly = nearestOnly ? 1 : 0, Target = target, MaxCost = maxCost,
                Dist = d, Pass = ps, Nxt = nx, Prv = pv, Touched = tc, Out = ot,
            };
            Native.MrpNative.FloorFlood(&a);
        }
        bx = a.Bx; by = a.By; bw = a.Bw; bh = a.Bh; popped = a.Popped; far = a.Far;
        if (a.Found < 0) Plugin.Logger.LogError($"[FloorFlood] seed range too wide search={search}");
        if (a.Found <= 0) return null;
        var dist = new ushort[bw * bh];
        Array.Copy(_out, dist, dist.Length);
        return dist;
    }

    // C# 版 (照合用)
    private static bool FillCs(bool walls, int x0, int y0, int w, int h, int lx, int ly, int search, bool nearestOnly,
        int target, int maxCost, ushort[] dist, out int popped, out int far)
    {
        popped = 0; far = 0;
        // 通れる升 = 歩ける升で、周り 8 升も歩ける (壁の線の継ぎ目の細い隙間から漏れないように 1 升削る)。
        // 塗りが触る升は範囲の一部なので、触った時に調べて覚える (0 = まだ・1 = 通れる・2 = 通れない)
        var pass = new byte[w * h];
        bool Pass(int k)
        {
            if (pass[k] == 0) pass[k] = !walls || Eroded(x0 + k % w, y0 + k / w) ? (byte)1 : (byte)2;
            return pass[k] == 1;
        }

        // 種: 範囲の中の通れる升を、種の点からの直線距離で始める (打撃は一番近い 1 升だけ = 叩いた側)
        Array.Fill(dist, ushort.MaxValue);
        var queue = new PriorityQueue<int, int>();
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
        if (nearest < 0) return false;
        if (nearestOnly) { dist[nearest] = 0; queue.Enqueue(nearest, 0); }

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
        return true;
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
