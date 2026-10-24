// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/MetaWidget/GUIWidget.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// 自前の画面の部品。部品は「作り方」だけを持つ記述で、Build で GameObject を作り、実際の大きさを返す。
// 作った物の原点は自分の四角の中心。並べる箱 (Stack) が子の中心の位置を決める。
// 画面 (UiScreen) は中身を変える時に前の物を全部捨てて作り直す (毎フレームの処理は持たない)。
internal enum Align { Start, Center, End }

internal sealed class BuildCtx
{
    public readonly List<int> Slots;   // この画面が借りたボタンの番号 (作り直す時に返す)
    public readonly List<UiScreen> Screens = new(); // 中に作った画面 (スクロール欄の中身)。外の画面と一緒に片付ける
    public Collider2D ClickMask;       // スクロール欄の中なら、その欄の当たり判定 (欄の外では押せない)
    public int Layer;

    public BuildCtx(List<int> slots, int layer)
    {
        Slots = slots;
        Layer = layer;
    }
}

internal abstract class Widget
{
    // 親の四角の中での揃え (縦に並べる箱では横・横に並べる箱では縦)
    public Align Align = Align.Center;

    public abstract GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size);

    internal static GameObject Node(string name, Transform parent, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        return go;
    }
}

// 空き
internal sealed class Spacer : Widget
{
    private readonly Vector2 _size;
    public Spacer(float w, float h) { _size = new Vector2(w, h); }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        size = _size;
        return Node("Spacer", parent, ctx.Layer);
    }
}

// 字。Width = 0 なら字の幅に合わせる (maxWidth を超える分は折り返す)
internal sealed class Label : Widget
{
    public string Content;
    public float FontSize = 1.6f;
    public Color Color = Color.white;
    public bool Bold;
    public float Width;
    public TextAlignmentOptions TextAlign = TextAlignmentOptions.Left;
    public bool MenuFont;     // 見出し・役職名のフォント (無い字は本編のフォント)
    public float Outline;

    // 作った字 (作り直さずに字だけ書き換える時に使う)
    public TextMeshPro Built { get; private set; }

    public Label(string content, float fontSize = 1.6f)
    {
        Content = content;
        FontSize = fontSize;
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var proto = UiAssets.TextProto;
        if (!proto) { size = Vector2.zero; return Node("Label", parent, ctx.Layer); }
        var t = Object.Instantiate(proto, parent);
        t.name = "Label";
        t.gameObject.layer = ctx.Layer;
        t.gameObject.SetActive(true);
        t.transform.localPosition = Vector3.zero;
        t.transform.localScale = Vector3.one;
        var rt = t.rectTransform;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        t.enableAutoSizing = false;
        t.margin = Vector4.zero;
        t.fontSize = FontSize;
        t.fontStyle = Bold ? FontStyles.Bold : FontStyles.Normal;
        t.color = Color;
        t.alignment = TextAlign;
        t.enableWordWrapping = true;
        t.richText = true;
        if (MenuFont) Options.MenuFont.Apply(t, 0, Outline);
        t.text = Content ?? "";
        float w = Width > 0f ? Width : Math.Min(t.GetPreferredValues(t.text).x + 0.02f, maxWidth);
        float h = t.GetPreferredValues(t.text, w, 0f).y;
        rt.sizeDelta = new Vector2(w, h);
        size = new Vector2(w, h);
        Built = t;
        return t.gameObject;
    }
}

// 絵
internal sealed class Picture : Widget
{
    public Sprite Sprite;
    public Vector2 Size;
    public Color Color = Color.white;

    public Picture(Sprite sprite, float w, float h)
    {
        Sprite = sprite;
        Size = new Vector2(w, h);
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node("Picture", parent, ctx.Layer);
        size = Size;
        if (!Sprite) return go;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Sprite;
        sr.color = Color;
        // 絵の元の大きさから Size に合わせる
        var b = Sprite.bounds.size;
        if (b.x > 0f && b.y > 0f) go.transform.localScale = new Vector3(Size.x / b.x, Size.y / b.y, 1f);
        return go;
    }
}

// 縦 (Horizontal = false) / 横に並べる。Width を決めると縦の箱は子をその幅の中で揃える
internal sealed class Stack : Widget
{
    public readonly List<Widget> Children = new();
    public bool Horizontal;
    public float Gap = 0.05f;
    public float Width;

    public Stack(bool horizontal, params Widget[] children)
    {
        Horizontal = horizontal;
        foreach (var c in children) if (c != null) Children.Add(c);
    }

