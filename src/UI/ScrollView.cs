// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/MetaWidget/GUIScrollView.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoreRolesPlus.UI;

// 縦にスクロールする欄。欄の外は SpriteMask で切り抜き、ホイールとドラッグは本編の Scroller で動かす。
// 中身は Inner の UiScreen で作り直せる (欄そのものは作り直さない)。
// Tag を付けると、作り直しても前のスクロールの位置へ戻す (位置は作り直す時と閉じる時に控える)。
internal sealed class ScrollView : Widget
{
    public Vector2 Size;
    public Widget Content;
    public string Tag;
    public Action<ScrollView> OnBuiltView;

    private static readonly Dictionary<string, float> Positions = new();

    public UiScreen Inner { get; private set; }
    public Scroller Scroller { get; private set; }

    public ScrollView(Vector2 size, Widget content)
    {
        Size = size;
        Content = content;
    }

    private const float BarW = 0.08f, BarGap = 0.06f;

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var view = Node("ScrollView", parent, ctx.Layer);
        view.AddComponent<SortingGroup>();
        float innerW = Size.x - BarW - BarGap;

        var maskGo = Node("Mask", view.transform, ctx.Layer);
        maskGo.transform.localPosition = new Vector3(-(BarW + BarGap) / 2f, 0f, 0f);
        var mask = maskGo.AddComponent<SpriteMask>();
        mask.sprite = UiAssets.Blank;
        // 欄の中の物 (順 1 以上) より先に型板を書く
        mask.sortingOrder = 0;
        var mb = mask.sprite ? mask.sprite.bounds.size : Vector3.one;
        maskGo.transform.localScale = new Vector3(innerW / Math.Max(mb.x, 0.0001f), Size.y / Math.Max(mb.y, 0.0001f), 1f);

        // 当たり判定 (ホイールとドラッグ・欄の外の押下を止める) は Scroller より先に付ける
        var scrollGo = Node("Scroller", view.transform, ctx.Layer);
        scrollGo.transform.localPosition = new Vector3(0f, 0f, 0.05f);
        var hit = scrollGo.AddComponent<BoxCollider2D>();
        hit.size = Size;

        var innerGo = Node("Inner", view.transform, ctx.Layer);
        innerGo.transform.localPosition = new Vector3(-(BarW + BarGap) / 2f, 0f, -0.05f);

        var track = Node("Track", view.transform, ctx.Layer);
        track.transform.localPosition = new Vector3(Size.x / 2f - BarW / 2f, 0f, -0.02f);
        var trackSr = track.AddComponent<SpriteRenderer>();
        trackSr.sprite = UiAssets.Blank;
        trackSr.color = new Color(1f, 1f, 1f, 0.15f);
        trackSr.sortingOrder = 1;
        track.transform.localScale = new Vector3(BarW / Math.Max(mb.x, 0.0001f), Size.y / Math.Max(mb.y, 0.0001f), 1f);

        var bar = Node("Bar", view.transform, ctx.Layer);
        bar.transform.localPosition = new Vector3(Size.x / 2f - BarW / 2f, 0f, -0.04f);
        var barSr = bar.AddComponent<SpriteRenderer>();
        barSr.sprite = UiAssets.Box;
        barSr.drawMode = SpriteDrawMode.Sliced;
        float barH = Math.Max(Size.y * 0.25f, 0.3f);
        barSr.size = new Vector2(BarW, barH);
        barSr.color = new Color(0.75f, 0.9f, 0.9f, 1f);
        barSr.sortingOrder = 2;
        bar.AddComponent<BoxCollider2D>().size = new Vector2(BarW * 2f, barH);
        var sb = bar.AddComponent<Scrollbar>();
        sb.graphic = barSr;
        sb.trackGraphic = trackSr;

        var scroller = scrollGo.AddComponent<Scroller>();
        scroller.Inner = innerGo.transform;
        scroller.allowX = false;
        scroller.allowY = true;
        scroller.active = true;
        scroller.MouseMustBeOverToScroll = true;
        scroller.ScrollWheelSpeed = 0.3f;
        scroller.ScrollbarY = sb;
        scroller.ScrollbarYBounds = new FloatRange(-Size.y / 2f + barH / 2f, Size.y / 2f - barH / 2f);
        sb.parent = scroller;
        Scroller = scroller;

        Inner = new UiScreen(innerGo.transform) { ClickMask = hit, Anchor = Align.Center, MaxWidth = innerW, BaseOrder = 10 };
        ctx.Screens.Add(Inner);
        SetContent(Content);
        size = Size;
        OnBuiltView?.Invoke(this);
        return view;
    }

    // 中身を作り直す。Tag があれば前の位置へ
    public void SetContent(Widget content)
    {
        if (Inner == null || !Inner.Alive) return;
        Remember();
        Content = content;
        var go = Inner.Set(content);
        float h = Inner.LastSize.y;
        if (go)
        {
            // 中身の上端を欄の上端に
            go.transform.localPosition = new Vector3(0f, Size.y / 2f - h / 2f, 0f);
            Masked(go.transform);
        }
        var s = Scroller;
        if (!s) return;
        s.SetBounds(new FloatRange(0f, Math.Max(0f, h - Size.y)), null);
        float y = Tag != null && Positions.TryGetValue(Tag, out var p) ? p : 0f;
        var ip = s.Inner.localPosition;
        s.Inner.localPosition = new Vector3(ip.x, Math.Clamp(y, 0f, Math.Max(0f, h - Size.y)), ip.z);
        s.UpdateScrollBars();
        if (s.ScrollbarY) s.ScrollbarY.gameObject.SetActive(h > Size.y);
    }

    // 今のスクロールの位置を控える (画面を閉じる前に呼ぶ)
    public void Remember()
    {
        if (Tag != null && Scroller) Positions[Tag] = Scroller.Inner.localPosition.y;
    }

    // 欄の中の絵と字を、欄の内側だけに描くようにする
    internal static void Masked(Transform root)
    {
        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
            sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
        foreach (var t in root.GetComponentsInChildren<TextMeshPro>(true))
            t.fontSharedMaterial = UiAssets.MaskedFont(t.fontSharedMaterial);
    }
}
