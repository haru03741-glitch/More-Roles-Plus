using System;
using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 回避の枠 (立ち合いの間だけ・サムライでない側)。アクションゲームのスキルの枠の形:
// 丸い枠の中に回避の絵・周りに次の 1 回ぶんが溜まるまでのリング・上に溜まっている回数の菱形・下にキーの札 (PC) と「回避」の字。
// PC = 画面の左下 (下の真ん中は本編の部屋の名前と重なる)・スマホ = 右の縁 (押せるボタンを兼ねる)。書き直すのはリングの区切り・回数・使えるかが変わった時と、溜まった瞬間の膨らみの間だけ
internal static class DodgePad
{
    private const float Z = -490f;
    private const int Segs = 24;
    private const float PcLeft = 1.25f, PcBottom = 1.15f, PadEdge = 0.78f, PadY = 0.5f, PadScale = 1.05f;
    private const float PulseTime = 0.25f;

    internal static bool ForceShow; // 確かめ用: PC でもスマホの置き方で出す
    internal static bool Shown => _active;

    private static GameObject _root;
    private static Transform _btn;
    private static SpriteRenderer _back, _glow, _icon, _head;
    private static SpriteRenderer _fire;
    private static float _bx, _by, _bs = 1f, _emberCarry;
    private static bool _headOn, _fireMat;
    private static int _fireKey = -1;
    private static readonly SpriteRenderer[] _seg = new SpriteRenderer[Segs];
    private static readonly SpriteRenderer[] _pip = new SpriteRenderer[DuelDodge.MaxStock];
    private static GameObject _cap;
    private static TextMeshPro _capText, _label;
    private static Material[] _mats = Array.Empty<Material>();
    private static Sprite _chevrons, _wedge, _diamond, _soft, _capSprite, _dot;
    private static int _lit = -1, _full = -1, _ready = -1;
    private static float _pulseT = -10f, _scale = 1f;
    private static KeyCode _shownKey = (KeyCode)(-1);
    private static bool _padLayout, _active, _hasCap;

    private static bool PadLayout => ForceShow || System.OperatingSystem.IsAndroid();

    // 出して塗る。stock = 溜まり具合 (0〜MaxStock)・ready = 今押せば飛び退ける。押せる形で出していれば true
    internal static bool Tick(float stock, bool ready, float now)
    {
        if (!Build()) return false;
        if (!_active)
        {
            _root.SetActive(true);
            _active = true;
        }
        int full = (int)stock;
        int lit = full >= DuelDodge.MaxStock ? Segs : (int)((stock - full) * Segs);
        int rd = ready ? 1 : 0;
        if (full > _full && _full >= 0)
        {
            _pulseT = now;
            UI.DuelHud.Burst(_bx, _by, 8, 1.6f * _bs, 0f);
        }
        if (lit != _lit || full != _full) PaintRing(lit, full);
        if (full != _full) PaintPips(full);
        if (rd != _ready) PaintReady(ready);
        _lit = lit;
        _full = full;
        _ready = rd;
        Pulse(now);
        Motion(stock, full, now);
        if (_hasCap) Key();
        return true;
    }

    // 炎の輪 (MRP/UiFire): 溜まった分だけ燃え、満タンで背が高く明るい。溜まっていく間はリングの先端に光の点が走る。満タンの間は火の粉が昇る
    private static void Motion(float stock, int full, float now)
    {
        bool max = full >= DuelDodge.MaxStock, head = !max;
        float frac = max ? 1f : stock - full;
        if (!_fireMat && MrpBundle.UiFireMaterial)
        {
            _fireMat = true;
            _fire.sharedMaterial = MrpBundle.UiFireMaterial;
            _fire.enabled = true;
        }
        if (_fireMat)
        {
            // 溜まり具合は 1/64 刻みで書く (シェーダが境目をぼかす)
            float heat = max ? 1f : 0.35f + 0.25f * full;
            int key = (int)(frac * 64f) * 4 + (max ? 3 : full);
            if (key != _fireKey)
            {
                _fireKey = key;
                _fire.color = FxMath.Rgba(frac, heat, 1f, 1f);
            }
        }
        if (head != _headOn) { _headOn = head; _head.enabled = head; }
        if (head)
        {
            float a = (90f - frac * 360f) * FxMath.Deg2Rad, fl = 0.85f + 0.15f * FxMath.Sin(now * 31f);
            _head.transform.localPosition = FxMath.V3(FxMath.Cos(a) * 0.46f, FxMath.Sin(a) * 0.46f, -0.15f);
            _head.transform.localScale = FxMath.V3(0.2f * fl, 0.2f * fl, 1f);
            return;
        }
        _emberCarry += 4f * Time.deltaTime;
        while (_emberCarry >= 1f)
        {
            _emberCarry -= 1f;
            float a = FxMath.Range(0.3f, 2.84f), r = 0.47f * _bs * _scale;
            UI.DuelHud.Emit(_bx + FxMath.Cos(a) * r, _by + FxMath.Sin(a) * r, 0, FxMath.Range(-0.15f, 0.15f), FxMath.Range(0.4f, 0.8f), 0.1f);
        }
    }

