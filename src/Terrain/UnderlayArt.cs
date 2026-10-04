using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 穴の向こうに敷く「瓦礫の床」の絵を手続きで作る (素材ファイルを持ち込まない)。
// 暗い金属の床板 + 継ぎ目 + 細かい汚れ + 砕けた破片。縁は中心から外へ滑らかに消える。
internal static class UnderlayArt
{
    private const int Size = 128;

    public static unsafe Sprite Make()
    {
        var px = new byte[Size * Size * 4];
        var rnd = new System.Random(7);

        // 破片の位置と大きさ
        const int chunks = 26;
        var cx = new float[chunks];
        var cy = new float[chunks];
        var cr = new float[chunks];
        var cs = new float[chunks];
        for (int k = 0; k < chunks; k++)
        {
            float a = (float)(rnd.NextDouble() * Math.PI * 2);
            float d = (float)Math.Sqrt(rnd.NextDouble()) * 0.85f;
            cx[k] = 0.5f + MathF.Cos(a) * d * 0.5f;
            cy[k] = 0.5f + MathF.Sin(a) * d * 0.5f;
            cr[k] = 0.012f + (float)rnd.NextDouble() * 0.035f;
            cs[k] = 0.25f + (float)rnd.NextDouble() * 0.3f;
        }

        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
            float du = u - 0.5f, dv = v - 0.5f;
            float r = MathF.Sqrt(du * du + dv * dv) * 2f;

            // 床板: 32px ごとの継ぎ目と板ごとの微妙な明暗
            int plate = (x / 32) * 7 + (y / 32) * 13;
            float c = 0.20f + (plate % 5) * 0.012f;
            if (x % 32 == 0 || y % 32 == 0) c *= 0.55f;
            c += (float)(rnd.NextDouble() - 0.5) * 0.05f; // 細かい汚れ

            // 破片 (明るい欠片 + 下側に影)
            for (int k = 0; k < chunks; k++)
            {
                float ex = u - cx[k], ey = v - cy[k];
                float e2 = ex * ex + ey * ey * 1.6f;
                if (e2 < cr[k] * cr[k]) { c = cs[k]; break; }
                float sy = ey + cr[k] * 0.6f;
                if (ex * ex + sy * sy * 1.6f < cr[k] * cr[k]) c *= 0.6f;
            }

            float alpha = r >= 1f ? 0f : r <= 0.8f ? 1f : 1f - (r - 0.8f) / 0.2f;
            int i = (y * Size + x) * 4;
            byte b = (byte)(Math.Clamp(c, 0f, 1f) * 255f);
            px[i] = b;
            px[i + 1] = b;
            px[i + 2] = (byte)Math.Min(255, b + 6);
            px[i + 3] = (byte)(alpha * 255f);
        }

        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            name = "MrpRubble",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);

        var sprite = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }
}
