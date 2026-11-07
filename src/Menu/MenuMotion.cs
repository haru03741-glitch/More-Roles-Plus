using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// メインメニューを毎フレーム滑らかに動かす (本編の ModManager.LateUpdate に乗る。メニューの間だけ)。
//   - 左のボタンは開いた時に左から順に滑り込む
//   - 指・カーソルが乗ったボタンはふわっと大きく、押している間は少し縮む (メニューのどの画面のボタンでも: 持ち物・ショップ・部屋を作る など)
//   - 開いた画面 (持ち物・部屋を作る・遊び方の選択・設定・クレジット) は小さい所から弾むように開く
//   - 題字はゆっくり上下し、夜空はゆっくり流れ、右の窓の「ようこそ」は下から浮かび上がる。ヒントは替わる時に薄く消えて現れる
// どれも見た目だけで、本編のボタンの押し方・位置の決め方には触らない (動かした分は毎フレーム戻す)
internal static class MenuMotion
{
    private const float SlideTime = 0.5f, SlideStagger = 0.06f, SlideFrom = 2.4f;
    private const float HoverScale = 1.06f, PressScale = 0.95f, ScaleSpeed = 14f;
    private const float PopTime = 0.32f, PopFrom = 0.9f;
    private const float RescanEvery = 0.5f;

    private static MainMenuManager _menu;
    private static float _openedAt;

    // 滑り込むボタン (元の位置)
    private static readonly List<(Transform t, Vector3 home)> Slides = new();
    // 大きさを動かしているボタン (元の大きさ・今の倍率)
    private sealed class Bounce { public Transform T; public Vector3 Home; public float K = 1f; }
    private static readonly Dictionary<IntPtr, Bounce> Bounces = new();
    // 開いたら弾ませる画面 (前のフレームに出ていたか)
    private sealed class Pop { public Transform T; public bool Was; public float At = -9f; public Vector3 Home; }
    private static readonly List<Pop> Pops = new();
    private static float _rescanAt;

    private static Transform _logo, _sky;
    private static Vector3 _logoHome, _skyHome;
    private static Transform _hero;
    private static bool _heroWas;
    private static float _heroAt;
    private static readonly List<(TextMeshPro t, Vector3 home, float delay)> HeroTexts = new();
    private static TextMeshPro _tip;
    private static float _tipAt = -9f;

    // HomeScreen が作り終えた時
    internal static void Attach(MainMenuManager menu, Transform logo, Transform sky, Transform hero, TextMeshPro[] heroTexts, TextMeshPro tip)
    {
        _menu = menu;
        _openedAt = Time.time;
        Slides.Clear();
        Bounces.Clear();
        _overPtr = IntPtr.Zero; _overButton = null;
        Pops.Clear();
        HeroTexts.Clear();
        _rescanAt = 0f;
        var buttons = menu.transform.Find("MainUI/AspectScaler/LeftPanel/Main Buttons");
        if (buttons)
        {
            // 上から順に
            var list = new List<Transform>();
            for (int i = 0; i < buttons.childCount; i++)
            {
                var c = buttons.GetChild(i);
                if (c.gameObject.activeSelf && c.name != "Divider") list.Add(c);
            }
            list.Sort((a, b) => b.localPosition.y.CompareTo(a.localPosition.y));
            foreach (var c in list) Slides.Add((c, c.localPosition));
        }
        _logo = logo;
        _logoHome = logo ? logo.localPosition : Vector3.zero;
        _sky = sky;
        _skyHome = sky ? sky.localPosition : Vector3.zero;
        _hero = hero;
        _heroWas = false;
        float d = 0f;
        foreach (var t in heroTexts)
        {
            if (t) HeroTexts.Add((t, t.transform.localPosition, d));
            d += 0.08f;
        }
        _tip = tip;
        var scaler = menu.transform.Find("MainUI/AspectScaler");
        AddPop(scaler ? scaler.Find("CreateGameScreen") : null);
        var screens = scaler ? scaler.Find("RightPanel/MaskedBlackScreen") : null;
        if (screens)
            for (int i = 0; i < screens.childCount; i++) AddPop(screens.GetChild(i));
        AddPop(scaler ? scaler.Find("RightPanel/CreditsSizer/CreditsScreen") : null);
    }

    internal static void TipChanged() => _tipAt = Time.time;

    // メインメニューの外 (ゲームを探す など) でも、乗ったボタンをふわっと大きくする (MenuSkin から毎フレーム)
    internal static void HoverOutside()
    {
        if (_menu) return;
        Hover(Time.time, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
    }

    private static void AddPop(Transform t)
    {
        if (t) Pops.Add(new Pop { T = t, Was = t.gameObject.activeInHierarchy, Home = t.localScale });
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            if (_menu is null) return;
            if (!_menu) { Detach(); return; }
            try { Frame(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"menu motion: {e.Message}");
                Detach();
            }
        }
    }

    private static void Detach()
    {
        _menu = null;
        Slides.Clear();
        Bounces.Clear();
        _overPtr = IntPtr.Zero; _overButton = null;
        Pops.Clear();
        HeroTexts.Clear();
    }

