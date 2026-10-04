using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 穴の向こうに敷く「瓦礫の床」と、穴の縁から走る「ひび」の絵を手続きで作る (素材ファイルを持ち込まない)。
// 本編の絵柄に合わせて、平たい塗り + 濃い輪郭線 + 一段だけの明暗で描く。
internal static class UnderlayArt
{
    private const int FloorSize = 256;
    private const int CrackSize = 512;

    // 床の絵の半径 1 に対する穴の縁の位置 (DamageMap が床を穴の半径の 1.5 倍で敷くため)。縁の暗がりはシェーダが付ける
    private const float HoleEdgeNorm = 1f / 1.5f;

    // ひびの絵の半径 1 = 穴の半径の CrackReach 倍
    public const float CrackReach = 2.2f;

    private static readonly byte[] Outline = { 22, 22, 24 };

    // ── 瓦礫の床 ─────────────────────────────────────────────────────────
    public static unsafe Sprite MakeFloor()
    {
        const int n = FloorSize;
        var px = new byte[n * n * 4];
        var rnd = new System.Random(11);

        // 瓦礫 (輪郭付きの多角形)。穴の内側に散らす
        const int shards = 9;
        var shardPts = new float[shards][];
        var shardTone = new float[shards];
        for (int k = 0; k < shards; k++)
        {
            float a = (float)(rnd.NextDouble() * Math.PI * 2);
            float d = (float)Math.Sqrt(rnd.NextDouble()) * HoleEdgeNorm * 0.8f;
            float cx = MathF.Cos(a) * d, cy = MathF.Sin(a) * d;
            float size = 0.07f + (float)rnd.NextDouble() * 0.08f;
            int verts = 4 + rnd.Next(3);
            float rot = (float)(rnd.NextDouble() * Math.PI * 2);
            var pts = new float[verts * 2];
            for (int v = 0; v < verts; v++)
            {
                float ang = rot + v * MathF.PI * 2 / verts + (float)(rnd.NextDouble() - 0.5) * 0.9f;
                float rr = size * (0.6f + (float)rnd.NextDouble() * 0.5f);
                pts[v * 2] = cx + MathF.Cos(ang) * rr;
                pts[v * 2 + 1] = cy + MathF.Sin(ang) * rr * 0.75f; // 斜め上から見た潰れ
            }
            shardPts[k] = pts;
            shardTone[k] = 0.42f + (float)rnd.NextDouble() * 0.1f; // 壁の灰 (本編の壁面より一段暗い)
        }

        float outlineW = 0.018f; // 床の絵の半径 1 に対する輪郭の太さ

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float r = MathF.Sqrt(u * u + v * v);

            // 床: 鋼の板 + 継ぎ目 (板ごとに少し明暗)
            float gx = (u + 1f) * 3f, gy = (v + 1f) * 3f;
            int plate = (int)gx * 7 + (int)gy * 13;
            float br = 1f + (plate % 3 - 1) * 0.04f;
            float cr = 80 * br, cg = 86 * br, cb = 94 * br;
            float fx = gx - MathF.Floor(gx), fy = gy - MathF.Floor(gy);
            if (fx < 0.03f || fy < 0.03f) { cr = 52; cg = 56; cb = 62; }
            else if (fx < 0.06f || fy < 0.06f) { cr += 14; cg += 14; cb += 14; } // 継ぎ目の照り返し

            // 瓦礫
            for (int k = 0; k < shards; k++)
            {
                float dEdge = PolyEdgeDistance(shardPts[k], u, v, out bool inside);
                if (inside && dEdge > outlineW)
                {
                    float t = shardTone[k] * 255f;
                    // 上半分を一段明るく (本編の一段影)
                    float lift = v > Centroid(shardPts[k], 1) ? 26f : 0f;
                    cr = t * 0.80f + lift; cg = t * 0.86f + lift; cb = t * 0.92f + lift;
                    break;
                }
                if (dEdge <= outlineW && (inside || dEdge <= outlineW * 0.5f))
                {
                    cr = Outline[0]; cg = Outline[1]; cb = Outline[2];
                    break;
                }
                // 瓦礫の落ち影 (右下へ)
                float sd = PolyEdgeDistance(shardPts[k], u - 0.015f, v + 0.02f, out bool inShadow);
                if (inShadow && sd > 0) { cr *= 0.7f; cg *= 0.7f; cb *= 0.7f; }
            }

            // 切り口の内側を暗く落として深さを出す

            float alpha = 1f; // 形は損傷マスクで切るので、絵は四角いまま全面を塗る
            int i = (y * n + x) * 4;
            px[i] = ToByte(cr); px[i + 1] = ToByte(cg); px[i + 2] = ToByte(cb);
            px[i + 3] = ToByte(alpha * 255f);
        }

