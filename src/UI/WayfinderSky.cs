using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 航路守 (特別枠) の場面の背景: 夜空・揺れるオーロラのカーテン・瞬く星・下の夜明けの帯・月の船の印。
// 役職の発表と勝った時の終わりの画面で使う。修羅 (Blood) は空と光を赤黒くし、欠けた印にする。
// 重ね順 (奥 → 手前) = 夜空 → 星 → オーロラ → 夜明けの帯 → 印の後ろの光 → 印。動かすのは出ている間だけ
internal sealed class WayfinderSky
{
    public enum Tone { Dawn, Blood }

    private const int Curtains = 5, Stars = 40;
    private const float FadeIn = 0.3f, MarkAt = 0.35f, MarkIn = 0.4f;

    private static readonly List<WayfinderSky> Live = new();
    private static Sprite _vramp, _curtain, _dawnL, _dawnR, _dot, _soft;

    private GameObject _go;
    private SpriteRenderer _night, _nightLow, _dawnBlue, _dawnPink, _markGlow, _mark;
    private readonly SpriteRenderer[] _curtain_ = new SpriteRenderer[Curtains], _star = new SpriteRenderer[Stars];
    private readonly Color[] _curtainC = new Color[Curtains];
    private readonly float[] _curtainX = new float[Curtains], _curtainF = new float[Curtains], _curtainA = new float[Curtains];
    private readonly float[] _starA = new float[Stars], _starF = new float[Stars], _starP = new float[Stars];
    private Color _nightC, _nightLowC, _blueC, _pinkC, _glowC;
    private float _t0, _markH, _markA, _markK, _curtainY, _alpha = 1f;
    private bool _hasGlow, _hasMark;

    public GameObject Root => _go;
    // 全体の濃さ (消える時に呼ぶ側が下げる)
    public float Alpha { set => _alpha = value; }

    // parent の localPosition (cx, cy, z) を中心に w×h を埋める。markH = 印の高さ (0 = 出さない)。markAlpha = 印の濃さ
    public static WayfinderSky Make(Transform parent, float cx, float cy, float z, float w, float h, Tone tone, int layer, float markH, float markAlpha)
    {
        var s = new WayfinderSky();
        s.Build(parent, cx, cy, z, w, h, tone, layer, markH, markAlpha);
        Live.Add(s);
        return s;
    }

