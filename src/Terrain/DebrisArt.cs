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