    public Stack Add(Widget w)
    {
        if (w != null) Children.Add(w);
        return this;
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node(Horizontal ? "HStack" : "VStack", parent, ctx.Layer);
        var objs = new GameObject[Children.Count];
        var sizes = new Vector2[Children.Count];
        float inner = Width > 0f ? Width : maxWidth;
        float left = inner;
        for (int i = 0; i < Children.Count; i++)
        {
            objs[i] = Children[i].Build(go.transform, ctx, Horizontal ? Math.Max(left, 0f) : inner, out sizes[i]);
            if (Horizontal) left -= sizes[i].x + Gap;
        }

        float main = 0f, cross = 0f;
        for (int i = 0; i < sizes.Length; i++)
        {
            main += Horizontal ? sizes[i].x : sizes[i].y;
            cross = Math.Max(cross, Horizontal ? sizes[i].y : sizes[i].x);
        }
        if (sizes.Length > 1) main += Gap * (sizes.Length - 1);
        if (!Horizontal && Width > 0f) cross = Width;

        // 先頭 = 左 / 上
        float cursor = -main / 2f;
        for (int i = 0; i < objs.Length; i++)
        {
            float len = Horizontal ? sizes[i].x : sizes[i].y;
            float c = cursor + len / 2f;
            float extent = Horizontal ? sizes[i].y : sizes[i].x;
            float off = Children[i].Align switch
            {
                Align.Start => -(cross - extent) / 2f,
                Align.End => (cross - extent) / 2f,
                _ => 0f,
            };
            // 横の箱の Start = 上
            objs[i].transform.localPosition = Horizontal ? new Vector3(c, -off, 0f) : new Vector3(off, -c, 0f);
            cursor += len + Gap;
        }
        size = Horizontal ? new Vector2(main, cross) : new Vector2(cross, main);
        return go;
    }
}

// 本編の値の欄と同じ白い縁の黒い箱で中身を囲む。Size を決めると中身に関わらずその大きさ
internal class Box : Widget
{
    public Widget Content;
    public Vector2 Padding = new(0.12f, 0.08f);
    public Vector2 MinSize;
    public Vector2 Size;
    public Color Color = Color.white;
    public Color? Fill;   // 中を塗る色 (null = 本編の箱のまま黒)

    public Box(Widget content) { Content = content; }

    protected SpriteRenderer Back;
    public SpriteRenderer BackRenderer => Back;
    public SpriteRenderer FillRenderer { get; private set; }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node(GetType().Name, parent, ctx.Layer);
        Vector2 inner = Vector2.zero;
        GameObject content = null;
        if (Content != null)
        {
            float w = (Size.x > 0f ? Size.x : maxWidth) - Padding.x * 2f;
            content = Content.Build(go.transform, ctx, Math.Max(w, 0f), out inner);
        }
        var s = new Vector2(
            Size.x > 0f ? Size.x : Math.Max(inner.x + Padding.x * 2f, MinSize.x),
            Size.y > 0f ? Size.y : Math.Max(inner.y + Padding.y * 2f, MinSize.y));
        if (content)
        {
            float x = Content.Align switch
            {
                Align.Start => -(s.x - inner.x) / 2f + Padding.x,
                Align.End => (s.x - inner.x) / 2f - Padding.x,
                _ => 0f,
            };
            content.transform.localPosition = new Vector3(x, 0f, -0.02f);
        }
        var bg = Node(UiScreen.BackName, go.transform, ctx.Layer);
        Back = bg.AddComponent<SpriteRenderer>();
        Back.sprite = UiAssets.Box;
        Back.drawMode = SpriteDrawMode.Sliced;
        Back.size = s;
        Back.color = Color;
        if (Fill is Color fill)
        {
            var f = Node(UiScreen.FillName, bg.transform, ctx.Layer);
            f.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            var fr = f.AddComponent<SpriteRenderer>();
            fr.sprite = UiAssets.Blank;
            // 白い縁の内側だけを塗る (縁の太さと角の丸みのぶん小さく)
            var b = fr.sprite ? fr.sprite.bounds.size : Vector3.one;
            f.transform.localScale = new Vector3(Math.Max(s.x - 0.12f, 0.01f) / Math.Max(b.x, 0.0001f), Math.Max(s.y - 0.12f, 0.01f) / Math.Max(b.y, 0.0001f), 1f);
            fr.color = fill;
            FillRenderer = fr;
        }
        size = s;
        OnBuilt(go, ctx, s);
        return go;
    }

    protected virtual void OnBuilt(GameObject go, BuildCtx ctx, Vector2 size) { }
}

// 押せる箱。当てると縁が緑になる (本編のボタンの当て方の色)
internal sealed class Button : Box
{
    public static readonly Color HoverColor = new(0.2f, 1f, 0.2f, 1f);

    public Action OnClick;
    public Action OnHover;
    public Action OnUnhover;

    public Button(Widget content, Action onClick) : base(content) { OnClick = onClick; }

    public Button(string text, Action onClick, float fontSize = 1.6f) : base(new Label(text, fontSize) { TextAlign = TextAlignmentOptions.Center })
    {
        OnClick = onClick;
    }

