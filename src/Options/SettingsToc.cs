using System;
using System.Collections.Generic;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 本編の行で組む頁 (ゲーム・全般) の右の細い列に、節の見出しの目次。押すとその節が頁の上に来るまでスクロールする。
// 見出しの位置は行の出入りで動くので、押した時に読む。節の中に初期値から変えた MRP の項目があれば見出しの後ろに橙の点
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
        Entries.Remove(m.Pointer);
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
        var entries = new List<Entry>();
        for (int i = 0; i < heads.Count; i++)
        {
            var t = heads[i].transform;
            var e = new Entry { Head = t, Title = heads[i].Title.text };
            e.Label = new Label(e.Title, 1.05f) { Width = W - 0.08f, MinFontSize = 0.65f, TextAlign = TMPro.TextAlignmentOptions.Center };
            entries.Add(e);
            col.Add(new Button(e.Label, () => Jump(m, t))
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
        Entries[m.Pointer] = (m, entries);
        RefreshMarks();
    }

    private sealed class Entry
    {
        public Transform Head;
        public string Title;
        public Label Label;
        public bool Changed;
    }

    private static readonly Dictionary<IntPtr, (GameOptionsMenu Menu, List<Entry> List)> Entries = new();

    // 値を変えた後: 節の中に初期値から変えた MRP の項目があるかを数え直し、点の有無が変わった見出しだけ字を書き直す。
    // 見出しは上から並べてあるので、行を 1 回だけなめて y で節へ振り分ける (本編の項目は既定を持たないので見ない)
    public static void RefreshMarks()
    {
        foreach (var (m, list) in Entries.Values)
        {
            if (!m || !m.settingsContainer) continue;
            var tops = new float[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Changed = false;
                tops[i] = list[i].Head ? list[i].Head.localPosition.y : float.NaN;
            }
            var c = m.settingsContainer;
            for (int k = 0; k < c.childCount; k++)
            {
                var row = c.GetChild(k);
                if (!row.gameObject.activeSelf) continue;
                float y = row.localPosition.y;
                int sec = -1;
                for (int i = list.Count - 1; i >= 0; i--)
                    if (tops[i] > y) { sec = i; break; }
                if (sec < 0 || list[sec].Changed) continue;
                var ob = row.GetComponent<OptionBehaviour>();
                if (ob && SettingsMenu.TryRow(ob, out var opt) && opt.Changed) list[sec].Changed = true;
            }
            foreach (var e in list)
            {
                var t = e.Label.Built;
                if (!t) continue;
                string s = e.Changed ? e.Title + OptEditor.ChangedMark : e.Title;
                if (s != e.Label.Content) { e.Label.Content = s; t.text = s; }
            }
        }
    }

    // ボタンの番号は借り物なので、設定画面を作り直す時と閉じる時に返す
    private static readonly Dictionary<IntPtr, UiScreen> Screens = new();

    public static void Forget()
    {
        foreach (var s in Screens.Values) s.Clear();
        Screens.Clear();
        Offsets.Clear();
        Entries.Clear();
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
