using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 縁を越えた水の行き先を、マップに塗った「空」(水が落ちて消える所) で決める。
// 縁の先を真っすぐ探し、低い床に着けばそこへ・空に着けば消える。空の手前に歩けない升 (壁) が AirLip 升より多くあれば壁に当たって止まる。
// 本編の作りでは壁と手すり・穴の縁を見分けられない (南向きの壁や部屋の間の壁にも視界の影の線が無い) ので、どこが空かは人が塗る
internal static partial class WaterSim
{
    // 空の手前に許す歩けない升。0 = 縁のすぐ隣の升から空 (手すりの線の上まで塗る)。部屋の間の壁は 1 升の細さの所があり、
    // 1 升許すと壁の向こうの空へ漏れる
    private const int AirLip = 0;
    private static byte[] _air;       // 空の升

    private static void LoadAir()
    {
        _air = new byte[_w * _h];
        if (MapNotes.HasAir) MapNotes.RasterAir(_air, _w, _h, _org, _cell);
    }

    // 光線の理由 (C++ の並びと同じ)
    private static readonly string[] RayWhy = { "scan", "border", "door", "prop", "fall", "same", "nolevel", "air", "wall", "side", "airside" };

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct RayArgs
    {
        public byte* Open, Door, Prop, Air; public sbyte* Lvl;
        public byte* Isl; public int* IslBase;
        public int* Q, Out;
        public int N, W, H, IslN, UseZ;
        public fixed int FallScan[4];
        public int SideScan, AirLip;
    }

    private static int[] _rayQ = Array.Empty<int>(), _rayOut = Array.Empty<int>();
    private static int _rayN;
    internal static long VerifyRays, VerifyRayDiff;

    // 床の高さ (島の基準 + 段 × 段差・家具と坂は含めない)。決まらない升は int.MinValue。C++ の base_z と同じ
    private static int BaseZ(int k)
    {
        int l = _lvl[k];
        if (l == NoLevel || l == VoidLevel || _cellIsl == null) return int.MinValue;
        int i = _cellIsl[k];
        return i == 0 || i >= _islBase.Length ? int.MinValue : _islBase[i] == int.MinValue ? int.MinValue : _islBase[i] + l * StepHeight;
    }

    // 縁 (k → c) が段差か。床の高さで比べる時は差が段の高さ以内・それ以外は段の順位が 1 つ違う。どちらも同じ島で面が短い所だけ
    private static bool StepBetween(int k, int c, int dist, bool useZ, int up)
    {
        if (!StepEdge(k, c, dist)) return false;
        int zk, zc;
        if (useZ && (zk = BaseZ(k)) != int.MinValue && (zc = BaseZ(c)) != int.MinValue)
        {
            int dz = (zc - zk) * up;
            return dz > 0 && dz <= StepHeight;
        }
        return _lvl[c] == _lvl[k] + up;
    }

    private static void RayAdd(int key)
    {
        if (_rayN == _rayQ.Length) Array.Resize(ref _rayQ, Math.Max(1024, _rayN * 2));
        _rayQ[_rayN++] = key;
    }

    // 集めた縁の光線をまとめて C++ で引く。照合の時は C# でも引いて比べ、続きは C# の結果で
    private static unsafe void RayRun(bool useZ)
    {
        if (_rayOut.Length < _rayN * 5) _rayOut = new int[Math.Max(_rayQ.Length * 5, _rayN * 5)];
        fixed (byte* open = _open) fixed (byte* door = _doorCell) fixed (byte* prop = _propCell) fixed (byte* air = _air)
        fixed (sbyte* lvl = _lvl) fixed (byte* isl = _cellIsl) fixed (int* islBase = _islBase)
        fixed (int* q = _rayQ) fixed (int* o = _rayOut)
        {
            var a = new RayArgs
            {
                Open = open, Door = door, Prop = prop, Air = air, Lvl = lvl, Isl = isl, IslBase = islBase,
                Q = q, Out = o, N = _rayN, W = _w, H = _h, IslN = _islBase.Length,
                UseZ = useZ && !Native.MrpNative.Verify && isl != null ? 1 : 0, SideScan = SideScan, AirLip = AirLip,
            };
            for (int d = 0; d < 4; d++) a.FallScan[d] = FallScan[d];
            Native.MrpNative.FallRay(&a);
        }
        if (!Native.MrpNative.Verify) return;
        for (int i = 0; i < _rayN; i++)
        {
            int k = _rayQ[i] >> 2, d = _rayQ[i] & 3;
            string why = Ray(k % _w, k / _w, d, _lvl[k], out int to, out int dist, out int tx, out int ty);
            int code = Array.IndexOf(RayWhy, why), o = i * 5;
            VerifyRays++;
            if (_rayOut[o] != code || _rayOut[o + 1] != to || _rayOut[o + 2] != dist || _rayOut[o + 3] != tx || _rayOut[o + 4] != ty) VerifyRayDiff++;
            _rayOut[o] = code; _rayOut[o + 1] = to; _rayOut[o + 2] = dist; _rayOut[o + 3] = tx; _rayOut[o + 4] = ty;
        }
    }

