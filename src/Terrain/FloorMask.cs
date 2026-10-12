using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水を見せてよい床の画素の地図 (部屋の絵の画素ごとに 床 / 床でない)。
// 当たり判定の形は絵と合わない (壁は継ぎ目を塞ぐために太らせてあり、家具の当たり判定は絵より大きい・小さい) ので、
// 水の縁を部屋の絵そのものの床の画素で切る。地図は tools/make-floor-mask.py がゲームの絵から作る。
internal static class FloorMask
{
    private static readonly byte[] Magic = { (byte)'M', (byte)'R', (byte)'P', (byte)'F' };
    private const int Version = 4;
    private const float OriginTolerance = 0.01f;

    private static int _gen = -1;
    private static byte[] _bits;   // 1 画素 1 ビット (左下原点・行ごと・下位ビットから)
    private static byte[] _face;   // 壁の面 (床の真上に途切れず続く床でない絵の画素)。並びは _bits と同じ
    private static int _w, _h;
    private static float _ox, _oy, _ppu;
    internal static string Stats { get; private set; } = "none";
    internal static bool Off; // テスト用: マスクがあっても使わない (前の見え方と見比べる)

    // 試合中に床になった所 (持ち上げた家具の跡)。マスクを読み直した時にも重ねる。
    // Dry = 跡にする前のマスクで床でなかった画素 (= 家具そのもの)。家具と一緒に動かして、今いる所を床でなくする
    // (影の中では家具の絵が水より手前に来ないので、マスクで抜かないと水が家具の上に乗る)
    private sealed class Patch
    {
        public Rect World; public bool[] Shape; public int N;
        public FurnitureLift.Lift Lift;
        public byte[] Dry; public int DX0, DY0, DW, DH; // マスクの画素の範囲 (左下 DX0, DY0 から DW×DH)
    }
    private static readonly List<Patch> Patches = new();
    private static int _patchGen = -1;

    // 今の船のマスクがあるか (無い・合わない時は今までどおり当たり判定で切る)
    internal static bool Active
    {
        get
        {
            Ensure();
            return _bits != null;
        }
    }

