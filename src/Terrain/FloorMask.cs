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

    // 壁の面の画素の床からの高さ (FaceUnit 分の 1 単位・壁の面でない = 255)。並びと画素の位置は Fill と同じ。
    // 斜め上から見た絵では、床から高さ z の壁の画素は床の縁より画面で z だけ上にある。真下の床の画素までの距離がその高さ
    internal const float FaceUnit = 64f;
    private const float FaceMax = 254f / FaceUnit;

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
                    into[py * n + px] = (_face[k >> 3] & (1 << (k & 7))) != 0 && last != int.MinValue && z < scan
                        ? (byte)Math.Min(254f, z / _ppu * FaceUnit) : (byte)255;
                    any |= into[py * n + px] != 255;
                    py++;
                    next = (int)((y + py * step - _oy) * _ppu);
                }
            }
            for (; py < n; py++) into[py * n + px] = 255;
        }
        return any;
    }

    // 世界の四角 (x, y から size 四方) を n×n 画素で into へ (左下原点・床 = 255・マスクの外は床でない)。
    // 画素 0 と n - 1 は四角の端ちょうど (隣のタイルの端の画素と同じ所。シェーダは端の画素の真ん中から引く)。
    // 縁は 1, 4, 6, 4, 1 でならす: 1 画素 1 ビットの段がそのまま水の縁に出ないよう、シェーダが 128 の線を
    // 画素より細かく引ける値にする。ならしは周りの Blur 画素も読んでからなので、隣のタイルと縁がつながる
    private const int Blur = 2;
    private static byte[] _raw = Array.Empty<byte>();
    private static ushort[] _row = Array.Empty<ushort>();

    internal static void Fill(float x, float y, float size, int n, byte[] into)
    {
        int m = n + Blur * 2;
        if (_raw.Length < m * m) _raw = new byte[m * m];
        if (_row.Length < m * n) _row = new ushort[m * n];
        float step = size / (n - 1);
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

    // 床の絵が z 1〜4 にある船 (tools/make-floor-mask.py の BACK_Z_SHIP と同じ表)。他は季節の飾り (z 1.07) を除くため 4
    private static float BackZ(string ship) => ship is "PolusShip" or "FungleShip" ? 0.5f : 4f;

    private static uint Signature(string ship)
    {
        float back = BackZ(ship);
        var keys = new HashSet<string>();
        foreach (var sr in DamageMap.RoomArts)
        {
            var sp = sr ? sr.sprite : null;
            if (!sp) continue;
            float z = sr.transform.position.z;
            if (z < back || z >= SkyZ) continue;
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
        TestBridge.Register("floorexport", "水の床マスクを作る道具用に、部屋の絵の配置と歩ける所の地図を Screens/floor_<船>.json へ書き出す", (_, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship || !DamageMap.Ready() || !SolidMap.Ensure()) { reply("ERR floorexport not ready"); return; }
            var sb = new StringBuilder("{\n  \"ship\": \"").Append(ship.name).Append("\",\n  \"sig\": ").Append(Signature(ship.name.Replace("(Clone)", ""))).Append(",\n  \"rooms\": [\n");
            int n = 0, skipped = 0;
            var arts = DamageMap.RoomArts;
            for (int i = 0; i < arts.Count; i++)
            {
                var sr = arts[i];
                if (!sr) continue;
                var sp = sr.sprite;
                var tex = sp ? sp.texture : null;
                if (!tex || (sp.packed && (sp.packingMode == SpritePackingMode.Tight || sp.packingRotation != SpritePackingRotation.None)))
                {
                    skipped++;
                    continue;
                }
                Rect tr = sp.textureRect;
                Vector2 off = sp.textureRectOffset, pv = sp.pivot;
                float ppu = sp.pixelsPerUnit;
                bool fx = sr.flipX, fy = sr.flipY;
                var t = sr.transform;
                Vector3 World(float tx, float ty)
                {
                    float lx = (tx - tr.x + off.x - pv.x) / ppu, ly = (ty - tr.y + off.y - pv.y) / ppu;
                    if (fx) lx = -lx;
                    if (fy) ly = -ly;
                    return t.TransformPoint(new Vector3(lx, ly, 0f));
                }
                const float span = 1000f;
                Vector3 w0 = World(0f, 0f), wx = World(span, 0f), wy = World(0f, span);
                if (MathF.Abs(wx.y - w0.y) > 0.01f || MathF.Abs(wy.x - w0.x) > 0.01f) { skipped++; continue; }
                sb.Append(n == 0 ? "" : ",\n").Append("    {\"sprite\": \"").Append(sp.name).Append("\", \"texture\": \"").Append(tex.name)
                  .Append("\", \"texSize\": [").Append(tex.width).Append(", ").Append(tex.height)
                  .Append("], \"texRect\": [").Append(F(tr.xMin)).Append(", ").Append(F(tr.yMin)).Append(", ").Append(F(tr.width)).Append(", ").Append(F(tr.height))
                  .Append("], \"w0\": [").Append(F(w0.x)).Append(", ").Append(F(w0.y))
                  .Append("], \"d\": [").Append(F((wx.x - w0.x) / span)).Append(", ").Append(F((wy.y - w0.y) / span))
                  .Append("], \"z\": ").Append(F(w0.z)).Append(", \"order\": ").Append(sr.sortingOrder).Append(", \"tris\": [");
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
                n++;
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
            sb.Append("\n  ]\n}\n");
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"floor_{ship.name}.json");
            System.IO.File.WriteAllText(path, sb.ToString());
            reply($"OK floorexport mask={Stats} rooms={n} skipped={skipped} solid={SolidMap.W}x{SolidMap.H} furniture={cols.Count} -> {path}");
        });
    }

    private static string F(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}
