using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// サムライのスマホの操作ボタン (画面の右の縁・本編の能力ボタンの列とは別)。上 = 納刀・下 = 踏み込み。
// 右半分のなぞり (斬る) と重なるので、押し始めがボタンの上ならなぞりにしない (SwipeInput.TouchBlocked)。
// PC では出さない (右クリックと Shift)。確かめ用に PC でも出せる (ForceShow)
internal static class SamuraiPad
{
    private const float Z = -490f;
    private const float Size = 0.95f, Edge = 0.72f, SheatheY = 1.5f, DashY = 0.5f;

    internal static bool ForceShow;
    internal static int Taps;

    private static GameObject _root;
    private static Transform _sheatheTf, _dashTf;
    private static SpriteRenderer _sheatheBg, _dashBg, _sheatheIcon, _dashIcon;
    private static int _shown = -1;

    private static bool Wanted => ForceShow || System.OperatingSystem.IsAndroid();

    // FixedUpdate ごと: 出すか・塗り。出していれば true (押した瞬間は毎フレームの Poll で読む)
    internal static bool Tick(Samurai s, bool usable)
    {
        if (!Wanted || !usable)
        {
            Hide();
            return false;
        }
        if (!Build()) return false;
        if (!_root.activeSelf) _root.SetActive(true);
        bool canSheathe = s.Drawn && !s.Spent, canDash = s.Drawn && !s.Spent && s.Ki > 0f;
        int key = (canSheathe ? 1 : 0) | (canDash ? 2 : 0);
        if (key != _shown)
        {
            _shown = key;
            Paint(_sheatheBg, _sheatheIcon, canSheathe);
            Paint(_dashBg, _dashIcon, canDash);
        }
        return true;
    }

    // 毎フレーム: 押した瞬間だけ見る (スマホ = 指・PC で出している時 = 左クリック)
    internal static void Poll(Samurai s)
    {
        if (System.OperatingSystem.IsAndroid())
        {
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began) Press(s, t.position);
            }
        }
        else if (Input.GetMouseButtonDown(0))
        {
            Vector3 m = Input.mousePosition;
            Press(s, FxMath.V2(m.x, m.y));
        }
    }

    private static void Press(Samurai s, Vector2 screen)
    {
        int b = Which(screen);
        if (b == 0) return;
        Taps++;
        if (b == 1) s.PadSheathe();
        else s.PadDash();
    }

    // 押し始めがボタンの上か (なぞりにしない)
    internal static bool Hit(Vector2 screen) => Which(screen) != 0;

    // 0 = どれでもない・1 = 納刀・2 = 踏み込み
    private static int Which(Vector2 screen)
    {
        if (!_root || !_root.activeSelf) return 0;
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!cam) return 0;
        Vector3 w = cam.ScreenToWorldPoint(FxMath.V3(screen.x, screen.y, 0f));
        float r = Size * 0.5f;
        if (Near(_sheatheTf.position, w, r)) return 1;
        if (Near(_dashTf.position, w, r)) return 2;
        return 0;
    }

    private static bool Near(Vector3 c, Vector3 w, float r)
    {
        float dx = w.x - c.x, dy = w.y - c.y;
        return dx * dx + dy * dy <= r * r;
    }

    private static void Paint(SpriteRenderer bg, SpriteRenderer icon, bool on)
    {
        bg.color = on ? FxMath.Rgba(0.10f, 0.12f, 0.25f, 0.78f) : FxMath.Rgba(0.08f, 0.08f, 0.12f, 0.45f);
        icon.color = on ? FxMath.Rgba(1f, 1f, 1f, 1f) : FxMath.Rgba(1f, 1f, 1f, 0.3f);
    }

    internal static void Hide()
    {
        if (_root && _root.activeSelf) _root.SetActive(false);
    }

    private static bool Build()
    {
        if (_root) return true;
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!hud || !cam) return false;
        float halfW = cam.orthographicSize * cam.aspect;
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpSamuraiPad") { layer = layer };
        var tf = _root.transform;
        tf.SetParent(hud.transform, false);
        tf.localPosition = FxMath.V3(0f, 0f, Z);
        _sheatheTf = Button("Sheathe", halfW - Edge, SheatheY, "katana_draw", layer, out _sheatheBg, out _sheatheIcon);
        _dashTf = Button("Dash", halfW - Edge, DashY, "katana_slash", layer, out _dashBg, out _dashIcon);
        _shown = -1;
        return true;
    }

    private static Transform Button(string name, float x, float y, string icon, int layer, out SpriteRenderer bg, out SpriteRenderer ic)
    {
        var go = new GameObject(name) { layer = layer };
        var t = go.transform;
        t.SetParent(_root.transform, false);
        t.localPosition = FxMath.V3(x, y, 0f);
        bg = go.AddComponent<SpriteRenderer>();
        bg.sprite = DuelHud.Circle();
        t.localScale = FxMath.V3(Size, Size, 1f);
        var igo = new GameObject("Icon") { layer = layer };
        igo.transform.SetParent(t, false);
        igo.transform.localPosition = FxMath.V3(0f, 0f, -0.1f);
        // 絵は 1.28 単位なので丸の 0.62 倍に収める
        float k = 0.62f / 1.28f;
        igo.transform.localScale = FxMath.V3(k, k, 1f);
        ic = igo.AddComponent<SpriteRenderer>();
        ic.sprite = ButtonIcons.Get(icon);
        return t;
    }
}
