using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Terrain;

// テスト用: マップ全体の絵の上に、壊れ方の判定を色分けで重ねた画像を書く (目で答え合わせをするため)。
// 背景 = 船の絵 (暗くする)・床 = 歩いて行き来できるまとまり (島) ごとの色・壁の線 = 判定の色。
// 壁の判定は壁の線を 0.25 ずつに分け、線の両側へ奥の面の深さ (2.6) まで進んで最初に出会う床で決める。
// 段差 (橙) は本番の縁と同じ判定。外壁・厚い壁は本番 (叩いた面の奥の面を辿る) の目安
internal static class TerrainReview
{
    private const int ShipLayers = (1 << 9) | (1 << 11) | (1 << 12); // 船の絵 (影のカメラが描く層から視界の形の層 10 を除いたもの)
    private const int Tile = 512;
    private const float Piece = 0.25f;
    private const float Depth = 2.6f;

    // 面の片側の床の島 (船の外を通らずに届く床だけ。外を通るなら外壁と同じ 0)
    private static int Beyond(Vector2 m, Vector2 dir)
        => SolidMap.AlongFloorOrOutside(m, dir, Depth) > 0 ? SolidMap.IslandAlong(m, dir, Depth) : 0;

    // 壁の判定の色
    private static readonly byte[] Breakable = { 60, 220, 60 };   // 同じ島どうし = 壊して通れる
    private static readonly byte[] Ledge = { 255, 140, 0 };       // 違う島 = 高さが違う (段差)
    private static readonly byte[] Outer = { 70, 120, 255 };      // 片側に床が無い = 外壁
    private static readonly byte[] Thick = { 0, 220, 220 };       // 片側が船体の塊 = 掘り進める厚い壁
    private static readonly byte[] Hull = { 255, 255, 255 };      // 爆発で宇宙まで掘り抜ける外壁
    private static readonly byte[] Guarded = { 200, 60, 255 };    // 名前で守る物・扉
    private static readonly byte[] Furniture = { 255, 230, 0 };   // 家具の保護範囲 (壁も切らない)
    private static readonly byte[] Lone = { 140, 140, 140 };      // 両側とも床が無い
    private static readonly byte[] Void = { 230, 20, 20 };        // 奈落 (塗った注釈)

    // 面の近くで爆発 (半径 SkyBlast) した時に、抜ける形の中で裏の壁の中が空へつながるか (外壁に付いた出っ張り。爆発と同じ判定)
    private const float SkyBlast = 1.2f;
    private static bool SkyBump(Vector2 m, Vector2 n) =>
        SolidMap.FacesSky(SolidMap.SkyNear(m, SkyBlast + 0.5f, new CircleShape(m, SkyBlast)), m, n);

    // 画像に名前を書き込むための一覧 (はしごの両端・部屋の範囲)
    internal static readonly List<string> Notes = new();

