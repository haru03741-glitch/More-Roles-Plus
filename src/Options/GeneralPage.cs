using System;
using System.Collections.Generic;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面の「全般」の頁。真ん中 = 節の見出しと行のスクロール欄・右 = 頁の見出しと節の目次・戻すボタン。
// 行は役職の頁の右の欄と同じ OptEditor の行 (名前の右の点で初期値から変えた項目が分かる)。
internal static class GeneralPage
{
    // 頁の置き場と大きさは役職の頁と同じ (左の列からの並びを揃える)。右の欄は目次だけなので行の欄を広く取る
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    private static readonly float ListW = (float)Math.Round(W * 0.65f, 2), SideW = W - ListW - 0.15f;
    private const float HeadH = 0.4f, BarH = 0.4f;

    private static readonly OptEditor Editor = new(OnShownChanged, _ => AfterEdit(), CanEdit);

    private sealed class Entry
    {
        public Registry.Section Sec;
        public Box Head;
        public Label TocLabel;
        public bool Changed;
    }

    private static readonly List<Entry> Entries = new();
    private static UiScreen _screen;
    private static ScrollView _list;
    private static Button _resetBtn, _undoBtn;
    private static bool _built;

    private static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    public static void Build(GameOptionsMenu m)
    {
        Forget();
        if (!m || !UiAssets.Box) return;
        // タブの元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        var root = Widget.Node("MrpGeneralPage", m.transform, m.gameObject.layer);
        root.transform.localPosition = Center;
        root.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        _screen = new UiScreen(root.transform) { MaxWidth = W, BaseOrder = 1 };
        _built = true;
        Rebuild();
    }

    public static void Forget()
    {
        _list?.Remember();
        _screen?.Clear();
        _screen = null;
        _list = null;
        _resetBtn = _undoBtn = null;
        Entries.Clear();
        ShownOpts.Clear();
        Editor.Clear();
        _built = false;
    }

    private static IEnumerable<Registry.Section> Sections()
    {
        foreach (var sec in Registry.SectionsOf(Tab.General))
            if (sec.Opts.Exists(o => !o.OffTab)) yield return sec;
    }

    private static void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        Entries.Clear();
        Editor.Clear();
        bool edit = CanEdit();

