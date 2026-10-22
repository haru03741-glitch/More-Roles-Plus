using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 構築士の設計台の絵: 斜め上から見た製図台。手前へ傾いた板に青い図面 (方眼と白い壁の線)、濃い鉄の枠と脚 4 本・横木。
// 部品ごとに太い黒の縁取り・面の明暗 (光は左上から) を 1 枚の RGBA に焼き、床に接地の影を敷く。
// 計算は C# の数値だけなので作業スレッドで作れる。役職が付いた時に作り始め、初めて置く時に受け取る
internal static class ArchitectDeskArt
{
    internal sealed class Handle
    {
        public float X, Y;     // 床の点 (台の真ん中の真下)
        public GameObject Root;
    }

    // 絵の範囲 (床の点が原点・単位は世界の長さ)
    private const float Left = -0.66f, Right = 0.66f, Bottom = -0.16f, Top = 0.98f;
    private const int Ppu = 200;
    private const int Super = 2;
    private const float Outline = 0.032f;
    private const int DeskLayer = 0;          // 仕掛け壁の絵と同じ層 (人と z で前後し、視界の外は家具と同じく暗く見える)
    private const float BodyLift = 0.3636f;   // 人の奥行きは体の y/1000 で、体は足元よりこれだけ上

    private static readonly float[] OutRgb = { 0.07f, 0.06f, 0.10f };
    private static readonly float[] Steel = { 0.36f, 0.39f, 0.45f };
    private static readonly float[] SteelDark = { 0.22f, 0.24f, 0.29f };
    private static readonly float[] Paper = { 0.13f, 0.36f, 0.68f };
    private static readonly float[] PaperGrid = { 0.33f, 0.58f, 0.88f };
    private static readonly float[] Ink = { 0.90f, 0.96f, 1f };

    private static int TexW => (int)MathF.Round((Right - Left) * Ppu);
    private static int TexH => (int)MathF.Round((Top - Bottom) * Ppu);

    private static Task<byte[]> _bake;
    private static Sprite _sprite;
    private static GameObject _container;
    private static int _gen = -1;
    private static readonly List<Handle> Live = new();

    internal static int LiveCount => Live.Count;

    // 確かめ用: 最初の設計台の描画の状態と、焼いた絵の不透明な画素の数
    internal static string Probe()
    {
        if (Live.Count == 0 || !Live[0].Root) return "none";
        var sr = Live[0].Root.GetComponent<SpriteRenderer>();
        var t = Live[0].Root.transform;
        int solid = 0;
        if (_bake is { IsCompletedSuccessfully: true })
            for (int i = 3; i < _bake.Result.Length; i += 4) if (_bake.Result[i] > 128) solid++;
        Vector3 p = t.position, s = t.lossyScale;
        return $"en={sr.enabled} active={Live[0].Root.activeInHierarchy} layer={Live[0].Root.layer} pos=({p.x:0.000},{p.y:0.000},{p.z:0.00000}) scale=({s.x:0.00},{s.y:0.00},{s.z:0.00}) solid={solid}";
    }

    internal static void Prewarm() => _bake ??= Task.Run(Bake);

    internal static Handle Place(float x, float y)
    {
        var parent = Container();
        var sp = EnsureSprite();
        if (!parent || !sp) return null;
        var root = new GameObject("MrpArchitectDesk") { layer = DeskLayer };
        root.transform.SetParent(parent.transform, false);
        root.transform.position = FxMath.V3(x, y, (y + BodyLift) / 1000f); // 船の根は z が 0 でない (Skeld は 8) ので世界の座標で置く
        var sr = root.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        var h = new Handle { X = x, Y = y, Root = root };
        Live.Add(h);
        return h;
    }

    internal static void Remove(Handle h)
    {
        if (h == null) return;
        Live.Remove(h);
        if (h.Root) UnityEngine.Object.Destroy(h.Root);
    }

