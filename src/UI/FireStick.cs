using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// スマホの射撃スティック (トリガーハッピーが構えている間だけ・自分の端末だけ)。
// 画面の右半分の、ボタンの上でない所に触れると、その指の下に本編の移動スティックと同じ輪が出る。
// 指を輪の中心から倒している間だけ、倒した向きへ撃つ (左半分は本編の移動スティックのまま)。
// PC では出さない (右クリック)。確かめ用に PC でも出せ (ForceShow・左ボタンを指の代わりに)、bridge から倒した扱いにできる (Synth)
internal static class FireStick
{
    private const float Z = -480f;
    private const float Ring = 1.5f, Knob = 0.6f, Reach = 0.55f; // 輪と玉の大きさ・玉が動ける半径 (UI の単位)
    private const float DeadZone = 0.12f;                         // これより倒していなければ撃たない (UI の単位)

    internal static bool ForceShow;
    internal static bool Held { get; private set; }
    internal static Vector2 Dir { get; private set; } = Vector2.right;

    private static bool _armed;
    private static int _finger = -1;
    private static bool _mouse;
    private static Vector2 _center;   // 輪の中心 (UI の座標)
    private static float _synUntil = -1f;
    private static Vector2 _synDir;

    private static GameObject _root;
    private static Transform _ringTf, _knobTf;
    private static bool _shown;

    private static bool Wanted => ForceShow || System.OperatingSystem.IsAndroid();

    // 構えている間だけ true (役職が tick ごとに入れ直す)
    internal static void Arm(bool on)
    {
        if (on == _armed) return;
        _armed = on;
        if (!on) Release();
        // 輪の絵は構えた時に探しておく (触れた瞬間に全部の絵を探して引っかからないように)
        else if (Wanted && !_root && Vanilla.Hud is { } hud) Build(hud);
    }

    // 確かめ用: deg の向きへ seconds 秒倒した扱い
    internal static void Synth(float deg, float seconds)
    {
        float r = deg * (System.MathF.PI / 180f);
        _synDir = FxMath.V2(FxMath.Cos(r), FxMath.Sin(r));
        _synUntil = Time.time + seconds;
    }

    // 毎フレーム (構えている間だけ中を見る)
    internal static void Tick()
    {
        if (!_armed) return;
        if (_synUntil >= 0f)
        {
            if (Time.time < _synUntil) { Held = true; Dir = _synDir; return; }
            _synUntil = -1f;
            Held = false;
        }
        if (!Wanted) return;
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!cam) return;
        if (!Pointer(out Vector2 screen, out bool began, out bool down))
        {
            if (_finger >= 0 || _mouse) Release();
            return;
        }
        Vector3 w = cam.ScreenToWorldPoint(FxMath.V3(screen.x, screen.y, 0f));
        var hp = hud.transform.position;
        float ux = w.x - hp.x, uy = w.y - hp.y;
        if (began)
        {
            _center = FxMath.V2(ux, uy);
            if (!Build(hud)) return;
            _ringTf.localPosition = FxMath.V3(ux, uy, 0f);
            Show(true);
        }
        if (!down) { Release(); return; }
        float dx = ux - _center.x, dy = uy - _center.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        float k = d > Reach ? Reach / d : 1f;
        if (_knobTf) _knobTf.localPosition = FxMath.V3(_center.x + dx * k, _center.y + dy * k, -0.1f);
        Held = d > DeadZone;
        if (Held) Dir = FxMath.V2(dx / d, dy / d);
    }

    // 輪を始めた指 (か左ボタン) の今の位置。began = この輪を今始めた
    private static bool Pointer(out Vector2 screen, out bool began, out bool down)
    {
        screen = default;
        began = down = false;
        if (System.OperatingSystem.IsAndroid())
        {
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                if (_finger >= 0)
                {
                    if (t.fingerId != _finger) continue;
                }
                else if (t.phase != TouchPhase.Began || t.position.x < Screen.width * 0.5f || OverUi()) continue;
                screen = t.position;
                began = _finger < 0;
                down = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
                if (began) _finger = t.fingerId;
                return true;
            }
            return false;
        }
        bool held = Input.GetMouseButton(0);
        if (!_mouse)
        {
            Vector3 m0 = Input.mousePosition;
            if (!Input.GetMouseButtonDown(0) || m0.x < Screen.width * 0.5f || OverUi()) return false;
            _mouse = true;
            began = true;
        }
        Vector3 m = Input.mousePosition;
        screen = FxMath.V2(m.x, m.y);
        down = held;
        return true;
    }

    // 押し始めがボタン・パネルの上なら輪を出さない (人の体の上は出す)
    internal static bool OverUi()
    {
        if (!PassiveButtonManager.InstanceExists) return false;
        var over = PassiveButtonManager.Instance.currentOver;
        return over && !over.GetComponent<PlayerControl>();
    }

    private static void Release()
    {
        _finger = -1;
        _mouse = false;
        Held = false;
        Show(false);
    }

    private static void Show(bool on)
    {
        if (on == _shown || !_root) return;
        _shown = on;
        _root.SetActive(on);
    }

    // 本編の移動スティックの輪の絵 (PC でも読まれている) を借りる。無ければ輪を出さずに操作だけ受ける
    private static bool Build(HudManager hud)
    {
        if (_root) return true;
        Sprite circle = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (s && s.name == "Joystickcircle") { circle = s; break; }
        }
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpFireStick") { layer = layer };
        var tf = _root.transform;
        tf.SetParent(hud.transform, false);
        tf.localPosition = FxMath.V3(0f, 0f, Z);
        _ringTf = Piece("Ring", circle, Ring, 0.55f, layer, tf);
        _knobTf = Piece("Knob", circle, Knob, 0.9f, layer, tf);
        _shown = true;
        Show(false);
        if (!circle) Plugin.Logger.LogWarning("fire stick: Joystickcircle not found");
        return true;
    }

    private static Transform Piece(string name, Sprite sprite, float size, float alpha, int layer, Transform parent)
    {
        var go = new GameObject(name) { layer = layer };
        var t = go.transform;
        t.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = FxMath.Rgba(1f, 1f, 1f, alpha);
        float k = sprite ? size / Mathf.Max(0.01f, sprite.bounds.size.x) : size;
        t.localScale = FxMath.V3(k, k, 1f);
        return t;
    }
}
