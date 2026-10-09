using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 瓦礫・土煙・火花の絵を手続きで作る。本編の岩の描き方に合わせる:
// ベタ塗り + 上面を一段明るく + 太い輪郭 (例: rock1-3 / rockblock / EmergencyButtonBroken の破片)。
internal static class DebrisArt
{
    private static Sprite[] _chunks;
    private static Sprite _pebble, _puff, _spark, _dust, _footprint, _drop, _ripple, _foam;
    private static readonly Sprite[] Pipes = new Sprite[PipeKinds * PipePaints];

    // 壁の色 (Skeld の壁面・枠の内側の灰)
    private static readonly Color32 WallTop = new(188, 196, 202, 255);
    private static readonly Color32 WallSide = new(124, 134, 142, 255);
    private static readonly Color32 Line = new(26, 27, 30, 255);

    public static Sprite Chunk(int i) { _chunks ??= MakeChunks(); return _chunks[(i & 0x7fffffff) % _chunks.Length]; }

    // 船の中の瓦礫: 曲がった金属板・パイプの切れ端・配線・ナット・壁の中身の暗い塊・床の土埃の染み
    private static Sprite[] _plates, _pipes, _wires, _cores;
    private static Sprite _nut, _stain;
    public static Sprite Plate(int i) => Pick(_plates ??= Make(3, k => MakePlate(new System.Random(300 + k), "MrpPlate" + k)), i);
    // 木の裂けた欠片 (ファングルのたき火の丸太の紫)
    private static Sprite[] _splinters;
    public static Sprite Splinter(int i) => Pick(_splinters ??= Make(3, k => MakeSplinter(new System.Random(700 + k), "MrpSplinter" + k)), i);
    public static Sprite Pipe(int i) => Pick(_pipes ??= Make(2, k => MakePipe(new System.Random(400 + k), "MrpPipe" + k)), i);
    public static Sprite Wire(int i) => Pick(_wires ??= Make(4, k => MakeWire(new System.Random(500 + k), WireColors[k], "MrpWire" + k)), i);
    public static Sprite Core(int i) => Pick(_cores ??= Make(3, k => MakePolygonSprite(64, new System.Random(600 + k), 5 + k, 0.78f, CoreTop, CoreSide, 0.09f, "MrpCore" + k)), i);
    public static Sprite Nut => _nut ??= MakeNut();
    public static Sprite Stain => _stain ??= MakeStain();

    private static Sprite Pick(Sprite[] a, int i) => a[(i & 0x7fffffff) % a.Length];
    private static Sprite[] Make(int n, Func<int, Sprite> f)
    {
        var a = new Sprite[n];
        for (int k = 0; k < n; k++) a[k] = f(k);
        return a;
    }

    // 金属 (枠・パイプ)・壁の中身 (断面と同じ灰)・配線 (本編の配線タスクの 4 色)・銅
    private static readonly Color32 MetalTop = new(176, 184, 192, 255), MetalSide = new(116, 124, 134, 255), MetalHi = new(222, 228, 234, 255);
    private static readonly Color32 CoreTop = new(98, 95, 106, 255), CoreSide = new(68, 66, 76, 255);
    private static readonly Color32 Copper = new(232, 146, 64, 255), Hole = new(40, 40, 46, 255);
    private static readonly Color32[] WireColors = { new(214, 36, 36, 255), new(38, 84, 214, 255), new(238, 214, 40, 255), new(224, 70, 196, 255) };
    public static Sprite Pebble => _pebble ??= MakePolygonSprite(32, new System.Random(5), 6, 0.62f, WallTop, WallSide, 0.12f, "MrpPebble");
    public static Sprite Puff => _puff ??= MakePuff();
    public static Sprite Spark => _spark ??= MakeSpark();
    public static Sprite DustBlob => _dust ??= MakeDustBlob();
    public static Sprite Footprint => _footprint ??= MakeFootprint();
    public static Sprite WaterDrop => _drop ??= MakeWaterDrop();
    public static Sprite Ripple => _ripple ??= MakeRipple();
    public static Sprite WaterBlob(int i) => Pick(_blobs ??= Make(3, k => MakeWaterBlob(new System.Random(900 + k), "MrpWaterBlob" + k)), i);
    public static Sprite WaterStreak => _streak ??= MakeWaterStreak();
    public static Sprite SoftShadow => _shadow ??= MakeSoftShadow();
    public static Sprite WaveRing => _wave ??= MakeWaveRing();
    private static Sprite[] _blobs;
    private static Sprite _streak, _shadow, _wave;
    // 壁の中の配管の絵: 裂け方 (PipeKinds・最後の 1 つは裂けていない管) × 色 (PipePaints)。原点は裂け目
    public const int PipeKinds = 5, PipePaints = 4, IntactPipe = PipeKinds - 1;
    public static Sprite Pipe(int kind, int paint)
    {
        int i = kind * PipePaints + paint;
        return Pipes[i] ??= MakePipe(kind, paint);
    }
    // 裂け目の位置 (管の長さを -1〜1 とした時)。原点をここに置くので、管は裂け目から左右に不揃いに伸びる
    private static readonly float[] PipeSlitAt = { 0f, -0.32f, 0.24f, -0.12f, 0f };
    private static readonly float[,] PipePaint = { { 0.55f, 0.58f, 0.63f }, { 0.74f, 0.46f, 0.28f }, { 0.40f, 0.40f, 0.43f }, { 0.50f, 0.62f, 0.54f } };
    public static Sprite Foam => _foam ??= MakeFoam();

