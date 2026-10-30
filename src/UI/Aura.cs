using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 特別枠の役職の札を別格に見せる飾り: 札の外側の柔らかい光 (オーロラの色が巡りながら明滅) と、縁で瞬く小さな星。
// 中身の札はそのまま作り、光を後ろ・星を手前に重ねる。動かすのは画面に出ている間だけ (Tick)
internal sealed class Aura : Widget
{
    private const float Spill = 0.16f;         // 札の外へ光がはみ出す幅
    private const int Sparks = 6;
    private const float SparkPeriod = 1.6f;   // 1 つの星が瞬いて次の場所へ移るまで (秒)
    private const float SparkOn = 0.55f;      // そのうち見えている割合
    private const float CycleSpeed = 0.12f;   // 色が 1 巡する速さ (巡/秒 × 3 色)

    // オーロラの 3 色 (水色 → 青緑 → 紫 → 水色)
    private static readonly float[] Hues = { 0.345f, 0.831f, 1f, 0.18f, 0.9f, 0.69f, 0.663f, 0.545f, 1f };

    private static readonly List<Aura> Live = new();

    public Widget Content;
    public Func<bool> Dim; // 出ない設定の役職は光を弱く (一覧を作り直さずに設定が変わっても追う)

    private GameObject _go;
    private SpriteRenderer _glow;
    private readonly Transform[] _spark = new Transform[Sparks];
    private readonly SpriteRenderer[] _sparkSr = new SpriteRenderer[Sparks];
    private readonly float[] _offset = new float[Sparks], _scale = new float[Sparks], _shown = new float[Sparks];
    private readonly int[] _cycle = new int[Sparks];
    private Vector2 _size;

    public Aura(Widget content) { Content = content; }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        // 重ねる箱として作る (後の子ほど手前: 光 → 札 → 星)
        var go = Node(Layers.NodeName, parent, ctx.Layer);
        var content = Content.Build(go.transform, ctx, maxWidth, out size);
        content.transform.localPosition = Vector3.zero;
        _size = size;

        var glow = Node("Glow", go.transform, ctx.Layer);
        glow.transform.SetAsFirstSibling();
        glow.transform.localPosition = new Vector3(0f, 0f, 0.01f);
        _glow = glow.AddComponent<SpriteRenderer>();
        _glow.sprite = GlowSprite;
        _glow.drawMode = SpriteDrawMode.Sliced;
        _glow.size = new Vector2(size.x + Spill * 2f, size.y + Spill * 2f);

