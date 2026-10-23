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

    // 柔らかい丸 (縁へ消える・中心が明るい)
    internal static float Dot(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y) * 2f;
        if (r >= 1f) return 0f;
        float core = FxMath.Clamp01((0.55f - r) / 0.12f);
        return FxMath.Max(core, 0.45f * (1f - r) * (1f - r));
    }
}