    // 縁の升 (x, y) の向き d の落ちる先。to = 落ちる先の升・-1 = 空 (落ちた水は消える)・int.MinValue = 落ちない。返り値は理由 (確認用)
    private static string Target(int x, int y, int d, int lk, out int to, out int dist, out int tx, out int ty)
    {
        if (OnGlassRail(x, y, d)) { to = -1; dist = GlassDrop; tx = x; ty = y; return "glass"; }
        return Ray(x, y, d, lk, out to, out dist, out tx, out ty);
    }

    // ガラスの手すりを除いた光線 (C++ の target と同じ)
    private static string Ray(int x, int y, int d, int lk, out int to, out int dist, out int tx, out int ty)
    {
        to = int.MinValue; dist = 0; tx = x; ty = y;
        int wall = 0, airDist = 0, airX = 0, airY = 0;
        bool walled = false;
        for (int i = 1; i <= FallScan[d]; i++)
        {
            int cx = x + Nx[d] * i, cy = y + Ny[d] * i;
            if (cx < 1 || cy < 1 || cx >= _w - 1 || cy >= _h - 1) return "border";
            int c = cy * _w + cx;
            if (_doorCell[c] != 0) return "door";
            if (i == 1 && _propCell[c] != 0 && _open[c] == 0) return "prop";
            tx = cx; ty = cy; dist = i;
            if (_open[c] == 0)
            {
                if (airDist != 0)
                {
                    // 空の先: 奈落の注釈に着いたら空へ落ちる
                    if (_lvl[c] == VoidLevel) break;
                    continue;
                }
                if (_air[c] != 0)
                {
                    // 壁の向こうの空へは落ちない (壁として数え、その先の低い床と横を向いた崖の下は探す)
                    if (wall > AirLip) { walled = true; wall++; continue; }
                    if (_lvl[c] == VoidLevel) { to = -1; return "air"; }
                    // 塗った空が崖の面や縁の帯 (奈落の注釈でない) なら、その下に見えている低い床を先に探す
                    airDist = i; airX = cx; airY = cy;
                    continue;
                }
                wall++;
                continue;
            }
            int lc = _lvl[c];
            if (lc != NoLevel && lc < lk) { to = c; return "fall"; }
            if (airDist != 0) break;
            return lc == NoLevel ? "nolevel" : "same";
        }
        if (airDist != 0 && Nx[d] == 0) { to = -1; dist = airDist; tx = airX; ty = airY; return "air"; }
        if (Nx[d] == 0) return walled ? "wall" : "scan";
        bool seen = false;
        SideAir(x, y, d, lk, ref to, ref dist, ref tx, ref ty, ref seen);
        if (airDist != 0 && to < 0) { to = -1; dist = airDist; tx = airX; ty = airY; return "air"; }
        if (to >= 0) return "side";
        if (to == -1) return "airside";
        return seen ? "same" : walled ? "wall" : "scan";
    }

    // 横を向いた崖: 真横の先が壁の中のままなら、外へ 1 列ずつずらしながら画面の下へ探し、いちばん近い低い床を選ぶ (真上から斜めに見ているので崖の下の床は斜め下に見える)。空の列は、縁の行で越えた歩けない升が AirLip 以下の列だけ (壁の帯の向こうの空へ落とさない)
    private static void SideAir(int x, int y, int d, int lk, ref int to, ref int dist, ref int tx, ref int ty, ref bool seen)
    {
        int best = int.MaxValue, wall = 0, faceBest = int.MaxValue, faceDist = 0, faceX = 0, faceY = 0;
        for (int i = 1; i <= SideScan; i++)
        {
            int cx = x + Nx[d] * i;
            if (cx < 1 || cx >= _w - 1 || i * i >= best) break;
            int row = y * _w + cx;
            if (_open[row] == 0 && _air[row] == 0) wall++;
            for (int j = 1; j <= FallScan[3] && i * i + j * j < best; j++)
            {
                int cy = y - j;
                if (cy < 1) break;
                int c = cy * _w + cx;
                if (_doorCell[c] != 0) break;
                if (_open[c] == 0)
                {
                    if (_air[c] == 0) continue;
                    if (wall > AirLip) break;
                    if (_lvl[c] != VoidLevel)
                    {
                        // 崖の面や縁の帯の空: 下に低い床が見えればそちらを先に (無ければ空へ)
                        if (i * i + j * j < faceBest) { faceBest = i * i + j * j; faceDist = j; faceX = cx; faceY = cy; }
                        continue;
                    }
                    best = i * i + j * j; to = -1; dist = j; tx = cx; ty = cy;
                    break;
                }
                seen = true;
                int lc = _lvl[c];
                if (lc != NoLevel && lc < lk) { best = i * i + j * j; to = c; dist = j; tx = cx; ty = cy; }
                break;
            }
        }
        if (to == int.MinValue && faceBest != int.MaxValue) { to = -1; dist = faceDist; tx = faceX; ty = faceY; }
    }
}