    private void Build(Transform parent, float cx, float cy, float z, float w, float h, Tone tone, int layer, float markH, float markAlpha)
    {
        bool blood = tone == Tone.Blood;
        _go = new GameObject("WayfinderSky") { layer = layer };
        var tf = _go.transform;
        tf.SetParent(parent, false);
        tf.localPosition = FxMath.V3(cx, cy, z);
        _t0 = Time.time;

        _nightC = blood ? FxMath.Rgba(0.04f, 0.008f, 0.016f, 1f) : FxMath.Rgba(0.02f, 0.027f, 0.06f, 1f);
        _nightLowC = blood ? FxMath.Rgba(0.17f, 0.02f, 0.03f, 1f) : FxMath.Rgba(0.1f, 0.063f, 0.19f, 1f);
        _night = Quad("Night", DuelHud.WhiteSprite(), 0f, 0f, 0.05f, w, h, layer);
        _nightLow = Quad("NightLow", VRamp, 0f, -h * 0.2f, 0.04f, w, h * 0.6f, layer);

        var rng = new System.Random(blood ? 7 : 3);
        for (int i = 0; i < Stars; i++)
        {
            float sx = ((float)rng.NextDouble() - 0.5f) * w, sy = h * (-0.2f + 0.7f * (float)rng.NextDouble());
            float sz = 0.03f + 0.05f * (float)rng.NextDouble();
            _star[i] = Quad("Star", Dot, sx, sy, 0.03f, sz, sz, layer);
            _starA[i] = 0.35f + 0.65f * (float)rng.NextDouble();
            _starF[i] = 0.6f + 1.6f * (float)rng.NextDouble();
            _starP[i] = (float)rng.NextDouble() * 6.283f;
        }

        // カーテンは画面の上半分へ・下の縁がいちばん明るい (本物のオーロラの裾)
        Color[] pal = blood
            ? new[] { FxMath.Rgba(1f, 0.23f, 0.23f, 1f), FxMath.Rgba(0.62f, 0.05f, 0.14f, 1f), FxMath.Rgba(1f, 0.42f, 0.2f, 1f) }
            : new[] { FxMath.Rgba(0.36f, 1f, 0.69f, 1f), FxMath.Rgba(0.44f, 0.88f, 1f, 1f), FxMath.Rgba(0.7f, 0.55f, 1f, 1f) };
        for (int i = 0; i < Curtains; i++)
        {
            float cw = w * (0.3f + 0.16f * (float)rng.NextDouble());
            _curtainX[i] = w * (-0.4f + 0.8f * (i + 0.5f) / Curtains) + ((float)rng.NextDouble() - 0.5f) * w * 0.06f;
            _curtainF[i] = 0.25f + 0.15f * (float)rng.NextDouble();
            _curtainA[i] = 0.24f + 0.16f * (float)rng.NextDouble();
            _curtainC[i] = pal[i % 3];
            _curtainY = h * 0.16f;
            _curtain_[i] = Quad("Curtain", Curtain, _curtainX[i], _curtainY, 0.02f, cw, h * 0.62f, layer);
        }

        // 夜明けの帯 (下の 18%・左が青で右が桃。修羅は黒から赤)
        _blueC = blood ? FxMath.Rgba(0.05f, 0f, 0.02f, 0.85f) : FxMath.Rgba(0.33f, 0.62f, 1f, 0.62f);
        _pinkC = blood ? FxMath.Rgba(0.85f, 0.08f, 0.1f, 0.7f) : FxMath.Rgba(1f, 0.55f, 0.78f, 0.62f);
        float bh = h * 0.18f;
        _dawnBlue = Quad("DawnBlue", DawnL, 0f, -h * 0.5f + bh * 0.5f, 0.01f, w, bh, layer);
        _dawnPink = Quad("DawnPink", DawnR, 0f, -h * 0.5f + bh * 0.5f, 0.005f, w, bh, layer);

        _markH = markH;
        _markA = markAlpha;
        if (markH > 0f)
        {
            _glowC = blood ? FxMath.Rgba(0.9f, 0.1f, 0.12f, 0.45f) : FxMath.Rgba(0.55f, 0.8f, 1f, 0.4f);
            _markGlow = Quad("MarkGlow", Soft, 0f, 0f, 0f, markH * 1.5f, markH * 1.5f, layer);
            _hasGlow = true;
            var sp = RoleArt.MarkSprite(blood ? "mark_Wayfinder_Fallen" : "mark_Wayfinder_Large");
            if (sp)
            {
                _mark = Quad("Mark", sp, 0f, 0f, -0.01f, 1f, 1f, layer);
                _markK = markH / sp.bounds.size.y;
                _mark.transform.localScale = FxMath.V3(_markK, _markK, 1f);
                _hasMark = true;
            }
        }
        Tick(Time.time);
    }