    // 回避を使った: 枠から火花を放射する
    internal static void OnUse()
    {
        if (!_active) return;
        // 枠の縁から外へ
        for (int k = 0; k < 16; k++)
        {
            float a = (k + FxMath.Range(0f, 0.8f)) / 16f * 6.2832f, v = FxMath.Range(3.2f, 5f) * _bs, r = 0.45f * _bs;
            float c = FxMath.Cos(a), sn = FxMath.Sin(a);
            UI.DuelHud.Emit(_bx + c * r, _by + sn * r, 1, c * v, sn * v, 0.2f);
        }
    }

    private static void PaintRing(int lit, int full)
    {
        bool max = full >= DuelDodge.MaxStock;
        var on = max ? FxMath.Rgba(1f, 0.84f, 0.42f, 1f) : FxMath.Rgba(0.42f, 0.88f, 1f, 1f);
        var off = FxMath.Rgba(0.30f, 0.35f, 0.50f, 0.85f); // 暗い床の上でも区切りが見える明るさ
        for (int i = 0; i < Segs; i++) _seg[i].color = i < lit ? on : off;
    }

    private static void PaintPips(int full)
    {
        for (int i = 0; i < _pip.Length; i++)
            _pip[i].color = i < full ? FxMath.Rgba(1f, 0.84f, 0.42f, 1f) : FxMath.Rgba(0.30f, 0.35f, 0.50f, 0.85f);
    }

    private static void PaintReady(bool ready)
    {
        _back.color = ready ? FxMath.Rgba(0.1f, 0.13f, 0.28f, 0.92f) : FxMath.Rgba(0.06f, 0.07f, 0.12f, 0.82f);
        _icon.color = ready ? FxMath.Rgba(1f, 1f, 1f, 1f) : FxMath.Rgba(1f, 1f, 1f, 0.28f);
        _glow.enabled = ready;
        if (_label) _label.color = ready ? FxMath.Rgba(1f, 1f, 1f, 1f) : FxMath.Rgba(0.7f, 0.72f, 0.78f, 0.8f);
    }

    // 1 回ぶん溜まった瞬間: 膨らんで白く光ってから戻る
    private static void Pulse(float now)
    {
        float t = (now - _pulseT) / PulseTime;
        float k = t >= 1f ? 0f : (1f - t) * (1f - t);
        float s = 1f + 0.15f * k;
        if (s == _scale) return;
        _scale = s;
        float b = _padLayout ? PadScale : 1f;
        _btn.localScale = FxMath.V3(b * s, b * s, 1f);
        _glow.color = FxMath.Rgba(0.55f + 0.45f * k, 0.85f + 0.15f * k, 1f, 0.35f + 0.5f * k);
    }

    // 割り当てたキーの名前を札に (変えた時だけ)
    private static void Key()
    {
        var key = Menu.Keybinds.Key(Menu.Keybinds.Slot.Dodge);
        if (key == _shownKey) return;
        _shownKey = key;
        _capText.text = key is KeyCode.LeftShift or KeyCode.RightShift ? "Shift" : key == KeyCode.None ? "-" : key.ToString();
    }