    protected override void OnBuilt(GameObject go, BuildCtx ctx, Vector2 size)
    {
        var col = go.AddComponent<BoxCollider2D>();
        col.size = size;
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        if (ctx.ClickMask) pb.ClickMask = ctx.ClickMask;
        var hover = OnHover;
        var unhover = OnUnhover;
        if (OnClick != null) pb.OnClick.AddListener(UiActions.Take(OnClick, ctx.Slots));
        pb.OnMouseOver.AddListener(UiActions.Take(() =>
        {
            _over = true;
            if (Back) Back.color = HoverColor;
            hover?.Invoke();
        }, ctx.Slots));
        pb.OnMouseOut.AddListener(UiActions.Take(() =>
        {
            _over = false;
            if (Back) Back.color = Color;
            unhover?.Invoke();
        }, ctx.Slots));
    }

    private bool _over;

    // 縁の色を変える (当てている間は当てた色のまま、外れた時にこの色へ)
    public void SetColor(Color c)
    {
        Color = c;
        if (Back && !_over) Back.color = c;
    }
}

// 決まった大きさの置き場。中身は Screen で後から何度でも作り直せる (左上が起点)
internal sealed class Holder : Widget
{
    public Vector2 Size;
    public Widget Initial;
    public UiScreen Screen { get; private set; }

    public Holder(Vector2 size, Widget initial = null)
    {
        Size = size;
        Initial = initial;
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node("Holder", parent, ctx.Layer);
        var inner = Node("Inner", go.transform, ctx.Layer);
        inner.transform.localPosition = new Vector3(-Size.x / 2f, Size.y / 2f, -0.01f);
        Screen = new UiScreen(inner.transform) { Anchor = Align.Start, MaxWidth = Size.x, ClickMask = ctx.ClickMask, BaseOrder = 20 };
        ctx.Screens.Add(Screen);
        if (Initial != null) Screen.Set(Initial);
        size = Size;
        return go;
    }
}

// 作り直しの単位。Root の下を中身ごと捨てて作り直す
internal sealed class UiScreen
{
    public readonly Transform Root;
    private readonly List<int> _slots = new();
    public Collider2D ClickMask;
    public Vector2 LastSize { get; private set; }

    // 中身の置き方: Start = 左上を Root の原点に / Center = 中心を原点に
    public Align Anchor = Align.Center;
    public float MaxWidth = 10f;

    public UiScreen(Transform root) { Root = root; }

    public bool Alive => Root;

    public GameObject Set(Widget w)
    {
        Clear();
        if (!Root || w == null) return null;
        var ctx = new BuildCtx(_slots, Root.gameObject.layer) { ClickMask = ClickMask };
        var go = w.Build(Root, ctx, MaxWidth, out var size);
        _inner.AddRange(ctx.Screens);
        LastSize = size;
        go.transform.localPosition = Anchor == Align.Start ? new Vector3(size.x / 2f, -size.y / 2f, 0f) : Vector3.zero;
        Order(go.transform, BaseOrder);
        return go;
    }

    internal const string BackName = "Back", FillName = "Fill";

    // 描く順の起点。部品は深いほど後に描く
    public int BaseOrder;

    // 窓とスクロール欄は SortingGroup の中なので、z ではなく描く順で前後が決まる。
    // 箱の中身は箱より深いので、深さをそのまま順にすれば中身が箱の上に出る
    internal static void Order(Transform t, int order)
    {
        var r = t.GetComponent<Renderer>();
        if (r) r.sortingOrder = order;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            // 入れ子の SortingGroup (スクロール欄) の中の順は、その欄が自分で決める
            if (c.GetComponent<UnityEngine.Rendering.SortingGroup>()) continue;
            // 箱の地 (Back) は中身と同じ深さだが中身より先に描き、地の塗り (Back の子) はその間に描く
            Order(c, c.name == BackName ? order : c.name == FillName ? order + 1 : order + 2);
        }
    }

    private readonly List<UiScreen> _inner = new();

    public void Clear()
    {
        foreach (var c in _inner) c.Clear();
        _inner.Clear();
        UiActions.Release(_slots);
        if (!Root) return;
        // 指が乗っているボタンを捨てると、本編のボタンの管理役が捨てたボタンへ毎フレーム「外れた」を送って落ち続ける
        if (PassiveButtonManager.InstanceExists)
        {
            var pm = PassiveButtonManager.Instance;
            var over = pm.currentOver;
            if (over && over.transform.IsChildOf(Root)) pm.currentOver = null;
        }
        for (int i = Root.childCount - 1; i >= 0; i--)
        {
            var c = Root.GetChild(i).gameObject;
            // Destroy は遅れるので、同じフレームで作り直す物と混ざらないよう先に外す
            c.transform.SetParent(null, false);
            Object.Destroy(c);
        }
    }
}
