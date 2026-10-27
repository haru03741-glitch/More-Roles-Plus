// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/NebulaSettingMenu.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Roles;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 役職の一覧と細かい頁。設定画面の「役職」タブ (RolePage) と役職辞典の窓 (RoleDictionary) が 1 つずつ持つ。
// 1 枚目 = 上に陣営の札・左に役職のカード (縁の色で出方が分かる: 灰 = 出ない・白 = 確率で出る・黄 = 必ず出る)・
//          右にカードへ当てた役職の要約 (設定画面では当てていない時はまとめて変える札)。カードを押すと 2 枚目へ。
// 2 枚目 = 辞典では役職の説明の本文、そのあと人数・出現率・細かい設定の行と、一緒に出ない役職の札。
// 検索の間 = 語に当たる役職をカードと当たった行で並べる (設定画面では本編のゲーム設定・MRP 全般・陣営の設定の行も)。
// 値を変えられるのは CanEdit が真の間だけ (それ以外は値の文だけ出す)。
internal sealed class RoleBrowser
{
    private static readonly Color Gray = new(0.42f, 0.42f, 0.45f, 1f);
    private static readonly Color Gold = new(1f, 0.82f, 0.2f, 1f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color Highlight = new(1f, 0.88f, 0.4f, 1f);
    private static readonly Color GameColor = new(0.5f, 0.85f, 0.75f);
    private static readonly Color ChipOff = new(0.32f, 0.33f, 0.36f, 1f);
    private static readonly bool Touch = OperatingSystem.IsAndroid();
    private const float ChipH = 0.36f;

    private readonly float W, H, ListW, SideW, CardW;
    private readonly bool _settings;   // 設定画面のタブ (まとめて変える札・本編の設定の検索)
    private readonly Func<bool> _canEdit;
    private readonly string _tag;

    // 値を変えた後 (カードの数・左の説明の欄などを持ち主が書き直す)
    public Action Changed;
    // 2 枚目の見出しに足すボタン (設定画面から辞典へ・辞典から設定画面へなど)
    public Func<Registry.Section, Widget> HeadExtra;

    // 開き直しても残す
    private int _team;
    private bool _enabledOnly;
    private int _bulkChance = 50, _bulkCount = 1;

    private UiScreen _screen;
    private Registry.Section _open;   // 2 枚目に開いている物 (null = 1 枚目)
    private Registry.Section _hover;  // 右に要約を出している物
    private Registry.Section _picked; // スマホで 1 回押した物 (もう 1 回で開く)
    private Holder _side;
    private ScrollView _list;
    private readonly List<(Registry.Section sec, Button card, Label values, Label name)> _cards = new();
    private readonly OptEditor _editor;
    private Button _resetBtn, _undoBtn;  // 2 枚目の「既定に戻す」「元に戻す」(行を押した時に色だけ書き換える)
    private string[] _tokens = Array.Empty<string>();
    private readonly int[] _hits = new int[3];  // 検索の件数: 0 = ゲーム設定・1 = MRP 全般・2 = 役職

    public RoleBrowser(float width, float height, bool settings, Func<bool> canEdit, string tag)
    {
        W = width;
        H = height;
        ListW = (float)Math.Round(width * 0.595f, 2);
        SideW = width - ListW - 0.15f;
        CardW = ListW - 0.2f;
        _settings = settings;
        _canEdit = canEdit;
        _tag = tag;
        _editor = new OptEditor(Rebuild, _ => AfterEdit(), canEdit);
    }

    public bool Searching => _tokens.Length > 0;
    public bool Editable => _canEdit();
    public Registry.Section Open => _open;
    public IReadOnlyList<int> Hits => _hits;

    // 画面に付ける (前の画面の物は捨てる)。新しく開いた時は 1 枚目から
    public void Attach(UiScreen screen, Registry.Section open = null)
    {
        _screen = screen;
        _open = open;
        _hover = null;
        _picked = null;
        Prepare();
        Rebuild();
    }

    public void Detach()
    {
        _screen?.Clear();
        _screen = null;
        _side = null;
        _list = null;
        _cards.Clear();
        _editor.Clear();
        _tokens = Array.Empty<string>();
    }

    // 検索の語が変わった (空 = 検索をやめた)
    public void SetQuery(string q)
    {
        var tokens = RoleSearch.Tokens(q);
        bool was = _tokens.Length > 0;
        _tokens = tokens;
        if (tokens.Length > 0 != was) { _open = null; _hover = null; }
        Rebuild();
    }

    public void OpenSection(Registry.Section sec)
    {
        _list?.Remember();
        _open = sec;
        _picked = null;
        Rebuild();
    }

    private static void Prepare()
    {
        var chars = new System.Text.StringBuilder("0123456789%/×:+-<>");
        foreach (var t in new[] { Tab.Crew, Tab.Impostor, Tab.Neutral, Tab.Addon })
            foreach (var sec in Registry.SectionsOf(t)) chars.Append(sec.Title);
        MenuFont.Prepare(chars.ToString());
    }

    // ---- 組み立て ----

    public void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        _cards.Clear();
        _editor.Clear();
        _side = null;
        _list = null;
        _resetBtn = _undoBtn = null;
        _screen.Set(_open != null ? DetailPage(_open) : _tokens.Length > 0 ? SearchPage() : ListPage());
    }