        var front = Node("Sparks", go.transform, ctx.Layer);
        front.transform.localPosition = new Vector3(0f, 0f, -0.05f);
        for (int i = 0; i < Sparks; i++)
        {
            var s = Node(UiScreen.FrontName, front.transform, ctx.Layer);
            _sparkSr[i] = s.AddComponent<SpriteRenderer>();
            _sparkSr[i].sprite = SparkSprite;
            _spark[i] = s.transform;
            _offset[i] = (float)i / Sparks + FxMath.Range(-0.05f, 0.05f);
            _cycle[i] = -1;
            s.transform.localScale = Vector3.zero;
        }
        _go = go;
        Apply(Time.unscaledTime);
        Live.Add(this);
        return go;
    }

    public static void Tick()
    {
        if (Live.Count == 0) return;
        float t = Time.unscaledTime;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var a = Live[i];
            if (!a._go)
            {
                Live.RemoveAt(i);
                continue;
            }
            // 閉じたタブの中 (非表示) では動かさない
            if (a._go.activeInHierarchy) a.Apply(t);
        }
    }

    private void Apply(float t)
    {
        float k = Dim != null && Dim() ? 0.4f : 1f;
        float pulse = 0.78f + 0.22f * FxMath.Sin(t * 2.4f);
        _glow.color = Hue(t * CycleSpeed, pulse * k);
        for (int i = 0; i < Sparks; i++)
        {
            float p = t / SparkPeriod + _offset[i];
            int cycle = (int)FxMath.Floor(p);
            float u = p - cycle;
            if (cycle != _cycle[i])
            {
                // 瞬き終えたら縁の別の場所へ
                _cycle[i] = cycle;
                _spark[i].localPosition = EdgePoint();
                _scale[i] = FxMath.Range(0.13f, 0.22f);
            }
            float s = u < SparkOn ? FxMath.Sin(u / SparkOn * 3.14159265f) : 0f;
            // 隠れている間は書き直さない
            if (s == 0f && _shown[i] == 0f) continue;
            _shown[i] = s;
            _spark[i].localScale = FxMath.V3(s * _scale[i], s * _scale[i], 1f);
            if (s > 0f)
            {
                // 白に色を 35% 混ぜる (Unity の色の演算は使わない)
                var c = Hue(t * CycleSpeed + i * 0.2f, 1f);
                _sparkSr[i].color = FxMath.Rgba(1f + (c.r - 1f) * 0.35f, 1f + (c.g - 1f) * 0.35f, 1f + (c.b - 1f) * 0.35f, k);
            }
        }
    }

    // 札の縁のどこか (少し内と外へばらす)
    private Vector3 EdgePoint()
    {
        float w = _size.x, h = _size.y;
        float d = FxMath.Range(0f, (w + h) * 2f);
        float j = FxMath.Range(-0.04f, 0.04f);
        float x, y;
        if (d < w) { x = d - w / 2f; y = h / 2f + j; }
        else if ((d -= w) < h) { x = w / 2f + j; y = d - h / 2f; }
        else if ((d -= h) < w) { x = d - w / 2f; y = -h / 2f + j; }
        else { d -= w; x = -w / 2f + j; y = d - h / 2f; }
        return FxMath.V3(x, y, 0f);
    }

    // オーロラの色 (pos は 1 巡 = 1)
    private static Color Hue(float pos, float alpha)
    {
        float f = FxMath.Repeat(pos, 1f) * 3f;
        int a = (int)f % 3, b = (a + 1) % 3;
        float u = f - (int)f;
        return FxMath.Rgba(
            Hues[a * 3] + (Hues[b * 3] - Hues[a * 3]) * u,
            Hues[a * 3 + 1] + (Hues[b * 3 + 1] - Hues[a * 3 + 1]) * u,
            Hues[a * 3 + 2] + (Hues[b * 3 + 2] - Hues[a * 3 + 2]) * u, alpha);
    }

    // ---- 絵 (式から 1 回だけ焼く) ----

    private static Sprite _glowSprite, _sparkSprite;

    // 角の丸い四角の外へ消えていく光。9 分割で札の大きさへ伸ばす (縁の幅 = Spill)
    private static Sprite GlowSprite => _glowSprite ? _glowSprite : _glowSprite = BakeGlow();

    private static unsafe Sprite BakeGlow()
    {
        const int n = 64, m = 24, r = 6;
        const float ppu = m / Spill;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            // 内側の四角 (縁から m 内へ・角の丸み r) からの外向きの距離
            float half = n / 2f - m;
            float qx = FxMath.Abs(x + 0.5f - n / 2f) - (half - r), qy = FxMath.Abs(y + 0.5f - n / 2f) - (half - r);
            float ox = FxMath.Max(qx, 0f), oy = FxMath.Max(qy, 0f);
            float d = FxMath.Max(FxMath.Sqrt(ox * ox + oy * oy) - r, 0f);
            float a = 1f - FxMath.Clamp01(d / m);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * FxMath.Sqrt(a) * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpUiGlow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        fixed (byte* p = px) tex.LoadRawTextureData((System.IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        const float b = m + r;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 細い 4 本の光の星 (高さ 1 単位)
    private static Sprite SparkSprite => _sparkSprite ? _sparkSprite : _sparkSprite = FxSprites.Rect(32, 32, (x, y) =>
    {
        float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
        float ray = FxMath.Max(1f - ay * 2f - ax * 14f, 1f - ax * 2f - ay * 14f);
        return FxMath.Max(ray * 1.6f, FxSprites.Dot(x * 2.6f, y * 2.6f));
    }, "MrpUiSpark");
}
