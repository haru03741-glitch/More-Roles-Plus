using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace MoreRolesPlus.Terrain;

// 歩けないが水は届く床 (当たり判定だけで閉じた床の続き・家具の当たり判定の張り出しの下・見えない柵の向こう)。
// マップごとの表 (Resources/WaterOpen/<船>.bin) は SolidMap と同じ升のビット列で、絵の床の色が歩ける所から壁の根元 (影の線) と
// 手前の絵を越えずに続く所。升を作る時に歩ける升へ足す。表の無いマップ・升の並びが合わない時は今までどおり歩ける所だけ
internal static partial class WaterSim
{
    private static readonly byte[] WetMagic = { (byte)'M', (byte)'R', (byte)'P', (byte)'W' };
    private static byte[] _wet;                // SolidMap の升ごと: 1 = 水が届く (歩ける升は含まない)
    internal static string WetStats = "-";

    private static void LoadWet()
    {
        _wet = null;
        var ship = ShipStatus.Instance;
        if (!ship) { WetStats = "no ship"; return; }
        string name = ship.name.Replace("(Clone)", "");
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.WaterOpen." + name + ".bin");
            if (s == null) { WetStats = $"{name}: none"; return; }
            using var br = new BinaryReader(s);
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != WetMagic[0] || magic[1] != WetMagic[1] || magic[2] != WetMagic[2] || magic[3] != WetMagic[3]) { WetStats = $"{name}: bad magic"; return; }
            int ver = br.ReadByte();
            if (ver != 1) { WetStats = $"{name}: version {ver}"; return; }
            int ox16 = br.ReadInt16(), oy16 = br.ReadInt16();
            int sw = br.ReadUInt16(), sh = br.ReadUInt16(), ppu = br.ReadByte();
            int len = br.ReadInt32();
            // 升の並び (原点・大きさ・細かさ) が SolidMap と同じ時だけ使う
            int mx16 = (int)MathF.Round(SolidMap.Origin.x * 16f), my16 = (int)MathF.Round(SolidMap.Origin.y * 16f);
            if (sw != SolidMap.W || sh != SolidMap.H || ppu != (int)MathF.Round(SolidMap.Ppu) || ox16 != mx16 || oy16 != my16)
            { WetStats = $"{name}: grid {sw}x{sh}@{ox16},{oy16} != {SolidMap.W}x{SolidMap.H}@{mx16},{my16}"; return; }
            var packed = br.ReadBytes(len);
            if (packed.Length != len) { WetStats = $"{name}: short"; return; }
            int n = sw * sh;
            var bits = new byte[(n + 7) / 8];
            using (var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int got = 0, r;
                while (got < bits.Length && (r = z.Read(bits, got, bits.Length - got)) > 0) got += r;
                if (got != bits.Length) { WetStats = $"{name}: short bits"; return; }
            }
            var wet = new byte[n];
            var solid = SolidMap.OpenCells;
            int cnt = 0;
            for (int k = 0; k < n; k++)
            {
                if ((bits[k >> 3] >> (k & 7) & 1) == 0 || solid[k] != 0 || SolidMap.OutsideAt(k)) continue;
                wet[k] = 1;
                cnt++;
            }
            _wet = wet;
            WetStats = $"{name}: {cnt}";
        }
        catch (Exception e) { WetStats = $"{name}: {e.Message}"; }
    }

    // 歩ける升を含まない (水だけが届く) 升は、開いている隣から島をもらう (幅優先・升の番号の順で決まる)
    private static int SpreadWetIslands(byte[] isl, byte[] wetOnly)
    {
        if (_wet == null) return 0;
        int n = _w * _h;
        var q = new int[n];
        int head = 0, tail = 0, left = 0;
        for (int k = 0; k < n; k++)
        {
            if (_sub[k] == 0) continue;
            if (isl[k] != 0) q[tail++] = k;
            else if (wetOnly[k] != 0) left++;
        }
        while (head < tail && left > 0)
        {
            int k = q[head++];
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                if (j < 0 || j >= n || wetOnly[j] == 0 || isl[j] != 0) continue;
                isl[j] = isl[k];
                left--;
                q[tail++] = j;
            }
        }
        return left;
    }
}