    private Widget ListPage()
    {
        var chips = new Stack(true) { Gap = 0.07f, Align = Align.Start };
        for (int k = -1; k <= RoleMenu.AddonKind; k++)
        {
            int kind = k;
            Color c = k < 0 ? Color.white : RoleMenu.KindColor(k);
            chips.Add(Chip(k < 0 ? new Text("RoleMenu.All") : RoleMenu.KindName(k), c, _team == k, () => { _team = kind; _hover = null; Rebuild(); }));
        }
        chips.Add(Chip(new Text("RoleMenu.EnabledOnly"), new Color(0.6f, 1f, 0.6f), _enabledOnly, () => { _enabledOnly = !_enabledOnly; Rebuild(); }));

        var cards = new Stack(false) { Gap = 0.07f, Width = CardW };
        int n = 0;
        foreach (var sec in Visible())
        {
            cards.Add(Card(sec, CardW));
            n++;
        }
        if (n == 0) cards.Add(new Label(new Text("RoleMenu.NoMatchingRoles"), 1.6f) { Color = SubText, Width = CardW, TextAlign = TextAlignmentOptions.Center });

        _list = new ScrollView(new Vector2(ListW, H - ChipH - 0.12f), cards) { Tag = _tag + ":" + _team + (_enabledOnly ? "+" : "") };
        _side = new Holder(new Vector2(SideW, H - ChipH - 0.12f), SideContent());
        return new Stack(false, chips, new Stack(true, _list, _side) { Gap = 0.15f }) { Gap = 0.12f, Width = W };
    }

    private static Button Chip(string text, Color c, bool on, Action click)
    {
        var label = new Label(text, 1.45f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = on ? new Color(0.08f, 0.08f, 0.1f) : c };
        var b = new Button(label, click) { Size = new Vector2(0f, ChipH), MinSize = new Vector2(0.75f, ChipH), Padding = new Vector2(0.14f, 0.04f), Color = c };
        if (on) b.Fill = c;
        return b;
    }

