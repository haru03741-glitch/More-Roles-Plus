// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/NebulaSettingMenu.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Roles;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面の「役職」タブ。
// 1 枚目 = 上に陣営の札・左に役職のカード (縁の色で出方が分かる: 灰 = 出ない・白 = 確率で出る・黄 = 必ず出る)・
//          右にカードへ当てた役職の要約 (当てていない時はまとめて変える札)。カードを押すと 2 枚目へ。
// 2 枚目 = その役職の人数・出現率・細かい設定の行と、一緒に出ない役職の札。
// 検索の間 = 左の検索欄の語に当たる設定 (本編のゲーム設定・MRP 全般・陣営の設定) を見出しごとに行で、
//          当たる役職をカードと当たった行で並べる。行はその場で値を変えられる。
internal static class RolePage
{
    // タブの中の置き場 (タブの GameOptionsMenu の座標で)
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    private const float ListW = 3.75f, SideW = 2.4f, ChipH = 0.36f;
    private const float CardW = ListW - 0.2f;

    private static readonly Color Gray = new(0.42f, 0.42f, 0.45f, 1f);
    private static readonly Color Gold = new(1f, 0.82f, 0.2f, 1f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color Highlight = new(1f, 0.88f, 0.4f, 1f);
    private static readonly Color GameColor = new(0.5f, 0.85f, 0.75f);
    private static readonly bool Touch = OperatingSystem.IsAndroid();

    // 開き直しても残す
    private static int _team = 0;
    private static bool _enabledOnly;
    private static int _bulkChance = 50, _bulkCount = 1;

    private static GameObject _root;
    private static bool _built;  // 毎フレームの確かめを Unity の生存確認より先に済ませるため
    private static UiScreen _screen;
    private static Registry.Section _open;   // 2 枚目に開いている物 (null = 1 枚目)
    private static Registry.Section _hover;  // 右に要約を出している物
    private static Registry.Section _picked; // スマホで 1 回押した物 (もう 1 回で開く)
    private static Holder _side;
    private static ScrollView _list;
    private static List<(Assignable role, int chance, int count)> _undo;
    private static readonly List<(Registry.Section sec, Button card, Label values, Label name)> Cards = new();
    private static readonly OptEditor Editor = new(() => Rebuild(), _ => RefreshCards());
    private static string[] _tokens = Array.Empty<string>();
    private static readonly int[] Hits = new int[3];  // 検索の件数: 0 = ゲーム設定・1 = MRP 全般・2 = 役職

    public static void Build(GameOptionsMenu m)
    {
        Forget();
        if (!m || !UiAssets.Box) return;
        Roles.Builtin.VanillaRoles.EnsureSettings();
        // タブの元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        _root = Widget.Node("MrpRolePage", m.transform, m.gameObject.layer);
        _root.transform.localPosition = Center;
        // 頁ごと本編の設定画面の上に描く (中のスクロール欄は自分の切り抜きを持つ)
        var group = _root.AddComponent<UnityEngine.Rendering.SortingGroup>();
        group.sortingOrder = 5;
        _screen = new UiScreen(_root.transform) { MaxWidth = W, BaseOrder = 1 };
        _built = true;
        _open = null;
        _hover = null;
        _picked = null;
        _undo = null;
        Prepare();
        Rebuild();
    }

    public static void Forget()
    {
        _screen?.Clear();
        _screen = null;
        _root = null;
        _built = false;
        _side = null;
        _list = null;
        Cards.Clear();
        Editor.Clear();
        _tokens = Array.Empty<string>();
    }

    // 検索欄の語が変わった (空 = 検索をやめた)。タブがまだ作られていなければ、作る時に使う
    public static void SetQuery(string q)
    {
        var tokens = RoleSearch.Tokens(q);
        bool was = _tokens.Length > 0;
        _tokens = tokens;
        if (tokens.Length > 0 != was) { _open = null; _hover = null; }
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

    private static void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        Cards.Clear();
        Editor.Clear();
        _side = null;
        _list = null;
        _screen.Set(_open != null ? DetailPage(_open) : _tokens.Length > 0 ? SearchPage() : ListPage());
    }

    private static Widget ListPage()
    {
        var chips = new Stack(true) { Gap = 0.07f };
        for (int k = -1; k <= RoleMenu.AddonKind; k++)
        {
            int kind = k;
            Color c = k < 0 ? Color.white : RoleMenu.KindColor(k);
            bool on = _team == k;
            chips.Add(Chip(k < 0 ? new Text("RoleMenu.All") : RoleMenu.KindName(k), c, on, () => { _team = kind; _hover = null; Rebuild(); }));
        }
        chips.Add(Chip(new Text("RoleMenu.EnabledOnly"), new Color(0.6f, 1f, 0.6f), _enabledOnly, () => { _enabledOnly = !_enabledOnly; Rebuild(); }));

        var cards = new Stack(false) { Gap = 0.07f, Width = CardW };
        int n = 0;
        foreach (var sec in Visible())
        {
            cards.Add(Card(sec));
            n++;
        }
        if (n == 0) cards.Add(new Label(new Text("RoleMenu.NoMatchingRoles"), 1.6f) { Color = SubText, Width = CardW, TextAlign = TextAlignmentOptions.Center });

        _list = new ScrollView(new Vector2(ListW, H - ChipH - 0.12f), cards) { Tag = "rolepage:" + _team + (_enabledOnly ? "+" : "") };
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
    private static IEnumerable<Registry.Section> Visible()
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

    private static Color TitleColor(Registry.Section sec)
    {
        if (sec.Role != null) return RoleDisplay.ParseColor(sec.Role.Color);
        return sec.Color != null ? RoleDisplay.ParseColor(sec.Color) : Color.white;
    }

    private static string CardValues(Assignable a) =>
        a == null ? "" : $"×{a.Count.Display()}\n{a.Chance.Display()}";

    private static string ShortText(Registry.Section sec)
    {
        var a = sec.Role;
        if (a == null) return "";
        return RoleDoc.Has(a) ? new Text(a.Id + ".Summary").ToString() : a.Description.ToString();
    }

    private static Widget Card(Registry.Section sec, float width = CardW)
    {
        var a = sec.Role;
        bool on = a == null || Enabled(a);
        var title = TitleColor(sec);
        var name = new Label(sec.Title, 1.9f) { MenuFont = true, Color = on ? title : Color.Lerp(title, Gray, 0.55f) };
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
        Cards.Add((sec, card, values, name));
        return card;
    }

    private static void OnCard(Registry.Section sec)
    {
        // スマホは当てられないので、1 回目は右に要約・2 回目で開く
        if (Touch && _picked != sec)
        {
            _picked = sec;
            ShowSide(sec);
            return;
        }
        _picked = null;
        _list?.Remember();
        _open = sec;
        Rebuild();
    }

    // ---- 右の欄 ----

    private static void ShowSide(Registry.Section sec)
    {
        _hover = sec;
        if (_side?.Screen == null || !_side.Screen.Alive) return;
        _side.Screen.Set(SideContent());
    }

    private static Widget SideContent() => _hover != null ? Summary(_hover) : Bulk();

    private static Widget Summary(Registry.Section sec)
    {
        var col = new Stack(false) { Gap = 0.03f, Width = SideW };
        col.Add(new Label(sec.Title, 2.0f) { MenuFont = true, Color = TitleColor(sec), Width = SideW, Align = Align.Start });
        string s = ShortText(sec);
        if (s.Length > 0) col.Add(new Label(s, 1.2f) { Color = SubText, Width = SideW, Align = Align.Start });
        col.Add(new Spacer(0.1f, 0.06f));
        foreach (var opt in SectionOpts(sec))
        {
            if (!opt.Shown) continue;
            opt.Sync();
            string v = opt.IsToggle
                ? $"<color=#{ColorUtility.ToHtmlStringRGB(opt.Index != 0 ? OptEditor.OnColor : OptEditor.OffColor)}>{opt.Display()}</color>"
                : opt.Display();
            col.Add(new Label($"{opt.Label}: {v}", 1.25f) { Width = SideW, Align = Align.Start });
        }
        if (sec.Role is RoleBase rb && rb.Excluded != null)
            col.Add(new Label($"{new Text("Registry.NotWith")}: {ExclSummary(rb)}", 1.25f) { Width = SideW, Align = Align.Start });
        if (Touch)
        {
            col.Add(new Spacer(0.1f, 0.08f));
            col.Add(new Button(new Text("RolePage.OpenSettings"), () => OnCard(sec), 1.4f) { Size = new Vector2(SideW, 0.4f) });
            col.Add(new Button(new Text("RolePage.BackToBulk"), () => { _picked = null; ShowSide(null); }, 1.4f) { Size = new Vector2(SideW, 0.4f) });
        }
        return col;
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

    private static Widget Bulk()
    {
        string team = _team < 0 ? "" : RoleMenu.KindName(_team);
        var col = new Stack(false) { Gap = 0.08f, Width = SideW };
        col.Add(new Label(_team < 0 ? new Text("RolePage.BulkAll") : new Text("RolePage.BulkTeam").With("team", team), 1.6f) { Bold = true, Width = SideW, Align = Align.Start });
        col.Add(new Label(new Text("RolePage.Enabled").With("n", EnabledIn()).With("count", CountIn()), 1.3f) { Color = SubText, Width = SideW, Align = Align.Start });
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
        col.Add(new Button(new Text("RoleMenu.Undo"), Undo, 1.45f) { Size = new Vector2(SideW, 0.4f), Color = _undo != null ? Color.white : OptEditor.Dim });
        return col;
    }

    private static Widget BulkStepper(string label, Action minus, Action plus, Action apply)
    {
        const float step = 0.36f;
        return new Stack(true,
            new Button("-", minus, 1.5f) { Size = new Vector2(step, 0.4f) },
            new Button(label, apply, 1.3f) { Size = new Vector2(SideW - step * 2f - 0.16f, 0.4f) },
            new Button("+", plus, 1.5f) { Size = new Vector2(step, 0.4f) }) { Gap = 0.08f };
    }

    private static IEnumerable<Assignable> TeamRoles()
    {
        foreach (var sec in Visible()) if (sec.Role != null) yield return sec.Role;
    }

    private static int EnabledIn()
    {
        int n = 0;
        foreach (var a in TeamRoles()) if (Enabled(a)) n++;
        return n;
    }

    private static int CountIn()
    {
        int n = 0;
        foreach (var _ in TeamRoles()) n++;
        return n;
    }

    private static int MaxBulkCount()
    {
        int max = 1;
        foreach (var a in TeamRoles()) max = Math.Max(max, a.Count.Max);
        return max;
    }

    // まとめて変える: 今の陣営の札で見えている役職を全部 (変える前を 1 回分覚える)
    private static void Apply(Func<Assignable, bool> change)
    {
        var before = new List<(Assignable, int, int)>();
        foreach (var a in TeamRoles())
        {
            int chance = a.Chance.Index, count = a.Count.Index;
            if (change(a)) before.Add((a, chance, count));
        }
        if (before.Count == 0) return;
        _undo = before;
        foreach (var (a, _, _) in before) Roles.TeamSizes.OnEdited(a.Count);
        RefreshCards();
        ShowSide(null);
    }

    private static void Undo()
    {
        if (_undo == null) return;
        foreach (var (a, chance, count) in _undo)
        {
            a.Chance.SetIndex(chance);
            a.Count.SetIndex(count);
            Roles.TeamSizes.OnEdited(a.Count);
        }
        _undo = null;
        RefreshCards();
        ShowSide(null);
    }

    // カードの値と縁の色だけを今の値に (作り直さない)。左の説明の欄の「出る役職」の数も
    private static void RefreshCards()
    {
        var menu = GameSettingMenu.Instance;
        if (_tokens.Length == 0 && menu && menu.MenuDescriptionText) menu.MenuDescriptionText.text = RoleMenu.DefaultDescription;
        foreach (var (sec, card, values, name) in Cards)
        {
            var a = sec.Role;
            if (a == null) continue;
            bool on = Enabled(a);
            card.SetColor(CardColor(sec));
            if (values.Built)
            {
                values.Built.text = CardValues(a);
                values.Built.color = on ? Color.white : Gray;
            }
            if (name.Built) name.Built.color = on ? TitleColor(sec) : Color.Lerp(TitleColor(sec), Gray, 0.55f);
        }
    }

    // ---- 2 枚目 ----

    private static Widget DetailPage(Registry.Section sec)
    {
        var back = new Button("<<", () => { _open = null; _hover = null; Rebuild(); }, 1.9f) { Size = new Vector2(0.7f, 0.5f) };
        var title = new Label(sec.Title, 2.6f) { MenuFont = true, Color = TitleColor(sec), Outline = 0.2f };
        var head = new Stack(true, back, title) { Gap = 0.25f };
        head.Align = Align.Start;

        var rows = new Stack(false) { Gap = 0.1f, Width = W - 0.3f };
        string s = ShortText(sec);
        if (s.Length > 0) rows.Add(new Label(s, 1.3f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
        var opts = new List<Opt>(SectionOpts(sec));
        Editor.Watch(opts);
        foreach (var opt in opts)
        {
            if (!opt.Shown) continue;
            float indent = Depth(opt) * 0.3f;
            var r = Editor.Row(opt, true, indent);
            r.Align = Align.Start;
            rows.Add(r);
        }
        if (sec.Role is RoleBase rb && rb.Excluded != null) rows.Add(ExclSection(rb));
        Editor.Refresh();

        var list = new ScrollView(new Vector2(W, H - 0.62f), rows) { Tag = "rolepage:detail:" + (sec.Role?.Id ?? sec.Title.ToString()) };
        var page = new Stack(false, head, list) { Gap = 0.1f, Width = W };
        return page;
    }

    private static int Depth(Opt opt)
    {
        int d = 0;
        for (var p = opt.Parent; p != null && d < 4; p = p.Parent) d++;
        return d;
    }

    // 一緒に出ない役職: 札を押すと両方の役職の選びを揃えて切り替える (片方だけ残ると、外したのに選んだままに見えるため)
    private static Widget ExclSection(RoleBase me)
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

    private static readonly Color ChipOff = new(0.32f, 0.33f, 0.36f, 1f);

    private static Widget ExclChip(RoleBase me, RoleBase other, float w)
    {
        bool locked = AssignPlan.FixedPair(me, other);
        bool on = locked || AssignPlan.PickedPair(me, other);
        var c = RoleDisplay.ParseColor(other.Color);
        string text = locked ? new Text("RoleMenu.FixedPair").With("name", other.Name) : other.Name.ToString();
        var label = new Label(text, 1.35f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = on ? c : Gray, Width = w - 0.2f };
        Button b = null;
        b = new Button(label, () =>
        {
            if (locked) return;
            bool next = !AssignPlan.PickedPair(me, other);
            me.Excluded.Set(other.Order, next);
            other.Excluded.Set(me.Order, next);
            b.SetColor(next ? c : ChipOff);
            if (label.Built) label.Built.color = next ? c : Gray;
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
    private static bool Matches(string hay)
    {
        foreach (var t in _tokens)
            if (!hay.Contains(t, StringComparison.Ordinal)) return false;
        return true;
    }

    private static Widget SearchPage()
    {
        Array.Clear(Hits, 0, Hits.Length);
        var col = new Stack(false) { Gap = 0.08f, Width = W - 0.3f };
        var all = new List<Opt>();
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
                var r = Editor.Row(opt, true, 0.25f, RoleSearch.AnyIn(_tokens, hay) ? Highlight : Color.white);
                r.Align = Align.Start;
                rows.Add(r);
                Hits[area]++;
            }
            if (rows != null) col.Add(rows);
        }
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
        {
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(k)))
            {
                var a = sec.Role;
                if (a == null) continue;
                var hay = new System.Text.StringBuilder(RoleSearch.Haystack(a.Name)).Append('\n').Append(RoleSearch.Normalize(a.Reading));
                foreach (var opt in SectionOpts(sec)) hay.Append('\n').Append(RoleSearch.Haystack(opt.Label));
                if (!Matches(hay.ToString())) continue;
                Hits[2]++;
                var card = Card(sec, W - 0.3f);
                card.Align = Align.Start;
                col.Add(card);
                foreach (var opt in SectionOpts(sec))
                {
                    if (!opt.Shown || !RoleSearch.AnyIn(_tokens, RoleSearch.Haystack(opt.Label))) continue;
                    all.Add(opt);
                    var r = Editor.Row(opt, true, 0.4f, Highlight);
                    r.Align = Align.Start;
                    col.Add(r);
                }
            }
        }
        if (Hits[0] + Hits[1] + Hits[2] == 0)
            col.Add(new Label(new Text("RoleMenu.NothingMatchesShort"), 1.6f) { Color = SubText, Width = W - 0.4f, TextAlign = TextAlignmentOptions.Center });
        Editor.Watch(all);
        Editor.Refresh();
        _list = new ScrollView(new Vector2(W, H), col);
        return _list;
    }

    // 検索の件数 (左の説明の欄に出す)
    public static Text SearchSummary(string q)
    {
        int total = Hits[0] + Hits[1] + Hits[2];
        if (total == 0) return new Text("RoleMenu.NothingMatches").With("q", q);
        var sb = new System.Text.StringBuilder(new Text("RoleMenu.SearchCount").With("q", q).With("total", total));
        Text[] names = { VanillaGame.TabName, SettingsMenu.TabName(false), RoleMenu.TabName };
        string sep = "\n";
        for (int i = 0; i < 3; i++)
        {
            if (Hits[i] == 0) continue;
            sb.Append(sep).Append(new Text("RoleMenu.SearchTabCount").With("tab", names[i]).With("n", Hits[i]));
            sep = Tr.Get("RoleMenu.SearchSeparator");
        }
        sb.Append('\n').Append(Tr.Get("RoleMenu.SearchClearHint"));
        return Text.Raw(sb.ToString());
    }

    // ---- 毎フレーム (設定画面が開いている間だけ) ----

    // カードに当てている間の右クリック = 出現率を 0% と 100% で切り替える
    public static void Tick()
    {
        if (Touch || !_built || _tokens.Length > 0 || _open != null || _hover?.Role == null || !_root) return;
        if (!Input.GetMouseButtonDown(1)) return;
        var c = _hover.Role.Chance;
        c.SetIndex(c.Index > 0 ? 0 : c.Count - 1);
        Roles.TeamSizes.OnEdited(c);
        RefreshCards();
        ShowSide(_hover);
    }

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        // 検索は役職のタブが未だ作られていなくても打てる (打つと役職のタブが開いて作られる)
        if (a.Length > 0 && a[0] == "q" && !SettingsMenu.TypeSearch(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "")) return "no search box";
        if (!_root) return "role page not built";
        switch (a.Length > 0 ? a[0] : "dump")
        {
            case "q":
                return $"hits={Hits[0]}/{Hits[1]}/{Hits[2]} tokens={_tokens.Length} slots={UiActions.Census()}";
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
            case "rebuild":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Rebuild();
                return $"ms={sw.Elapsed.TotalMilliseconds:0.0} slots={UiActions.Census()}";
            }
        }
        var sb = new System.Text.StringBuilder($"team={_team} only={_enabledOnly} open={_open?.Role?.Id ?? _open?.Title.ToString() ?? "-"} hover={_hover?.Role?.Id ?? "-"} undo={_undo?.Count ?? -1} slots={UiActions.Census()} cards:");
        foreach (var (sec, card, values, _) in Cards)
            sb.Append($" {sec.Role?.Id ?? sec.Title.ToString()}({(values.Built ? values.Built.text.Replace('\n', ',') : "")})");
        return sb.ToString();
    }

    private static Registry.Section Find(string id)
    {
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(k)))
                if (sec.Role?.Id == id || sec.Title.ToString() == id) return sec;
        return null;
    }
}