        float rowW = ListW - 0.24f;
        var rows = new Stack(false) { Gap = 0.06f, Width = rowW };
        var watch = new List<Opt>();
        foreach (var sec in Sections())
            foreach (var opt in sec.Opts)
                if (!opt.OffTab) watch.Add(opt);
        Editor.Watch(watch);
        ShownOpts.Clear();
        foreach (var sec in Sections())
        {
            var e = new Entry { Sec = sec, Changed = AnyChanged(sec) };
            var shown = new List<Opt>();
            foreach (var opt in sec.Opts)
                if (!opt.OffTab && opt.Shown) shown.Add(opt);
            if (shown.Count == 0) continue;
            Color c = SecColor(sec);
            // 見出し: 節の色の字・暗い地の札 (前に少し間を空ける)
            if (Entries.Count > 0) rows.Add(new Spacer(rowW, 0.06f));
            e.Head = new Box(new Label(sec.Title.ToString(), 1.5f) { Bold = true, Color = c, Width = rowW * 0.66f - 0.24f, MinFontSize = 1.1f })
            {
                Size = new Vector2(rowW * 0.66f, HeadH),
                Padding = new Vector2(0.12f, 0.04f),
                Color = Color.Lerp(c, Color.black, 0.55f),
                Fill = new Color(0.15f, 0.16f, 0.18f, 1f),
                Align = Align.Start,
            };
            rows.Add(e.Head);
            foreach (var opt in shown)
            {
                var r = Editor.NarrowRow(opt, edit, rowW, RoleBrowser.Depth(opt) * 0.2f, opt.Fresh ? RoleBrowser.FreshColor : (Color?)null, true);
                r.Align = Align.Start;
                rows.Add(r);
            }
            Entries.Add(e);
            ShownOpts.AddRange(shown);
        }
        Editor.Refresh();
        _list = new ScrollView(new Vector2(ListW, H), rows) { Tag = "generalpage" };
        var side = Side(edit);
        _screen.Set(new Stack(true, _list, side) { Gap = 0.15f, Align = Align.Start });
    }

    private static Widget Side(bool edit)
    {
        var col = new Stack(false) { Gap = 0.06f, Width = SideW };
        Color gc = SettingsMenu.GeneralCatColor;
        col.Add(RoleBrowser.Hero(SideW, gc, RoleBrowser.PosePic(SettingsMenu.GeneralPose, gc), new Text("SettingsMenu.CatGeneral"), ""));
        // 目次: 押すとその節の見出しへ (節の中に初期値から変えた項目があれば点)
        var toc = new Stack(false) { Gap = 0.05f, Width = SideW - 0.2f };
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            int n = i;
            e.TocLabel = new Label(TocText(e), 1.3f) { Width = SideW - 0.44f, NoWrap = true, Bold = true };
            toc.Add(new Button(e.TocLabel, () => Jump(n)) { Size = new Vector2(SideW - 0.2f, 0.34f), Padding = new Vector2(0.1f, 0.02f), Color = new Color(0.42f, 0.42f, 0.45f, 1f) });
        }
        float tocH = H - RoleBrowser.HeroH - 0.06f - (edit ? BarH + 0.06f : 0f);
        col.Add(new ScrollView(new Vector2(SideW, tocH), toc) { Tag = "generalpage:toc" });
        if (edit)
        {
            float bw = (SideW - 0.05f) / 2f;
            _resetBtn = new Button(new Label(new Text("RolePage.ResetRole"), 1.2f) { Width = bw - 0.12f, MinFontSize = 0.8f, TextAlign = TextAlignmentOptions.Center }, ResetAll) { Size = new Vector2(bw, BarH), Color = ResetColor() };
            _undoBtn = new Button(new Text("RoleMenu.Undo"), Undo, 1.2f) { Size = new Vector2(bw, BarH), Color = UndoColor };
            col.Add(new Stack(true, _resetBtn, _undoBtn) { Gap = 0.05f });
        }
        return col;
    }

    private static Color SecColor(Registry.Section sec) => sec.Color != null ? Roles.RoleDisplay.ParseColor(sec.Color) : Color.white;

    private static string TocText(Entry e) => e.Changed ? e.Sec.Title + OptEditor.ChangedMark : e.Sec.Title.ToString();

    private static bool AnyChanged(Registry.Section sec)
    {
        foreach (var opt in sec.Opts)
            if (!opt.OffTab && opt.Changed && !opt.NoChangeMark) return true;
        return false;
    }

    private static Color ResetColor()
    {
        foreach (var e in Entries)
            if (AnyChanged(e.Sec)) return OptEditor.ChangedColor;
        return OptEditor.Dim;
    }

    private static Color UndoColor => OptHistory.Any ? Color.white : OptEditor.Dim;

    // 見出しを欄の上端へ
    private static void Jump(int n)
    {
        if (n < 0 || n >= Entries.Count || _list == null) return;
        var head = Entries[n].Head?.BackRenderer;
        var s = _list.Scroller;
        if (!head || !s) return;
        float hy = s.Inner.InverseTransformPoint(head.transform.position).y;
        _list.ScrollTo(_list.Size.y / 2f - hy - HeadH / 2f - 0.04f);
    }

    private static void ResetAll()
    {
        if (!CanEdit()) return;
        bool any = false;
        OptHistory.BeginGroup();
        try
        {
            foreach (var sec in Sections())
                foreach (var opt in sec.Opts)
                {
                    if (opt.OffTab) continue;
                    int before = opt.Index;
                    if (!opt.ResetToDefault()) continue;
                    OptHistory.Push(opt, before);
                    OptEditor.AfterStep(opt);
                    any = true;
                }
        }
        finally { OptHistory.EndGroup(); }
        if (any) Rebuild();
        SettingsMenu.RefreshCatCounts();
    }

    private static void Undo()
    {
        if (!OptHistory.Any || !CanEdit()) return;
        OptHistory.Undo();
        SettingsMenu.RefreshAll();
    }

    private static void OnShownChanged()
    {
        Rebuild();
        SettingsMenu.RefreshCatCounts();
    }

    private static void AfterEdit()
    {
        RefreshMarks();
        SettingsMenu.RefreshCatCounts();
    }

    // 値を変えた後: 目次の点と、戻すボタンの色
    public static void RefreshMarks()
    {
        if (!_built || _screen == null || !_screen.Alive) return;
        foreach (var e in Entries)
        {
            e.Changed = AnyChanged(e.Sec);
            var t = e.TocLabel?.Built;
            if (!t) continue;
            string s = TocText(e);
            if (s != e.TocLabel.Content) { e.TocLabel.Content = s; t.text = s; }
        }
        _resetBtn?.SetColor(ResetColor());
        _undoBtn?.SetColor(UndoColor);
    }

    // 外 (プリセット・設定のコード・取り消し・辞典の窓) で値を変えた後。出る行が変わっていれば組み直す
    public static void RefreshFromOutside()
    {
        if (!_built || _screen == null || !_screen.Alive) return;
        int k = 0;
        bool same = true;
        foreach (var sec in Sections())
            foreach (var opt in sec.Opts)
            {
                if (opt.OffTab || !opt.Shown) continue;
                if (k >= ShownOpts.Count || ShownOpts[k] != opt) { same = false; break; }
                k++;
            }
        if (!same || k != ShownOpts.Count) { Rebuild(); return; }
        Editor.Refresh();
        RefreshMarks();
    }

    // 今並べている行 (外で値を変えた後に、出る行が変わったかを比べる)
    private static readonly List<Opt> ShownOpts = new();

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        if (!_built || _screen == null || !_screen.Alive) return "general page not built";
        string op = a.Length > 0 ? a[0] : "dump";
        switch (op)
        {
            case "jump" when a.Length > 1 && int.TryParse(a[1], out int n):
                Jump(n);
                return $"jump {n} y={(_list?.Scroller ? _list.Scroller.Inner.localPosition.y : -1f):0.00}";
            case "reset":
                ResetAll();
                return "reset";
            case "undo":
                Undo();
                return "undo";
            case "rebuild":
                Rebuild();
                return "rebuild";
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Entries.Count; i++)
            sb.Append(i).Append(':').Append(Entries[i].Sec.Title).Append(Entries[i].Changed ? "*" : "").Append(' ');
        return sb.ToString();
    }
}
