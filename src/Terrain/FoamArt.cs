using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 外壁の穴の補修フォームの見た目。口の線の両端から泡の粒が膨らみ、FoamTime (2.5 秒) で中央まで埋まる。
// 膨らむ間は濡れた明るいクリーム色で艶があり、ふさがる頃に乾いたくすんだ色へ変わって、ふさがった後も跡として残る。
// ふさがる時刻は Decompression の刻み (全員同じ) で決まり、見た目はそこから手元の時計で滑らかに進める。物がふさいだ口には出さない。
// 重ね方 (奥から): 暗い下地 → 泡の粒 (陰影を焼いた絵) → 白い艶
internal static class FoamArt
{
    private const float Spacing = 0.15f;        // 線に沿った粒の間隔
    private const int MaxBlobs = 220;
    private const float GrowShare = 0.38f;      // 1 粒が膨らみ切るまで (FoamTime に対する割合)
    private const float SpreadShare = 0.5f;     // 端から中央の粒が膨らみ始めるまで
    private const float CureFrom = 0.7f;        // ここから乾いた色へ

    private sealed class Blob
    {
        public Transform Tf, ShadowTf, ShineTf;
        public SpriteRenderer Sr, Shine;
        public float X, Y, Z, Size, Start, Phase, Tint;
        public bool Done;
    }

    private sealed class Foam
    {
        public readonly List<Blob> Blobs = new();
        public float T0;                        // 手元の時計での膨らみ始め
        public bool Done;
    }

    private static readonly Dictionary<Decompression.Breach, Foam> Foams = new();
    private static GameObject _root;
    private static int _shipGen = -1, _failedGen = -2;
    private static Sprite[] _blobSprites;
    private static Sprite _shineSprite;

    private static readonly Color WetColor = new(1f, 0.96f, 0.8f, 1f);
    private static readonly Color DryColor = new(0.86f, 0.8f, 0.63f, 1f);