    private static void Frame()
    {
        float now = Time.time, dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        HomeScreen.Cover();

        // 左のボタンが滑り込む
        for (int i = 0; i < Slides.Count; i++)
        {
            var (t, home) = Slides[i];
            if (!t) continue;
            float k = Mathf.Clamp01((now - _openedAt - i * SlideStagger) / SlideTime);
            float e = 1f - Mathf.Pow(1f - k, 3f);
            t.localPosition = home + new Vector3(-SlideFrom * (1f - e), 0f, 0f);
        }

        // 題字はゆっくり上下・夜空はゆっくり流れる
        if (_logo) _logo.localPosition = _logoHome + new Vector3(0f, Mathf.Sin(now * 1.3f) * 0.05f, 0f);
        if (_sky) _sky.localPosition = _skyHome + new Vector3(Mathf.Sin(now * 0.05f) * 0.35f, Mathf.Sin(now * 0.07f) * 0.12f, 0f);

        // 右の窓の「ようこそ」: 出るたびに下から浮かぶ
        if (_hero)
        {
            bool on = _hero.gameObject.activeInHierarchy;
            if (on && !_heroWas) _heroAt = now;
            _heroWas = on;
            if (on)
                foreach (var (t, home, delay) in HeroTexts)
                {
                    if (!t) continue;
                    float k = Mathf.Clamp01((now - _heroAt - delay) / 0.45f);
                    float e = 1f - Mathf.Pow(1f - k, 3f);
                    t.transform.localPosition = home + new Vector3(0f, -0.25f * (1f - e), 0f);
                    t.alpha = e;
                }
        }
        if (_tip && _hero && _hero.gameObject.activeInHierarchy)
        {
            float k = Mathf.Clamp01((now - _tipAt) / 0.4f);
            _tip.alpha = Mathf.Min(_tip.alpha, k);
        }

        // 開いた画面を弾ませる
        foreach (var p in Pops)
        {
            if (!p.T) continue;
            bool on = p.T.gameObject.activeInHierarchy;
            if (on && !p.Was) p.At = now;
            p.Was = on;
            float k = (now - p.At) / PopTime;
            if (k < 0f || k > 1f)
            {
                if (k > 1f && k < 1.5f) p.T.localScale = p.Home;
                continue;
            }
            p.T.localScale = p.Home * Mathf.LerpUnclamped(PopFrom, 1f, BackOut(k));
        }

        Hover(now, dt);
    }

    // 少し行き過ぎて戻る (弾む)
    private static float BackOut(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
    }

    // 乗っているボタンをふわっと大きく (押している間は縮める)
    private static void Hover(float now, float dt)
    {
        var cam = Camera.main;
        PassiveButton target = null;
        // PC はマウスを重ねたボタンを本編が currentOver に立てる。スマホのタッチでは立てないので、押している間だけ点に重なるボタンを探す
        if (cam && PassiveButtonManager.InstanceExists)
        {
            var pm = PassiveButtonManager.Instance;
            PassiveUiElement over = pm.currentOver;
            if (!over && Input.touchCount > 0)
            {
                Vector3 w = cam.ScreenToWorldPoint(Input.mousePosition);
                over = UI.PointerHit.TopAt(pm, new Vector2(w.x, w.y));
            }
            if (over && over.enabled)
            {
                if (over.Pointer != _overPtr) { _overPtr = over.Pointer; _overButton = SmallButton(over, cam); }
                target = _overButton;
            }
            else _overPtr = IntPtr.Zero;
        }
        if (target)
        {
            var ptr = target.Pointer;
            if (!Bounces.ContainsKey(ptr)) Bounces[ptr] = new Bounce { T = target.transform, Home = target.transform.localScale };
        }
        bool down = Input.GetMouseButton(0);
        List<IntPtr> done = null;
        foreach (var kv in Bounces)
        {
            var b = kv.Value;
            if (!b.T) { (done ??= new List<IntPtr>()).Add(kv.Key); continue; }
            bool on = target && kv.Key == target.Pointer;
            float goal = on ? (down ? PressScale : HoverScale) : 1f;
            b.K = Mathf.Lerp(b.K, goal, 1f - Mathf.Exp(-ScaleSpeed * dt));
            if (!on && Mathf.Abs(b.K - 1f) < 0.002f)
            {
                b.T.localScale = b.Home;
                (done ??= new List<IntPtr>()).Add(kv.Key);
                continue;
            }
            b.T.localScale = b.Home * b.K;
        }
        if (done != null) foreach (var k in done) Bounces.Remove(k);
    }

    // 画面いっぱいの当たり (外を押して閉じる・押させない幕) は動かさない
    // 乗っているボタンが変わった時だけ型と大きさを調べ直す (毎フレームの TryCast を避ける)
    private static IntPtr _overPtr;
    private static PassiveButton _overButton;

    private static PassiveButton SmallButton(PassiveUiElement e, Camera cam)
    {
        var pb = e.TryCast<PassiveButton>();
        if (!pb) return null;
        var cols = e.Colliders;
        if (cols == null) return null;
        for (int i = 0; i < cols.Length; i++)
            if (cols[i] && cols[i].enabled && Small(cols[i], cam)) return pb;
        return null;
    }

    private static bool Small(Collider2D c, Camera cam)
    {
        var s = c.bounds.size;
        float h = cam.orthographicSize * 2f, w = h * cam.aspect;
        return s.x < w * 0.5f && s.y < h * 0.35f;
    }
}
