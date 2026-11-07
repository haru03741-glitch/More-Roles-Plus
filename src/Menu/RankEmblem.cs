using System;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// ランクの印 (盾の形のエンブレム)。段の色の盾に、中の印で段が分かる:
//   ブロンズ = 山形 1 本・シルバー = 2 本・ゴールド = 3 本・プラチナ = 星・ダイヤ = ひし形・マスター = 王冠
// 絵はその場で描いて段ごとに 1 枚だけ持つ
internal static class RankEmblem
{
    private const int Size = 128;
    private static readonly Sprite[] Cache = new Sprite[6];

    public static Sprite Get(int tier)
    {
        tier = Mathf.Clamp(tier, 0, 5);
        if (Cache[tier]) return Cache[tier];
        return Cache[tier] = Draw(tier);
    }

    // 盾の外形 (0〜1 の座標・上が 1)
    private static readonly Vector2[] Shield =
    {
        new(0.14f, 0.90f), new(0.36f, 0.95f), new(0.50f, 0.99f), new(0.64f, 0.95f), new(0.86f, 0.90f),
        new(0.86f, 0.52f), new(0.80f, 0.34f), new(0.68f, 0.18f), new(0.50f, 0.03f),
        new(0.32f, 0.18f), new(0.20f, 0.34f), new(0.14f, 0.52f),
    };

    private static Sprite Draw(int tier)
    {
        var baseColor = MrpAccount.TierColors[tier];
        var light = Color.Lerp(baseColor, Color.white, 0.45f);
        var dark = Color.Lerp(baseColor, Color.black, 0.45f);
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var p = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size);
            float d = SignedDist(p, Shield) * Size; // 内側が負 (画素)
            if (d > 1.5f) { px[y * Size + x] = Color.clear; continue; }
            Color c;
            if (d > -2.5f) c = new Color(0.05f, 0.06f, 0.10f, 1f);                       // 外の濃い縁
            else if (d > -8f) c = Color.Lerp(light, Color.white, 0.25f * (1f - p.y));     // 明るい枠
            else if (d > -10f) c = dark;                                                  // 枠の内側の影
            else c = Color.Lerp(dark, baseColor, Mathf.Clamp01(p.y * 1.3f));             // 中 (上ほど明るい)
            // 上の艶
            if (d < -10f && p.y > 0.62f && p.x < 0.55f) c = Color.Lerp(c, Color.white, 0.10f);
            float a = Mathf.Clamp01(1.5f - d);
            c.a = a;
            px[y * Size + x] = c;
        }
        Symbol(px, tier);
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "MrpRankEmblem" + tier, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        tex.SetPixels(px);
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
        sp.name = "MrpRankEmblem" + tier;
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 中の印 (白・下に少し影)
    private static void Symbol(Color[] px, int tier)
    {
        Func<Vector2, float> shape = tier switch
        {
            0 => p => Chevrons(p, 1),
            1 => p => Chevrons(p, 2),
            2 => p => Chevrons(p, 3),
            3 => p => Star(p),
            4 => p => Diamond(p),
            _ => p => Crown(p),
        };
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var p = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size);
            int i = y * Size + x;
            if (px[i].a <= 0f) continue;
            float shadow = Mathf.Clamp01(1.2f - shape(p + new Vector2(-0.012f, 0.018f)) * Size);
            float a = Mathf.Clamp01(0.8f - shape(p) * Size);
            var c = px[i];
            c = Color.Lerp(c, new Color(0f, 0f, 0f, c.a), shadow * 0.35f);
            c = Color.Lerp(c, new Color(1f, 0.99f, 0.95f, c.a), a);
            px[i] = c;
        }
    }

    // 山形 (∧ を上向きに n 本重ねる)。値 = 形の外への距離 (0〜1 の単位・内側は負)
    private static float Chevrons(Vector2 p, int n)
    {
        float best = 9f;
        float gap = 0.13f, top = 0.58f + (n - 1) * gap * 0.5f;
        for (int k = 0; k < n; k++)
        {
            float cy = top - k * gap;
            // 頂点 (0.5, cy)・左右へ下がる線分 2 本 (太さ 0.045)
            float d1 = SegDist(p, new Vector2(0.30f, cy - 0.12f), new Vector2(0.5f, cy));
            float d2 = SegDist(p, new Vector2(0.70f, cy - 0.12f), new Vector2(0.5f, cy));
            best = Mathf.Min(best, Mathf.Min(d1, d2) - 0.04f);
        }
        return best;
    }

    private static float Star(Vector2 p)
    {
        var pts = new Vector2[10];
        for (int k = 0; k < 10; k++)
        {
            float ang = Mathf.PI / 2f + k * Mathf.PI / 5f;
            float r = k % 2 == 0 ? 0.25f : 0.105f;
            pts[k] = new Vector2(0.5f + Mathf.Cos(ang) * r, 0.53f + Mathf.Sin(ang) * r);
        }
        return SignedDist(p, pts);
    }

    private static float Diamond(Vector2 p)
    {
        var pts = new[] { new Vector2(0.5f, 0.78f), new Vector2(0.70f, 0.55f), new Vector2(0.5f, 0.26f), new Vector2(0.30f, 0.55f) };
        float outer = SignedDist(p, pts);
        // 中の切り子の線 (少し暗く見せるため、形の外として扱う細い溝)
        float groove = Mathf.Min(SegDist(p, new Vector2(0.30f, 0.55f), new Vector2(0.70f, 0.55f)), SegDist(p, new Vector2(0.5f, 0.78f), new Vector2(0.5f, 0.26f))) - 0.008f;
        return groove < 0f ? -groove + 0.002f : outer;
    }

    private static float Crown(Vector2 p)
    {
        var pts = new[]
        {
            new Vector2(0.26f, 0.36f), new Vector2(0.74f, 0.36f), new Vector2(0.77f, 0.70f), new Vector2(0.63f, 0.52f),
            new Vector2(0.5f, 0.76f), new Vector2(0.37f, 0.52f), new Vector2(0.23f, 0.70f),
        };
        float body = SignedDist(p, pts);
        float band = SignedDist(p, new[] { new Vector2(0.26f, 0.28f), new Vector2(0.74f, 0.28f), new Vector2(0.74f, 0.33f), new Vector2(0.26f, 0.33f) });
        return Mathf.Min(body, band);
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return (p - (a + ab * t)).magnitude;
    }

    // 多角形までの符号付きの距離 (内側が負)
    private static float SignedDist(Vector2 p, Vector2[] poly)
    {
        float d = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[j];
            var b = poly[i];
            d = Mathf.Min(d, SegDist(p, a, b));
            if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x) inside = !inside;
        }
        return inside ? -d : d;
    }
}