    private SpriteRenderer Quad(string name, Sprite sp, float x, float y, float z, float w, float h, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(_go.transform, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        float sh = sp ? sp.bounds.size.y : 1f, sw = sp ? sp.bounds.size.x : 1f;
        go.transform.localScale = FxMath.V3(w / sw, h / sh, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        return sr;
    }

    private static Color A(Color c, float a) => FxMath.Rgba(c.r, c.g, c.b, c.a * a);

    private bool Tick(float now)
    {
        if (!_go) return false;
        float t = now - _t0;
        float k = FxMath.Clamp01(t / FadeIn) * _alpha;
        // 夜の地は最初から出す (本編の黒い板の代わりなので、薄い間に後ろが透けないように)
        _night.color = A(_nightC, _alpha);
        _nightLow.color = A(_nightLowC, k);
        _dawnBlue.color = A(_blueC, k);
        _dawnPink.color = A(_pinkC, k);
        for (int i = 0; i < Stars; i++)
        {
            float tw = 0.55f + 0.45f * FxMath.Sin(t * _starF[i] * 6.283f + _starP[i]);
            _star[i].color = FxMath.Rgba(1f, 1f, 1f, _starA[i] * tw * k);
        }
        // カーテン: 0.2 秒から出て、左右に揺れながら明滅する
        float ck = FxMath.Clamp01((t - 0.2f) / 0.6f) * _alpha;
        for (int i = 0; i < Curtains; i++)
        {
            float ph = t * _curtainF[i] * 6.283f + i * 1.7f;
            var tr = _curtain_[i].transform;
            tr.localPosition = FxMath.V3(_curtainX[i] + 0.3f * FxMath.Sin(ph), _curtainY, 0.02f);
            tr.localRotation = FxMath.RotZ((i % 2 == 0 ? 9f : -7f) + 4f * FxMath.Sin(ph * 0.7f + 0.5f));
            float pulse = 0.7f + 0.3f * FxMath.Sin(ph * 1.3f + 2f);
            _curtain_[i].color = A(_curtainC[i], _curtainA[i] * pulse * ck);
        }
        if (_hasGlow)
        {
            float mk = FxMath.Clamp01((t - MarkAt) / MarkIn);
            float ease = 1f - (1f - mk) * (1f - mk);
            float glow = 0.85f + 0.15f * FxMath.Sin(t * 2.1f);
            _markGlow.color = A(_glowC, ease * glow * _alpha);
            if (_hasMark)
            {
                _mark.color = FxMath.Rgba(1f, 1f, 1f, _markA * ease * _alpha);
                if (mk < 1f || t < MarkAt + MarkIn + 0.1f)
                {
                    float b = _markK * (0.92f + 0.08f * ease);
                    _mark.transform.localScale = FxMath.V3(b, b, 1f);
                }
            }
        }
        return true;
    }

    public void Destroy()
    {
        if (_go) Object.Destroy(_go);
        _go = null;
    }

    internal static void TickAll()
    {
        if (Live.Count == 0) return;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
            if (!Live[i].Tick(now)) Live.RemoveAt(i);
    }

    internal static int LiveCount => Live.Count;

    // ---- 絵 (式から 1 回だけ焼く) ----

    // 上が透明・下が不透明
    private static Sprite VRamp => _vramp ? _vramp : _vramp = FxSprites.Rect(4, 64, (x, y) =>
    {
        float k = FxMath.Clamp01(0.5f - y);
        return k * k * (3f - 2f * k);
    }, "MrpSkyVRamp");

    // オーロラの 1 本: 横は柔らかく、縦は下の縁で明るく上へ薄れる・裾は少し硬い
    private static Sprite Curtain => _curtain ? _curtain : _curtain = FxSprites.Rect(32, 128, (x, y) =>
    {
        float hx = FxMath.Clamp01(1f - FxMath.Abs(x) * 2f);
        hx = hx * hx * (3f - 2f * hx);
        float v = y + 0.5f; // 0 = 下・1 = 上
        float hem = FxMath.Clamp01(v / 0.08f);
        float up = FxMath.Pow(FxMath.Clamp01(1f - v), 1.6f);
        float rays = 0.9f + 0.1f * FxMath.Sin(x * 22f + y * 6f);
        return hx * hem * up * rays;
    }, "MrpSkyCurtain");

    private static Sprite DawnL => _dawnL ? _dawnL : _dawnL = FxSprites.Rect(64, 32, (x, y) => DawnShape(x, y, true), "MrpSkyDawnL");
    private static Sprite DawnR => _dawnR ? _dawnR : _dawnR = FxSprites.Rect(64, 32, (x, y) => DawnShape(x, y, false), "MrpSkyDawnR");

    // 下ほど濃い帯。左の絵は左が濃く、右の絵は右が濃い (重ねて横のグラデーションにする)
    private static float DawnShape(float x, float y, bool left)
    {
        float v = FxMath.Clamp01(0.5f - y);
        float hside = left ? FxMath.Clamp01(0.75f - x) : FxMath.Clamp01(0.75f + x);
        hside = FxMath.Min(1f, hside);
        return v * v * hside;
    }

    private static Sprite Dot => _dot ? _dot : _dot = FxSprites.Rect(16, 16, FxSprites.Dot, "MrpSkyDot");
    private static Sprite Soft => _soft ? _soft : _soft = FxSprites.Rect(64, 64, FxSprites.Soft, "MrpSkySoft");
}