    private static Sprite[] MakeChunks()
    {
        var list = new Sprite[5];
        for (int k = 0; k < list.Length; k++)
            list[k] = MakePolygonSprite(96, new System.Random(100 + k), 5 + k % 3, 0.78f, WallTop, WallSide, 0.07f, "MrpChunk" + k);
        return list;
    }

    // いびつな多角形の塊。上半分は明るい面、下半分は暗い面、太い輪郭、面の中に 1 本だけ割れ線
    private static Sprite MakePolygonSprite(int n, System.Random rnd, int verts, float size, Color32 top, Color32 side, float outline, string name)
    {
        var pts = new float[verts * 2];
        float rot = (float)(rnd.NextDouble() * Math.PI * 2);
        for (int v = 0; v < verts; v++)
        {
            float ang = rot + v * MathF.PI * 2 / verts + (float)(rnd.NextDouble() - 0.5) * 0.8f;
            float rr = size * (0.7f + (float)rnd.NextDouble() * 0.3f);
            pts[v * 2] = MathF.Cos(ang) * rr;
            pts[v * 2 + 1] = MathF.Sin(ang) * rr * 0.8f;
        }
        // 上面と側面の境目 (少し傾ける)
        float tilt = (float)(rnd.NextDouble() - 0.5) * 0.5f;
        float split = -0.05f;
        // 割れ線
        float cx0 = (float)(rnd.NextDouble() - 0.5) * 0.3f, cy0 = 0.15f, cx1 = cx0 + 0.25f, cy1 = 0.35f;

        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = PolyDistance(pts, u, v, out bool inside);
            if (!inside && d > outline * 0.35f) continue;
            Color32 c;
            if (d <= outline) c = Line;
            else
            {
                c = v > split + u * tilt ? top : side;
                if (SegDistance(u, v, cx0, cy0, cx1, cy1) < outline * 0.35f) c = Line;
            }
            int i = (y * n + x) * 4;
            px[i] = c.r; px[i + 1] = c.g; px[i + 2] = c.b; px[i + 3] = 255;
        }
        return ToSprite(px, n, name);
    }

    // 曲がった金属板: 片側が破れてぎざぎざ・折れ目で明るい面と暗い面・きれいな縁にリベット
    private static Sprite MakePlate(System.Random rnd, string name)
    {
        const int n = 64;
        float hw = 0.85f, hh = 0.5f;
        var pts = new System.Collections.Generic.List<float> { -hw, -hh, hw * 0.4f, -hh };
        for (int k = 0; k < 4; k++) // 破れた縁 (右)
        {
            float t = (k + 1) / 5f;
            pts.Add(hw * (0.55f + (float)rnd.NextDouble() * 0.45f) * (k % 2 == 0 ? 1f : 0.82f));
            pts.Add(-hh + t * hh * 2f);
        }
        pts.Add(hw * 0.55f); pts.Add(hh);
        pts.Add(-hw); pts.Add(hh);
        var poly = pts.ToArray();
        float fold = -0.1f + (float)rnd.NextDouble() * 0.3f, foldTilt = ((float)rnd.NextDouble() - 0.5f) * 0.6f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = PolyDistance(poly, u, v, out bool inside);
            if (!inside && d > 0.03f) continue;
            Color32 c = d <= 0.09f ? Line : u < fold + v * foldTilt ? MetalTop : MetalSide;
            if (inside && d > 0.09f && MathF.Abs(u - (fold + v * foldTilt)) < 0.035f) c = Line; // 折れ目
            for (int r = 0; r < 3; r++) // リベット
            {
                float rx = -0.62f, ry = -0.3f + r * 0.3f;
                float rd = (u - rx) * (u - rx) + (v - ry) * (v - ry);
                if (rd < 0.075f * 0.075f) c = rd < 0.04f * 0.04f ? MetalHi : Line;
            }
            Put(px, n, x, y, c);
        }
        return ToSprite(px, n, name);
    }

    // 木の欠片: 細長く両端がぎざぎざに裂けた板・上の面が明るく下が暗い・木目の線 2 本・裂けた口は中の明るい色
    private static readonly Color32 WoodTop = new(176, 92, 172, 255), WoodSide = new(122, 56, 124, 255), WoodCore = new(214, 150, 206, 255);
    private static Sprite MakeSplinter(System.Random rnd, string name)
    {
        const int n = 64;
        float hw = 0.92f, hh = 0.26f + (float)rnd.NextDouble() * 0.08f;
        var pts = new System.Collections.Generic.List<float>();
        pts.Add(-hw * 0.7f); pts.Add(-hh);
        pts.Add(hw * 0.75f); pts.Add(-hh);
        for (int k = 0; k < 3; k++) // 右の裂け目
        {
            float t = (k + 1) / 4f;
            pts.Add(hw * (k % 2 == 0 ? 1f : 0.7f + (float)rnd.NextDouble() * 0.15f)); pts.Add(-hh + t * hh * 2f);
        }
        pts.Add(hw * 0.6f); pts.Add(hh);
        pts.Add(-hw * 0.65f); pts.Add(hh);
        for (int k = 0; k < 2; k++) // 左の裂け目
        {
            float t = (k + 1) / 3f;
            pts.Add(-hw * (k % 2 == 0 ? 1f : 0.78f)); pts.Add(hh - t * hh * 2f);
        }
        var poly = pts.ToArray();
        float g0 = -hh * 0.25f + ((float)rnd.NextDouble() - 0.5f) * 0.06f, g1 = hh * 0.45f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = PolyDistance(poly, u, v, out bool inside);
            if (!inside && d > 0.03f) continue;
            Color32 c;
            if (d <= 0.07f) c = Line;
            else if (MathF.Abs(u) > hw * 0.62f) c = WoodCore; // 裂けた口
            else
            {
                c = v > 0f ? WoodTop : WoodSide;
                if (MathF.Abs(v - g0) < 0.025f && u > -hw * 0.5f && u < hw * 0.35f) c = Line; // 木目
                if (MathF.Abs(v - g1) < 0.02f && u > -hw * 0.2f && u < hw * 0.55f) c = WoodSide;
            }
            Put(px, n, x, y, c);
        }
        return ToSprite(px, n, name);
    }

    // パイプの切れ端: 横長の筒・上に照り・下に影・右の口は開いて中が暗い
    private static Sprite MakePipe(System.Random rnd, string name)
    {
        const int n = 64;
        float r = 0.26f + (float)rnd.NextDouble() * 0.06f, x0 = -0.8f, x1 = 0.7f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            // 口 (楕円) と胴
            float eu = (u - x1) / (r * 0.45f), ev = v / r;
            bool mouth = eu * eu + ev * ev <= 1f;
            float body = MathF.Abs(v) - r;
            bool inBody = u >= x0 && u <= x1 && body <= 0f;
            float lu = (u - x0) / (r * 0.45f);
            bool back = lu * lu + ev * ev <= 1f;
            if (!inBody && !mouth && !back) continue;
            Color32 c = v > r * 0.35f && v < r * 0.7f ? MetalHi : v < -r * 0.3f ? MetalSide : MetalTop;
            if (body > -0.08f) c = Line;
            if (mouth) c = eu * eu + ev * ev > 0.55f ? Line : Hole;
            Put(px, n, x, y, c);
        }
        return ToSprite(px, n, name);
    }

    // 配線: 曲がった太い線 (輪郭付き)・先の剥けた所は銅
    private static Sprite MakeWire(System.Random rnd, Color32 col, string name)
    {
        const int n = 48;
        float ph = (float)rnd.NextDouble() * 6f, amp = 0.25f + (float)rnd.NextDouble() * 0.15f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            if (u < -0.85f || u > 0.85f) continue;
            float cv = amp * MathF.Sin(u * 2.4f + ph);
            float d = MathF.Abs(v - cv);
            if (d > 0.2f) continue;
            Color32 c = d > 0.12f ? Line : u > 0.6f ? Copper : col;
            Put(px, n, x, y, c);
        }
        return ToSprite(px, n, name);
    }

    // ナット: 六角形・真ん中に穴
    private static Sprite MakeNut()
    {
        const int n = 24;
        var hex = new float[12];
        for (int k = 0; k < 6; k++) { hex[k * 2] = MathF.Cos(k * MathF.PI / 3f) * 0.85f; hex[k * 2 + 1] = MathF.Sin(k * MathF.PI / 3f) * 0.75f; }
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = PolyDistance(hex, u, v, out bool inside);
            if (!inside) continue;
            float rr = u * u + v * v;
            Color32 c = d < 0.18f || rr < 0.3f * 0.3f && rr > 0.18f * 0.18f ? Line : rr <= 0.18f * 0.18f ? Hole : v > 0f ? MetalTop : MetalSide;
            Put(px, n, x, y, c);
        }
        return ToSprite(px, n, "MrpNut");
    }

    // 床の土埃の染み: いびつな平たい楕円のベタ (半透明の黒 1 色。床の色に合わせて一段暗くなる)
    private static Sprite MakeStain()
    {
        const int n = 96;
        var rnd = new System.Random(77);
        var bumps = new float[9];
        for (int k = 0; k < bumps.Length; k++) bumps[k] = 0.82f + (float)rnd.NextDouble() * 0.18f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = ((y + 0.5f) / n * 2f - 1f) * 2.2f;
            float a = MathF.Atan2(v, u) / (MathF.PI * 2f) * bumps.Length;
            int i0 = ((int)MathF.Floor(a) % bumps.Length + bumps.Length) % bumps.Length;
            float f = a - MathF.Floor(a), lim = bumps[i0] + (bumps[(i0 + 1) % bumps.Length] - bumps[i0]) * f;
            if (u * u + v * v > lim * lim) continue;
            int i = (y * n + x) * 4;
            px[i] = 20; px[i + 1] = 20; px[i + 2] = 24; px[i + 3] = 56;
        }
        return ToSprite(px, n, "MrpStain");
    }

    private static void Put(byte[] px, int n, int x, int y, Color32 c)
    {
        int i = (y * n + x) * 4;
        px[i] = c.r; px[i + 1] = c.g; px[i + 2] = c.b; px[i + 3] = 255;
    }

    // 土煙: 本編の煙の塊 (weapons_explosion の灰色の玉) のように、明るい上半分と暗い下半分の丸い塊を 3 つ重ねる
    private static Sprite MakePuff()
    {
        const int n = 96;
        var px = new byte[n * n * 4];
        (float x, float y, float r)[] balls = { (-0.3f, -0.1f, 0.45f), (0.25f, -0.15f, 0.5f), (0f, 0.25f, 0.5f) };
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float best = float.MaxValue;
            int bi = -1;
            foreach (var (bx, by, br) in balls)
            {
                float d = MathF.Sqrt((u - bx) * (u - bx) + (v - by) * (v - by)) - br;
                if (d < best) { best = d; bi = 0; }
            }
            if (best > 0.02f) continue;
            byte g = best > -0.06f ? (byte)78 : v > -0.05f ? (byte)184 : (byte)150;
            int i = (y * n + x) * 4;
            px[i] = g; px[i + 1] = g; px[i + 2] = (byte)Math.Min(255, g + 6); px[i + 3] = 255;
        }
        return ToSprite(px, n, "MrpPuff");
    }

    // 粉塵の煙: もこもこした塊 (大きな玉 4 つの周りに小さな玉 14 個・縁ほど薄く・上が明るく下が暗い・ところどころむら)。
    // 色は置く側で掛ける
    private static Sprite MakeDustBlob()
    {
        const int n = 64;
        var px = new byte[n * n * 4];
        var rnd = new System.Random(11);
        var lumps = new System.Collections.Generic.List<(float x, float y, float r)>
        {
            (-0.22f, -0.1f, 0.5f), (0.22f, -0.05f, 0.5f), (0f, 0.22f, 0.48f), (0.05f, -0.28f, 0.42f),
        };
        for (int k = 0; k < 14; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2), dist = 0.45f + 0.2f * (float)rnd.NextDouble();
            lumps.Add((MathF.Cos(ang) * dist, MathF.Sin(ang) * dist, 0.16f + 0.12f * (float)rnd.NextDouble()));
        }
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float a = 0f, lit = 0f;
            foreach (var (bx, by, br) in lumps)
            {
                float d2 = ((u - bx) * (u - bx) + (v - by) * (v - by)) / (br * br);
                if (d2 >= 1f) continue;
                float w = (1f - d2) * (1f - d2);
                if (w > a) { a = w; lit = (v - by) / br; } // 玉ごとに上側が明るい
            }
            if (a <= 0f) continue;
            float edge = MathF.Sqrt(u * u + v * v);
            a *= edge > 0.85f ? MathF.Max(0f, (1f - edge) / 0.15f) : 1f;
            a *= 0.75f + 0.25f * (float)rnd.NextDouble();
            float g = 0.78f + 0.16f * lit + 0.06f * (float)rnd.NextDouble();
            byte c = (byte)(255f * Math.Clamp(g, 0f, 1f));
            int i = (y * n + x) * 4;
            px[i] = c; px[i + 1] = c; px[i + 2] = c; px[i + 3] = (byte)(255f * MathF.Min(1f, a));
        }
        return ToSprite(px, n, "MrpDustBlob");
    }

    // 粉の足跡: つま先 (+x) からかかとまでつながった靴底 (土踏まずで細い)。粉らしく粒の抜けがある。色は置く側で付ける
    private static Sprite MakeFootprint()
    {
        const int n = 32;
        var px = new byte[n * n * 4];
        var rnd = new System.Random(7);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            // 幅は前が広く・真ん中で細く・かかとで少し戻る
            float half;
            if (u > 0.1f) half = 0.42f * MathF.Sqrt(MathF.Max(0f, 1f - ((u - 0.1f) / 0.85f) * ((u - 0.1f) / 0.85f)));
            else if (u > -0.35f) half = 0.3f + 0.12f * (u + 0.35f) / 0.45f;
            else half = 0.32f * MathF.Sqrt(MathF.Max(0f, 1f - ((u + 0.35f) / 0.6f) * ((u + 0.35f) / 0.6f)));
            float d = MathF.Abs(v) / MathF.Max(0.001f, half);
            if (d > 1f || u < -0.95f || u > 0.95f) continue;
            float a = d > 0.75f ? (1f - d) / 0.25f : 1f;
            a *= rnd.NextDouble() < 0.15 ? 0.35f : 0.8f + 0.2f * (float)rnd.NextDouble();
            int i = (y * n + x) * 4;
            px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = (byte)(255f * a);
        }
        return ToSprite(px, n, "MrpFootprint");
    }

    // 水の粒: 濃い青の輪郭 + 水色 + 左上の白い光 (色は焼き込み・頂点色は白で使う)
    private static Sprite MakeWaterDrop()
    {
        const int n = 24;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = MathF.Sqrt(u * u + v * v);
            if (d > 0.95f) continue;
            float hx = u + 0.3f, hy = v - 0.32f; // 光は左上
            float r, g, b;
            if (d > 0.72f) { r = 0.13f; g = 0.29f; b = 0.52f; }
            else if (hx * hx + hy * hy < 0.07f) { r = 0.96f; g = 0.99f; b = 1f; }
            else { float k = 0.85f + 0.15f * (-v); r = 0.52f * k; g = 0.78f * k; b = 0.97f * k; }
            float a = d > 0.85f ? (0.95f - d) / 0.1f : 1f;
            int i = (y * n + x) * 4;
            px[i] = (byte)(255f * Math.Clamp(r, 0f, 1f)); px[i + 1] = (byte)(255f * Math.Clamp(g, 0f, 1f));
            px[i + 2] = (byte)(255f * Math.Clamp(b, 0f, 1f)); px[i + 3] = (byte)(255f * a);
        }
        return ToSprite(px, n, "MrpWaterDrop");
    }

    // 飛ぶ水のかたまり: 頭が +x の雫形 (ふちは低い周波数で揺らぐ)。透ける水色の本体・濃い青のふち・
    // 左上の白い光と小さな光・右下に光が抜けた明るい帯 (光の重ね)
    private static Sprite MakeWaterBlob(System.Random rnd, string name)
    {
        const int n = 64;
        var px = new byte[n * n * 4];
        float a2 = (float)(rnd.NextDouble() * 6.3), a3 = (float)(rnd.NextDouble() * 6.3);
        float k2 = 0.06f + 0.06f * (float)rnd.NextDouble(), k3 = 0.04f + 0.05f * (float)rnd.NextDouble();
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float cu = u - 0.12f;
            float ry = 0.6f * (cu < 0f ? MathF.Max(0.25f, 1f + cu * 0.75f) : 1f);   // 後ろへ細る
            float th = MathF.Atan2(v, cu);
            float wob = 1f + k2 * MathF.Sin(2f * th + a2) + k3 * MathF.Sin(3f * th + a3);
            float d = MathF.Sqrt(cu * cu / (0.78f * 0.78f) + v * v / (ry * ry)) / wob;
            if (d > 1f) continue;
            float r, g, b, a;
            if (d > 0.88f) { r = 0.18f; g = 0.40f; b = 0.66f; a = 0.7f; }
            else
            {
                float f = d / 0.88f;                                  // 中ほど透けて、ふちへ濃く
                r = 0.46f + 0.06f * v; g = 0.73f + 0.06f * v; b = 0.96f; a = 0.38f + 0.4f * f * f * f;
                float lx = cu - 0.22f, ly = v + 0.2f;                 // 右下に光が抜けた明るい帯
                float lb = MathF.Max(0f, 1f - MathF.Abs(MathF.Sqrt(lx * lx + ly * ly) - 0.32f) / 0.12f) * (ly < 0.1f ? 1f : 0.3f);
                r += 0.35f * lb; g += 0.25f * lb; b += 0.05f * lb; a += 0.15f * lb;
            }
            float hx = (cu + 0.05f) / 0.2f, hy = (v - 0.3f) / 0.11f;
            if (hx * hx + hy * hy < 1f) { r = g = b = 1f; a = 0.95f; }
            float sx = (cu + 0.4f) / 0.07f, sy = (v - 0.12f) / 0.06f;
            if (sx * sx + sy * sy < 1f) { r = 0.9f; g = 0.97f; b = 1f; a = 0.85f; }
            a *= MathF.Min(1f, (1f - d) / 0.08f);
            int i = (y * n + x) * 4;
            px[i] = (byte)(255f * Math.Clamp(r, 0f, 1f)); px[i + 1] = (byte)(255f * Math.Clamp(g, 0f, 1f));
            px[i + 2] = (byte)(255f * Math.Clamp(b, 0f, 1f)); px[i + 3] = (byte)(255f * Math.Clamp(a, 0f, 1f));
        }
        return ToSprite(px, n, name);
    }

    // 細かいしぶき: 頭が +x の光の筋 (頭は丸く白い・後ろへ細く透ける)
    private static Sprite MakeWaterStreak()
    {
        const int n = 32;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float t = (0.75f - u) / 1.6f;                             // 頭 0 → 尾 1
            if (t < -0.15f || t > 1f) continue;
            float w = t < 0f ? 0.2f * MathF.Sqrt(MathF.Max(0f, 1f - t * t / 0.0225f)) : 0.2f * (1f - t);
            if (MathF.Abs(v) > w) continue;
            float edge = 1f - MathF.Abs(v) / MathF.Max(0.001f, w);
            float a = MathF.Min(1f, edge * 2f) * (1f - MathF.Max(0f, t)) * 0.95f;
            int i = (y * n + x) * 4;
            px[i] = (byte)(255f * (0.75f + 0.25f * edge)); px[i + 1] = (byte)(255f * (0.9f + 0.1f * edge)); px[i + 2] = 255;
            px[i + 3] = (byte)(255f * a);
        }
        return ToSprite(px, n, "MrpWaterStreak");
    }

    // 広がる波: 外側に白い波頭の細い線・内側へ薄い水色がにじんで消える (大きく引き伸ばしても角が出ないよう細かく作る)
    private static Sprite MakeWaveRing()
    {
        const int n = 160;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = MathF.Sqrt(u * u + v * v);
            if (d > 0.99f || d < 0.55f) continue;
            float crest = MathF.Max(0f, 1f - MathF.Abs(d - 0.93f) / 0.035f);       // 波頭
            float trough = MathF.Max(0f, 1f - MathF.Abs(d - 0.86f) / 0.05f);       // 波頭の内側の濃い帯
            float inner = MathF.Max(0f, (d - 0.55f) / 0.33f);                       // 内へ消えるにじみ
            inner = inner * inner * 0.25f;
            float a = MathF.Max(crest * 0.95f, MathF.Max(trough * 0.4f, inner));
            float w = crest;                                                         // 白さ
            int i = (y * n + x) * 4;
            px[i] = (byte)(255f * (0.25f + 0.75f * w)); px[i + 1] = (byte)(255f * (0.5f + 0.5f * w)); px[i + 2] = (byte)(255f * (0.78f + 0.22f * w));
            px[i + 3] = (byte)(255f * Math.Clamp(a, 0f, 1f));
        }
        return ToSprite(px, n, "MrpWaveRing");
    }

    // 飛ぶ物の床の影: やわらかい黒い円 (楕円にするのは置く側)
    private static Sprite MakeSoftShadow()
    {
        const int n = 32;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = MathF.Sqrt(u * u + v * v);
            if (d >= 1f) continue;
            int i = (y * n + x) * 4;
            px[i] = 8; px[i + 1] = 16; px[i + 2] = 30;
            px[i + 3] = (byte)(255f * (1f - d) * (1f - d));
        }
        return ToSprite(px, n, "MrpSoftShadow");
    }

    // 波紋: 細い輪 (外側が濃い青・内側に白い筋)。色は焼き込み
    private static Sprite MakeRipple()
    {
        const int n = 48;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = MathF.Sqrt(u * u + v * v);
            if (d < 0.74f || d > 0.97f) continue;
            bool outer = d > 0.86f;
            float a = MathF.Min(1f, MathF.Min(d - 0.74f, 0.97f - d) / 0.03f);
            int i = (y * n + x) * 4;
            px[i] = outer ? (byte)40 : (byte)235; px[i + 1] = outer ? (byte)84 : (byte)248; px[i + 2] = outer ? (byte)140 : (byte)255;
            px[i + 3] = (byte)(255f * a * (outer ? 0.9f : 0.8f));
        }
        return ToSprite(px, n, "MrpRipple");
    }

    // 壁の中を通る配管を横から見た絵: 横に長い管・継ぎ目の帯・裂け目。
    // 裂け方 0 = 目の形の裂け目 / 1 = ぎざぎざに破れて縁が外へめくれる / 2 = 折れて途切れ、間から壁の中が見える / 3 = 小さな穴 / 4 = 裂けていない管
    private static Sprite MakePipe(int kind, int paint)
    {
        const int w = 96, h = 24;
        var px = new byte[w * h * 4];
        var rnd = new System.Random(3 + kind * 17 + paint * 5);
        var lip = new float[w];
        for (int x = 0; x < w; x++) lip[x] = (kind == 1 ? 0.22f : 0.05f) * (float)rnd.NextDouble();
        float sc = PipeSlitAt[kind];
        float pr = PipePaint[paint, 0], pg = PipePaint[paint, 1], pb = PipePaint[paint, 2];
        float joint = 0.45f + 0.3f * (float)rnd.NextDouble(); // 継ぎ目の帯の位置 (裂け目からの距離)
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h * 2f - 1f;
            float av = MathF.Abs(v), du = u - sc, adu = MathF.Abs(du);
            float edge = 0.92f;
            if (kind == 1 && adu < 0.3f) edge += 0.3f * lip[x] * (1f - adu / 0.3f); // めくれた縁は管の外へはみ出す
            if (av > edge) continue;
            // 2: 折れた所は管が無い (端はぎざぎざ)。間は暗い壁の中
            if (kind == 2 && adu < 0.1f + lip[x] * 1.6f)
            {
                if (av > 0.7f) continue;
                Put(px, (y * w + x) * 4, 0.06f, 0.07f, 0.09f, 1f);
                continue;
            }
            float r, g, b;
            float open = -1f;
            if (kind == 0 || kind == 1)
            {
                float sx = du / (kind == 1 ? 0.3f : 0.26f), slit = 1f - sx * sx;
                if (slit > 0f) open = (kind == 1 ? 0.5f : 0.42f) * MathF.Sqrt(slit) + lip[x];
            }
            else if (kind == 3)
            {
                float hx = du / 0.1f, hy = v / 0.5f, hr = hx * hx + hy * hy;
                if (hr < 1f) open = 2f; else if (hr < 1.8f) open = 0f; // 穴と、そのまわりのめくれ
                if (open > 1f) { Put(px, (y * w + x) * 4, 0.07f, 0.09f, 0.12f, 1f); continue; }
                if (open == 0f) { Put(px, (y * w + x) * 4, 0.82f, 0.85f, 0.88f, 1f); continue; }
                open = -1f;
            }
            if (open > 0f && av < open) { r = 0.07f; g = 0.09f; b = 0.12f; }
            else if (open > 0f && av < open + 0.16f) { r = 0.82f; g = 0.85f; b = 0.88f; }
            else if (av > 0.76f || MathF.Abs(u) > 0.97f || (kind == 2 && adu < 0.17f + lip[x] * 1.6f)) { r = 0.14f; g = 0.15f; b = 0.18f; }
            else
            {
                float k = 0.62f + 0.24f * v; // 上が明るい
                float band = MathF.Abs(adu - joint);
                if (band < 0.05f) k *= 0.72f;          // 継ぎ目の帯
                if (v > 0.25f && v < 0.45f) k = 0.92f; // 光の筋
                r = pr * k + 0.05f; g = pg * k + 0.05f; b = pb * k + 0.06f;
            }
            float a = av > edge - 0.08f ? (edge - av) / 0.08f : 1f;
            Put(px, (y * w + x) * 4, r, g, b, a);
        }
        return ToSpriteWH(px, w, h, "MrpSplitPipe", new Vector2((sc + 1f) * 0.5f, 0.5f));
    }

    private static void Put(byte[] px, int i, float r, float g, float b, float a)
    {
        px[i] = (byte)(255f * Math.Clamp(r, 0f, 1f)); px[i + 1] = (byte)(255f * Math.Clamp(g, 0f, 1f));
        px[i + 2] = (byte)(255f * Math.Clamp(b, 0f, 1f)); px[i + 3] = (byte)(255f * Math.Clamp(a, 0f, 1f));
    }

    // 泡としぶきの霧: 白い玉の塊 (下側がうっすら水色)
    private static Sprite MakeFoam()
    {
        const int n = 48;
        var px = new byte[n * n * 4];
        var rnd = new System.Random(23);
        var balls = new System.Collections.Generic.List<(float x, float y, float r)>();
        for (int k = 0; k < 16; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2), dist = 0.55f * (float)Math.Sqrt(rnd.NextDouble());
            balls.Add((MathF.Cos(ang) * dist, MathF.Sin(ang) * dist, 0.14f + 0.16f * (float)rnd.NextDouble()));
        }
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float a = 0f, lit = 0f;
            foreach (var (bx, by, br) in balls)
            {
                float d2 = ((u - bx) * (u - bx) + (v - by) * (v - by)) / (br * br);
                if (d2 >= 1f) continue;
                float wgt = 1f - d2;
                if (wgt > a) { a = wgt; lit = (v - by) / br; }
            }
            if (a <= 0f) continue;
            a = MathF.Min(1f, a * 2.5f);
            float k = 0.9f + 0.1f * lit;
            int i = (y * n + x) * 4;
            px[i] = (byte)(255f * Math.Clamp(0.86f + 0.14f * k, 0f, 1f));
            px[i + 1] = (byte)(255f * Math.Clamp(0.93f + 0.07f * k, 0f, 1f));
            px[i + 2] = 255;
            px[i + 3] = (byte)(255f * a);
        }
        return ToSprite(px, n, "MrpFoam");
    }

    private static unsafe Sprite ToSpriteWH(byte[] px, int w, int h, string name, Vector2 pivot)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), pivot, 100f);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 火花: 輪郭付きの小さなひし形 (黄色の芯)
    private static Sprite MakeSpark()
    {
        const int n = 24;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = MathF.Abs((x + 0.5f) / n * 2f - 1f), v = MathF.Abs((y + 0.5f) / n * 2f - 1f);
            float d = u + v * 1.6f;
            if (d > 1f) continue;
            Color32 c = d > 0.72f ? new Color32(60, 20, 10, 255) : d > 0.4f ? new Color32(255, 140, 30, 255) : new Color32(255, 236, 140, 255);
            int i = (y * n + x) * 4;
            px[i] = c.r; px[i + 1] = c.g; px[i + 2] = c.b; px[i + 3] = 255;
        }
        return ToSprite(px, n, "MrpSpark");
    }

    private static unsafe Sprite ToSprite(byte[] px, int n, string name)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    private static float PolyDistance(float[] pts, float x, float y, out bool inside)
    {
        int count = pts.Length / 2;
        inside = false;
        float best = float.MaxValue;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float xi = pts[i * 2], yi = pts[i * 2 + 1], xj = pts[j * 2], yj = pts[j * 2 + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            float d = SegDistance(x, y, xi, yi, xj, yj);
            if (d < best) best = d;
        }
        return best;
    }

    private static float SegDistance(float x, float y, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        float t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0f, 1f) : 0f;
        float ex = x - ax - dx * t, ey = y - ay - dy * t;
        return MathF.Sqrt(ex * ex + ey * ey);
    }
}
