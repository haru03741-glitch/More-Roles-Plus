using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// 立ち合いの入りの幕と、上下の黒い帯・画面の縁の暗がり。
// 入り (IntroTime 秒・操作は止めない): 上下から帯が入り、左右から二人の名札 (陣営色の板・顔・名前) が滑り込み、
// 中央を斜めの斬り線が走って「VS」が弾ける。終わりに名札は外へ抜け、続けて FIGHT! の題字。
// 鍔迫り合いの間は帯が厚く入り、画面の縁が暗くなる。動かすのは幕と帯が動いている間だけ
internal static partial class DuelHud
{
    private const float IntroTime = 0.8f, IntroIn = 0.16f, IntroOut = 0.18f, SlashAt = 0.08f, SlashGrow = 0.09f, VsAt = 0.16f, VsPop = 0.1f;
    private const float BandIntro = 0.15f, BandLock = 0.2f, BandEase = 14f;  // 帯の高さ (画面の高さの半分に対する割合)・寄る速さ
    private const float PlateH = 1.0f, SlashAngle = 16f;

    private static GameObject _introRoot;
    private static Transform _plateL, _plateR, _slashTf, _vsTf;
    private static SpriteRenderer _bandTop, _bandBottom, _vignette, _slash, _slashGlow;
    private static SpriteRenderer _plateBackL, _plateBackR, _plateEdgeL, _plateEdgeR, _plateFaceL, _plateFaceR;
    private static readonly SpriteRenderer[] _plateLinesL = new SpriteRenderer[2], _plateLinesR = new SpriteRenderer[2];
    private static TextMeshPro _vs, _plateNameL, _plateNameR;
    private static float _plateY, _plateX, _band, _bandGoal, _vig, _vigGoal;
    private static bool _introOn, _vsFlashed, _bandsMoving;

    private static void BuildIntro(Sprite white, int layer, TextMeshPro src)
    {
        // 帯と暗がりは HUD の板より奥 (z が大きい)・幕は題字の手前
        float w = _halfW * 2f + 0.4f;
        _bandTop = Quad("BandTop", white, 0f, _halfH, 0.6f, w, 0f, FxMath.Rgba(0f, 0f, 0f, 1f), layer);
        _bandBottom = Quad("BandBottom", white, 0f, -_halfH, 0.6f, w, 0f, FxMath.Rgba(0f, 0f, 0f, 1f), layer);
        _vignette = Quad("Vignette", Vignette(), 0f, 0f, 0.8f, _halfW * 2f + 0.2f, _halfH * 2f + 0.2f, FxMath.Rgba(0f, 0f, 0f, 0f), layer);
        _bandTop.gameObject.SetActive(false);
        _bandBottom.gameObject.SetActive(false);
        _vignette.gameObject.SetActive(false);

        _introRoot = new GameObject("Intro") { layer = layer };
        var root = _introRoot.transform;
        root.SetParent(_rootTf, false);
        root.localPosition = FxMath.V3(0f, 0f, -2.5f);
        _plateY = _halfH * 0.32f;
        float pw = _halfW - 0.25f;
        _plateX = 0.25f + pw * 0.5f;
        _plateL = Plate(white, layer, root, -1f, pw, src, _plateLinesL, out _plateBackL, out _plateEdgeL, out _plateFaceL, out _plateNameL);
        _plateR = Plate(white, layer, root, 1f, pw, src, _plateLinesR, out _plateBackR, out _plateEdgeR, out _plateFaceR, out _plateNameR);

        // 斬り線 = 外の淡い光 + 白い芯 (斜めに 1 本)
        var slash = new GameObject("Slash") { layer = layer };
        _slashTf = slash.transform;
        _slashTf.SetParent(root, false);
        _slashTf.localPosition = FxMath.V3(0f, _plateY, -0.2f);
        _slashTf.localRotation = Quaternion.Euler(0f, 0f, SlashAngle);
        _slashGlow = Quad("Glow", white, 0f, 0f, 0f, 1f, 0.26f, FxMath.Rgba(0.6f, 0.9f, 1f, 0.45f), layer, _slashTf);
        _slash = Quad("Core", white, 0f, 0f, -0.05f, 1f, 0.06f, FxMath.Rgba(1f, 1f, 1f, 1f), layer, _slashTf);

        _vs = Text(src, "Vs", 0f, _plateY, 9.5f, TextAlignmentOptions.Center, layer, 4f);
        _vs.transform.SetParent(root, true);
        _vsTf = _vs.transform;
        _vsTf.localPosition = FxMath.V3(0f, _plateY, -0.4f);
        _vs.fontStyle = FontStyles.Bold | FontStyles.Italic;
        _vs.enableVertexGradient = true;
        _vs.colorGradient = GoldGradient;
        Outline(_vs, 0.3f, FxMath.Rgba(0.45f, 0.04f, 0.04f, 1f));
        _vs.text = "VS";
        _introRoot.SetActive(false);
    }

