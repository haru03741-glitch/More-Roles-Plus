using System;
using System.Collections.Generic;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面の「ゲーム」「全般」の頁。真ん中 = (上の段) + 節の見出しと行のスクロール欄・右 = 頁の見出しと節の目次・戻すボタン。
// 行は役職の頁の右の欄と同じ OptEditor の行 (名前の右の点で初期値から変えた項目が分かる)。
internal sealed class SectionPage
{
    internal sealed class Group
    {
        public string Title;
        public Color Color = Color.white;
        public readonly List<Opt> Opts = new();
    }

    // 頁の置き場と大きさは役職の頁と同じ (左の列からの並びを揃える)。右の欄は目次だけなので行の欄を広く取る
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    private static readonly float ListW = (float)Math.Round(W * 0.65f, 2), SideW = W - ListW - 0.15f;
    internal static float RowW => ListW - 0.24f;
    private const float HeadH = 0.4f, BarH = 0.4f, RowPad = 0.08f;

    private readonly string _tag;
    private readonly Func<string> _title;
    private readonly Color _color;
    private readonly Func<Sprite> _backdrop;    // 右の欄の後ろに敷く絵 (null = 無し)
    private Sprite _lastBackdrop;               // 前に敷いた絵 (替わった時だけ滑り込ませる)
    private readonly Func<List<Group>> _groups;
    private readonly Func<bool, Widget> _top;   // 節の前に置く物 (編集できるか)。null = 無し
    private readonly Action _onBuild;           // 組む前に呼ぶ (項目の作り直しなど)
    private readonly Func<int> _topKey;         // 上の段の見た目が変わる値 (外で変わったら組み直す)
    private int _builtTopKey;
    public Func<string> HeroSub;                // 頁の見出しの下の字 (null = 無し)
    private readonly OptEditor _editor;

    private sealed class Entry
    {
        public Group Group;
        public Box Head;
        public Label TocLabel;
        public bool Changed;
    }

    private readonly List<Entry> _entries = new();
    private readonly List<Opt> _shownOpts = new(); // 今並べている行 (外で値を変えた後に、出る行が変わったかを比べる)
    private List<Group> _all = new();
    private UiScreen _screen;
    private ScrollView _list;
    private Button _resetBtn, _undoBtn;
    private bool _built;

    public SectionPage(string tag, Func<string> title, Color color, Func<Sprite> backdrop, Func<List<Group>> groups, Func<bool, Widget> top = null, Action onBuild = null, Func<int> topKey = null)
    {
        _tag = tag;
        _title = title;
        _color = color;
        _backdrop = backdrop;
        _groups = groups;
        _top = top;
        _onBuild = onBuild;
        _topKey = topKey;
        _editor = new OptEditor(OnShownChanged, _ => AfterEdit(), CanEdit);
    }

    private static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    public bool Built => _built && _screen != null && _screen.Alive;

    public void Build(GameOptionsMenu m)
    {
        Forget();
        if (!m || !UiAssets.Box) return;
        // タブの元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        var root = Widget.Node("Mrp_" + _tag, m.transform, m.gameObject.layer);
        root.transform.localPosition = Center;
        root.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        _screen = new UiScreen(root.transform) { MaxWidth = W, BaseOrder = 1 };
        _built = true;
        Rebuild();
    }

    public void Forget()
    {
        _list?.Remember();
        _screen?.Clear();
        if (_screen != null && _screen.Root) UnityEngine.Object.Destroy(_screen.Root.gameObject);
        _screen = null;
        _list = null;
        _resetBtn = _undoBtn = null;
        _entries.Clear();
        _shownOpts.Clear();
        _all = new List<Group>();
        _editor.Clear();
        _built = false;
    }

    public void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        _entries.Clear();
        _shownOpts.Clear();
        _editor.Clear();
        _onBuild?.Invoke();
        _all = _groups();
        _builtTopKey = _topKey?.Invoke() ?? 0;
        bool edit = CanEdit();

