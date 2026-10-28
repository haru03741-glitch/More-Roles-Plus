using System;
using System.Collections.Generic;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 本編の行で組む頁 (ゲーム・全般) の右の細い列に、節の見出しの目次。押すとその節が頁の上に来るまでスクロールする。
// 見出しの位置は行の出入りで動くので、押した時に読む
internal static class SettingsToc
{
    // タブの GameOptionsMenu の座標で、行の右とスクロールバーの間
    private static readonly Vector3 TopLeft = new(6.5f, -0.24f, -6f);
    private const float W = 0.9f, H = 3.95f, TopY = 2f, BtnH = 0.32f;
    private static readonly Color Rim = new(0.42f, 0.42f, 0.45f, 1f);

    // dx = 頁ごとの行の右端の違い (本編のゲーム設定のタブは数値の欄が右へ長い)
    public static void Attach(GameOptionsMenu m, float dx = 0f)
    {
        if (!m || !m.settingsContainer || !UiAssets.Box) return;
        // 本編のゲーム設定のタブは同じ頁で何度も作り直されるので、前の目次のボタンの番号を返してから捨てる
        if (Screens.TryGetValue(m.Pointer, out var prev)) prev.Clear();
        Screens.Remove(m.Pointer);
        var old = m.transform.Find(NodeName);
        if (old) UnityEngine.Object.Destroy(old.gameObject);

        var heads = new List<CategoryHeaderMasked>();
        var container = m.settingsContainer;
        for (int i = 0; i < container.childCount; i++)
        {
            var h = container.GetChild(i).GetComponent<CategoryHeaderMasked>();
            if (h && h.gameObject.activeSelf && h.Title) heads.Add(h);
        }
        if (heads.Count < 2) return;
        heads.Sort((a, b) => b.transform.localPosition.y.CompareTo(a.transform.localPosition.y));

        var go = Widget.Node(NodeName, m.transform, m.gameObject.layer);
        go.transform.localPosition = TopLeft + new Vector3(dx, 0f, 0f);
        go.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        var col = new Stack(false) { Gap = 0.06f, Width = W };
        foreach (var h in heads)
        {
            var t = h.transform;
            col.Add(new Button(new Label(h.Title.text, 1.05f) { Width = W - 0.08f, MinFontSize = 0.65f, TextAlign = TMPro.TextAlignmentOptions.Center }, () => Jump(m, t))
            {
                Size = new Vector2(W, BtnH),
                Padding = new Vector2(0.04f, 0.02f),
                Color = Rim,
            });
        }
        // 見出しが多い頁は目次の中もスクロールする
        Widget body = heads.Count * (BtnH + 0.06f) > H ? new ScrollView(new Vector2(W, H), col) : col;
        var screen = new UiScreen(go.transform) { Anchor = Align.Start, MaxWidth = W, BaseOrder = 1 };
        screen.Set(body);
        Screens[m.Pointer] = screen;
        Offsets[m.Pointer] = dx;
    }

    // ボタンの番号は借り物なので、設定画面を作り直す時と閉じる時に返す
    private static readonly Dictionary<IntPtr, UiScreen> Screens = new();

    public static void Forget()
    {
        foreach (var s in Screens.Values) s.Clear();
        Screens.Clear();
        Offsets.Clear();
    }

    private const string NodeName = "MrpToc";

    // 見出しが出入りした後: 目次を作ってある頁だけ作り直す (まだ作っていない頁は、作る時に今の見出しで作る)
    public static void Refresh(GameOptionsMenu m)
    {
        if (m && Screens.ContainsKey(m.Pointer)) Attach(m, Offsets.TryGetValue(m.Pointer, out float dx) ? dx : 0f);
    }

    private static readonly Dictionary<IntPtr, float> Offsets = new();

    private static void Jump(GameOptionsMenu m, Transform header)
    {
        if (!m || !header || !header.gameObject.activeInHierarchy) return;
        var s = m.scrollBar;
        if (!s || !s.Inner) return;
        var b = s.ContentYBounds;
        var p = s.Inner.localPosition;
        s.Inner.localPosition = new Vector3(p.x, Math.Clamp(TopY - header.localPosition.y, b.min, b.max), p.z);
        s.UpdateScrollBars();
    }
}
