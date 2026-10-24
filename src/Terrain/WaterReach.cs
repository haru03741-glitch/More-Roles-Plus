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

    // 集めた縁の光線をまとめて C++ で引く
    private static unsafe void RayRun(bool useZ)
    {
        if (_rayOut.Length < _rayN * 5) _rayOut = new int[Math.Max(_rayQ.Length * 5, _rayN * 5)];
        fixed (int* q = _rayQ) fixed (int* o = _rayOut) RayCall(q, o, _rayN, useZ);
    }

    // 光線は C++ だけが引く (水の計算も確認のコマンドも同じ関数と同じルール)。q = 升 × 4 + 向き・o = 1 本につき 理由/行き先/距離/x/y
    private static unsafe void RayCall(int* q, int* o, int n, bool useZ)
    {
        fixed (byte* open = _open) fixed (byte* door = _doorCell) fixed (byte* prop = _propCell) fixed (byte* air = _air)
        fixed (sbyte* lvl = _lvl) fixed (byte* isl = _cellIsl) fixed (int* islBase = _islBase)
        {
            var a = new RayArgs
            {
                Open = open, Door = door, Prop = prop, Air = air, Lvl = lvl, Isl = isl, IslBase = islBase,
                Q = q, Out = o, N = n, W = _w, H = _h, IslN = _islBase.Length,
                UseZ = useZ && isl != null ? 1 : 0, SideScan = SideScan, AirLip = AirLip,
            };
            for (int d = 0; d < 4; d++) a.FallScan[d] = FallScan[d];
            Native.MrpNative.FallRay(&a);
        }
    }

    // 縁の升 (x, y) の向き d の落ちる先。to = 落ちる先の升・-1 = 空 (落ちた水は消える)・int.MinValue = 落ちない。返り値は理由 (確認用)
    private static unsafe string Target(int x, int y, int d, out int to, out int dist, out int tx, out int ty)
    {
        if (OnGlassRail(x, y, d)) { to = -1; dist = GlassDrop; tx = x; ty = y; return "glass"; }
        int key = (y * _w + x) * 4 + d;
        int* o = stackalloc int[5];
        RayCall(&key, o, 1, ZFlow);
        to = o[1]; dist = o[2]; tx = o[3]; ty = o[4];
        return (uint)o[0] < (uint)RayWhy.Length ? RayWhy[o[0]] : "?" + o[0];
    }
}