    // 押した瞬間か (スマホ = 指・PC = 左クリック)
    internal static bool Pressed()
    {
        if (!Shown) return false;
        if (System.OperatingSystem.IsAndroid())
        {
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && Hit(t.position)) return true;
            }
            return false;
        }
        if (!Input.GetMouseButtonDown(0)) return false;
        Vector3 m = Input.mousePosition;
        return Hit(FxMath.V2(m.x, m.y));
    }

    private static bool Hit(Vector2 screen)
    {
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!cam || !_btn) return false;
        Vector3 w = cam.ScreenToWorldPoint(FxMath.V3(screen.x, screen.y, 0f));
        Vector3 c = _btn.position;
        float dx = w.x - c.x, dy = w.y - c.y, r = 0.5f * _btn.localScale.x;
        return dx * dx + dy * dy <= r * r;
    }

    internal static void Hide()
    {
        if (!_active) return;
        _active = false;
        if (_root) _root.SetActive(false);
    }

    private static bool Build()
    {
        if (_root && _padLayout == PadLayout) return true;
        if (_root) Object.Destroy(_root);
        foreach (var m in _mats) if (m) Object.Destroy(m);
        _mats = Array.Empty<Material>();
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        var src = hud && hud.TaskPanel ? hud.TaskPanel.taskText : null;
        if (!hud || !cam || !src) return false;
        _padLayout = PadLayout;
        float halfH = cam.orthographicSize, halfW = halfH * cam.aspect;
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpDodgePad") { layer = layer };
        var tf = _root.transform;
        tf.SetParent(hud.transform, false);
        tf.localPosition = FxMath.V3(0f, 0f, Z);

        var go = new GameObject("Dodge") { layer = layer };
        _btn = go.transform;
        _btn.SetParent(tf, false);
        _btn.localPosition = _padLayout ? FxMath.V3(halfW - PadEdge, PadY, 0f) : FxMath.V3(-halfW + PcLeft, -halfH + PcBottom, 0f);
        _scale = 1f;
        float b = _padLayout ? PadScale : 1f;
        _btn.localScale = FxMath.V3(b, b, 1f);
        // 火の粉は立ち合いの HUD (同じ HUD の座標) に出すので、枠の位置を覚えておく
        _bx = _padLayout ? halfW - PadEdge : -halfW + PcLeft;
        _by = _padLayout ? PadY : -halfH + PcBottom;
        _bs = b;

        _soft ??= FxSprites.Rect(64, 64, FxSprites.Soft, "MrpDodgeGlow");
        _wedge ??= FxSprites.Rect(64, 64, Wedge, "MrpDodgeWedge");
        _diamond ??= FxSprites.Rect(32, 32, Diamond, "MrpDodgePip");
        _chevrons ??= FxSprites.Rect(64, 64, Chevrons, "MrpDodgeIcon");
        _capSprite ??= FxSprites.Rect(96, 36, Cap, "MrpDodgeCap");

        _glow = Part("Glow", _soft, 0f, 0f, 0.05f, 1.55f, 1.55f, layer, _btn);
        _glow.color = FxMath.Rgba(0.55f, 0.85f, 1f, 0.35f);
        _back = Part("Back", UI.DuelHud.Circle(), 0f, 0f, 0f, 0.84f, 0.84f, layer, _btn);
        for (int i = 0; i < Segs; i++)
        {
            var seg = Part("Seg", _wedge, 0f, 0f, -0.05f, 1f, 1f, layer, _btn);
            // 上から時計回りに埋まる
            seg.transform.localRotation = FxMath.RotZ(90f - (i + 0.5f) * (360f / Segs));
            _seg[i] = seg;
        }
        _icon = Part("Icon", _chevrons, 0f, 0f, -0.1f, 0.56f, 0.56f, layer, _btn);
        _dot ??= FxSprites.Rect(32, 32, FxSprites.Dot, "MrpDodgeSpark");
        _head = Part("Head", _dot, 0f, 0.46f, -0.15f, 0.2f, 0.2f, layer, _btn);
        _head.color = FxMath.Rgba(0.75f, 0.95f, 1f, 1f);
        _headOn = true;
        // 炎の輪の板 (輪の半径 0.46 = シェーダの輪 0.62 × 板の半分)。枠の後ろから立ち、炎の先は枠の外へ
        _fire = Part("Fire", UI.DuelHud.WhiteSprite(), 0f, 0f, 0.02f, 1.48f, 1.48f, layer, _btn);
        _fire.color = FxMath.Rgba(0f, 0f, 1f, 0f);
        _fire.enabled = false;
        _fireMat = false;
        _fireKey = -1;
        for (int i = 0; i < _pip.Length; i++)
            _pip[i] = Part("Pip", _diamond, (i - (_pip.Length - 1) * 0.5f) * 0.24f, 0.66f, -0.1f, 0.2f, 0.2f, layer, _btn);

        _cap = null;
        _hasCap = false;
        _capText = _label = null;
        if (!_padLayout)
        {
            // キーの札 (枠の下)・「回避」の字 (札の右)
            // 札の絵は幅 96 / 高さ 36 で高さ 1 単位 → 幅 0.84・高さ 0.32 にする
            var cap = Part("Cap", _capSprite, 0f, -0.7f, -0.1f, 0.84f * 36f / 96f, 0.32f, layer, _btn);
            cap.color = FxMath.Rgba(0.93f, 0.94f, 0.97f, 0.95f);
            _cap = cap.gameObject;
            _hasCap = true;
            _capText = Label(src, "CapText", 0f, -0.7f, 1.45f, layer, _btn, FxMath.Rgba(0.08f, 0.09f, 0.14f, 1f), false);
            _label = Label(src, "Label", 0.62f, 0f, 2.1f, layer, _btn, FxMath.Rgba(1f, 1f, 1f, 1f), true);
            _label.alignment = TextAlignmentOptions.Left;
            _label.rectTransform.pivot = FxMath.V2(0f, 0.5f);
            _label.text = new Text("Duel.DodgeLabel").ToString();
        }
        else
        {
            _label = Label(src, "Label", 0f, -0.66f, 1.6f, layer, _btn, FxMath.Rgba(1f, 1f, 1f, 1f), true);
            _label.text = new Text("Duel.DodgeLabel").ToString();
        }
        _shownKey = (KeyCode)(-1);
        _lit = _full = _ready = -1;
        _pulseT = -10f;
        _root.SetActive(false);
        _active = false;
        return true;
    }

    private static SpriteRenderer Part(string name, Sprite sprite, float x, float y, float z, float w, float h, int layer, Transform parent)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(w, h, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        return sr;
    }

    // 本編の字の欄を写して字だけにする (DuelHud と同じ作り)。outline = 黒い縁取り (材質の複製は自分で片付ける)
    private static TextMeshPro Label(TextMeshPro src, string name, float x, float y, float size, int layer, Transform parent, Color c, bool outline)
    {
        var go = Object.Instantiate(src.gameObject, parent);
        go.name = name;
        go.layer = layer;
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var comp in go.GetComponents<Component>())
            if (!comp.TryCast<Transform>() && !comp.TryCast<TextMeshPro>() && !comp.TryCast<Renderer>() && !comp.TryCast<MeshFilter>()) Object.Destroy(comp);
        go.transform.localPosition = FxMath.V3(x, y, -0.2f);
        // 枠の大きさに引きずられないよう、字は親の倍率を打ち消さない (枠ごと膨らむ)
        go.transform.localScale = FxMath.V3(1f, 1f, 1f);
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.rectTransform.sizeDelta = FxMath.V2(2f, 0.5f);
        tmp.enableAutoSizing = false;
        tmp.enableWordWrapping = false;
        tmp.margin = default;
        tmp.fontSize = size;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = c;
        tmp.text = "";
        if (outline)
        {
            var m = tmp.fontMaterial;
            tmp.outlineWidth = 0.25f;
            tmp.outlineColor = FxMath.Rgba(0.05f, 0.05f, 0.08f, 1f);
            var list = new Material[_mats.Length + 1];
            Array.Copy(_mats, list, _mats.Length);
            list[^1] = m;
            _mats = list;
        }
        return tmp;
    }

    // リングの 1 区切り (右向き・幅 15° のうち 11°・半径 0.42〜0.5)
    private static float Wedge(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y);
        float a = FxMath.Abs(FxMath.Atan2(y, x)) * FxMath.Rad2Deg;
        float ring = FxMath.Clamp01((r - 0.42f) / 0.012f) * FxMath.Clamp01((0.495f - r) / 0.012f);
        return ring * FxMath.Clamp01((5.5f - a) / 1.2f);
    }

    // 回数の菱形
    private static float Diamond(float x, float y) => FxMath.Clamp01((0.46f - FxMath.Abs(x) - FxMath.Abs(y)) / 0.05f);

    // キーの札 (角の丸い板)
    private static float Cap(float x, float y)
    {
        // 幅 96 / 高さ 36 の比で、角の半径は高さの 0.3
        float w = 0.5f * 96f / 36f, ax = FxMath.Abs(x * 96f / 36f), ay = FxMath.Abs(y);
        float rr = 0.3f, dx = FxMath.Max(ax - (w - rr), 0f), dy = FxMath.Max(ay - (0.5f - rr), 0f);
        return FxMath.Clamp01((rr - FxMath.Sqrt(dx * dx + dy * dy)) / 0.04f);
    }

    // 2 つ重ねた « (飛び退く)
    private static float Chevrons(float x, float y)
    {
        float best = 0f;
        for (int k = 0; k < 2; k++)
        {
            float cx = x + 0.12f - 0.24f * k;
            float d = FxMath.Abs(cx - (FxMath.Abs(y) * 0.6f - 0.1f));
            if (FxMath.Abs(y) > 0.3f) continue;
            best = FxMath.Max(best, FxMath.Clamp01((0.06f - d) / 0.025f));
        }
        return best;
    }
}