        return ToSprite(px, n, "MrpRubble");
    }

    // ── ひび ─────────────────────────────────────────────────────────────
    public static unsafe Sprite MakeCracks()
    {
        const int n = CrackSize;
        var dark = new float[n * n];
        var lite = new float[n * n];
        var rnd = new System.Random(23);

        float holeNorm = 1f / CrackReach;      // 穴の縁 (絵の半径 1 に対する位置)
        float unit = holeNorm;                 // 長さは穴の半径を単位に決める
        const int branches = 7;

        for (int b = 0; b < branches; b++)
        {
            float ang = b * MathF.PI * 2 / branches + (float)(rnd.NextDouble() - 0.5) * 0.6f;
            float len = (0.5f + (float)rnd.NextDouble() * 0.6f) * unit;
            var start = new Vec(MathF.Cos(ang) * holeNorm * 0.92f, MathF.Sin(ang) * holeNorm * 0.92f * 0.85f);
            Crack(dark, lite, n, rnd, start, ang, len, 0.05f / 3.52f * 2f, 0, unit);
        }

        var px = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            float d = Math.Min(1f, dark[i]), l = Math.Min(1f, lite[i]) * (1f - d);
            // 暗い線 #1a1a1a を α で、ハイライトは白を薄く
            float a = Math.Max(d * 0.88f, l * 0.3f);
            if (a <= 0f) continue;
            float c = (26f * d * 0.88f + 255f * l * 0.3f) / a;
            px[i * 4] = ToByte(c); px[i * 4 + 1] = ToByte(c); px[i * 4 + 2] = ToByte(c);
            px[i * 4 + 3] = ToByte(a * 255f);
        }

        return ToSprite(px, n, "MrpCracks");
    }

    // 折れ線のひびを 1 本描く。途中で 1 回まで枝分かれする
    private static void Crack(float[] dark, float[] lite, int n, System.Random rnd, Vec p, float ang, float len, float width, int depth, float unit)
    {
        float step = 0.08f * unit;
        int steps = Math.Max(2, (int)(len / step));
        for (int s = 0; s < steps; s++)
        {
            ang += (float)(rnd.NextDouble() - 0.5) * 0.87f; // ±25°
            var q = new Vec(p.X + MathF.Cos(ang) * step, p.Y + MathF.Sin(ang) * step * 0.85f);
            float w = width * (1f - s / (float)steps * 0.7f);
            Stroke(dark, n, p, q, w);
            Stroke(lite, n, new Vec(p.X, p.Y - w * 1.6f), new Vec(q.X, q.Y - w * 1.6f), w * 0.45f);

            if (depth == 0 && s == steps / 2 && rnd.NextDouble() < 0.7)
            {
                float side = rnd.NextDouble() < 0.5 ? -1f : 1f;
                Crack(dark, lite, n, rnd, q, ang + side * (0.5f + (float)rnd.NextDouble() * 0.5f), len * 0.45f, w * 0.7f, 1, unit);
            }
            p = q;
        }
    }

    private static void Stroke(float[] buf, int n, Vec a, Vec b, float w)
    {
        // 絵の座標 (-1..1) → 画素
        float ax = (a.X + 1f) * 0.5f * n, ay = (a.Y + 1f) * 0.5f * n;
        float bx = (b.X + 1f) * 0.5f * n, by = (b.Y + 1f) * 0.5f * n;
        float wp = Math.Max(0.6f, w * 0.25f * n); // 半幅 (w は絵の座標 -1..1 での太さ)
        int x0 = Math.Max(0, (int)(Math.Min(ax, bx) - wp - 2)), x1 = Math.Min(n - 1, (int)(Math.Max(ax, bx) + wp + 2));
        int y0 = Math.Max(0, (int)(Math.Min(ay, by) - wp - 2)), y1 = Math.Min(n - 1, (int)(Math.Max(ay, by) + wp + 2));
        float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float px = x + 0.5f - ax, py = y + 0.5f - ay;
            float t = l2 > 0 ? Math.Clamp((px * dx + py * dy) / l2, 0f, 1f) : 0f;
            float ex = px - dx * t, ey = py - dy * t;
            float d = MathF.Sqrt(ex * ex + ey * ey);
            float cov = Math.Clamp(wp + 0.5f - d, 0f, 1f);
            int i = y * n + x;
            if (cov > buf[i]) buf[i] = cov;
        }
    }

    // ── 共通 ─────────────────────────────────────────────────────────────

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

        var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private readonly struct Vec
    {
        public readonly float X, Y;
        public Vec(float x, float y) { X = x; Y = y; }
    }

    // 多角形の辺までの距離と内外判定
    private static float PolyEdgeDistance(float[] pts, float x, float y, out bool inside)
    {
        int count = pts.Length / 2;
        inside = false;
        float best = float.MaxValue;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float xi = pts[i * 2], yi = pts[i * 2 + 1], xj = pts[j * 2], yj = pts[j * 2 + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            float dx = xj - xi, dy = yj - yi, l2 = dx * dx + dy * dy;
            float t = l2 > 0 ? Math.Clamp(((x - xi) * dx + (y - yi) * dy) / l2, 0f, 1f) : 0f;
            float ex = x - xi - dx * t, ey = y - yi - dy * t;
            float d = ex * ex + ey * ey;
            if (d < best) best = d;
        }
        return MathF.Sqrt(best);
    }

    private static float Centroid(float[] pts, int axis)
    {
        float s = 0;
        int count = pts.Length / 2;
        for (int i = 0; i < count; i++) s += pts[i * 2 + axis];
        return s / count;
    }

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)v, 0, 255);
}