        float rowW = RowW;
        var rows = new Stack(false) { Gap = 0.06f, Width = rowW };
        var watch = new List<Opt>();
        foreach (var g in _all) watch.AddRange(g.Opts);
        _editor.Watch(watch);
        var top = _top?.Invoke(edit);
        if (top != null)
        {
            top.Align = Align.Start;
            rows.Add(top);
        }
        foreach (var g in _all)
        {
            var shown = new List<Opt>();
            foreach (var opt in g.Opts)
                if (opt.Shown) shown.Add(opt);
            if (shown.Count == 0) continue;
            var e = new Entry { Group = g, Changed = AnyChanged(g) };
            // 見出し: 節の色の字・暗い地の札 (前に少し間を空ける)
            if (_entries.Count > 0 || top != null) rows.Add(new Spacer(rowW, 0.06f));
            e.Head = new Box(new Label(g.Title, 1.5f) { Bold = true, Color = g.Color, Width = rowW * 0.66f - 0.24f, MinFontSize = 1.1f })
            {
                Size = new Vector2(rowW * 0.66f, HeadH),
                Padding = new Vector2(0.12f, 0.04f),
                Color = Color.Lerp(g.Color, Color.black, 0.55f),
                Fill = new Color(0.15f, 0.16f, 0.18f, 1f),
                Align = Align.Start,
            };
            rows.Add(e.Head);
            // 行ごとに灰の地 (行の境目を見やすく)
            foreach (var opt in shown)
            {
                var r = _editor.NarrowRow(opt, edit, rowW - RowPad * 2f, RoleBrowser.Depth(opt) * 0.2f, opt.Fresh ? RoleBrowser.FreshColor : (Color?)null);
                r.Align = Align.Start;
                rows.Add(new Plate(r) { Width = rowW, Padding = new Vector2(RowPad, 0.025f), Align = Align.Start });
            }
            _entries.Add(e);
            _shownOpts.AddRange(shown);
        }
        _editor.Refresh();
        _list = new ScrollView(new Vector2(ListW, H), rows) { Tag = _tag };
        var bd = new Backdrop(SideW, H);
        _screen.Set(new Stack(true, _list, new Layers(SideW, H).Add(bd, 0f, 0f).Add(Side(edit), 0f, 0f)) { Gap = 0.15f, Align = Align.Start });
        var art = _backdrop?.Invoke();
        bd.Show(art, art != _lastBackdrop);
        _lastBackdrop = art;
    }

    private Widget Side(bool edit)
    {
        var col = new Stack(false) { Gap = 0.06f, Width = SideW };
        col.Add(RoleBrowser.Hero(SideW, _color, null, _title(), HeroSub?.Invoke() ?? ""));
        // 目次: 押すとその節の見出しへ (節の中に初期値から変えた項目があれば点)
        var toc = new Stack(false) { Gap = 0.05f, Width = SideW - 0.2f };
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            int n = i;
            e.TocLabel = new Label(TocText(e), 1.3f) { Width = SideW - 0.44f, NoWrap = true, Bold = true, MinFontSize = 0.9f };
            toc.Add(new Button(e.TocLabel, () => Jump(n)) { Size = new Vector2(SideW - 0.2f, 0.34f), Padding = new Vector2(0.1f, 0.02f), Color = new Color(0.42f, 0.42f, 0.45f, 1f), Hollow = true });
        }
        float tocH = H - RoleBrowser.HeroH - 0.06f - (edit ? BarH + 0.06f : 0f);
        col.Add(new ScrollView(new Vector2(SideW, tocH), toc) { Tag = _tag + ":toc" });
        if (edit)
        {
            float bw = (SideW - 0.05f) / 2f;
            _resetBtn = new Button(new Label(new Text("RolePage.ResetRole"), 1.2f) { Width = bw - 0.12f, MinFontSize = 0.8f, TextAlign = TextAlignmentOptions.Center }, ResetAll) { Size = new Vector2(bw, BarH), Color = ResetColor() };
            _undoBtn = new Button(new Text("RoleMenu.Undo"), Undo, 1.2f) { Size = new Vector2(bw, BarH), Color = UndoColor };
            col.Add(new Stack(true, _resetBtn, _undoBtn) { Gap = 0.05f });
        }
        return col;
    }

    private static string TocText(Entry e) => e.Changed ? e.Group.Title + OptEditor.ChangedMark : e.Group.Title;

    private static bool AnyChanged(Group g)
    {
        foreach (var opt in g.Opts)
            if (opt.Changed && !opt.NoChangeMark) return true;
        return false;
    }

    private Color ResetColor()
    {
        foreach (var g in _all)
            if (AnyChanged(g)) return OptEditor.ChangedColor;
        return OptEditor.Dim;
    }

    private static Color UndoColor => OptHistory.Any ? Color.white : OptEditor.Dim;

    // 見出しを欄の上端へ
    private void Jump(int n)
    {
        if (n < 0 || n >= _entries.Count || _list == null) return;
        var head = _entries[n].Head?.BackRenderer;
        var s = _list.Scroller;
        if (!head || !s) return;
        float hy = s.Inner.InverseTransformPoint(head.transform.position).y;
        _list.ScrollTo(_list.Size.y / 2f - hy - HeadH / 2f - 0.04f);
    }

    private void ResetAll()
    {
        if (!CanEdit()) return;
        bool any = false;
        OptHistory.BeginGroup();
        try
        {
            foreach (var g in _all)
                foreach (var opt in g.Opts)
                {
                    int before = opt.Index;
                    if (!opt.ResetToDefault()) continue;
                    OptHistory.Push(opt, before);
                    OptEditor.AfterStep(opt);
                    any = true;
                }
        }
        finally { OptHistory.EndGroup(); }
        if (any) SettingsMenu.RefreshAll();
    }

    private static void Undo()
    {
        if (!OptHistory.Any || !CanEdit()) return;
        OptHistory.Undo();
        SettingsMenu.RefreshAll();
    }

    private void OnShownChanged()
    {
        Rebuild();
        SettingsMenu.RefreshCatCounts();
    }

    private void AfterEdit()
    {
        RefreshMarks();
        SettingsMenu.RefreshCatCounts();
    }

    // 値を変えた後: 目次の点と、戻すボタンの色
    public void RefreshMarks()
    {
        if (!Built) return;
        foreach (var e in _entries)
        {
            e.Changed = AnyChanged(e.Group);
            var t = e.TocLabel?.Built;
            if (!t) continue;
            string s = TocText(e);
            if (s != e.TocLabel.Content) { e.TocLabel.Content = s; t.text = s; }
        }
        _resetBtn?.SetColor(ResetColor());
        _undoBtn?.SetColor(UndoColor);
    }

    // 外 (プリセット・設定のコード・取り消し・辞典の窓) で値を変えた後。出る行が変わっていれば組み直す
    public void RefreshFromOutside()
    {
        if (!Built) return;
        if (_topKey != null && _topKey() != _builtTopKey) { Rebuild(); return; }
        int k = 0;
        bool same = true;
        foreach (var g in _all)
        {
            foreach (var opt in g.Opts)
            {
                if (!opt.Shown) continue;
                if (k >= _shownOpts.Count || _shownOpts[k] != opt) { same = false; break; }
                k++;
            }
            if (!same) break;
        }
        if (!same || k != _shownOpts.Count) { Rebuild(); return; }
        _editor.Refresh();
        RefreshMarks();
    }

    // ---- 試験用 ----

    internal string Command(string[] a)
    {
        if (!Built) return "page not built";
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
            case "step" when a.Length > 2 && int.TryParse(a[^1], out int d):
            {
                string q = string.Join(" ", a, 1, a.Length - 2);
                var opt = _shownOpts.Find(o => o.Label.ToString().Contains(q));
                if (opt == null) return "no row " + q;
                int before = opt.Index;
                OptEditor.Step(opt, d);
                if (opt.Index != before) OptHistory.Push(opt, before);
                if (_editor.ShownChangedForTest()) OnShownChanged();
                else { _editor.Refresh(); AfterEdit(); }
                return $"{opt.Label}={opt.Display()}";
            }
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _entries.Count; i++)
            sb.Append(i).Append(':').Append(_entries[i].Group.Title).Append(_entries[i].Changed ? "*" : "").Append(' ');
        return sb.ToString();
    }
}