    public static string Dump(string path, out string legend)
    {
        legend = "";
        Notes.Clear();
        if (!SolidMap.Ensure()) return "solid map invalid";
        var ship = ShipStatus.Instance;
        int w = SolidMap.W, h = SolidMap.H;
        float ppu = SolidMap.Ppu;
        Vector2 o = SolidMap.Origin;
        // 1. 背景: 船の絵 (暗くする)
        var rgb = Background(w, h, ppu, o, 45); // 下から上の行の順 (書く時に反転)

        // 2. 床: 島ごとの色を薄く重ねる (漏れて捨てた塗りは青)
        var sizes = new Dictionary<int, int>();
        for (int k = 0; k < w * h; k++)
        {
            int id = SolidMap.IslandCell(k);
            if (id != 0) { sizes.TryGetValue(id, out int s); sizes[id] = s + 1; Tint(rgb, k, IslandColor(id), 35); }
            else if (SolidMap.LeakedCell(k)) Tint(rgb, k, Outer, 35);
            if (MapNotes.InVoid(new Vector2(o.x + (k % w + 0.5f) / ppu, o.y + (k / w + 0.5f) / ppu))) Tint(rgb, k, Void, 40);
        }

        // 3. 壁の線の判定
        var counts = new Dictionary<string, float>();
        var pairs = new Dictionary<string, float>();
        var segs = new List<Vector2>();
        var line = new byte[w * h];
        foreach (var col in ship.GetComponentsInChildren<Collider2D>(false))
        {
            if (!col || !col.enabled || col.isTrigger || col.gameObject.layer != 9) continue;
            segs.Clear();
            SolidMap.Segments(col, segs);
            for (int i = 0; i + 1 < segs.Count; i += 2)
            {
                Vector2 a = segs[i], b = segs[i + 1], d = b - a;
                float len = d.magnitude;
                if (len < 1e-4f) continue;
                Vector2 dir = d / len, n = new(-dir.y, dir.x);
                int parts = Math.Max(1, (int)MathF.Ceiling(len / Piece));
                for (int p = 0; p < parts; p++)
                {
                    Vector2 p0 = a + dir * (len * p / parts), p1 = a + dir * (len * (p + 1) / parts), m = (p0 + p1) * 0.5f;
                    string kind;
                    byte[] color;
                    if (TerrainDamage.IsProtected(col, m)) { kind = "guarded"; color = Guarded; }
                    else if (InFurniture(m)) { kind = "furniture"; color = Furniture; }
                    else
                    {
                        int ia = Beyond(m, n), ib = Beyond(m, -n);
                        if (ia == 0 && ib == 0) { kind = "lone"; color = Lone; }
                        else if (HeightLevels.Differs(m, n)) { kind = "ledge"; color = Ledge; }
                        else if (ia == 0 || ib == 0 || SolidMap.FacesOutside(m, n) || SkyBump(m, n))
                        {
                            Vector2 back = ia == 0 ? n : -n;
                            bool thick = SolidMap.HasHull && SolidMap.InHull(m + back * 0.3f);
                            kind = thick ? "thick" : "outer"; color = thick ? Thick : Outer;
                            if (!thick && SolidMap.BreachableHull && SolidMap.HullDepth(m, back, TerrainDamage.BreachDepth) > 0f) { kind = "hull"; color = Hull; }
                            if (ia != 0 && ib != 0 && !SolidMap.FacesOutside(m, n)) Notes.Add($"SKYBUMP {m.x:0.00} {m.y:0.00}");
                        }
                        else { kind = "breakable"; color = Breakable; }
                        if (ia != 0 && ib != 0)
                        {
                            string ra = RoomAlong(m, n), rb = RoomAlong(m, -n);
                            if (ra != rb)
                            {
                                string key = string.CompareOrdinal(ra, rb) < 0 ? $"{ra}|{rb}" : $"{rb}|{ra}";
                                pairs.TryGetValue(key, out float pl); pairs[key] = pl + len / parts;
                                Notes.Add($"PIECE {key} {m.x:0.00} {m.y:0.00}");
                            }
                        }
                    }
                    counts.TryGetValue(kind, out float sum); counts[kind] = sum + len / parts;
                    Array.Clear(line, 0, line.Length);
                    SolidMap.Raster(line, w, h, 0, 0, p0, p1);
                    Paint(rgb, line, w, h, color);
                }
            }
        }

        // 4. はしご (両端を桃色の点と線で)
        var mark = new byte[w * h];
        int ladders = 0;
        foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
        {
            if (!l) continue;
            ladders++;
            Vector2 lp = l.transform.position;
            Dot(mark, w, h, lp, o, ppu);
            if (l.Destination) SolidMap.Raster(mark, w, h, 0, 0, lp, l.Destination.transform.position);
            Notes.Add($"LADDER {l.name} {lp.x:0.00} {lp.y:0.00} top={l.IsTop} island={NearIsland(lp)}");
        }
        foreach (var r in ship.AllRooms)
        {
            if (!r || !r.roomArea) continue;
            var b = r.roomArea.bounds;
            Notes.Add($"ROOM {r.RoomId} {b.min.x:0.00} {b.min.y:0.00} {b.max.x:0.00} {b.max.y:0.00}");
        }
        Paint(rgb, mark, w, h, new byte[] { 255, 80, 200 });

        // 書く (PPM・上の行から)
        using (var fs = System.IO.File.Create(path))
        {
            var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            fs.Write(head, 0, head.Length);
            for (int y = h - 1; y >= 0; y--) fs.Write(rgb, y * w * 3, w * 3);
        }

        foreach (var kv in pairs) Notes.Add($"PAIR {kv.Key} {kv.Value:0.0}");
        var parts2 = new List<string>();
        foreach (var kv in counts) parts2.Add($"{kv.Key}={kv.Value:0.0}");
        var isl = new List<string>();
        foreach (var kv in sizes) isl.Add($"#{kv.Key}:{kv.Value / (ppu * ppu):0}");
        legend = $"walls(len) {string.Join(" ", parts2)} | islands(area) {string.Join(" ", isl)} | ladders={ladders} {HeightLevels.Describe()} {MapNotes.Stats} | origin=({o.x:0.##},{o.y:0.##}) ppu={ppu}";
        return $"{w}x{h}";
    }

