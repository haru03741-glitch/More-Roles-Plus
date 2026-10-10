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
    private const int Version = 2;
    private const float OriginTolerance = 0.01f;

    private static int _gen = -1;
    private static byte[] _bits;   // 1 画素 1 ビット (左下原点・行ごと・下位ビットから)
    private static int _w, _h;
    private static float _ox, _oy, _ppu;
    internal static string Stats { get; private set; } = "none";
    internal static bool Off; // テスト用: マスクがあっても使わない (前の見え方と見比べる)

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
            uint now = Signature();
            if (sig != now) { Stats = $"{name}: art changed {sig:x8}/{now:x8}"; return; }
            if (!SolidMap.Ensure() || MathF.Abs(SolidMap.Origin.x - ox) > OriginTolerance || MathF.Abs(SolidMap.Origin.y - oy) > OriginTolerance)
            {
                Stats = $"{name}: origin mismatch";
                return;
            }
            var bits = new byte[(w * h + 7) / 8];
            using (var z = new ZLibStream(s, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < bits.Length)
                {
                    int n = z.Read(bits, read, bits.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != bits.Length) { Stats = $"{name}: short {read}/{bits.Length}"; return; }
            }
            _bits = bits;
            _w = w; _h = h; _ox = ox; _oy = oy; _ppu = ppu;
            Stats = $"{name}: {w}x{h} ppu={ppu:0.##}";
        }
        catch (Exception e)
        {
            Stats = $"{name}: {e.GetType().Name}";
            Plugin.Logger.LogError($"floor mask load failed: {e}");
        }
    }

    // 世界の四角 (x, y から size 四方) を n×n 画素の 0/255 で into へ (左下原点)。マスクの外は床でない
    internal static void Fill(float x, float y, float size, int n, byte[] into)
    {
        float step = size / n * _ppu;
        float bx = (x - _ox) * _ppu + step * 0.5f, by = (y - _oy) * _ppu + step * 0.5f;
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
    }

    internal static float Ppu => _ppu;

    // 部屋の絵 (名前・テクスチャの範囲) の指紋。マスクを作った時と今のゲームの絵が同じかを見る。
    // 床の層 (z が BackZ 以上) だけを見る: 手前にはコマ送りで絵が替わる物 (機関室のエンジンなど) がある
    private const float BackZ = 4f;

    private static uint Signature()
    {
        var keys = new List<string>();
        foreach (var sr in DamageMap.RoomArts)
        {
            var sp = sr ? sr.sprite : null;
            if (!sp || sr.transform.position.z < BackZ) continue;
            var r = sp.textureRect;
            keys.Add($"{sp.name}|{(int)MathF.Round(r.x)}|{(int)MathF.Round(r.y)}|{(int)MathF.Round(r.width)}|{(int)MathF.Round(r.height)}");
        }
        keys.Sort(string.CompareOrdinal);
        uint h = 2166136261;
        foreach (var k in keys)
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
            var sb = new StringBuilder("{\n  \"ship\": \"").Append(ship.name).Append("\",\n  \"sig\": ").Append(Signature()).Append(",\n  \"rooms\": [\n");
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