    // 今の陣営の札・「出る役職だけ」で見せる物 (役職とアドオン・陣営に置いた設定のまとまり)
    private IEnumerable<Registry.Section> Visible()
    {
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
        {
            if (_team >= 0 && _team != k) continue;
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(k)))
            {
                if (sec.Role == null)
                {
                    if (_enabledOnly || !sec.Opts.Exists(o => !o.OffTab)) continue;
                }
                else if (_enabledOnly && !Enabled(sec.Role)) continue;
                yield return sec;
            }
        }
    }

    private static bool Enabled(Assignable a) => a.Chance.Value > 0 && a.Count.Value > 0;

    private static Color CardColor(Registry.Section sec)
    {
        var a = sec.Role;
        if (a == null) return Color.white;
        if (!Enabled(a)) return Gray;
        return a.Chance.Value >= 100 ? Gold : Color.white;
    }

    internal static Color TitleColor(Registry.Section sec)
    {
        if (sec.Role != null) return RoleDisplay.ParseColor(sec.Role.Color);
        return sec.Color != null ? RoleDisplay.ParseColor(sec.Color) : Color.white;
    }

    private static string CardValues(Assignable a)
    {
        if (a == null) return "";
        string count = $"×{a.Count.Display()}";
        if (a.Count.Changed) count = $"<color=#{ChangedHex}>{count}</color>";
        return $"{count}\n{a.Chance.Display()}";
    }

    private static readonly string ChangedHex = ColorUtility.ToHtmlStringRGB(OptEditor.ChangedColor);

    // 細かい設定を既定から変えてあるか。出現率は既定が 0 (出ない) なので縁の色で分かる → 数えない
    private static bool DetailChanged(Registry.Section sec)
    {
        foreach (var opt in SectionOpts(sec))
            if (opt != sec.Role?.Chance && opt.Changed) return true;
        return false;
    }

    // カードの名前 (細かい設定を変えてあれば印を付ける)
    private static string CardName(Registry.Section sec) =>
        DetailChanged(sec) ? $"{sec.Title} <color=#{ChangedHex}>*</color>" : sec.Title.ToString();

    private static string ShortText(Registry.Section sec)
    {
        var a = sec.Role;
        if (a == null) return "";
        return RoleDoc.Has(a) ? new Text(a.Id + ".Summary").ToString() : a.Description.ToString();
    }

    private Widget Card(Registry.Section sec, float width)
    {
        var a = sec.Role;
        bool on = a == null || Enabled(a);
        var title = TitleColor(sec);
        var name = new Label(CardName(sec), 1.9f) { MenuFont = true, Color = on ? title : Color.Lerp(title, Gray, 0.55f) };
        var desc = new Label(ShortText(sec), 1.15f) { Width = width - 1.0f, Color = on ? SubText : Gray };
        var values = new Label(CardValues(a), 1.35f) { Width = 0.6f, TextAlign = TextAlignmentOptions.Right, Bold = true, Color = on ? Color.white : Gray };
        var text = new Stack(false, name) { Gap = 0.01f, Width = width - 0.95f };
        if (desc.Content.Length > 0) text.Add(desc);
        name.Align = Align.Start;
        desc.Align = Align.Start;
        var body = new Stack(true, text, values) { Gap = 0.1f };
        var card = new Button(body, () => OnCard(sec))
        {
            Size = new Vector2(width, 0f),
            MinSize = new Vector2(width, 0.55f),
            Padding = new Vector2(0.14f, 0.07f),
            Color = CardColor(sec),
            OnHover = () => { if (!Touch) ShowSide(sec); },
            OnUnhover = () => { if (!Touch && _hover == sec) ShowSide(null); },
        };
        _cards.Add((sec, card, values, name));
        return card;
    }

    private void OnCard(Registry.Section sec)
    {
        // スマホは当てられないので、1 回目は右に要約・2 回目で開く (検索の結果は右の欄が無いのですぐ開く)
        if (Touch && _picked != sec && _side != null)
        {
            _picked = sec;
            ShowSide(sec);
            return;
        }
        OpenSection(sec);
    }

    // ---- 右の欄 ----

    private void ShowSide(Registry.Section sec)
    {
        _hover = sec;
        if (_side?.Screen == null || !_side.Screen.Alive) return;
        _side.Screen.Set(SideContent());
    }

    private Widget SideContent()
    {
        if (_hover != null) return Summary(_hover);
        if (_settings && Editable) return Bulk();
        return new Stack(false,
            new Label(new Text(Touch ? "RoleDictionary.HintTouch" : "RoleDictionary.Hint"), 1.3f) { Color = SubText, Width = SideW, Align = Align.Start },
            new Label(new Text("RolePage.Legend"), 1.15f) { Color = SubText, Width = SideW, Align = Align.Start }) { Gap = 0.12f, Width = SideW };
    }

    private Widget Summary(Registry.Section sec)
    {
        var col = new Stack(false) { Gap = 0.03f, Width = SideW };
        col.Add(new Label(sec.Title, 2.0f) { MenuFont = true, Color = TitleColor(sec), Width = SideW, Align = Align.Start });
        string s = ShortText(sec);
        if (s.Length > 0) col.Add(new Label(s, 1.2f) { Color = SubText, Width = SideW, Align = Align.Start });
        col.Add(new Spacer(0.1f, 0.06f));
        foreach (var opt in SectionOpts(sec))
        {
            if (!opt.Shown) continue;
            col.Add(new Label($"{opt.Label}: {ValueText(opt)}", 1.25f) { Width = SideW, Align = Align.Start });
        }
        if (DetailChanged(sec))
            col.Add(new Label(new Text("RolePage.ChangedNote"), 1.15f) { Color = OptEditor.ChangedColor, Width = SideW, Align = Align.Start });
        if (sec.Role is RoleBase rb && rb.Excluded != null)
            col.Add(new Label($"{new Text("Registry.NotWith")}: {ExclSummary(rb)}", 1.25f) { Width = SideW, Align = Align.Start });
        if (Touch)
        {
            col.Add(new Spacer(0.1f, 0.08f));
            col.Add(new Button(new Text("RolePage.OpenSettings"), () => OpenSection(sec), 1.4f) { Size = new Vector2(SideW, 0.4f) });
            col.Add(new Button(new Text(_settings ? "RolePage.BackToBulk" : "RoleDictionary.BackToHint"), () => { _picked = null; ShowSide(null); }, 1.4f) { Size = new Vector2(SideW, 0.4f) });
        }
        return col;
    }

    private static string ValueText(Opt opt)
    {
        opt.Sync();
        if (opt.IsToggle)
            return $"<color=#{ColorUtility.ToHtmlStringRGB(opt.Index != 0 ? OptEditor.OnColor : OptEditor.OffColor)}>{opt.Display()}</color>";
        return opt.Changed ? $"<color=#{ChangedHex}>{opt.Display()}</color>" : opt.Display();
    }

    // 人数・出現率を先に、そのあと細かい設定 (タブに出さない項目は除く)
    private static IEnumerable<Opt> SectionOpts(Registry.Section sec)
    {
        var a = sec.Role;
        if (a != null)
        {
            if (a.Count != null) yield return a.Count;
            if (a.Chance != null) yield return a.Chance;
        }
        foreach (var opt in sec.Opts)
        {
            if (opt.OffTab || (a != null && (opt == a.Count || opt == a.Chance))) continue;
            yield return opt;
        }
    }

    private Widget Bulk()
    {
        string team = _team < 0 ? "" : RoleMenu.KindName(_team);
        var col = new Stack(false) { Gap = 0.08f, Width = SideW };
        col.Add(new Label(_team < 0 ? new Text("RolePage.BulkAll") : new Text("RolePage.BulkTeam").With("team", team), 1.6f) { Bold = true, Width = SideW, Align = Align.Start });
        col.Add(new Label(new Text("RolePage.Enabled").With("n", EnabledIn()).With("count", CountIn()), 1.3f) { Color = SubText, Width = SideW, Align = Align.Start });
        col.Add(new Label(new Text("RolePage.Legend"), 1.15f) { Color = SubText, Width = SideW, Align = Align.Start });
        col.Add(new Spacer(0.1f, 0.05f));
        float half = (SideW - 0.08f) / 2f;
        col.Add(new Stack(true,
            new Button("0%", () => Apply(a => a.Chance.SetValue(0)), 1.45f) { Size = new Vector2(half, 0.4f) },
            new Button("100%", () => Apply(a => a.Chance.SetValue(100)), 1.45f) { Size = new Vector2(half, 0.4f) }) { Gap = 0.08f });
        col.Add(BulkStepper(new Text("RolePage.ChanceTo").With("pct", _bulkChance + "%"),
            () => { _bulkChance = Math.Max(0, _bulkChance - 10); ShowSide(null); },
            () => { _bulkChance = Math.Min(100, _bulkChance + 10); ShowSide(null); },
            () => Apply(a => a.Chance.SetValue(_bulkChance))));
        col.Add(BulkStepper(new Text("RolePage.CountTo").With("n", _bulkCount),
            () => { _bulkCount = Math.Max(1, _bulkCount - 1); ShowSide(null); },
            () => { _bulkCount = Math.Min(MaxBulkCount(), _bulkCount + 1); ShowSide(null); },
            () => Apply(a => a.Count.SetValue(_bulkCount))));
        col.Add(new Button(new Text("RoleMenu.Undo"), Undo, 1.45f) { Size = new Vector2(SideW, 0.4f), Color = OptHistory.Any ? Color.white : OptEditor.Dim });
        return col;
    }

    private Widget BulkStepper(string label, Action minus, Action plus, Action apply)
    {
        const float step = 0.36f;
        return new Stack(true,
            new Button("-", minus, 1.5f) { Size = new Vector2(step, 0.4f) },
            new Button(label, apply, 1.3f) { Size = new Vector2(SideW - step * 2f - 0.16f, 0.4f) },
            new Button("+", plus, 1.5f) { Size = new Vector2(step, 0.4f) }) { Gap = 0.08f };
    }

    private IEnumerable<Assignable> TeamRoles()
    {
        foreach (var sec in Visible()) if (sec.Role != null) yield return sec.Role;
    }

    private int EnabledIn()
    {
        int n = 0;
        foreach (var a in TeamRoles()) if (Enabled(a)) n++;
        return n;
    }

    private int CountIn()
    {
        int n = 0;
        foreach (var _ in TeamRoles()) n++;
        return n;
    }

    private int MaxBulkCount()
    {
        int max = 1;
        foreach (var a in TeamRoles()) max = Math.Max(max, a.Count.Max);
        return max;
    }

    // まとめて変える: 今の陣営の札で見えている役職を全部 (変える前を 1 回分覚える)
    internal void Apply(Func<Assignable, bool> change)
    {
        if (!Editable) return;
        int n = 0;
        OptHistory.BeginGroup();
        try
        {
            foreach (var a in TeamRoles())
            {
                int chance = a.Chance.Index, count = a.Count.Index;
                if (!change(a)) continue;
                if (a.Chance.Index != chance) OptHistory.Push(a.Chance, chance);
                if (a.Count.Index != count) OptHistory.Push(a.Count, count);
                Roles.TeamSizes.OnEdited(a.Count);
                n++;
            }
        }
        finally { OptHistory.EndGroup(); }
        if (n == 0) return;
        AfterEdit();
        ShowSide(null);
    }

    // 最後の 1 回の操作を戻す (行の 1 押し・まとめて変える・既定に戻す。設定画面と辞典で共通)
    internal void Undo()
    {
        if (!OptHistory.Any || !Editable) return;
        OptHistory.Undo();
        AfterEdit();
        if (_open != null || _tokens.Length > 0) Rebuild();
        else ShowSide(null);
    }

    // 開いている役職の項目を全部既定へ (1 回の取り消しで戻る)
    private void ResetSection(Registry.Section sec)
    {
        if (!Editable) return;
        bool any = false;
        OptHistory.BeginGroup();
        try
        {
            foreach (var opt in SectionOpts(sec))
            {
                int before = opt.Index;
                if (!opt.ResetToDefault()) continue;
                OptHistory.Push(opt, before);
                OptEditor.AfterStep(opt);
                any = true;
            }
        }
        finally { OptHistory.EndGroup(); }
        if (!any) return;
        AfterEdit();
        Rebuild();
    }

    private static Color UndoColor => OptHistory.Any ? Color.white : OptEditor.Dim;

    private static Color ResetColor(Registry.Section sec)
    {
        foreach (var opt in SectionOpts(sec))
            if (opt.Changed) return OptEditor.ChangedColor;
        return OptEditor.Dim;
    }

    private void AfterEdit()
    {
        if (_open != null)
        {
            _resetBtn?.SetColor(ResetColor(_open));
            _undoBtn?.SetColor(UndoColor);
        }
        RefreshCards();
        Changed?.Invoke();
    }

    // カードの値と縁の色だけを今の値に (作り直さない)
    // 窓の外 (役職辞典など) で値を変えた後: 細かい頁と検索の結果は行ごと作り直し、一覧ならカードだけ書き換える
    public void RefreshFromOutside()
    {
        if (_open != null || _tokens.Length > 0) Rebuild();
        else RefreshCards();
    }

    public void RefreshCards()
    {
        foreach (var (sec, card, values, name) in _cards)
        {
            var a = sec.Role;
            if (a == null) continue;
            bool on = Enabled(a);
            card.SetColor(CardColor(sec));
            // 字はゲーム側へ写すたびに費用が掛かるので、変わったカードだけ書く (Content = 今出している字)
            if (values.Built)
            {
                string v = CardValues(a);
                if (v != values.Content) { values.Content = v; values.Built.text = v; }
                values.Built.color = on ? Color.white : Gray;
            }
            if (name.Built)
            {
                string s = CardName(sec);
                if (s != name.Content) { name.Content = s; name.Built.text = s; }
                name.Built.color = on ? TitleColor(sec) : Color.Lerp(TitleColor(sec), Gray, 0.55f);
            }
        }
    }

    // ---- 2 枚目 ----

    private Widget DetailPage(Registry.Section sec)
    {
        bool edit = Editable;
        var back = new Button("<<", () => { _open = null; _hover = null; Rebuild(); }, 1.9f) { Size = new Vector2(0.7f, 0.5f) };
        var title = new Label(sec.Title, 2.6f) { MenuFont = true, Color = TitleColor(sec), Outline = 0.2f };
        var head = new Stack(true, back, title) { Gap = 0.25f, Align = Align.Start };
        var extra = HeadExtra?.Invoke(sec);
        if (extra != null) head.Add(extra);

        var rows = new Stack(false) { Gap = 0.1f, Width = W - 0.3f };
        if (!_settings && sec.Role != null)
        {
            // 辞典は役職の説明の本文から (試合の情報・/r と同じ文)
            rows.Add(new Label(sec.Role.Help, 1.3f) { Width = W - 0.4f, Align = Align.Start });
            rows.Add(new Spacer(0.1f, 0.1f));
            rows.Add(new Label(new Text(edit ? "RoleDictionary.SettingsEditable" : "RoleDictionary.Settings"), 1.5f) { Bold = true, Color = GameColor, Width = W - 0.4f, Align = Align.Start });
        }
        else
        {
            string s = ShortText(sec);
            if (s.Length > 0) rows.Add(new Label(s, 1.3f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
        }
        var opts = new List<Opt>(SectionOpts(sec));
        if (edit)
        {
            _resetBtn = new Button(new Text("RolePage.ResetRole"), () => ResetSection(sec), 1.35f) { Size = new Vector2(0f, 0.4f), MinSize = new Vector2(1.5f, 0.4f), Color = ResetColor(sec) };
            _undoBtn = new Button(new Text("RoleMenu.Undo"), Undo, 1.35f) { Size = new Vector2(0f, 0.4f), MinSize = new Vector2(1.2f, 0.4f), Color = UndoColor };
            rows.Add(new Stack(true, _resetBtn, _undoBtn) { Gap = 0.1f, Align = Align.Start });
        }
        _editor.Watch(opts);
        foreach (var opt in opts)
        {
            if (!opt.Shown) continue;
            float indent = Depth(opt) * 0.3f;
            var r = _editor.Row(opt, edit, indent);
            r.Align = Align.Start;
            rows.Add(r);
        }
        if (sec.Role is RoleBase rb && rb.Excluded != null)
        {
            if (edit) rows.Add(ExclSection(rb));
            else rows.Add(new Label($"{new Text("Registry.NotWith")}: {ExclSummary(rb)}", OptEditor.FontSize) { Bold = true, Width = W - 0.4f, Align = Align.Start });
        }
        _editor.Refresh();

        _list = new ScrollView(new Vector2(W, H - 0.62f), rows) { Tag = _tag + ":detail:" + (sec.Role?.Id ?? sec.Title.ToString()) };
        return new Stack(false, head, _list) { Gap = 0.1f, Width = W };
    }

    private static int Depth(Opt opt)
    {
        int d = 0;
        for (var p = opt.Parent; p != null && d < 4; p = p.Parent) d++;
        return d;
    }

    // 一緒に出ない役職: 札を押すと両方の役職の選びを揃えて切り替える (片方だけ残ると、外したのに選んだままに見えるため)
    private Widget ExclSection(RoleBase me)
    {
        const int columns = 3;
        float w = (W - 0.3f - 0.08f * (columns - 1)) / columns;
        var col = new Stack(false) { Gap = 0.06f, Width = W - 0.3f, Align = Align.Start };
        col.Add(new Spacer(0.1f, 0.1f));
        col.Add(new Label(new Text("Registry.NotWith"), OptEditor.FontSize) { Bold = true, Align = Align.Start });
        col.Add(new Label(new Text("RoleMenu.NotWithHint"), 1.15f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
        Stack row = null;
        int i = 0;
        foreach (var other in ExclCandidates(me))
        {
            if (i % columns == 0) { row = new Stack(true) { Gap = 0.08f, Align = Align.Start }; col.Add(row); }
            row.Add(ExclChip(me, other, w));
            i++;
        }
        return col;
    }

    private static IEnumerable<RoleBase> ExclCandidates(RoleBase me)
    {
        foreach (var t in new[] { Tab.Crew, Tab.Impostor, Tab.Neutral })
            foreach (var sec in Registry.SectionsOf(t))
                if (sec.Role is RoleBase rb && rb != me && rb.Excluded != null) yield return rb;
    }

    private Widget ExclChip(RoleBase me, RoleBase other, float w)
    {
        bool locked = AssignPlan.FixedPair(me, other);
        bool on = locked || AssignPlan.PickedPair(me, other);
        var c = RoleDisplay.ParseColor(other.Color);
        string text = locked ? new Text("RoleMenu.FixedPair").With("name", other.Name) : other.Name.ToString();
        var label = new Label(text, 1.35f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = on ? c : Gray, Width = w - 0.2f };
        Button b = null;
        b = new Button(label, () =>
        {
            if (locked || !Editable) return;
            bool next = !AssignPlan.PickedPair(me, other);
            me.Excluded.Set(other.Order, next);
            other.Excluded.Set(me.Order, next);
            b.SetColor(next ? c : ChipOff);
            if (label.Built) label.Built.color = next ? c : Gray;
            Changed?.Invoke();
        })
        { Size = new Vector2(w, 0.4f), Color = on ? c : ChipOff };
        return b;
    }

    private static string ExclSummary(RoleBase me)
    {
        string first = null;
        int n = 0;
        foreach (var other in ExclCandidates(me))
        {
            if (!AssignPlan.FixedPair(me, other) && !AssignPlan.PickedPair(me, other)) continue;
            first ??= other.Name.ToString();
            n++;
        }
        if (n == 0) return Tr.Get("RoleSetOpt.None");
        return n > 1 ? new Text("RoleSetOpt.More").With("name", first).With("n", n - 1) : first;
    }

    // ---- 検索 ----

    private static Text Join(Text a, Text b) => new Text("RoleMenu.Breadcrumb").With("a", a).With("b", b);

    // 役職でない設定のまとまり (見出し・色・件数の数え先・項目)
    private static IEnumerable<(Text title, Color color, int area, IEnumerable<Opt> opts)> PlainGroups()
    {
        VanillaGame.Rebuild();
        foreach (var sec in VanillaGame.All)
        {
            // インポスター数 (= 最多人数) の前に最少人数
            var opts = new List<Opt>();
            foreach (var opt in sec.Opts)
            {
                if (opt.IsImpostorCount) opts.Add(ImpostorRange.Min);
                opts.Add(opt);
            }
            yield return (Join(VanillaGame.TabName, sec.Title), GameColor, 0, opts);
        }
        yield return (Join(VanillaGame.TabName, MapVariants.Title), GameColor, 0, new Opt[] { MapSettings.Mirror });
        foreach (var g in GameTabRows.All()) yield return (Join(VanillaGame.TabName, g.Title), GameColor, 0, g.Opts);
        foreach (var sec in Registry.SectionsOf(Tab.General))
            yield return (Join(SettingsMenu.TabName(false), sec.Title), SettingsMenu.TabColor(false), 1, OnTab(sec.Opts));
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(k)))
                if (sec.Role == null) yield return (Join(RoleMenu.KindName(k), sec.Title), RoleMenu.KindColor(k), 2, OnTab(sec.Opts));
    }

    private static IEnumerable<Opt> OnTab(IEnumerable<Opt> opts)
    {
        foreach (var o in opts) if (!o.OffTab) yield return o;
    }

    // 語が全部入っているか
    private bool Matches(string hay)
    {
        foreach (var t in _tokens)
            if (!hay.Contains(t, StringComparison.Ordinal)) return false;
        return true;
    }

    private Widget SearchPage()
    {
        Array.Clear(_hits, 0, _hits.Length);
        bool edit = Editable;
        var col = new Stack(false) { Gap = 0.08f, Width = W - 0.3f };
        var all = new List<Opt>();
        if (_settings)
        {
            foreach (var (title, color, area, opts) in PlainGroups())
            {
                string context = RoleSearch.Haystack(title);
                Stack rows = null;
                foreach (var opt in opts)
                {
                    string hay = RoleSearch.Haystack(opt.Label);
                    if (!opt.Shown || !Matches(hay + "\n" + context)) continue;
                    if (rows == null)
                    {
                        rows = new Stack(false) { Gap = 0.06f, Width = W - 0.3f, Align = Align.Start };
                        rows.Add(new Label(title, 1.7f) { Color = color, Bold = true, MenuFont = true, Width = W - 0.4f, Align = Align.Start });
                    }
                    all.Add(opt);
                    var r = _editor.Row(opt, edit, 0.25f, RoleSearch.AnyIn(_tokens, hay) ? Highlight : Color.white);
                    r.Align = Align.Start;
                    rows.Add(r);
                    _hits[area]++;
                }
                if (rows != null) col.Add(rows);
            }
        }
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
        {
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(k)))
            {
                var a = sec.Role;
                if (a == null) continue;
                // 名前・読み・1 行の説明・設定の名前のどれかに当たれば出す
                var hay = new System.Text.StringBuilder(RoleSearch.Haystack(a.Name)).Append('\n').Append(RoleSearch.Normalize(a.Reading))
                    .Append('\n').Append(RoleSearch.Normalize(ShortText(sec)));
                foreach (var opt in SectionOpts(sec)) hay.Append('\n').Append(RoleSearch.Haystack(opt.Label));
                if (!Matches(hay.ToString())) continue;
                _hits[2]++;
                var card = Card(sec, W - 0.3f);
                card.Align = Align.Start;
                col.Add(card);
                foreach (var opt in SectionOpts(sec))
                {
                    if (!opt.Shown || !RoleSearch.AnyIn(_tokens, RoleSearch.Haystack(opt.Label))) continue;
                    all.Add(opt);
                    var r = _editor.Row(opt, edit, 0.4f, Highlight);
                    r.Align = Align.Start;
                    col.Add(r);
                }
            }
        }
        if (_hits[0] + _hits[1] + _hits[2] == 0)
            col.Add(new Label(new Text(_settings ? "RoleMenu.NothingMatchesShort" : "RoleMenu.NoMatchingRoles"), 1.6f) { Color = SubText, Width = W - 0.4f, TextAlign = TextAlignmentOptions.Center });
        _editor.Watch(all);
        _editor.Refresh();
        _list = new ScrollView(new Vector2(W, H), col);
        return _list;
    }

    // ---- 毎フレーム (開いている間だけ) ----

    // カードに当てている間の右クリック = 出現率を 0% と 100% で切り替える (設定画面で値を変えられる時だけ)
    public void Tick()
    {
        if (Touch || !_settings || _tokens.Length > 0 || _open != null || _hover?.Role == null) return;
        if (!Input.GetMouseButtonDown(1) || !Editable) return;
        var c = _hover.Role.Chance;
        OptHistory.Push(c, c.Index);
        c.SetIndex(c.Index > 0 ? 0 : c.Count - 1);
        Roles.TeamSizes.OnEdited(c);
        AfterEdit();
        ShowSide(_hover);
    }

    // ---- 試験用 ----

    internal string Command(string[] a)
    {
        switch (a.Length > 0 ? a[0] : "dump")
        {
            case "team":
                _team = a.Length > 1 && int.TryParse(a[1], out int t) ? t : -1;
                _hover = null;
                Rebuild();
                break;
            case "only":
                _enabledOnly = !_enabledOnly;
                Rebuild();
                break;
            case "hover":
                ShowSide(a.Length > 1 ? Find(a[1]) : null);
                break;
            case "open":
                _open = a.Length > 1 ? Find(a[1]) : null;
                Rebuild();
                break;
            case "scroll":
                if (_list?.Scroller != null && a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sp))
                    _list.Scroller.ScrollPercentY(sp);
                break;
            case "bulk":
                // bulk <0 = 0% | 1 = 100% | 2 = 元に戻す>
                if (a.Length > 1 && a[1] == "0") Apply(x => x.Chance.SetValue(0));
                else if (a.Length > 1 && a[1] == "1") Apply(x => x.Chance.SetValue(100));
                else Undo();
                break;
            case "reset":
                if (_open != null) ResetSection(_open);
                break;
            case "rebuild":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Rebuild();
                return $"ms={sw.Elapsed.TotalMilliseconds:0.0} slots={UiActions.Census()}";
            }
        }
        var sb = new System.Text.StringBuilder($"team={_team} only={_enabledOnly} edit={Editable} open={_open?.Role?.Id ?? _open?.Title.ToString() ?? "-"} hover={_hover?.Role?.Id ?? "-"} undo={(OptHistory.Any ? 1 : 0)} hits={_hits[0]}/{_hits[1]}/{_hits[2]} slots={UiActions.Census()} cards:");
        foreach (var (sec, _, values, _) in _cards)
            sb.Append($" {sec.Role?.Id ?? sec.Title.ToString()}({(values.Built ? values.Built.text.Replace('\n', ',') : "")})");
        return sb.ToString();
    }

    internal static Registry.Section Find(string id)
    {
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(k)))
                if (sec.Role?.Id == id || sec.Title.ToString() == id) return sec;
        return null;
    }
}
