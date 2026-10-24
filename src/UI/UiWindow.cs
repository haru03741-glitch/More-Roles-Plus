// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/MetaScreen.cs (GPL-3.0)
using System;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// 画面の真ん中に出す窓。後ろに暗い幕を敷き、幕は画面全体の押下を受け止める (下の HUD やロビーへ押しが抜けない)。
// 幕を押すか × で閉じる。中身は Body (UiScreen) で作り直す。同時に開くのは 1 枚だけ。
internal sealed class UiWindow
{
    private const int UiLayer = 5;
    // 本編の Esc の画面・試合の情報 (-900) より手前 (押した所がそちらへ届かないように)
    private const float Z = -950f;

    public static UiWindow Current { get; private set; }

    public readonly GameObject Root;
    public readonly UiScreen Body;
    public readonly Vector2 Size;
    private readonly UiScreen _chrome;
    private Action _onClose;

    public static bool IsOpen => Current != null && Current.Root;

    private UiWindow(GameObject root, Vector2 size)
    {
        Root = root;
        Size = size;
        var chrome = Widget.Node("Chrome", root.transform, UiLayer);
        _chrome = new UiScreen(chrome.transform);
        var body = Widget.Node("Body", root.transform, UiLayer);
        body.transform.localPosition = new Vector3(0f, 0f, -0.5f);
        Body = new UiScreen(body.transform) { MaxWidth = size.x - 0.3f, BaseOrder = 10 };
    }

    // 開く。onClose は閉じた時に 1 回 (幕・×・Close のどれでも)
    public static UiWindow Open(string name, Vector2 size, Action onClose = null)
    {
        Current?.Close();
        var hud = Vanilla.Hud;
        var parent = hud ? hud.transform : Camera.main ? Camera.main.transform : null;
        if (!parent || !UiAssets.Box) return null;
        var root = Widget.Node("MrpWindow_" + name, parent, UiLayer);
        root.transform.localPosition = new Vector3(0f, 0f, Z);
        // 窓をひとまとまりで HUD やロビーの絵より前に描く
        var group = root.AddComponent<UnityEngine.Rendering.SortingGroup>();
        group.sortingOrder = 500;
        var w = new UiWindow(root, size) { _onClose = onClose };
        w.BuildChrome();
        Current = w;
        return w;
    }

    private void BuildChrome()
    {
        var ctx = new BuildCtx(new System.Collections.Generic.List<int>(), UiLayer);
        var t = _chrome.Root;

        // 幕: 画面全体を覆う暗い板 + 当たり判定。押すと閉じる
        var shade = Widget.Node("Shade", t, UiLayer);
        shade.transform.localPosition = new Vector3(0f, 0f, 1f);
        var sr = shade.AddComponent<SpriteRenderer>();
        sr.sprite = UiAssets.Blank;
        sr.color = new Color(0f, 0f, 0f, 0.6f);
        sr.sortingOrder = -2;
        var b = sr.sprite.bounds.size;
        shade.transform.localScale = new Vector3(40f / b.x, 40f / b.y, 1f);
        shade.AddComponent<BoxCollider2D>().size = new Vector2(b.x, b.y);
        var pb = shade.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener(CloseAction);

        // 窓の板 (押下を受け止めるだけ。押しても閉じない)
        var panel = Widget.Node("Panel", t, UiLayer);
        panel.transform.localPosition = new Vector3(0f, 0f, 0.5f);
        var ps = panel.AddComponent<SpriteRenderer>();
        ps.sprite = UiAssets.Box;
        ps.drawMode = SpriteDrawMode.Sliced;
        ps.size = Size;
        ps.color = new Color(0.75f, 0.75f, 0.75f, 1f);
        ps.sortingOrder = -1;
        panel.AddComponent<BoxCollider2D>().size = Size;
        var guard = panel.AddComponent<PassiveButton>();
        guard.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        guard.OnMouseOver = new UnityEvent();
        guard.OnMouseOut = new UnityEvent();

        // ×
        var close = new Button(new Label("×", 2.6f) { TextAlign = TMPro.TextAlignmentOptions.Center }, () => Current?.Close())
        {
            Size = new Vector2(0.55f, 0.55f),
        }.Build(t, ctx, 1f, out _);
        close.transform.localPosition = new Vector3(-Size.x / 2f - 0.05f, Size.y / 2f + 0.05f, -1f);
        UiScreen.Order(close.transform, 1);
        _closeSlots = ctx.Slots;
    }

    private System.Collections.Generic.List<int> _closeSlots;

    private static UnityAction _closeAction;
    private static UnityAction CloseAction => _closeAction ??= (UnityAction)(() => Current?.Close());

    // 場面が変わって窓が消えた時に、借りた番号と参照を手放す (毎フレーム・窓が無い時は何もしない)
    public static void Tick()
    {
        if (Current != null && !Current.Root) Current.Close();
    }

    public void Close()
    {
        if (Current == this) Current = null;
        Body.Clear();
        _chrome.Clear();
        if (_closeSlots != null) UiActions.Release(_closeSlots);
        if (Root) Object.Destroy(Root);
        var cb = _onClose;
        _onClose = null;
        cb?.Invoke();
    }
}