    // 船ごとに 1 つの入れ物 (船と一緒に消える)。船の倍率と反転は打ち消して世界の座標で置く
    private static GameObject Container()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return null;
        if (_gen == GameClock.ShipGen && _container) return _container;
        Live.Clear();
        _container = new GameObject("MrpArchitectDesks");
        _container.transform.SetParent(ship.transform, false);
        Vector3 ls = ship.transform.lossyScale;
        _container.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _gen = GameClock.ShipGen;
        return _container;
    }

    private static unsafe Sprite EnsureSprite()
    {
        if (_sprite) return _sprite;
        byte[] px;
        try
        {
            Prewarm();
            px = _bake.Result;
        }
        catch (Exception e)
        {
            _bake = null;
            Plugin.Logger.LogError($"[ArchitectDesk] bake: {e}");
            return null;
        }
        int w = TexW, h = TexH;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpArchitectDesk", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        var pivot = FxMath.V2(-Left / (Right - Left), -Bottom / (Top - Bottom));
        _sprite = Sprite.Create(tex, new Rect(0, 0, w, h), pivot, Ppu, 0, SpriteMeshType.FullRect);
        _sprite.hideFlags |= HideFlags.HideAndDontSave;
        return _sprite;
    }

    // ---- 焼く (作業スレッド) ----

    // 凸の多角形 (左回り)。点との符号付きの距離 (内側が負) を返す
    private sealed class Part
    {
        public readonly float[] X, Y, Nx, Ny;
        public readonly int Kind;

        public Part(int kind, params float[] xy)
        {
            Kind = kind;
            int n = xy.Length / 2;
            X = new float[n]; Y = new float[n]; Nx = new float[n]; Ny = new float[n];
            for (int i = 0; i < n; i++) { X[i] = xy[i * 2]; Y[i] = xy[i * 2 + 1]; }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float ex = X[j] - X[i], ey = Y[j] - Y[i], len = MathF.Sqrt(ex * ex + ey * ey);
                Nx[i] = ey / len; Ny[i] = -ex / len;
            }
        }

        public float Dist(float px, float py)
        {
            float d = float.MinValue;
            for (int i = 0; i < X.Length; i++) d = MathF.Max(d, (px - X[i]) * Nx[i] + (py - Y[i]) * Ny[i]);
            return d;
        }
    }

    private const int LegBack = 0, LegFront = 1, Bar = 2, Edge = 3, Board = 4, Sheet = 5;

    // 奥から手前の順 (後の部品が上に描かれる)
    private static readonly Part[] Parts =
    {
        new(LegBack, -0.44f, 0.12f, -0.38f, 0.12f, -0.38f, 0.56f, -0.44f, 0.56f),
        new(LegBack, 0.38f, 0.12f, 0.44f, 0.12f, 0.44f, 0.56f, 0.38f, 0.56f),
        new(Bar, -0.42f, 0.13f, 0.42f, 0.13f, 0.42f, 0.19f, -0.42f, 0.19f),
        new(LegFront, -0.50f, -0.02f, -0.42f, -0.02f, -0.42f, 0.44f, -0.50f, 0.44f),
        new(LegFront, 0.42f, -0.02f, 0.50f, -0.02f, 0.50f, 0.44f, 0.42f, 0.44f),
        new(Edge, -0.58f, 0.40f, 0.58f, 0.40f, 0.58f, 0.48f, -0.58f, 0.48f),
        new(Board, -0.58f, 0.48f, 0.58f, 0.48f, 0.52f, 0.92f, -0.52f, 0.92f),
        new(Sheet, -0.49f, 0.53f, 0.49f, 0.53f, 0.44f, 0.87f, -0.44f, 0.87f),
    };

    // 図面の白い線 (紙の上の 0〜1 の座標・折れ線)
    private static readonly float[][] InkLines =
    {
        new[] { 0.10f, 0.15f, 0.10f, 0.82f, 0.48f, 0.82f },
        new[] { 0.30f, 0.45f, 0.62f, 0.45f },
        new[] { 0.88f, 0.82f, 0.88f, 0.15f, 0.55f, 0.15f },
    };
    private const float InkWidth = 0.035f;    // 紙の上の線の太さ (紙の高さに対して)

    private static byte[] Bake()
    {
        int w = TexW, h = TexH;
        var px = new byte[w * h * 4];
        float step = 1f / (Ppu * Super);
        Span<float> acc = stackalloc float[4];
        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                acc.Clear();
                for (int sy = 0; sy < Super; sy++)
                {
                    for (int sx = 0; sx < Super; sx++)
                    {
                        float x = Left + (ix * Super + sx + 0.5f) * step;
                        float y = Bottom + (iy * Super + sy + 0.5f) * step;
                        Sample(x, y, out float r, out float g, out float b, out float a);
                        acc[0] += r * a; acc[1] += g * a; acc[2] += b * a; acc[3] += a;
                    }
                }
                int k = (iy * w + ix) * 4;
                float n = Super * Super, al = acc[3] / n;
                float inv = acc[3] > 1e-5f ? 1f / acc[3] : 0f;
                px[k] = ToByte(acc[0] * inv);
                px[k + 1] = ToByte(acc[1] * inv);
                px[k + 2] = ToByte(acc[2] * inv);
                px[k + 3] = ToByte(al);
            }
        }
        return px;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private static void Sample(float x, float y, out float r, out float g, out float b, out float a)
    {
        // 接地の影 (脚の下に横長のぼけた楕円)
        float ex = x / 0.6f, ey = (y - 0.02f) / 0.11f, e = ex * ex + ey * ey;
        r = g = b = 0f;
        a = e < 1f ? 0.38f * (1f - e) * (1f - e) : 0f;
        foreach (var p in Parts)
        {
            float d = p.Dist(x, y);
            if (d > Outline) continue;
            a = 1f;
            if (d > 0f) { r = OutRgb[0]; g = OutRgb[1]; b = OutRgb[2]; continue; }
            Shade(p, x, y, -d, out r, out g, out b);
        }
    }

    private static void Shade(Part p, float x, float y, float inside, out float r, out float g, out float b)
    {
        float[] c;
        float lit;
        switch (p.Kind)
        {
            case LegBack:
                c = SteelDark; lit = 0.8f;
                break;
            case LegFront:
                // 左の縁に照り
                c = Steel; lit = x - p.X[0] < 0.025f ? 1.35f : 1f;
                break;
            case Bar:
                c = SteelDark; lit = y > 0.17f ? 1.2f : 0.95f;
                break;
            case Edge:
                c = SteelDark; lit = y > 0.46f ? 1.25f : 1f;
                break;
            case Board:
                // 左上ほど明るい・上の縁に照り
                c = Steel; lit = 1.05f - 0.18f * x + 0.25f * (y - 0.48f) + (y > 0.895f ? 0.25f : 0f);
                break;
            default:
                SheetColor(x, y, out r, out g, out b);
                return;
        }
        // 縁取りのすぐ内側を少し暗く (厚みの影)
        if (inside < 0.012f) lit *= 0.85f;
        r = MathF.Min(1f, c[0] * lit); g = MathF.Min(1f, c[1] * lit); b = MathF.Min(1f, c[2] * lit);
    }

    private static void SheetColor(float x, float y, out float r, out float g, out float b)
    {
        // 台形の紙の上の 0〜1 の座標
        const float y0 = 0.53f, y1 = 0.87f;
        float v = (y - y0) / (y1 - y0);
        float half = 0.49f + (0.44f - 0.49f) * v;
        float u = (x + half) / (2f * half);
        float[] c = Paper;
        // 方眼 (10 × 6)・少し照りの帯 (左上から右下へ)
        float gu = u * 10f, gv = v * 6f;
        float fu = gu - MathF.Floor(gu), fv = gv - MathF.Floor(gv);
        if (fu < 0.08f || fu > 0.92f || fv < 0.1f || fv > 0.9f) c = PaperGrid;
        float lit = 1f + 0.12f * (v - u);
        float sheen = (u + v) - 1.15f;
        if (sheen > 0f && sheen < 0.12f) lit += 0.12f;
        for (int i = 0; i < InkLines.Length; i++)
        {
            var l = InkLines[i];
            for (int k = 0; k + 3 < l.Length; k += 2)
            {
                if (SegDist(u, v * 0.62f, l[k], l[k + 1] * 0.62f, l[k + 2], l[k + 3] * 0.62f) >= InkWidth * 0.5f) continue;
                r = Ink[0]; g = Ink[1]; b = Ink[2];
                return;
            }
        }
        r = MathF.Min(1f, c[0] * lit); g = MathF.Min(1f, c[1] * lit); b = MathF.Min(1f, c[2] * lit);
    }

    private static float SegDist(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        float t = l2 > 0f ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0f, 1f) : 0f;
        float qx = ax + dx * t - px, qy = ay + dy * t - py;
        return MathF.Sqrt(qx * qx + qy * qy);
    }
}