    // 点から 0.6 以内のいちばん近い島 (はしごの端は壁際にある)
    private static int NearIsland(Vector2 p)
    {
        for (float r = 0f; r <= 0.6f; r += 0.05f)
        for (int i = 0; i < 16; i++)
        {
            float a = i * MathF.PI / 8f;
            int k = SolidMap.IslandAt(p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
            if (k != 0) return k;
        }
        return 0;
    }

    // 線の片側の、最初に出会う床の部屋の名前 (部屋の範囲の外なら "-")
    private static string RoomAlong(Vector2 from, Vector2 dir)
    {
        var ship = ShipStatus.Instance;
        for (float d = 0.03f; d <= Depth; d += 0.03f)
        {
            Vector2 p = from + dir * d;
            if (SolidMap.IslandAt(p) == 0) continue;
            foreach (var r in ship.AllRooms)
                if (r && r.roomArea && r.roomArea.OverlapPoint(p)) return r.RoomId.ToString();
            return "-";
        }
        return "-";
    }

    // 注釈を塗ってもらう用の画像: 船の絵だけを明るいまま scale 倍の細かさで (判定の色は乗せない)。
    // 塗った画像との差分を、同じ原点と細かさで世界座標へ戻す
    public static string PaintBase(string path, int scale, out string info)
    {
        info = "";
        if (!SolidMap.Ensure()) return "solid map invalid";
        float ppu = SolidMap.Ppu * scale;
        int w = SolidMap.W * scale, h = SolidMap.H * scale;
        Vector2 o = SolidMap.Origin;
        var rgb = Background(w, h, ppu, o, 100);
        WritePpm(path, rgb, w, h);
        info = $"origin=({o.x:0.##},{o.y:0.##}) ppu={ppu}";
        return $"{w}x{h}";
    }

    // 船の絵を Tile 画素の升ごとに撮る (下から上の行の順・明るさ pct %)
    private static byte[] Background(int w, int h, float ppu, Vector2 o, int pct)
    {
        var rgb = new byte[w * h * 3];
        var go = new GameObject("MrpReviewCamera");
        var cam = go.AddComponent<Camera>();
        try
        {
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = ShipLayers;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 300f;
            cam.orthographicSize = Tile / ppu * 0.5f;
            cam.aspect = 1f;
            var rt = RenderTexture.GetTemporary(Tile, Tile, 16, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            cam.targetTexture = rt;
            for (int ty = 0; ty < h; ty += Tile)
            for (int tx = 0; tx < w; tx += Tile)
            {
                cam.transform.position = new Vector3(o.x + (tx + Tile * 0.5f) / ppu, o.y + (ty + Tile * 0.5f) / ppu, -150f);
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0, false);
                var px = tex.GetPixels32();
                for (int y = 0; y < Tile && ty + y < h; y++)
                for (int x = 0; x < Tile && tx + x < w; x++)
                {
                    var c = px[y * Tile + x];
                    int k = ((ty + y) * w + tx + x) * 3;
                    rgb[k] = (byte)(c.r * pct / 100); rgb[k + 1] = (byte)(c.g * pct / 100); rgb[k + 2] = (byte)(c.b * pct / 100);
                }
            }
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(tex);
        }
        finally { Object.Destroy(go); }
        return rgb;
    }

    private static void WritePpm(string path, byte[] rgb, int w, int h)
    {
        using var fs = System.IO.File.Create(path);
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(head, 0, head.Length);
        for (int y = h - 1; y >= 0; y--) fs.Write(rgb, y * w * 3, w * 3);
    }

    private static bool InFurniture(Vector2 m)
    {
        foreach (var r in DamageMap.FurnitureAt(m, 0.05f))
            if (r.Contains(m)) return true;
        return false;
    }

    private static void Tint(byte[] rgb, int k, byte[] c, int pct)
    {
        int o = k * 3;
        for (int i = 0; i < 3; i++) rgb[o + i] = (byte)((rgb[o + i] * (100 - pct) + c[i] * pct) / 100);
    }

    // 線を 1 升太らせて塗る
    private static void Paint(byte[] rgb, byte[] line, int w, int h, byte[] c)
    {
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int k = y * w + x;
            bool on = line[k] != 0 || (x > 0 && line[k - 1] != 0) || (y > 0 && line[k - w] != 0);
            if (!on) continue;
            rgb[k * 3] = c[0]; rgb[k * 3 + 1] = c[1]; rgb[k * 3 + 2] = c[2];
        }
    }

    private static void Dot(byte[] grid, int w, int h, Vector2 p, Vector2 o, float ppu)
    {
        int cx = (int)MathF.Floor((p.x - o.x) * ppu), cy = (int)MathF.Floor((p.y - o.y) * ppu);
        for (int y = cy - 3; y <= cy + 3; y++)
        for (int x = cx - 3; x <= cx + 3; x++)
            if (x >= 0 && y >= 0 && x < w && y < h) grid[y * w + x] = 1;
    }

    private static byte[] IslandColor(int id)
    {
        // 色相を黄金比でずらす (隣り合う番号が似た色にならないように)
        float hue = (id * 0.618034f) % 1f;
        var c = Color.HSVToRGB(hue, 0.9f, 1f);
        return new[] { (byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255) };
    }
}
