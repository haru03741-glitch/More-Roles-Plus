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

    // 展開した表 (船の名前ごとに 1 つ)。表は試合で変わらないので、試合の始めの準備 (Warm) で読んでおく
    private static string _bitsName;
    private static byte[] _bits;
    private static int _bitsOx16, _bitsOy16, _bitsW, _bitsH, _bitsPpu;
    private static string _bitsErr;

    // 焼いた時の SolidMap の升の並び (原点・幅・高さ)。左右反転の船はこの並びを鏡に映して使う
    internal static bool BakedGrid(string name, out float ox, out float oy, out int w, out int h, out int ppu)
    {
        LoadWetBits(name);
        ox = _bitsOx16 / 16f; oy = _bitsOy16 / 16f;
        w = _bitsW; h = _bitsH; ppu = _bitsPpu;
        return _bits != null;
    }

    private static void LoadWetBits(string name)
    {
        if (_bitsName == name) return;
        _bitsName = name;
        _bits = null;
        _bitsErr = null;
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.WaterOpen." + name + ".bin");
            if (s == null) { _bitsErr = "none"; return; }
            using var br = new BinaryReader(s);
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != WetMagic[0] || magic[1] != WetMagic[1] || magic[2] != WetMagic[2] || magic[3] != WetMagic[3]) { _bitsErr = "bad magic"; return; }
            int ver = br.ReadByte();
            if (ver != 1) { _bitsErr = $"version {ver}"; return; }
            _bitsOx16 = br.ReadInt16(); _bitsOy16 = br.ReadInt16();
            _bitsW = br.ReadUInt16(); _bitsH = br.ReadUInt16(); _bitsPpu = br.ReadByte();
            int len = br.ReadInt32();
            var packed = br.ReadBytes(len);
            if (packed.Length != len) { _bitsErr = "short"; return; }
            var bits = new byte[(_bitsW * _bitsH + 7) / 8];
            using (var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int got = 0, r;
                while (got < bits.Length && (r = z.Read(bits, got, bits.Length - got)) > 0) got += r;
                if (got != bits.Length) { _bitsErr = "short bits"; return; }
            }
            _bits = bits;
        }
        catch (Exception e) { _bitsErr = e.Message; }
    }

    private static void LoadWet()
    {
        _wet = null;
        var ship = ShipStatus.Instance;
        if (!ship) { WetStats = "no ship"; return; }
        string name = ship.name.Replace("(Clone)", "");
        LoadWetBits(name);
        if (_bits == null) { WetStats = $"{name}: {_bitsErr}"; return; }
        // 升の並び (原点・大きさ・細かさ) が SolidMap と同じ時だけ使う
        int mx16 = (int)MathF.Round(SolidMap.BakedOrigin.x * 16f), my16 = (int)MathF.Round(SolidMap.BakedOrigin.y * 16f);
        if (_bitsW != SolidMap.W || _bitsH != SolidMap.H || _bitsPpu != (int)MathF.Round(SolidMap.Ppu) || _bitsOx16 != mx16 || _bitsOy16 != my16)
        { WetStats = $"{name}: grid {_bitsW}x{_bitsH}@{_bitsOx16},{_bitsOy16} != {SolidMap.W}x{SolidMap.H}@{mx16},{my16}"; return; }
        // 歩ける升と船の外は除く (水が動く前の爆破で歩ける所が変わっているので、升を作る時に濾す)
        int n = _bitsW * _bitsH;
        var wet = new byte[n];
        // 読んだ格子は船の名前ごとに持ち回すので、左右反転の船では写しを裏返す (列の対応は SolidMap.MirrorSpan)
        var bits = _bits;
        if (MapFlip.Flipped) bits = MapFlip.MirrorBitRows(_bits, _bitsW, _bitsH, SolidMap.MirrorSpan);
        int cnt = WetFilterNative(bits, wet, n);
        _wet = wet;
        WetStats = $"{name}: {cnt}";
    }

    private unsafe struct WetFilterArgs
    {
        public byte* Bits, Solid, Outside;
        public int N;
        public byte* Wet;
        public int Count;
    }

    private static unsafe int WetFilterNative(byte[] bits, byte[] wet, int n)
    {
        fixed (byte* bp = bits) fixed (byte* sp = SolidMap.OpenCells) fixed (byte* op = SolidMap.OutsideCells) fixed (byte* wp = wet)
        {
            var a = new WetFilterArgs { Bits = bp, Solid = sp, Outside = op, N = n, Wet = wp };
            Native.MrpNative.WetFilter(&a);
            return a.Count;
        }
    }

}
