using System;

namespace MoreRolesPlus.Terrain;

// 縁の走査 (LinkPass の前半): 範囲の前の縁の印を消し、隣へ流れない向きを光線の待ち行列へ並べる。
// C++ の部品があればそちら (native/src/link_scan.cpp)。照合の時は C++ を印を消さずに走らせ、C# の答えと比べる
internal static partial class WaterSim
{
    // 升ごとの縁の印: 0〜3 ビット = その向きにガラスの手すり (LowWalls)・4 ビット = はしごの上 (Ladders)
    private const byte RimLadder = 16;
    private static byte[] _rim;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct LinkScanArgs
    {
        public byte* Open; public sbyte* Lvl; public byte* Edge;
        public byte* Rim;
        public byte* FallMask, StepMask, SeaMask, LipMask;
        public int W, X0, Y0, X1, Y1, Dry;
        public int* Q; public int QCap, QN;
        public int* Cleared; public int ClearedCap, ClearedN;
        public int SeaDirty;
    }

    private static int[] _scanCleared = Array.Empty<int>(), _vScanQ = Array.Empty<int>(), _vScanCleared = Array.Empty<int>();
    private static readonly System.Collections.Generic.List<int> CsCleared = new();
    internal static long VerifyScans, VerifyScanDiff;

    // 範囲の升の縁の印を作り直す (はしごの表は作った時に読んだ物・低い壁の表は Links の始めに読んだ物)
    private static void RimMarks(int x0, int y0, int x1, int y1)
    {
        if (_rim == null || _rim.Length != _w * _h) _rim = new byte[_w * _h];
        for (int y = y0; y <= y1; y++) Array.Clear(_rim, y * _w + x0, x1 - x0 + 1);
        for (int i = 0; i + 3 < Ladders.Count; i += 4)
            RimRect(Ladders[i], Ladders[i + 1], Ladders[i + 2], Ladders[i + 3], RimLadder, x0, y0, x1, y1);
        var walls = LowWalls.All;
        for (int i = 0; i < walls.Count; i++)
        {
            var e = walls[i];
            if (e.Dir >= 0 && e.Dir < 4) RimRect(e.X0, e.Y0, e.X1, e.Y1, (byte)(1 << e.Dir), x0, y0, x1, y1);
        }
    }

    // 升の真ん中が範囲 (両端を含む) に入る升へ印を付ける。判定の式は OnLadder / LowWalls.At と同じ (候補の升は前後 1 升広く取る)
    private static void RimRect(float l, float b, float r, float t, byte bit, int x0, int y0, int x1, int y1)
    {
        int lo = Math.Max(x0, (int)MathF.Floor((l - _org.x) / _cell - 0.5f) - 1), hi = Math.Min(x1, (int)MathF.Ceiling((r - _org.x) / _cell - 0.5f) + 1);
        int bo = Math.Max(y0, (int)MathF.Floor((b - _org.y) / _cell - 0.5f) - 1), to = Math.Min(y1, (int)MathF.Ceiling((t - _org.y) / _cell - 0.5f) + 1);
        for (int y = bo; y <= to; y++)
        {
            float py = _org.y + (y + 0.5f) * _cell;
            if (py < b || py > t) continue;
            for (int x = lo; x <= hi; x++)
            {
                float px = _org.x + (x + 0.5f) * _cell;
                if (px >= l && px <= r) _rim[y * _w + x] |= bit;
            }
        }
    }

    private static unsafe int ScanNative(int x0, int y0, int x1, int y1, bool dry, int[] q, int[] cleared, out int clearedN)
    {
        fixed (byte* open = _open) fixed (sbyte* lvl = _lvl) fixed (byte* edge = _edge) fixed (byte* rim = _rim)
        fixed (byte* fm = _fallMask) fixed (byte* sm = _stepMask) fixed (byte* sea = _seaMask) fixed (byte* lip = _lipMask)
        fixed (int* qp = q) fixed (int* cp = cleared)
        {
            var a = new LinkScanArgs
            {
                Open = open, Lvl = lvl, Edge = edge, Rim = rim, FallMask = fm, StepMask = sm, SeaMask = sea, LipMask = lip,
                W = _w, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Dry = dry ? 1 : 0,
                Q = qp, QCap = q.Length, Cleared = cp, ClearedCap = cleared.Length,
            };
            Native.MrpNative.LinkScan(&a);
            if (a.SeaDirty != 0) _seaListDirty = true;
            clearedN = a.ClearedN;
            return a.QN;
        }
    }

