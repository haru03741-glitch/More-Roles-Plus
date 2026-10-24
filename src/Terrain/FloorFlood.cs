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
        if (dist == null) return null;

        float reach = MathF.Max(far / (float)CostStraight / ppu, 0.3f);
        return new T
        {
            X0 = x0 + bx, Y0 = y0 + by, W = bw, H = bh, Dist = dist, Reach = reach, Cells = popped,
            Cx = org.x + (sx + 0.5f) / ppu, Cy = org.y + (sy + 0.5f) / ppu,
        };
    }

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
}
