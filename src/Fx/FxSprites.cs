using System;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 演出の白い絵を式から焼く。色は SpriteRenderer.color で付ける
internal static class FxSprites
{
    // w×h・高さ 1 単位。f(x, y) は中心からの位置 (x は -0.5〜0.5 を幅で・y は -0.5〜0.5) → 不透明度
    internal static unsafe Sprite Rect(int w, int h, Func<float, float, float> f, string name)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float a = FxMath.Clamp01(f((x + 0.5f) / w - 0.5f, (y + 0.5f) / h - 0.5f));
            int i = (y * w + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new UnityEngine.Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), h);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    internal delegate float RgbFn(float x, float y, out float r, out float g, out float b);

    // 色つき版: f は色を書き出して不透明度を返す (位置の取り方は Rect と同じ)。
    // 1 回ごとに焼いて使い終わったら捨てる物向け (シーンの切り替えで消えてよいので DontUnloadUnusedAsset を付けない)
    internal static unsafe Sprite RectRgb(int w, int h, RgbFn f, string name)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float a = FxMath.Clamp01(f((x + 0.5f) / w - 0.5f, (y + 0.5f) / h - 0.5f, out float r, out float g, out float b));
            int i = (y * w + x) * 4;
            px[i] = (byte)(FxMath.Clamp01(r) * 255f + 0.5f);
            px[i + 1] = (byte)(FxMath.Clamp01(g) * 255f + 0.5f);
            px[i + 2] = (byte)(FxMath.Clamp01(b) * 255f + 0.5f);
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new UnityEngine.Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), h);
        return sp;
    }

    // 縁へ滑らかに薄れる丸 (芯の無い煙・影の形)
    internal static float Soft(float x, float y)
    {
        float t = FxMath.Clamp01(1f - FxMath.Sqrt(x * x + y * y) * 2f);
        return t * t * (3f - 2f * t);
    }

    // 柔らかい丸 (縁へ消える・中心が明るい)
    internal static float Dot(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y) * 2f;
        if (r >= 1f) return 0f;
        float core = FxMath.Clamp01((0.55f - r) / 0.12f);
        return FxMath.Max(core, 0.45f * (1f - r) * (1f - r));
    }
}
