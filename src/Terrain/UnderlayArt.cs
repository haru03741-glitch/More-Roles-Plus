using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 穴の向こうに敷く「船体の中」と、穴の縁から走る「ひび」の絵を手続きで作る (素材ファイルを持ち込まない)。
// 本編の絵柄に合わせて、平たい塗り + 濃い輪郭線 + 一段だけの明暗で描く。
internal static class UnderlayArt
{
    private const int HullSize = 128;
    private const int CrackSize = 512;

    // ひびの絵の半径 1 = 穴の半径の CrackReach 倍
    public const float CrackReach = 2.2f;

    private static readonly byte[] Outline = { 22, 22, 24 };

    // ── 船体の中 (壁を抜いた隙間の奥) ─────────────────────────────────
    // 1 枚 = 1 世界単位の継ぎ目なく並ぶ升。x = 壁に沿う向き。暗い奥 + 縦の柱 + 横の梁 (上面と手前の面の一段) + 配管 2 本。
    // 本編の絵柄: 平たい塗り + 濃い輪郭線 + 一段の明暗。奥にあるので全体を船体の地 (≈62,72,74) より暗く
    public static Sprite MakeHullTile()
    {
        const int n = HullSize;
        var px = new byte[n * n * 4];
        const float line = 1.6f / n; // 輪郭の太さ (升に対する比)
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
            float cr = 36, cg = 41, cb = 48; // 奥の暗がり

            // 縦の柱 (梁の奥)。左を一段明るく
            if (u > 0.40f && u < 0.52f)
            {
                bool lit = u < 0.45f;
                cr = lit ? 54 : 44; cg = lit ? 58 : 48; cb = lit ? 66 : 56;
                if (u - 0.40f < line || 0.52f - u < line) { cr = 18; cg = 18; cb = 20; }
            }

            // 梁の落ち影 (梁のすぐ下を一段暗く)
            if (v > 0.36f && v <= 0.42f) { cr *= 0.72f; cg *= 0.72f; cb *= 0.72f; }

            // 配管 (上の太い 1 本・下の細い 1 本)。上端に照り返しの線
            Pipe(v, 0.14f, 0.22f, 60, 68, 74, line, ref cr, ref cg, ref cb);
            Pipe(v, 0.79f, 0.84f, 52, 58, 64, line, ref cr, ref cg, ref cb);

            // 横の梁: 手前の面 (下) + 上面 (上・一段明るい)・面の境と上下に輪郭・手前の面にリベット
            if (v > 0.42f && v < 0.60f)
            {
                if (v < 0.51f) { cr = 50; cg = 55; cb = 63; } else { cr = 70; cg = 76; cb = 86; }
                float ru = u * 4f - MathF.Floor(u * 4f) - 0.5f, rv = (v - 0.465f) * 4f;
                if (v < 0.51f && ru * ru + rv * rv < 0.0045f) { cr = 76; cg = 82; cb = 92; }
                if (v - 0.42f < line || 0.60f - v < line || MathF.Abs(v - 0.51f) < line * 0.5f) { cr = 18; cg = 18; cb = 20; }
            }

            int i = (y * n + x) * 4;
            px[i] = ToByte(cr); px[i + 1] = ToByte(cg); px[i + 2] = ToByte(cb); px[i + 3] = 255;
        }
        return ToSprite(px, n, "MrpHullInterior", n);
    }

    private static void Pipe(float v, float v0, float v1, float r, float g, float b, float line, ref float cr, ref float cg, ref float cb)
    {
        if (v <= v0 || v >= v1) return;
        cr = r; cg = g; cb = b;
        if (v1 - v < (v1 - v0) * 0.3f) { cr += 26; cg += 26; cb += 26; } // 上の照り
        if (v - v0 < line || v1 - v < line) { cr = 18; cg = 18; cb = 20; }
    }

    // ── ひび ─────────────────────────────────────────────────────────────
    // impact = false: 穴の縁から外へ走るひび (中央は穴なので空ける)
    // impact = true : 叩いた点から放射状に走るひび (抜けない打撃・爆発の外側)
    public static unsafe Sprite MakeCracks(bool impact)
    {
        const int n = CrackSize;
        var dark = new float[n * n];
        var lite = new float[n * n];
        var rnd = new System.Random(impact ? 41 : 23);

        if (impact)
        {
            // 絵の半径 1 = ひびの届く距離。中心から 5 本、根元を太く
            const int branches = 5;
            for (int b = 0; b < branches; b++)
            {
                float ang = b * MathF.PI * 2 / branches + (float)(rnd.NextDouble() - 0.5) * 0.9f;
                float len = 0.55f + (float)rnd.NextDouble() * 0.4f;
                Crack(dark, lite, n, rnd, new Vec(0f, 0f), ang, len, 0.07f, 0, 0.9f);
            }
        }
        else
        {
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
        }

        var px = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            float d = Math.Min(1f, dark[i]), l = Math.Min(1f, lite[i]) * (1f - d);
            // 暗い線 #1a1a1a を α で、ハイライトは白を薄く
            // 真っ黒にせず半透明の暗い線にして、下地の色になじませる
            float a = Math.Max(d * 0.62f, l * 0.18f);
            if (a <= 0f) continue;
            float c = (38f * d * 0.62f + 235f * l * 0.18f) / a;
            px[i * 4] = ToByte(c); px[i * 4 + 1] = ToByte(c); px[i * 4 + 2] = ToByte(c);
            px[i * 4 + 3] = ToByte(a * 255f);
        }

        return ToSprite(px, n, impact ? "MrpImpactCracks" : "MrpCracks");
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

    private static unsafe Sprite ToSprite(byte[] px, int n, string name, float ppu = 100f)
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

        var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private readonly struct Vec
    {
        public readonly float X, Y;
        public Vec(float x, float y) { X = x; Y = y; }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)v, 0, 255);
}
