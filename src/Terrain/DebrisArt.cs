using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 瓦礫・土煙・火花の絵を手続きで作る。本編の岩の描き方に合わせる:
// ベタ塗り + 上面を一段明るく + 太い輪郭 (例: rock1-3 / rockblock / EmergencyButtonBroken の破片)。
internal static class DebrisArt
{
    private static Sprite[] _chunks;
    private static Sprite _pebble, _puff, _spark;

    // 壁の色 (Skeld の壁面・枠の内側の灰)
    private static readonly Color32 WallTop = new(188, 196, 202, 255);
    private static readonly Color32 WallSide = new(124, 134, 142, 255);
    private static readonly Color32 Line = new(26, 27, 30, 255);

    public static Sprite Chunk(int i) { _chunks ??= MakeChunks(); return _chunks[(i & 0x7fffffff) % _chunks.Length]; }

    // 船の中の瓦礫: 曲がった金属板・パイプの切れ端・配線・ナット・壁の中身の暗い塊・床の土埃の染み
    private static Sprite[] _plates, _pipes, _wires, _cores;
    private static Sprite _nut, _stain;
    public static Sprite Plate(int i) => Pick(_plates ??= Make(3, k => MakePlate(new System.Random(300 + k), "MrpPlate" + k)), i);
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