    // 範囲を走査して _rayQ / _rayN を作る
    private static void Scan(int x0, int y0, int x1, int y1)
    {
        RimMarks(x0, y0, x1, y1);
        // 前の縁の升は落ちる縁と段差の表の件数より多くない
        int cap = FallTo.Count + StepTo.Count + 1;
        if (!Native.MrpNative.Verify)
        {
            if (_scanCleared.Length < cap) _scanCleared = new int[Math.Max(cap, 256)];
            int n = ScanNative(x0, y0, x1, y1, false, _rayQ, _scanCleared, out int cn);
            if (cn > _scanCleared.Length)
            {
                // 表と印がずれていた時の保険: 範囲の升の向きを表から全部消す
                Plugin.Logger.LogError($"[WaterSim] scan cleared {cn} > {_scanCleared.Length}");
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    for (int d = 0; d < 4; d++) { FallTo.Remove((y * _w + x) * 4 + d); StepTo.Remove((y * _w + x) * 4 + d); }
                    FreeSlot(y * _w + x);
                }
            }
            for (int i = 0, m = Math.Min(cn, _scanCleared.Length); i < m; i++)
            {
                int k = _scanCleared[i] >> 4;
                if ((_scanCleared[i] & 1) != 0) for (int d = 0; d < 4; d++) FallTo.Remove(k * 4 + d);
                if ((_scanCleared[i] & 2) != 0) for (int d = 0; d < 4; d++) StepTo.Remove(k * 4 + d);
                FreeSlot(k);
            }
            if (n > _rayQ.Length)
            {
                // 待ち行列が足りなければ広げて引き直す (印は消えているので答えだけ)
                _rayQ = new int[Math.Max(n, _rayQ.Length * 2)];
                ScanNative(x0, y0, x1, y1, true, _rayQ, _scanCleared, out _);
            }
            _rayN = n;
            return;
        }
        if (_vScanCleared.Length < cap) _vScanCleared = new int[Math.Max(cap, 256)];
        int vn = ScanNative(x0, y0, x1, y1, true, _vScanQ, _vScanCleared, out int vcn);
        if (vn > _vScanQ.Length)
        {
            _vScanQ = new int[vn];
            ScanNative(x0, y0, x1, y1, true, _vScanQ, _vScanCleared, out vcn);
        }
        CsCleared.Clear();
        ScanCs(x0, y0, x1, y1, true);
        VerifyScans++;
        long diff = vn != _rayN ? 1 : 0;
        if (vcn != CsCleared.Count) diff++;
        for (int i = 0; i < Math.Min(vn, _rayN); i++) if (_vScanQ[i] != _rayQ[i]) diff++;
        for (int i = 0; i < Math.Min(vcn, CsCleared.Count); i++) if (_vScanCleared[i] != CsCleared[i]) diff++;
        VerifyScanDiff += diff;
    }

    private static void ScanCs(int x0, int y0, int x1, int y1, bool record)
    {
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int k = y * _w + x;
            if (record && (_fallMask[k] != 0 || _stepMask[k] != 0)) CsCleared.Add(k * 16 + (_fallMask[k] != 0 ? 1 : 0) + (_stepMask[k] != 0 ? 2 : 0));
            if (_fallMask[k] != 0)
            {
                for (int d = 0; d < 4; d++) FallTo.Remove(k * 4 + d);
                _fallMask[k] = 0;
                _lipMask[k] = 0;
            }
            if (_stepMask[k] != 0)
            {
                for (int d = 0; d < 4; d++) StepTo.Remove(k * 4 + d);
                _stepMask[k] = 0;
            }
            FreeSlot(k);
            if (_seaMask[k] != 0) { _seaMask[k] = 0; _seaListDirty = true; }
            int lk = _lvl[k];
            if (_open[k] == 0 || lk == NoLevel || lk == VoidLevel) continue;
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                if (_open[j] != 0 && Passable(k, d)) continue;
                if (Nx[d] != 0 && OnLadder(x, y)) continue;
                // ガラスの手すりは光線を引かない (C++ の答えの -1 と同じ印で後で見分ける)
                RayAdd(OnGlassRail(x, y, d) ? -1 - (k * 4 + d) : k * 4 + d);
            }
        }
    }
}