    // 片側の名札。原点は板の中央 (外側の画面の端から中央の手前まで)。板と上下の線は中央の側ほど濃い (外側は透けて画面に溶ける)
    private static Transform Plate(Sprite white, int layer, Transform root, float side, float pw, TextMeshPro src, SpriteRenderer[] lines,
        out SpriteRenderer back, out SpriteRenderer edge, out SpriteRenderer face, out TextMeshPro name)
    {
        var go = new GameObject(side < 0 ? "PlateL" : "PlateR") { layer = layer };
        var tf = go.transform;
        tf.SetParent(root, false);
        back = Quad("Back", Ramp(), 0f, 0f, 0f, pw, PlateH, FxMath.Rgba(0f, 0f, 0f, 0.9f), layer, tf);
        back.flipX = side > 0f;
        for (int i = 0; i < 2; i++)
        {
            lines[i] = Quad("Line", Ramp(), 0f, (i == 0 ? 1f : -1f) * (PlateH * 0.5f - 0.02f), -0.03f, pw, 0.04f, FxMath.Rgba(1f, 1f, 1f, 1f), layer, tf);
            lines[i].flipX = side > 0f;
        }
        // 中央の側の縁に明るい線 (陣営色)
        edge = Quad("Edge", white, -side * (pw * 0.5f - 0.04f), 0f, -0.05f, 0.08f, PlateH, FxMath.Rgba(1f, 1f, 1f, 1f), layer, tf);
        face = Quad("Face", Circle(), side * (pw * 0.5f - 0.6f), 0f, -0.1f, 0.72f, 0.72f, FxMath.Rgba(1f, 1f, 1f, 1f), layer, tf);
        name = Text(src, "Name", 0f, 0f, 4.8f, side < 0 ? TextAlignmentOptions.Left : TextAlignmentOptions.Right, layer, 3.2f);
        name.transform.SetParent(tf, true);
        // 顔のすぐ内側から中央へ向けて書く (箱の幅 3.2 の外側の端 = 顔の内側の縁)
        name.transform.localPosition = FxMath.V3(side * (pw * 0.5f - 1.15f - 1.6f), 0f, -0.1f);
        name.fontStyle = FontStyles.Bold | FontStyles.Italic;
        Outline(name, 0.22f, FxMath.Rgba(0.04f, 0.04f, 0.06f, 1f));
        return tf;
    }

    private static void IntroBegin()
    {
        if (!_introRoot) return;
        PaintPlate(_plateBackL, _plateEdgeL, _plateFaceL, _plateNameL, _plateLinesL, _me);
        PaintPlate(_plateBackR, _plateEdgeR, _plateFaceR, _plateNameR, _plateLinesR, _opp);
        _introOn = true;
        _vsFlashed = false;
        _introRoot.SetActive(true);
        SetBands(BandIntro, _vigGoal);
        IntroTick(Time.time);
    }

    private static void PaintPlate(SpriteRenderer back, SpriteRenderer edge, SpriteRenderer face, TextMeshPro name, SpriteRenderer[] lines, byte id)
    {
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
        int color = p != null ? p.DefaultOutfit.ColorId : 0;
        var pc = Palette.PlayerColors;
        Color c = color >= 0 && color < pc.Length ? pc[color] : FxMath.Rgba(1f, 1f, 1f, 1f);
        face.color = c;
        edge.color = FxMath.Rgba(FxMath.Min(c.r * 1.3f + 0.15f, 1f), FxMath.Min(c.g * 1.3f + 0.15f, 1f), FxMath.Min(c.b * 1.3f + 0.15f, 1f), 1f);
        lines[0].color = lines[1].color = edge.color;
        // 板は陣営色を暗く沈めた色 (名前の白が読めるように)
        back.color = FxMath.Rgba(c.r * 0.32f + 0.03f, c.g * 0.32f + 0.03f, c.b * 0.32f + 0.05f, 0.92f);
        name.text = p != null ? p.PlayerName : "?";
    }

    private static void IntroHide()
    {
        _introOn = false;
        if (_introRoot) _introRoot.SetActive(false);
        _band = _bandGoal = _vig = _vigGoal = 0f;
        _bandsMoving = true;
        WriteBands();
    }

    // 鍔迫り合いの帯と暗がり (入りの幕の途中なら幕の帯より厚くする)
    private static void LockBands(bool on)
    {
        if (!_bandTop) return;
        SetBands(on ? BandLock : _introOn ? BandIntro : 0f, on ? 1f : 0f);
    }

    private static void SetBands(float band, float vig)
    {
        _bandGoal = band;
        _vigGoal = vig;
        _bandsMoving = true;
    }

