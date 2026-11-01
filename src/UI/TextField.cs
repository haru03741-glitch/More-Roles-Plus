using System;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.UI;

// 字を打ち込む欄 (本編のチャットの欄が無いメインメニューでも使える)。
// 押すと入力を受け、Input.inputString で字を足す。日本語の変換中の字 (IME) は下線で出す。
// Ctrl+V で貼り付け・Backspace で消す・Enter は 1 行の欄なら OnSubmit、複数行の欄なら改行 (Ctrl+Enter で OnSubmit)。
// 欄の外を押すと入力をやめる。入力中は Typing が true (本編の移動やキーの割り当てを止める)
// Android は Input.inputString が無いので、入力中は端末のキーボードを開いてその字を写す (決定で OnSubmit)
internal sealed class TextField : Widget
{
    public string Value = "";
    public string Hint = "";
    public int MaxLength = 200;
    public bool Multiline;
    public Vector2 Size;
    public float FontSize = 1.4f;
    public Action<string> OnChange;
    public Action OnSubmit;
    public Action OnBlur;      // 入力をやめた時 (欄の外を押した・別の欄へ移った・欄が消えた)
    public Action OnCancel;    // Esc (null なら Esc は何もしない)
    public bool Selected;      // 字が全部選ばれている (最初に打った字・貼った字・Backspace で前の字を消す)

    private static readonly Color Field = new(0.07f, 0.075f, 0.095f, 1f), Edge = new(0.24f, 0.26f, 0.32f, 1f), EdgeFocus = new(0f, 0.9f, 0.46f, 1f);
    private const float Pad = 0.12f;

    private static TextField _focused;
    private static bool _clickedField;
#if ANDROID
    private static TouchScreenKeyboard _keyboard;
    private static TextField _keyboardFor;
#endif
    private TextMeshPro _text;
    private SpriteRenderer _edge;
    private GameObject _root;

    public static bool Typing => _focused != null && _focused._root;
    public static TextField Focused => _focused;

    public TextField(Vector2 size, string value = "", string hint = "")
    {
        Size = size;
        Value = value ?? "";
        Hint = hint ?? "";
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node("TextField", parent, ctx.Layer);
        _root = go;
        size = Size;
        float r = Math.Min(0.12f, Size.y / 2f);
        // 縁 (Back) と地 (Fill)
        var edge = Node(UiScreen.BackName, go.transform, ctx.Layer).AddComponent<SpriteRenderer>();
        edge.sprite = Options.MenuArt.Round(r);
        edge.drawMode = SpriteDrawMode.Sliced;
        edge.size = Size;
        edge.color = Edge;
        _edge = edge;
        var fill = Node(UiScreen.FillName, edge.transform, ctx.Layer).AddComponent<SpriteRenderer>();
        fill.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        fill.sprite = Options.MenuArt.Round(Math.Max(r - 0.03f, 0.01f));
        fill.drawMode = SpriteDrawMode.Sliced;
        fill.size = new Vector2(Size.x - 0.06f, Size.y - 0.06f);
        fill.color = Field;

        var proto = UiAssets.TextProto;
        if (proto)
        {
            var t = UnityEngine.Object.Instantiate(proto, go.transform);
            t.name = "Text";
            t.gameObject.layer = ctx.Layer;
            t.gameObject.SetActive(true);
            t.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            t.transform.localScale = Vector3.one;
            var rt = t.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(Size.x - Pad * 2f, Size.y - Pad * 1.2f);
            t.enableAutoSizing = false;
            t.margin = Vector4.zero;
            t.fontSize = FontSize;
            t.fontStyle = FontStyles.Normal;
            t.richText = true;
            t.enableWordWrapping = Multiline;
            t.alignment = Multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left;
            _text = t;
        }

        var col = go.AddComponent<BoxCollider2D>();
        col.size = Size;
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        if (ctx.ClickMask) pb.ClickMask = ctx.ClickMask;
        pb.OnClick.AddListener(UiActions.Take(Focus, ctx.Slots));
        Refresh();
        return go;
    }

    public void Focus()
    {
        _clickedField = true;
        if (_focused == this) return;
        var old = _focused;
        _focused = this;
        old?.Refresh();
        old?.OnBlur?.Invoke();
        Input.imeCompositionMode = IMECompositionMode.On;
        Refresh();
    }

    public static void Blur()
    {
        var old = _focused;
        _focused = null;
#if ANDROID
        CloseKeyboard();
#endif
        Input.imeCompositionMode = IMECompositionMode.Auto;
        old?.Refresh();
        old?.OnBlur?.Invoke();
    }

    public void Set(string v)
    {
        Value = Clip(v ?? "");
        Refresh();
        OnChange?.Invoke(Value);
    }

    private string Clip(string v)
    {
        if (!Multiline) v = v.Replace("\r", "").Replace("\n", " ");
        else v = v.Replace("\r\n", "\n").Replace('\r', '\n');
        return v.Length > MaxLength ? v.Substring(0, MaxLength) : v;
    }

    private static string Escape(string s) => s.Replace("<", "<​");

