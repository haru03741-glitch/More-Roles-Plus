using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// スペクターの満腹のゲージの狐 (横向き・右を向く)。式で 1 回だけ焼く。
// 0 = 足りない: 尾で体を包んで丸まり、耳を伏せて目を閉じた寝姿 (少し暗い)
// 1 = 足りた: 座って胸を張り、尾を立てて目を開けた姿
// 部品を奥から順に塗り、部品ごとに縁を描く (本編の絵と同じ太い暗い縁)。明るさは上ほど明るく下ほど暗い
internal static class SpectreFoxArt
{
    private const int Size = 160;
    private const float Edge = 0.022f;           // 縁の太さ (絵の高さに対して)

    private static Sprite[] _frames;

    internal static Sprite[] Frames => _frames ??= new[] { Bake(false), Bake(true) };

    private struct Rgb { public float R, G, B; public Rgb(float r, float g, float b) { R = r; G = g; B = b; } }

    private static readonly Rgb Fur = new(0.93f, 0.5f, 0.16f);
    private static readonly Rgb Cream = new(0.98f, 0.94f, 0.86f);
    private static readonly Rgb Dark = new(0.22f, 0.13f, 0.1f);
    private static readonly Rgb Line = new(0.14f, 0.08f, 0.07f);

    private static unsafe Sprite Bake(bool full)
    {
        int n = Size;
        var px = new byte[n * n * 4];
        float aa = 1.2f / n;
        for (int iy = 0; iy < n; iy++)
        for (int ix = 0; ix < n; ix++)
        {
            float x = (ix + 0.5f) / n - 0.5f, y = (iy + 0.5f) / n - 0.5f;
            float r = 0f, g = 0f, b = 0f, a = 0f;
            if (full) PaintSitting(x, y, aa, ref r, ref g, ref b, ref a);
            else PaintCurled(x, y, aa, ref r, ref g, ref b, ref a);
            if (!full) { r *= 0.82f; g *= 0.8f; b *= 0.82f; }
            int i = (iy * n + ix) * 4;
            px[i] = (byte)(FxMath.Clamp01(r) * 255f + 0.5f);
            px[i + 1] = (byte)(FxMath.Clamp01(g) * 255f + 0.5f);
            px[i + 2] = (byte)(FxMath.Clamp01(b) * 255f + 0.5f);
            px[i + 3] = (byte)(FxMath.Clamp01(a) * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = full ? "MrpSpectreFoxFull" : "MrpSpectreFoxHungry" };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // ---- 姿 ----

    private static void PaintSitting(float x, float y, float aa, ref float r, ref float g, ref float b, ref float a)
    {
        // 尾: 腰から後ろへ出て上へ立つ。先は白い
        float tail = Capsule3(x, y, -0.12f, -0.33f, -0.33f, -0.12f, -0.27f, 0.2f, 0.06f, 0.1f);
        Layer(tail, aa, y > 0.1f + (x + 0.27f) * 0.6f ? Cream : Fur, x, y, -0.12f, 0.2f, ref r, ref g, ref b, ref a);
        // 奥の後ろ足
        Layer(Ellipse(x, y, -0.06f, -0.33f, 0.12f, 0.1f), aa, Shade(Fur, 0.85f), x, y, -0.33f, -0.23f, ref r, ref g, ref b, ref a);
        // 胴: 斜めに起きた楕円
        float body = Ellipse(Rot(x + 0.01f, y + 0.12f, -0.35f, out float ry), ry, 0f, 0f, 0.15f, 0.23f);
        Layer(body, aa, Fur, x, y, -0.35f, 0.1f, ref r, ref g, ref b, ref a);
        // 胸の白
        float chest = FxMath.Max(Ellipse(x, y, 0.08f, -0.04f, 0.075f, 0.14f), -body);
        Layer(chest, aa, Cream, x, y, -0.18f, 0.1f, ref r, ref g, ref b, ref a, outline: false);
        // 手前の前足 (先は暗い)
        float leg = Capsule(x, y, 0.09f, -0.06f, 0.1f, -0.39f, 0.035f);
        Layer(leg, aa, y < -0.3f ? Dark : Fur, x, y, -0.4f, -0.06f, ref r, ref g, ref b, ref a);
        // 手前の後ろ足
        Layer(Ellipse(x, y, -0.1f, -0.35f, 0.13f, 0.08f), aa, Fur, x, y, -0.43f, -0.27f, ref r, ref g, ref b, ref a);
        // 耳 (奥・手前)。先は暗い
        Layer(Tri(x, y, 0.02f, 0.23f, 0.06f, 0.42f, 0.12f, 0.26f), aa, y > 0.35f ? Dark : Shade(Fur, 0.85f), x, y, 0.23f, 0.42f, ref r, ref g, ref b, ref a);
        // 頭と口先
        float head = FxMath.Min(Circle(x, y, 0.11f, 0.17f, 0.11f), Ellipse(x, y, 0.22f, 0.12f, 0.1f, 0.055f));
        Layer(head, aa, x > 0.15f && y < 0.12f ? Cream : Fur, x, y, 0.06f, 0.28f, ref r, ref g, ref b, ref a);
        Layer(Tri(x, y, 0.1f, 0.25f, 0.16f, 0.43f, 0.2f, 0.24f), aa, y > 0.36f ? Dark : Fur, x, y, 0.24f, 0.43f, ref r, ref g, ref b, ref a);
        // 鼻と目 (開いた目・光の点)
        Dot(Circle(x, y, 0.315f, 0.13f, 0.022f), aa, Dark, ref r, ref g, ref b, ref a);
        Dot(Ellipse(x, y, 0.15f, 0.19f, 0.02f, 0.028f), aa, Dark, ref r, ref g, ref b, ref a);
        Dot(Circle(x, y, 0.156f, 0.2f, 0.007f), aa, new Rgb(1f, 1f, 1f), ref r, ref g, ref b, ref a);
    }

    private static void PaintCurled(float x, float y, float aa, ref float r, ref float g, ref float b, ref float a)
    {
        // 丸まった胴
        float body = Ellipse(x, y, -0.03f, -0.17f, 0.3f, 0.16f);
        Layer(body, aa, Fur, x, y, -0.33f, -0.01f, ref r, ref g, ref b, ref a);
        // 頭: 胴の手前に乗せる
        float head = FxMath.Min(Circle(x, y, 0.13f, -0.15f, 0.1f), Ellipse(x, y, 0.24f, -0.19f, 0.09f, 0.05f));
        Layer(head, aa, x > 0.18f && y < -0.18f ? Cream : Fur, x, y, -0.25f, -0.05f, ref r, ref g, ref b, ref a);
        // 伏せた耳 (後ろへ寝る・先は暗い)
        Layer(Tri(x, y, 0.13f, -0.08f, 0.02f, -0.05f, 0.1f, -0.12f), aa, x < 0.06f ? Dark : Fur, x, y, -0.12f, -0.05f, ref r, ref g, ref b, ref a);
        // 体を包む尾: 後ろから手前の下を回って口先の下まで。先は白い
        float tail = Capsule3(x, y, -0.3f, -0.12f, -0.28f, -0.32f, 0.22f, -0.3f, 0.09f, 0.07f);
        // 白い先との境は毛並みに沿って斜め
        Layer(tail, aa, x > 0.1f + (y + 0.3f) * 0.6f ? Cream : Fur, x, y, -0.39f, -0.22f, ref r, ref g, ref b, ref a);
        // 鼻と閉じた目 (下へ弧)
        Dot(Circle(x, y, 0.325f, -0.18f, 0.02f), aa, Dark, ref r, ref g, ref b, ref a);
        float eye = FxMath.Abs(Circle(x, y, 0.16f, -0.12f, 0.035f)) - 0.008f;
        if (y < -0.12f) Dot(eye, aa, Dark, ref r, ref g, ref b, ref a);
    }

    // ---- 塗り ----

    // 部品を 1 つ塗る (d < 0 が中)。縁は外側へ Edge の幅。明るさは部品の下端 lo から上端 hi へ 0.78→1.08
    private static void Layer(float d, float aa, Rgb c, float x, float y, float lo, float hi,
        ref float r, ref float g, ref float b, ref float a, bool outline = true)
    {
        float w = outline ? Edge : 0f;
        float cover = FxMath.Clamp01((w - d) / aa + 0.5f);
        if (cover <= 0f) return;
        float inner = FxMath.Clamp01((-d) / aa + 0.5f);
        float t = FxMath.Clamp01((y - lo) / FxMath.Max(hi - lo, 0.01f));
        float lit = 0.78f + 0.3f * t + 0.06f * FxMath.Clamp01(x * 2f + 0.5f);
        // 縁のすぐ内側は少し暗い (丸みの陰)
        float rim = outline ? FxMath.Clamp01(1f - (-d) / 0.03f) * 0.12f : 0f;
        lit -= rim;
        float cr = c.R * lit, cg = c.G * lit, cb = c.B * lit;
        cr = Line.R + (cr - Line.R) * inner;
        cg = Line.G + (cg - Line.G) * inner;
        cb = Line.B + (cb - Line.B) * inner;
        r = r + (cr - r) * cover;
        g = g + (cg - g) * cover;
        b = b + (cb - b) * cover;
        a = a + (1f - a) * cover;
    }

    private static void Dot(float d, float aa, Rgb c, ref float r, ref float g, ref float b, ref float a)
    {
        float cover = FxMath.Clamp01((-d) / aa + 0.5f);
        if (cover <= 0f) return;
        r += (c.R - r) * cover;
        g += (c.G - g) * cover;
        b += (c.B - b) * cover;
        a += (1f - a) * cover;
    }

    private static Rgb Shade(Rgb c, float k) => new(c.R * k, c.G * k, c.B * k);

    // ---- 形 (符号付きの距離・中が負) ----

    private static float Circle(float x, float y, float cx, float cy, float rad)
    {
        float dx = x - cx, dy = y - cy;
        return FxMath.Sqrt(dx * dx + dy * dy) - rad;
    }

    // 近似の楕円 (縮めた円の距離に短い方の半径を掛ける)
    private static float Ellipse(float x, float y, float cx, float cy, float rx, float ry)
    {
        float dx = (x - cx) / rx, dy = (y - cy) / ry;
        return (FxMath.Sqrt(dx * dx + dy * dy) - 1f) * FxMath.Min(rx, ry);
    }

    private static float Rot(float x, float y, float ang, out float ry)
    {
        float c = FxMath.Cos(ang), s = FxMath.Sin(ang);
        ry = -s * x + c * y;
        return c * x + s * y;
    }

    private static float Capsule(float x, float y, float ax, float ay, float bx, float by, float rad)
    {
        float px = x - ax, py = y - ay, vx = bx - ax, vy = by - ay;
        float h = FxMath.Clamp01((px * vx + py * vy) / (vx * vx + vy * vy));
        float dx = px - vx * h, dy = py - vy * h;
        return FxMath.Sqrt(dx * dx + dy * dy) - rad;
    }

    // 3 点の 2 次曲線に沿った太さが r0→r1 へ変わる筒 (曲線を 24 分割した折れ線で近似)
    private static float Capsule3(float x, float y, float ax, float ay, float cx, float cy, float bx, float by, float r0, float r1)
    {
        float best = 1e9f;
        float px = ax, py = ay;
        for (int i = 1; i <= 24; i++)
        {
            float t = i / 24f, u = 1f - t;
            float qx = u * u * ax + 2f * u * t * cx + t * t * bx;
            float qy = u * u * ay + 2f * u * t * cy + t * t * by;
            float rad = r0 + (r1 - r0) * (t - 0.5f / 24f);
            // 先は丸く細る
            if (t > 0.8f) rad *= 1f - (t - 0.8f) * 2.2f;
            float d = Capsule(x, y, px, py, qx, qy, FxMath.Max(rad, 0.01f));
            if (d < best) best = d;
            px = qx; py = qy;
        }
        return best;
    }

    private static float Tri(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
    {
        // 3 辺の距離の最小と、中かどうかの符号
        float d = FxMath.Min(Seg(x, y, ax, ay, bx, by), FxMath.Min(Seg(x, y, bx, by, cx, cy), Seg(x, y, cx, cy, ax, ay)));
        float s1 = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
        float s2 = (cx - bx) * (y - by) - (cy - by) * (x - bx);
        float s3 = (ax - cx) * (y - cy) - (ay - cy) * (x - cx);
        bool inside = (s1 >= 0f && s2 >= 0f && s3 >= 0f) || (s1 <= 0f && s2 <= 0f && s3 <= 0f);
        return inside ? -d : d;
    }

    private static float Seg(float x, float y, float ax, float ay, float bx, float by) => Capsule(x, y, ax, ay, bx, by, 0f);
}