    private static void IntroTick(float now)
    {
        if (_bandsMoving) BandsTick();
        if (!_introOn) return;
        float t = now - _startT;
        if (t >= IntroTime)
        {
            _introOn = false;
            _introRoot.SetActive(false);
            if (!_lockOn) SetBands(0f, 0f);
            return;
        }
        if (t >= IntroTime - IntroOut && !_lockOn && _bandGoal > 0f) SetBands(0f, _vigGoal);

        // 名札: 外から勢いよく入って少し流れ、終わりに外へ抜ける
        float off = _halfW * 1.15f;
        float x = t < IntroIn ? off * Cube(1f - t / IntroIn) : 0f;
        if (t > IntroTime - IntroOut) x = off * Square((t - (IntroTime - IntroOut)) / IntroOut);
        float drift = 0.18f * FxMath.Clamp01((t - IntroIn) / (IntroTime - IntroIn));
        _plateL.localPosition = FxMath.V3(-_plateX - x + drift, _plateY, 0f);
        _plateR.localPosition = FxMath.V3(_plateX + x - drift, _plateY, 0f);

        // 斬り線: 中央から両へ一気に伸び、細くなって消える
        float sk = FxMath.Clamp01((t - SlashAt) / SlashGrow);
        float thin = FxMath.Clamp01(1f - (t - SlashAt - SlashGrow) / 0.3f);
        float len = _halfW * 2.6f * (1f - Square(1f - sk));
        _slashTf.localScale = FxMath.V3(FxMath.Max(len, 0.0001f), thin, 1f);

        // VS: 斬り線のあとに大きく弾けて収まる。弾けた瞬間に一度だけ白く光る
        bool vs = t >= VsAt;
        float vk = FxMath.Clamp01((t - VsAt) / VsPop);
        float vsScale = vs ? FxMath.Lerp(2.1f, 1f, 1f - Square(1f - vk)) : 0.0001f;
        float vsOut = t > IntroTime - IntroOut ? 1f - (t - (IntroTime - IntroOut)) / IntroOut : 1f;
        _vsTf.localScale = FxMath.V3(vsScale, vsScale, 1f);
        _vs.alpha = FxMath.Clamp01(vsOut);
        if (vs && !_vsFlashed)
        {
            _vsFlashed = true;
            SamuraiScreen.Flash(0.22f, 0.05f);
        }
    }

    private static float Square(float k) => k * k;
    private static float Cube(float k) => k * k * k;

    // 帯と暗がりを目標へ寄せる (着いたら止める)
    private static void BandsTick()
    {
        float k = FxMath.Min(1f, BandEase * Time.deltaTime);
        _band += (_bandGoal - _band) * k;
        _vig += (_vigGoal - _vig) * k;
        if (FxMath.Abs(_band - _bandGoal) < 0.002f && FxMath.Abs(_vig - _vigGoal) < 0.01f)
        {
            _band = _bandGoal;
            _vig = _vigGoal;
            _bandsMoving = false;
        }
        WriteBands();
    }

    private static void WriteBands()
    {
        if (!_bandTop) return;
        bool bands = _band > 0.001f, vig = _vig > 0.01f;
        if (_bandTop.gameObject.activeSelf != bands)
        {
            _bandTop.gameObject.SetActive(bands);
            _bandBottom.gameObject.SetActive(bands);
        }
        if (_vignette.gameObject.activeSelf != vig) _vignette.gameObject.SetActive(vig);
        if (bands)
        {
            float h = _band * _halfH, w = _halfW * 2f + 0.4f;
            var top = _bandTop.transform;
            top.localPosition = FxMath.V3(0f, _halfH - h * 0.5f, 0.6f);
            top.localScale = FxMath.V3(w, h, 1f);
            var bottom = _bandBottom.transform;
            bottom.localPosition = FxMath.V3(0f, -_halfH + h * 0.5f, 0.6f);
            bottom.localScale = FxMath.V3(w, h, 1f);
        }
        if (vig) _vignette.color = FxMath.Rgba(0f, 0f, 0f, _vig);
    }

    private static Sprite _vignetteSprite, _glow, _ramp;

    // 左端の透明から右端の不透明へ (名札の板と線。右の名札は左右を返して使う)
    private static Sprite Ramp()
    {
        if (_ramp) return _ramp;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int x = 0; x < n; x++)
        {
            float k = FxMath.Clamp01((x + 0.5f) / n * 1.6f - 0.1f);
            var c = FxMath.Rgba32(255, 255, 255, (byte)(255f * k * k * (3f - 2f * k)));
            for (int y = 0; y < n; y++) px[y * n + x] = c;
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _ramp = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _ramp.hideFlags = HideFlags.HideAndDontSave;
        return _ramp;
    }

    // 画面の縁ほど濃い黒 (中央は透明)。四角い画面に引き伸ばして使う
    private static Sprite Vignette()
    {
        if (_vignetteSprite) return _vignetteSprite;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float k = FxMath.Clamp01((r - 0.55f) / 0.6f);
                px[y * n + x] = FxMath.Rgba32(255, 255, 255, (byte)(255f * 0.82f * k * k * (3f - 2f * k)));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _vignetteSprite = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _vignetteSprite.hideFlags = HideFlags.HideAndDontSave;
        return _vignetteSprite;
    }

    // 中央ほど明るい柔らかな光の玉
    private static Sprite Glow()
    {
        if (_glow) return _glow;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float k = FxMath.Clamp01(1f - MathF.Sqrt(dx * dx + dy * dy));
                px[y * n + x] = FxMath.Rgba32(255, 255, 255, (byte)(255f * k * k));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _glow = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _glow.hideFlags = HideFlags.HideAndDontSave;
        return _glow;
    }
}