    public static void Tick()
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            Foams.Clear();
            _root = null;
        }
        if (!Decompression.Running)
        {
            if (Foams.Count > 0 && !GameClock.ShipAlive) Foams.Clear();
            return;
        }
        if (_failedGen == _shipGen) return;
        try { TickCore(); }
        catch (Exception e)
        {
            // 同じ船では作り直しても同じ所で落ちるので、この試合の泡は止める
            Plugin.Logger.LogError($"[FoamArt] tick: {e}");
            _failedGen = _shipGen;
        }
    }

    private static void TickCore()
    {
        var list = Decompression.OpenedBreaches;
        float now = -1f;
        for (int i = 0; i < list.Count; i++)
        {
            var br = list[i];
            if (br.ByProp) continue;
            if (!Foams.TryGetValue(br, out var f))
            {
                int age = Decompression.Step - br.Start - Decompression.FoamDelay;
                if (age < 0) continue;
                if (now < 0f) now = Time.time;
                f = Build(br, now - age / (float)GameClock.Hz);
                Foams[br] = f;
            }
            if (f.Done) continue;
            if (now < 0f) now = Time.time;
            Animate(f, (now - f.T0) / (Decompression.FoamTime / (float)GameClock.Hz));
        }
    }

    private static Foam Build(Decompression.Breach br, float t0)
    {
        var f = new Foam { T0 = t0 };
        if (!EnsureRoot()) { f.Done = true; return f; }
        var rnd = new System.Random(br.Start * 7919 + br.Lines.Count);
        foreach (var (a, b) in br.Lines)
        {
            float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.05f) continue;
            float ux = dx / len, uy = dy / len;
            // 船の中を向く法線 (線の中点から少し離れた点が宇宙でなく歩ける側)
            float nx = -uy, ny = ux;
            float mx = (a.x + b.x) * 0.5f, my = (a.y + b.y) * 0.5f;
            float px = mx + nx * 0.35f, py = my + ny * 0.35f, qx = mx - nx * 0.35f, qy = my - ny * 0.35f;
            bool inP = !SolidMap.BareSky(px, py) && Decompression.InsideAt(FxMath.V2(px, py));
            bool inQ = !SolidMap.BareSky(qx, qy) && Decompression.InsideAt(FxMath.V2(qx, qy));
            if (!inP && inQ) { nx = -nx; ny = -ny; }
            int n = Math.Max(3, (int)MathF.Ceiling(len / Spacing) + 1);
            // 列: 線の上 (宇宙側へ少しはみ出す)・船の中へ 1 段・まばらな垂れ
            AddRow(f, rnd, a, ux, uy, nx, ny, len, n, -0.03f, 0.28f, 1f);
            AddRow(f, rnd, a, ux, uy, nx, ny, len, n, 0.14f, 0.26f, 1f);
            AddRow(f, rnd, a, ux, uy, nx, ny, len, n, 0.3f, 0.2f, 0.8f);
            AddRow(f, rnd, a, ux, uy, nx, ny, len, Math.Max(2, n / 2), 0.43f, 0.13f, 0.5f);
        }
        Plugin.Logger.LogInfo($"[FoamArt] foam start={br.Start} blobs={f.Blobs.Count} lines={br.Lines.Count}");
        return f;
    }

    private static void AddRow(Foam f, System.Random rnd, Vector2 a, float ux, float uy, float nx, float ny, float len, int n, float off, float size, float chance)
    {
        for (int i = 0; i < n && f.Blobs.Count < MaxBlobs; i++)
        {
            if (chance < 1f && rnd.NextDouble() > chance) continue;
            float t = n == 1 ? 0.5f : i / (float)(n - 1);
            float along = t * len + ((float)rnd.NextDouble() - 0.5f) * Spacing * 0.5f;
            float side = off + ((float)rnd.NextDouble() - 0.5f) * 0.05f;
            float x = a.x + ux * along + nx * side, y = a.y + uy * along + ny * side;
            float edge = MathF.Min(t, 1f - t) * 2f;   // 0 = 端・1 = 中央
            var bl = new Blob
            {
                X = x,
                Y = y,
                Z = y / 1000f - 0.0004f,
                Size = size * (0.8f + 0.4f * (float)rnd.NextDouble()),
                Start = edge * SpreadShare + (float)rnd.NextDouble() * 0.05f + MathF.Max(0f, off) * 0.3f,
                Phase = (float)rnd.NextDouble() * 6.28f,
                Tint = 0.93f + 0.07f * (float)rnd.NextDouble(),
            };
            Make(bl, rnd.Next(_blobSprites.Length));
            f.Blobs.Add(bl);
        }
    }

    private static void Make(Blob bl, int variant)
    {
        var go = new GameObject("MrpFoam") { layer = 0 };
        go.transform.SetParent(_root.transform, false);
        bl.Tf = go.transform;
        bl.Sr = go.AddComponent<SpriteRenderer>();
        bl.Sr.sprite = _blobSprites[variant];
        bl.Sr.color = WetColor;
        bl.Tf.position = FxMath.V3(bl.X, bl.Y, bl.Z);
        bl.Tf.localRotation = FxMath.RotZ(bl.Phase * 57f);
        bl.Tf.localScale = FxMath.V3(0f, 0f, 1f);

        var sh = new GameObject("MrpFoamShade") { layer = 0 };
        sh.transform.SetParent(_root.transform, false);
        bl.ShadowTf = sh.transform;
        var ssr = sh.AddComponent<SpriteRenderer>();
        ssr.sprite = _blobSprites[variant];
        ssr.color = FxMath.Rgba(0.1f, 0.08f, 0.05f, 0.4f);
        bl.ShadowTf.position = FxMath.V3(bl.X + 0.015f, bl.Y - 0.03f, bl.Z + 0.0002f);
        bl.ShadowTf.localScale = FxMath.V3(0f, 0f, 1f);

        var sn = new GameObject("MrpFoamShine") { layer = 0 };
        sn.transform.SetParent(_root.transform, false);
        bl.ShineTf = sn.transform;
        bl.Shine = sn.AddComponent<SpriteRenderer>();
        bl.Shine.sprite = _shineSprite;
        bl.Shine.color = FxMath.Rgba(1f, 1f, 1f, 0.85f);
        bl.ShineTf.localScale = FxMath.V3(0f, 0f, 1f);
    }

    // p = 膨らみ始めからの経過 / FoamTime
    private static void Animate(Foam f, float p)
    {
        bool all = true;
        float cure = FxMath.Clamp01((p - CureFrom) / (1f - CureFrom));
        float r = WetColor.r + (DryColor.r - WetColor.r) * cure;
        float g = WetColor.g + (DryColor.g - WetColor.g) * cure;
        float b = WetColor.b + (DryColor.b - WetColor.b) * cure;
        float shineA = 0.85f - 0.65f * cure;
        foreach (var bl in f.Blobs)
        {
            if (bl.Done) continue;
            float k = (p - bl.Start) / GrowShare;
            if (k <= 0f) { all = false; continue; }
            float s;
            if (k >= 1f && p >= 1f) { s = 1f; bl.Done = true; }
            else
            {
                all = false;
                float kk = FxMath.Clamp01(k);
                // 少し行き過ぎて戻る膨らみ + 泡立ちの小さな脈
                float c1 = 1.70158f, c3 = c1 + 1f, q = kk - 1f;
                s = 1f + c3 * q * q * q + c1 * q * q;
                s *= 1f + 0.05f * FxMath.Sin(p * 26f + bl.Phase) * (1f - cure);
            }
            float sz = bl.Size * s, sy = sz * (1f + 0.07f * FxMath.Sin(bl.Phase * 3f));
            bl.Tf.localScale = FxMath.V3(sz, sy, 1f);
            bl.ShadowTf.localScale = FxMath.V3(sz * 1.12f, sy * 1.12f, 1f);
            bl.ShineTf.position = FxMath.V3(bl.X - sz * 0.16f, bl.Y + sz * 0.18f, bl.Z - 0.0002f);
            bl.ShineTf.localScale = FxMath.V3(sz * 0.42f, sz * 0.3f, 1f);
            bl.Sr.color = FxMath.Rgba(r * bl.Tint, g * bl.Tint, b * bl.Tint, 1f);
            bl.Shine.color = FxMath.Rgba(1f, 1f, 1f, shineA);
        }
        if (all && p >= 1f) f.Done = true;
    }

    private static bool EnsureRoot()
    {
        if (_root) return true;
        if (!ShipStatus.Instance) return false;
        _root = new GameObject("MrpFoamRoot") { layer = 0 };
        _root.transform.SetParent(ShipStatus.Instance.transform, false);
        GameClock.Ship.Bind(_root);
        if (_blobSprites == null)
        {
            _blobSprites = new Sprite[4];
            for (int i = 0; i < _blobSprites.Length; i++) _blobSprites[i] = BakeBlob(new System.Random(900 + i), "MrpFoamBlob" + i);
            _shineSprite = BakeShine();
        }
        return true;
    }

    // 泡の粒 (1 単位・64 px): 縁の少しでこぼこした丸に、左上から光が当たった陰影と暗い縁を焼く。色は SpriteRenderer で掛ける
    private static unsafe Sprite BakeBlob(System.Random rnd, string name)
    {
        const int n = 64;
        float p1 = (float)rnd.NextDouble() * 6.28f, p2 = (float)rnd.NextDouble() * 6.28f;
        float a1 = 0.035f + 0.02f * (float)rnd.NextDouble(), a2 = 0.02f + 0.015f * (float)rnd.NextDouble();
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float fx = (x + 0.5f) / n - 0.5f, fy = (y + 0.5f) / n - 0.5f;
            float d = MathF.Sqrt(fx * fx + fy * fy), th = MathF.Atan2(fy, fx);
            float rr = 0.42f + a1 * MathF.Sin(3f * th + p1) + a2 * MathF.Sin(5f * th + p2);
            float u = d / rr;
            float alpha = Math.Clamp((1f - u) * rr * n, 0f, 1f);
            float nz = MathF.Sqrt(MathF.Max(0f, 1f - u * u));
            float lx = fx / rr, ly = fy / rr;
            float light = Math.Clamp(-0.55f * lx + 0.6f * ly + 0.58f * nz, 0f, 1f);
            float shade = 0.7f + 0.3f * light;
            if (u > 0.84f) shade *= 0.72f + 0.28f * (1f - (u - 0.84f) / 0.16f);
            byte c = (byte)(Math.Clamp(shade, 0f, 1f) * 255f);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = c;
            px[i + 3] = (byte)(alpha * 255f);
        }
        return Make(px, n, name);
    }

    // 艶: 白い柔らかい楕円 (1 単位)
    private static unsafe Sprite BakeShine()
    {
        const int n = 32;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float fx = (x + 0.5f) / n - 0.5f, fy = (y + 0.5f) / n - 0.5f;
            float d = MathF.Sqrt(fx * fx + fy * fy) * 2f;
            float a = Math.Clamp(1f - d, 0f, 1f);
            a *= a;
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f);
        }
        return Make(px, n, "MrpFoamShine");
    }

    private static unsafe Sprite Make(byte[] px, int n, string name)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    internal static void Register()
    {
        TestBridge.Register("foam", "補修フォームの見た目: 口ごとの粒の数・膨らみ終わったか", (_, reply) =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in Foams) sb.Append($" [start={kv.Key.Start} blobs={kv.Value.Blobs.Count} done={kv.Value.Done} t={Time.time - kv.Value.T0:0.00}]");
            reply($"OK foam n={Foams.Count}{sb}");
        });
    }
}