    private void Refresh()
    {
        if (!_text) return;
        bool focused = _focused == this;
        if (_edge) _edge.color = focused ? EdgeFocus : Edge;
        string comp = focused ? Input.compositionString ?? "" : "";
        if (Value.Length == 0 && comp.Length == 0 && !focused)
        {
            _text.text = $"<color=#7E8592>{Escape(Hint)}</color>";
            return;
        }
        bool caret = focused && (int)(Time.realtimeSinceStartup * 2f) % 2 == 0;
        string tail = (comp.Length > 0 ? $"<u><color=#FFE082>{Escape(comp)}</color></u>" : "") + (caret ? "<color=#00E676>|</color>" : "<alpha=#00>|");
        // 入り切らない時は前を落として、打っている所 (最後) を見せる
        string body = Value;
        float w = Size.x - Pad * 2f, h = Size.y - Pad * 1.2f;
        if (Selected && focused && comp.Length == 0)
        {
            _text.text = $"<mark=#00E67666>{Escape(body)}</mark>" + tail;
            return;
        }
        string shown = Escape(body) + tail;
        _text.text = shown;
        int cut = 0;
        while (cut < body.Length && Overflow(shown, w, h))
        {
            cut += Math.Max(1, (body.Length - cut) / 8);
            shown = "…" + Escape(body.Substring(Math.Min(cut, body.Length))) + tail;
        }
        _text.text = shown;
    }

    private bool Overflow(string s, float w, float h)
    {
        if (!Multiline) return _text.GetPreferredValues(s).x > w;
        return _text.GetPreferredValues(s, w, 0f).y > h;
    }

    // 毎フレーム (Plugin.Update)
    public static void Tick()
    {
        var f = _focused;
        if (f == null) { _clickedField = false; return; }
        if (!f._root || !f._root.activeInHierarchy) { Blur(); return; }
        // 欄の外を押したらやめる (欄を押した時はボタンの処理が同じフレームか次で Focus を呼ぶ)
        if (Input.GetMouseButtonUp(0))
        {
            if (!_clickedField) { Blur(); return; }
        }
        if (Input.GetMouseButtonDown(0)) _clickedField = false;

        if (f.OnCancel != null && Input.GetKeyDown(KeyCode.Escape)) { f.OnCancel(); return; }
#if ANDROID
        TickKeyboard(f);
#else
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        string v = f.Value;
        bool changed = false, submit = false;
        if (f.Selected)
        {
            // Enter だけなら選んだまま (そのまま決める)
            string typed = (Input.inputString ?? "").Replace("\r", "").Replace("\n", "");
            if ((ctrl && Input.GetKeyDown(KeyCode.V)) || typed.Length > 0 || !string.IsNullOrEmpty(Input.compositionString))
            {
                f.Selected = false;
                // 選んだ字は消える (Backspace はそれで終わり: 空の字からは消す物が無い)
                v = "";
                changed = true;
            }
        }
        if (ctrl && Input.GetKeyDown(KeyCode.V))
        {
            v += GUIUtility.systemCopyBuffer ?? "";
            changed = true;
        }
        else
        {
            var sb = new StringBuilder(v);
            foreach (char c in Input.inputString ?? "")
            {
                if (c == '\b') { if (sb.Length > 0) sb.Length--; changed = true; }
                else if (c == '\n' || c == '\r')
                {
                    if (f.Multiline && !ctrl) { sb.Append('\n'); changed = true; }
                    else submit = true;
                }
                else if (!char.IsControl(c)) { sb.Append(c); changed = true; }
            }
            v = sb.ToString();
        }
        if (changed)
        {
            f.Value = f.Clip(v);
            f.OnChange?.Invoke(f.Value);
        }
        f.Refresh();
        if (submit) f.OnSubmit?.Invoke();
#endif
    }

#if ANDROID
    // 入力中の欄の字を端末のキーボードの字に揃える。決定で OnSubmit・キーボードを閉じたら入力をやめる
    private static void TickKeyboard(TextField f)
    {
        if (_keyboard == null || _keyboardFor != f)
        {
            CloseKeyboard();
            f.Selected = false;
            _keyboard = TouchScreenKeyboard.Open(f.Value, TouchScreenKeyboardType.Default, false, f.Multiline, false);
            _keyboardFor = f;
            if (_keyboard == null) { Blur(); return; }
        }
        var status = _keyboard.status;
        string t = _keyboard.text ?? "";
        if (!f.Multiline) t = t.Replace("\r", "").Replace("\n", "");
        if (t != f.Value)
        {
            f.Value = f.Clip(t);
            f.OnChange?.Invoke(f.Value);
        }
        f.Refresh();
        if (status == TouchScreenKeyboard.Status.Visible) return;
        _keyboard = null;
        _keyboardFor = null;
        if (status == TouchScreenKeyboard.Status.Done) f.OnSubmit?.Invoke();
        if (_focused == f) Blur();
    }

    private static void CloseKeyboard()
    {
        var k = _keyboard;
        _keyboard = null;
        _keyboardFor = null;
        if (k != null && k.status == TouchScreenKeyboard.Status.Visible) k.active = false;
    }
#endif

    // 打っている間は本編の移動 (WASD・矢印) を止める
    [HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    private static class MovePatch
    {
        public static bool Prefix() => !Typing;
    }
}