    private static void Ensure()
    {
        int gen = GameClock.ShipGen;
        if (gen == _gen) return;
        _gen = gen;
        _bits = null;
        if (!GameClock.ShipAlive) { Stats = "no ship"; return; }
        var ship = ShipStatus.Instance;
        if (!ship) { Stats = "no ship"; return; }
        // 部屋の絵の指紋を取るには部屋の絵の一覧が要る。揃うまでは毎回やり直す (ファイルはまだ開かない)
        if (!DamageMap.Ready()) { _gen = -1; Stats = "waiting"; return; }
        string name = ship.name.Replace("(Clone)", "");
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.FloorMasks." + name + ".bin");
            if (s == null) { Stats = $"{name}: none"; return; }
            using var br = new BinaryReader(s);
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3]) { Stats = $"{name}: bad magic"; return; }
            int ver = br.ReadInt32();
            int w = br.ReadInt32(), h = br.ReadInt32();
            if (ver != Version) { Stats = $"{name}: version {ver}"; return; }
            float ox = br.ReadSingle(), oy = br.ReadSingle(), ppu = br.ReadSingle();
            uint sig = br.ReadUInt32();
            // 部屋の絵が作った時と違う (ゲームの更新で絵が変わった) なら使わない
            uint now = Signature(name);
            if (sig != now) { Stats = $"{name}: art changed {sig:x8}/{now:x8}"; return; }
            if (!SolidMap.Ensure() || MathF.Abs(SolidMap.Origin.x - ox) > OriginTolerance || MathF.Abs(SolidMap.Origin.y - oy) > OriginTolerance)
            {
                Stats = $"{name}: origin mismatch";
                return;
            }
            int lenBits = br.ReadInt32(), lenFace = br.ReadInt32();
            var bits = new byte[(w * h + 7) / 8];
            var face = new byte[bits.Length];
            if (!Inflate(br.ReadBytes(lenBits), bits) || !Inflate(br.ReadBytes(lenFace), face)) { Stats = $"{name}: short"; return; }
            _bits = bits;
            _face = face;
            _w = w; _h = h; _ox = ox; _oy = oy; _ppu = ppu;
            if (_patchGen == gen) foreach (var p in Patches) Apply(p);
            Stats = $"{name}: {w}x{h} ppu={ppu:0.##}";
        }
        catch (Exception e)
        {
            Stats = $"{name}: {e.GetType().Name}";
            Plugin.Logger.LogError($"floor mask load failed: {e}");
        }
    }

    private static bool Inflate(byte[] src, byte[] into)
    {
        using var z = new ZLibStream(new MemoryStream(src), CompressionMode.Decompress);
        int read = 0;
        while (read < into.Length)
        {
            int n = z.Read(into, read, into.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == into.Length;
    }

    // 壁の面の画素の床からの高さ (FaceUnit 分の 1 単位・0..FaceTop)。床 = FaceFloor・どちらでもない = 255。並びと画素の位置は Fill と同じ。
    // 斜め上から見た絵では、床から高さ z の壁の画素は床の縁より画面で z だけ上にある。真下の床の画素までの距離がその高さ
    internal const float FaceUnit = 64f;
    internal const byte FaceTop = 253, FaceFloor = 254;
    private const float FaceMax = FaceTop / FaceUnit;

    internal static bool FaceHeights(float x, float y, float size, int n, byte[] into)
    {
        Ensure();
        if (_bits == null) return false;
        float step = size / (n - 1);
        int scan = (int)(FaceMax * _ppu) + 1;
        int top = (int)((y + size - _oy) * _ppu);
        bool any = false;
        for (int px = 0; px < n; px++)
        {
            int mx = (int)((x + px * step - _ox) * _ppu);
            int py = 0;
            if (mx < 0 || mx >= _w)
            {
                for (; py < n; py++) into[py * n + px] = 255;
                continue;
            }
            // 下から上へ 1 行ずつ、いちばん近い床の行を覚えながら、タイルの画素の行に来たら高さを書く
            int last = int.MinValue;
            int my = Math.Max(0, (int)((y - _oy) * _ppu) - scan);
            int next = (int)((y - _oy) * _ppu);
            for (; my <= top && my < _h && py < n; my++)
            {
                int k = my * _w + mx;
                if ((_bits[k >> 3] & (1 << (k & 7))) != 0) last = my;
                while (py < n && my == next)
                {
                    int z = my - last;
                    byte v = last == my ? FaceFloor
                        : (_face[k >> 3] & (1 << (k & 7))) != 0 && last != int.MinValue && z < scan ? (byte)Math.Min(FaceTop, z / _ppu * FaceUnit) : (byte)255;
                    into[py * n + px] = v;
                    any |= v <= FaceTop;
                    py++;
                    next = (int)((y + py * step - _oy) * _ppu);
                }
            }
            for (; py < n; py++) into[py * n + px] = 255;
        }
        return any;
    }

    // 世界の四角 (x, y から size 四方) の画素が全部床か (マスクの外は床でない)
    internal static bool AllFloor(float x, float y, float size)
    {
        Ensure();
        if (_bits == null) return false;
        int x0 = (int)MathF.Floor((x - _ox) * _ppu), y0 = (int)MathF.Floor((y - _oy) * _ppu);
        int x1 = (int)MathF.Ceiling((x + size - _ox) * _ppu), y1 = (int)MathF.Ceiling((y + size - _oy) * _ppu);
        if (x0 < 0 || y0 < 0 || x1 > _w || y1 > _h) return false;
        for (int my = y0; my < y1; my++)
        for (int mx = x0, k = my * _w + x0; mx < x1; mx++, k++)
            if ((_bits[k >> 3] & (1 << (k & 7))) == 0) return false;
        return true;
    }

    // 世界の四角 (x, y から size 四方) を n×n 画素で into へ (左下原点・床 = 255・マスクの外は床でない)。
    // 画素 0 と n - 1 は四角の端ちょうど (隣のタイルの端の画素と同じ所。シェーダは端の画素の真ん中から引く)。
    // 縁は 1, 4, 6, 4, 1 でならす: 1 画素 1 ビットの段がそのまま水の縁に出ないよう、シェーダが 128 の線を
    // 画素より細かく引ける値にする。ならしは周りの Blur 画素も読んでからなので、隣のタイルと縁がつながる
    private const int Blur = 2;
    private static byte[] _raw = Array.Empty<byte>();
    private static ushort[] _row = Array.Empty<ushort>();

    internal static unsafe void Fill(float x, float y, float size, int n, byte[] into)
    {
        int m = n + Blur * 2;
        if (_raw.Length < m * m) _raw = new byte[m * m];
        if (_row.Length < m * n) _row = new ushort[m * n];
        float step = size / (n - 1);
        if (Native.MrpNative.On)
        {
            FillRawNative(x - step * Blur, y - step * Blur, step, m, _raw);
            fixed (byte* raw = _raw) fixed (ushort* row = _row) fixed (byte* o = into) Native.MrpNative.FloorBlur(raw, n, row, o);
            return;
        }
        FillRaw(x - step * Blur, y - step * Blur, step, m, _raw);
        // 横にならす (m 行 × n 列)
        for (int py = 0; py < m; py++)
        {
            int src = py * m, dst = py * n;
            for (int px = 0; px < n; px++)
            {
                int k = src + px;
                _row[dst + px] = (ushort)(_raw[k] + _raw[k + 4] + ((_raw[k + 1] + _raw[k + 3]) << 2) + _raw[k + 2] * 6);
            }
        }
        // 縦にならす (n 行 × n 列)。重みの和は 16 × 16
        for (int py = 0; py < n; py++)
        {
            int dst = py * n;
            for (int px = 0; px < n; px++)
            {
                int k = py * n + px;
                int v = _row[k] + _row[k + n * 4] + ((_row[k + n] + _row[k + n * 3]) << 2) + _row[k + n * 2] * 6;
                into[dst + px] = (byte)((v + 128) >> 8);
            }
        }
    }

    // 壁の絵が抜けた所 (穴の値がこれ以上) は床にする。シェーダは穴の値を割れ口のずらし込みで 128 前後で切って絵を抜く
    private const int HoleFloor = 128;

    // FillRaw の床の地図と穴を C++ で (同じ結果)。動く床・手すり・持ち上げた家具はそのあと C# で
    private static unsafe void FillRawNative(float x, float y, float wstep, int n, byte[] into)
    {
        bool holes = DamageMap.HoleMap(out var hp, out int hw, out int hh, out float hox, out float hoy, out float hppu) && DamageMap.CurrentGen != 0;
        fixed (byte* bits = _bits) fixed (byte* o = into) fixed (byte* h = hp)
            Native.MrpNative.FloorRaw(bits, _w, _h, _ox, _oy, _ppu, x, y, wstep, n, o, holes ? h : null, hw, hh, hox, hoy, hppu, HoleFloor);
        if (WaterSim.LipMaps(out var fall, out var lip, out int lw, out int lh, out float lox, out float loy, out float cell))
            fixed (byte* f = fall) fixed (byte* l = lip) fixed (byte* o = into)
                Native.MrpNative.FloorLips(f, l, lw, lh, lox, loy, cell, x, y, wstep, n, o);
        // 床にするだけの書き込み (縁・動く床) は順番に依らない。床でなくする持ち上げた家具は最後
        WaterSim.StampPlatform(x, y, wstep, n, into);
        CutAllMoved(x, y, wstep, n, into);
    }

    // 画素 k の真ん中 = (x + k × step, y + …)
    private static void FillRaw(float x, float y, float wstep, int n, byte[] into)
    {
        float step = wstep * _ppu;
        float bx = (x - _ox) * _ppu, by = (y - _oy) * _ppu;
        for (int py = 0; py < n; py++)
        {
            int my = (int)(by + py * step);
            int row = py * n;
            if (my < 0 || my >= _h)
            {
                Array.Clear(into, row, n);
                continue;
            }
            int baseK = my * _w;
            for (int px = 0; px < n; px++)
            {
                int mx = (int)(bx + px * step);
                into[row + px] = mx >= 0 && mx < _w && (_bits[(baseK + mx) >> 3] & (1 << ((baseK + mx) & 7))) != 0 ? (byte)255 : (byte)0;
            }
        }
        // 壊した壁の穴: 水の升は開いて水が流れ込むので、絵が抜けた所も床にする (この試合でまだ何も壊していなければ飛ばす)
        if (DamageMap.CurrentGen != 0)
        {
            for (int py = 0; py < n; py++)
            {
                float wy = y + py * wstep;
                int row = py * n;
                for (int px = 0; px < n; px++)
                    if (into[row + px] == 0 && DamageMap.HoleSmoothAt(x + px * wstep, wy) >= HoleFloor) into[row + px] = 255;
            }
        }
        // 動く床 (昇降機の台) の今の所
        WaterSim.StampPlatform(x, y, wstep, n, into);
        WaterSim.StampLips(x, y, wstep, n, into);
        CutAllMoved(x, y, wstep, n, into);
    }

    private static void CutAllMoved(float x, float y, float wstep, int n, byte[] into)
    {
        if (_patchGen != GameClock.ShipGen) return;
        foreach (var p in Patches) if (p.Lift != null && p.Dry != null) CutMoved(p, x, y, wstep, n, into);
    }

    // 持ち上げた家具の今いる所 (元の所から回して動かした所) を床でなくする。画素 k の真ん中 = x + k × step
    private static void CutMoved(Patch p, float x, float y, float step, int n, byte[] into)
    {
        var l = p.Lift;
        float px0 = l.PivotX, py0 = l.PivotY, cx = l.PoseX, cy = l.PoseY;
        float rad = -l.PoseAng * (MathF.PI / 180f), cs = MathF.Cos(rad), sn = MathF.Sin(rad);
        // 動いた先の範囲 (回しても収まるよう元の範囲の対角線の半分で広げる)
        var r = p.World;
        float hx = r.width * 0.5f, hy = r.height * 0.5f, half = MathF.Sqrt(hx * hx + hy * hy);
        float rcx = r.xMin + hx - px0, rcy = r.yMin + hy - py0;
        float mcx = cx + rcx * MathF.Cos(-rad) - rcy * MathF.Sin(-rad), mcy = cy + rcx * MathF.Sin(-rad) + rcy * MathF.Cos(-rad);
        float size = step * (n - 1);
        if (mcx + half <= x || mcx - half >= x + size || mcy + half <= y || mcy - half >= y + size) return;
        for (int py = 0; py < n; py++)
        {
            float wy = y + py * step - cy;
            if (wy < mcy - cy - half || wy > mcy - cy + half) continue;
            for (int px = 0; px < n; px++)
            {
                int i = py * n + px;
                if (into[i] == 0) continue;
                float wx = x + px * step - cx;
                // 今の所から元の所へ戻す
                float ox = px0 + wx * cs - wy * sn, oy = py0 + wx * sn + wy * cs;
                int dx = (int)MathF.Floor((ox - _ox) * _ppu) - p.DX0, dy = (int)MathF.Floor((oy - _oy) * _ppu) - p.DY0;
                if (dx < 0 || dy < 0 || dx >= p.DW || dy >= p.DH) continue;
                int d = dy * p.DW + dx;
                if ((p.Dry[d >> 3] & (1 << (d & 7))) != 0) into[i] = 0;
            }
        }
    }

    internal static float Ppu => _ppu;

    // 世界の四角 world を N×N に割った形 shape (左下原点・true = 床にする) の所を床にする (家具を持ち上げて跡が床になった)
    internal static void AddFloor(Rect world, bool[] shape, int n, FurnitureLift.Lift lift)
    {
        int gen = GameClock.ShipGen;
        if (_patchGen != gen) { Patches.Clear(); _patchGen = gen; }
        var p = new Patch { World = world, Shape = shape, N = n, Lift = lift };
        Patches.Add(p);
        if (_bits != null && _gen == gen) Apply(p);
    }

    private static void Apply(Patch p)
    {
        var r = p.World;
        int x0 = Math.Max(0, (int)MathF.Floor((r.xMin - _ox) * _ppu)), x1 = Math.Min(_w - 1, (int)MathF.Ceiling((r.xMax - _ox) * _ppu));
        int y0 = Math.Max(0, (int)MathF.Floor((r.yMin - _oy) * _ppu)), y1 = Math.Min(_h - 1, (int)MathF.Ceiling((r.yMax - _oy) * _ppu));
        float sx = p.N / r.width, sy = p.N / r.height;
        p.DX0 = x0; p.DY0 = y0; p.DW = Math.Max(0, x1 - x0 + 1); p.DH = Math.Max(0, y1 - y0 + 1);
        p.Dry = new byte[(p.DW * p.DH + 7) / 8];
        for (int my = y0; my <= y1; my++)
        {
            int sy0 = (int)MathF.Floor(((my + 0.5f) / _ppu + _oy - r.yMin) * sy);
            if (sy0 < 0 || sy0 >= p.N) continue;
            for (int mx = x0; mx <= x1; mx++)
            {
                int sx0 = (int)MathF.Floor(((mx + 0.5f) / _ppu + _ox - r.xMin) * sx);
                if (sx0 < 0 || sx0 >= p.N || !p.Shape[sy0 * p.N + sx0]) continue;
                int k = my * _w + mx;
                if ((_bits[k >> 3] & (1 << (k & 7))) == 0)
                {
                    int d = (my - y0) * p.DW + (mx - x0);
                    p.Dry[d >> 3] |= (byte)(1 << (d & 7));
                }
                _bits[k >> 3] |= (byte)(1 << (k & 7));
            }
        }
    }

    // 部屋の絵のテクスチャ (名前・大きさ) の指紋。マスクを作った時と今のゲームの絵が同じかを見る。
    // 床の層 (z が BackZ 以上・空の絵より手前) だけを見る: 手前にはコマ送りで絵が替わる物 (機関室のエンジンなど) があり、
    // 床の層にもコマ送りの絵 (ファングルの原子炉) があるので、コマ送りの絵は外し、残りもテクスチャ単位で数える
    private const float SkyZ = 20f;
    private const int ShadowLayers = (1 << 10) | (1 << 11) | (1 << 13); // 視界を遮る層 (本編の Constants.ShadowMask と同じ)

    // 床の絵が z 1〜4 にある船 (tools/make-floor-mask.py の BACK_Z_SHIP と同じ表)。他は季節の飾り (z 1.07) を除くため 4
    private static float BackZ(string ship) => ship is "PolusShip" or "FungleShip" ? 0.5f : 4f;

    // z に関わらず床の層に入れる部屋の絵 (ポーラスの武器庫と通信室は床と壁が 1 枚の絵で、クルーと同じ奥行きにある)
    private static bool AlwaysFloor(string sprite) => sprite is "room_weapon" or "room_broadcast";

    internal static uint Signature(string ship)
    {
        float back = BackZ(ship);
        var keys = new HashSet<string>();
        foreach (var sr in DamageMap.RoomArts)
        {
            var sp = sr ? sr.sprite : null;
            if (!sp) continue;
            float z = sr.transform.position.z;
            if ((z < back && !AlwaysFloor(sp.name)) || z >= SkyZ) continue;
            // コマ送りの絵はテクスチャ (シート) ごと替わることがある
            if (sr.GetComponent<PowerTools.SpriteAnim>() || sr.GetComponent<Animator>()) continue;
            var tex = sp.texture;
            if (tex) keys.Add($"{tex.name}|{tex.width}|{tex.height}");
        }
        var list = new List<string>(keys);
        list.Sort(string.CompareOrdinal);
        uint h = 2166136261;
        foreach (var k in list)
            foreach (byte b in Encoding.UTF8.GetBytes(k + ";"))
                h = (h ^ b) * 16777619;
        return h;
    }

    // マスクを作る道具 (tools/make-floor-mask.py) へ: 部屋の絵ごとの名前・絵の画素と世界座標の対応・前後 (z)、
    // 歩ける所の地図 (SolidMap)、家具の当たり判定を Screens/floor_<船>.json へ
    internal static void Register()
    {
        TestBridge.Register("floormask", "[on|off] 水の床マスク (部屋の絵の床の画素で水を切る) の状態 / テスト用に切り替える (次に描き直したタイルから)", (args, reply) =>
        {
            string a = args.Trim();
            if (a.Length > 0)
            {
                Off = a == "off";
                WaterArt.FloorMaskToggled();
            }
            reply($"OK floormask active={Active} off={Off} {Stats}");
        });
        TestBridge.Register("native", "[on|off | check [x y] | verify [on|off] | async [on|off]] C++ の部品の状態 / 切り替え / 床マスク 1 タイルを C# と C++ で作って 1 バイトずつ比べる (既定は自分の所) / verify on = 水の絵を描くたびに C# でも作って違いを数える / async off = 水の小刻みをその場で回す (既定は作業スレッド)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool on = Native.MrpNative.On;
            if (a.Length > 0 && (a[0] == "on" || a[0] == "off")) { Native.MrpNative.Enabled = a[0] == "on"; reply($"OK native on={Native.MrpNative.On} {Native.MrpNative.Status}"); return; }
            if (a.Length > 0 && a[0] == "async")
            {
                TerrainStep.Settle();
                if (a.Length > 1) Native.MrpNative.Async = a[1] == "on";
                reply($"OK native async={Native.MrpNative.Async}");
                return;
            }
            if (a.Length > 0 && a[0] == "verify")
            {
                if (a.Length > 1) { Native.MrpNative.Verify = a[1] == "on"; WaterArt.VerifyTiles = WaterArt.VerifyDiff = 0; WaterArt.VerifyRaws = WaterArt.VerifyRawDiff = 0; WaterSim.VerifySteps = WaterSim.VerifyDiff = 0; DamageMap.VerifyStamps = DamageMap.VerifyStampDiff = 0; WaterSim.VerifyRays = WaterSim.VerifyRayDiff = 0; WaterSim.VerifyCells = WaterSim.VerifyCellDiff = 0; WaterSim.VerifyScans = WaterSim.VerifyScanDiff = 0; }
                reply($"OK native verify={Native.MrpNative.Verify} waterTiles={WaterArt.VerifyTiles} waterDiffBytes={WaterArt.VerifyDiff} raws={WaterArt.VerifyRaws} rawDiff={WaterArt.VerifyRawDiff} simSteps={WaterSim.VerifySteps} simDiff={WaterSim.VerifyDiff} sleepViol={WaterSim.VerifySleepViol} stamps={DamageMap.VerifyStamps} stampDiff={DamageMap.VerifyStampDiff} carves={SolidMap.VerifyCarves} carveDiff={SolidMap.VerifyCarveDiff} pieces={BreakPieces.VerifySpawns} pieceDiff={BreakPieces.VerifyDiff} rays={WaterSim.VerifyRays} rayDiff={WaterSim.VerifyRayDiff} cells={WaterSim.VerifyCells} cellDiff={WaterSim.VerifyCellDiff} scans={WaterSim.VerifyScans} scanDiff={WaterSim.VerifyScanDiff}");
                return;
            }
            if (a.Length == 0 || a[0] != "check") { reply($"OK native on={on} {Native.MrpNative.Status}"); return; }
            if (!Active) { reply("ERR native check: floor mask not active"); return; }
            float cx, cy;
            if (a.Length >= 3) { cx = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); cy = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); }
            else { var lp = PlayerControl.LocalPlayer; if (!lp) { reply("ERR native check: no local player"); return; } var p = lp.GetTruePosition(); cx = p.x; cy = p.y; }
            float size = WaterSim.TileCells * WaterSim.Cell;
            int n = (int)MathF.Round(size * _ppu);
            var cs = new byte[n * n];
            var nat = new byte[n * n];
            bool keep = Native.MrpNative.Enabled;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Native.MrpNative.Enabled = false;
            Fill(cx - size / 2, cy - size / 2, size, n, cs);
            double msCs = sw.Elapsed.TotalMilliseconds;
            Native.MrpNative.Enabled = true;
            sw.Restart();
            Fill(cx - size / 2, cy - size / 2, size, n, nat);
            double msNat = sw.Elapsed.TotalMilliseconds;
            Native.MrpNative.Enabled = keep;
            int diff = 0, first = -1, floor = 0;
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i] != nat[i]) { diff++; if (first < 0) first = i; }
                if (cs[i] >= 128) floor++;
            }
            reply($"OK native check native={on} n={n} diff={diff} first={first} floorPx={floor} cs={msCs:F2}ms native={msNat:F2}ms holes={DamageMap.CurrentGen != 0}");
        });
        TestBridge.Register("floorexport", "水の床マスクを作る道具用に、部屋の絵の配置と歩ける所の地図を Screens/floor_<船>.json へ書き出す", (_, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship || !DamageMap.Ready() || !SolidMap.Ensure()) { reply("ERR floorexport not ready"); return; }
            var sb = new StringBuilder("{\n  \"ship\": \"").Append(ship.name).Append("\",\n  \"sig\": ").Append(Signature(ship.name.Replace("(Clone)", ""))).Append(",\n  \"rooms\": [\n");
            int n = 0, skipped = 0;
            var arts = DamageMap.RoomArts;
            var roomIds = new HashSet<int>();
            for (int i = 0; i < arts.Count; i++)
            {
                var sr = arts[i];
                if (!sr) continue;
                roomIds.Add(sr.GetInstanceID());
                if (WriteArt(sb, sr, n == 0, true)) n++;
                else skipped++;
            }
            // 床の上に置かれた絵 (家具・木・岩・屋外の茂みの塊・床より手前の部屋の絵)。水はこの絵の形の際まで見せ、絵の下には見せない
            // (影の中では写しの水がこの絵より手前に出る)。屋外の茂みの塊は部屋の絵の一覧に無く、床と同じ奥行きにある。
            // 動く物 (流される小物・押される家具・扉) と通気口 (水の下へ沈める) は入れない
            sb.Append("\n  ],\n  \"covers\": [\n");
            string shipName = ship.name.Replace("(Clone)", "");
            float back = BackZ(shipName);
            int covers = 0;
            foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
            {
                if (!sr || !sr.enabled || !sr.sprite || sr.color.a < 0.25f) continue;
                float z = sr.transform.position.z;
                if ((z >= back && roomIds.Contains(sr.GetInstanceID())) || z >= SkyZ || sr.name.StartsWith("Mrp", StringComparison.Ordinal)) continue;
                var bs = sr.bounds.size;
                if (bs.x > 20f || bs.y > 20f) continue;
                if (PropSim.MovableSprite(sr.sprite.name) || FurnitureKinds.TryGet(sr.transform, shipName, out FurnitureKind _)) continue;
                if (sr.GetComponentInParent<OpenableDoor>() || sr.GetComponentInParent<Vent>()) continue;
                if (WriteArt(sb, sr, covers == 0, false)) covers++;
            }
            sb.Append("\n  ],\n  \"solid\": {\"w\": ").Append(SolidMap.W).Append(", \"h\": ").Append(SolidMap.H)
              .Append(", \"origin\": [").Append(F(SolidMap.Origin.x)).Append(", ").Append(F(SolidMap.Origin.y))
              .Append("], \"ppu\": ").Append(F(SolidMap.Ppu)).Append(", \"open\": \"");
            int cells = SolidMap.W * SolidMap.H;
            var bits = new byte[(cells + 7) / 8];
            for (int k = 0; k < cells; k++)
                if (SolidMap.OpenCell(k)) bits[k >> 3] |= (byte)(1 << (k & 7));
            sb.Append(Convert.ToBase64String(bits)).Append("\"},\n  \"furniture\": [\n");
            var cols = new List<Collider2D>();
            var segs = new List<Vector2>();
            DamageMap.FurnitureColliders(new Vector2(SolidMap.Origin.x + SolidMap.W / SolidMap.Ppu * 0.5f, SolidMap.Origin.y + SolidMap.H / SolidMap.Ppu * 0.5f),
                Math.Max(SolidMap.W, SolidMap.H) / SolidMap.Ppu, cols);
            for (int i = 0; i < cols.Count; i++)
            {
                segs.Clear();
                SolidMap.Segments(cols[i], segs);
                sb.Append(i == 0 ? "" : ",\n").Append("    {\"name\": \"").Append(cols[i].name).Append("\", \"segs\": [");
                for (int k = 0; k < segs.Count; k++)
                    sb.Append(k == 0 ? "" : ", ").Append('[').Append(F(segs[k].x)).Append(", ").Append(F(segs[k].y)).Append(']');
                sb.Append("]}");
            }
            // 視界を遮る影の線 (壁の根元)。水が物理的に届く所は、歩ける所からこの線を越えずに塗る。段の縁 (Ledge) は水が落ちる所なので入れない
            sb.Append("\n  ],\n  \"shadows\": [\n");
            int shadows = 0;
            foreach (var c in ship.GetComponentsInChildren<Collider2D>(true))
            {
                if (!c || c.isTrigger || ((1 << c.gameObject.layer) & ShadowLayers) == 0) continue;
                if (c.name.Contains("Ledge", StringComparison.OrdinalIgnoreCase)) continue;
                segs.Clear();
                SolidMap.Segments(c, segs);
                for (int k = 0; k + 1 < segs.Count; k += 2)
                {
                    sb.Append(shadows == 0 ? "    " : ", ").Append('[').Append(F(segs[k].x)).Append(", ").Append(F(segs[k].y)).Append(", ")
                      .Append(F(segs[k + 1].x)).Append(", ").Append(F(segs[k + 1].y)).Append(']');
                    shadows++;
                }
            }
            sb.Append("\n  ]\n}\n");
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"floor_{ship.name}.json");
            System.IO.File.WriteAllText(path, sb.ToString());
            reply($"OK floorexport mask={Stats} rooms={n} skipped={skipped} covers={covers} solid={SolidMap.W}x{SolidMap.H} furniture={cols.Count} shadows={shadows} -> {path}");
        });
    }

    // 絵 1 枚の配置 (テクスチャの四角・世界への写し方・絵が描かれる三角形)。部屋の絵は、詰め方が密・回転の物は画素を引けないので書かない。
    // 手前の絵は三角形だけでも形になるので、画素を引けない物はテクスチャ無しで書く
    private static bool WriteArt(StringBuilder sb, SpriteRenderer sr, bool first, bool needTex)
    {
        var sp = sr.sprite;
        var tex = sp ? sp.texture : null;
        bool texOk = tex && !(sp.packed && (sp.packingMode == SpritePackingMode.Tight || sp.packingRotation != SpritePackingRotation.None));
        if (!sp || (needTex && !texOk)) return false;
        bool fx = sr.flipX, fy = sr.flipY;
        var t = sr.transform;
        string place = null;
        if (texOk)
        {
            Rect tr = sp.textureRect;
            Vector2 off = sp.textureRectOffset, pv = sp.pivot;
            float ppu = sp.pixelsPerUnit;
            Vector3 World(float tx, float ty)
            {
                float lx = (tx - tr.x + off.x - pv.x) / ppu, ly = (ty - tr.y + off.y - pv.y) / ppu;
                if (fx) lx = -lx;
                if (fy) ly = -ly;
                return t.TransformPoint(new Vector3(lx, ly, 0f));
            }
            const float span = 1000f;
            Vector3 w0 = World(0f, 0f), wx = World(span, 0f), wy = World(0f, span);
            if (MathF.Abs(wx.y - w0.y) <= 0.01f && MathF.Abs(wy.x - w0.x) <= 0.01f)
                place = new StringBuilder(", \"texture\": \"").Append(tex.name)
                  .Append("\", \"texSize\": [").Append(tex.width).Append(", ").Append(tex.height)
                  .Append("], \"texRect\": [").Append(F(tr.xMin)).Append(", ").Append(F(tr.yMin)).Append(", ").Append(F(tr.width)).Append(", ").Append(F(tr.height))
                  .Append("], \"w0\": [").Append(F(w0.x)).Append(", ").Append(F(w0.y))
                  .Append("], \"d\": [").Append(F((wx.x - w0.x) / span)).Append(", ").Append(F((wy.y - w0.y) / span)).Append(']').ToString();
        }
        if (needTex && place == null) return false;
        sb.Append(first ? "" : ",\n").Append("    {\"sprite\": \"").Append(sp.name).Append('"').Append(place)
          .Append(", \"z\": ").Append(F(t.position.z)).Append(", \"order\": ").Append(sr.sortingOrder).Append(", \"tris\": [");
        // 絵が実際に描かれる三角形 (世界座標)。テクスチャの四角の中でも三角形の外はアトラスの隣の絵なので描かない
        var vs = sp.vertices;
        var ts = sp.triangles;
        for (int k = 0; k < ts.Length; k++)
        {
            var v = vs[ts[k]];
            var p = t.TransformPoint(new Vector3(fx ? -v.x : v.x, fy ? -v.y : v.y, 0f));
            sb.Append(k == 0 ? "" : ", ").Append(F(p.x)).Append(", ").Append(F(p.y));
        }
        sb.Append("]}");
        return true;
    }

    private static string F(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}
