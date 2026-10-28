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
    private const float CardGap = 0.07f;
    private const float IconW = 0.36f;   // 2 列のカードの左の絵の枠

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
    private bool _enabledOnly, _freshOnly;
    private int _sort, _filter;
    private bool _freshChip;  // 一覧に「変わった所だけ」の札を出したか
    private int _bulkChance = 50, _bulkCount = 1;

    private UiScreen _screen;
    // 開いている物 (null = 1 枚目)。設定画面の一覧では右の欄で編集している物 (一覧はそのまま)、それ以外は 2 枚目
    private Registry.Section _open;
    private Registry.Section _hover;  // 右に要約を出している物
    private Registry.Section _picked; // スマホで 1 回押した物 (もう 1 回で開く)
    private Holder _side;
    private float _sideH;
    private ScrollView _sideList;  // 右の欄で編集している時の行の欄
    private ScrollView _list;
    private readonly List<(Registry.Section sec, Button card, Label values, Label name, bool narrow)> _cards = new();
    private readonly OptEditor _editor;
    private Button _resetBtn, _undoBtn;  // 2 枚目の「既定に戻す」「元に戻す」(行を押した時に色だけ書き換える)
    private string[] _tokens = Array.Empty<string>();
    private string _queryText = "";
    private readonly int[] _hits = new int[3];  // 検索の件数: 0 = ゲーム設定・1 = MRP 全般・2 = 役職

    public RoleBrowser(float width, float height, bool settings, Func<bool> canEdit, string tag)
    {
        W = width;
        H = height;
        ListW = (float)Math.Round(width * 0.56f, 2);
        SideW = width - ListW - 0.15f;
        CardW = ListW - 0.2f;
        _settings = settings;
        _canEdit = canEdit;
        _tag = tag;
        _editor = new OptEditor(OnShownChanged, _ => AfterEdit(), canEdit);
    }

    // true = 一覧に陣営の札を出さず、陣営は持ち主が Team で選ぶ
    public bool TeamFromOutside;

    public int Team
    {
        get => _team;
        set
        {
            if (_team == value && _open == null) return;
            _team = value;
            _open = null;
            _hover = null;
            _picked = null;
            Rebuild();
        }
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
        var menu = GameSettingMenu.Instance;
        _queryText = RoleSearch.ForDisplay((q ?? "").Trim(), menu && menu.MenuDescriptionText ? menu.MenuDescriptionText.font : null);
        var tokens = RoleSearch.Tokens(q);
        bool was = _tokens.Length > 0;
        _tokens = tokens;
        if (tokens.Length > 0 != was) { _open = null; _hover = null; }
        Rebuild();
    }

    public void OpenSection(Registry.Section sec)
    {
        if (SideEdit && _side != null && sec?.Role != null)
        {
            Select(sec);
            return;
        }
        _list?.Remember();
        _open = sec;
        _picked = null;
        Rebuild();
    }

    private static void Prepare()
    {
        // カードの名前に付ける印 (★・*・「更新」) も先に入れておく
        var chars = new System.Text.StringBuilder("0123456789%/×:+-<>*").Append(FavMark).Append(FreshMark.ToString());
        foreach (var t in new[] { Tab.Crew, Tab.Impostor, Tab.Neutral, Tab.Addon })
            foreach (var sec in Registry.SectionsOf(t)) chars.Append(sec.Title);
        MenuFont.Prepare(chars.ToString());
    }

    // ---- 組み立て ----

    public void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        _sideList?.Remember();
        _sideList = null;
        _cards.Clear();
        _editor.Clear();
        _side = null;
        _list = null;
        _resetBtn = _undoBtn = null;
        _screen.Set(_open != null && !SideEdit ? DetailPage(_open) : _tokens.Length > 0 ? SearchPage() : ListPage());
    }

    // 設定画面の一覧 = 役職を押すと右の欄で編集する (2 枚目へ切り替えない)。役職辞典と検索の結果から開いた時は 2 枚目
    private bool SideEdit => _settings && _tokens.Length == 0;

    // 項目の出入り (ほかの項目の値で出る行) が変わった: 右の欄で編集中なら右の欄だけ作り直す
    private void OnShownChanged()
    {
        if (SideEdit && _open != null && _side != null) RefreshSide();
        else Rebuild();
    }

    private Widget ListPage()
    {
        var chips = new Stack(true) { Gap = 0.07f, Align = Align.Start };
        _freshChip = SettingNotice.FreshCount > 0;
        for (int k = -1; k <= RoleMenu.AddonKind && !TeamFromOutside; k++)
        {
            int kind = k;
            Color c = k < 0 ? Color.white : RoleMenu.KindColor(k);
            chips.Add(Chip(k < 0 ? new Text("RoleMenu.All") : RoleMenu.KindName(k), c, _team == k, () => { _team = kind; _hover = null; Rebuild(); }));
        }
        var enabledOnly = Chip(new Text("RoleMenu.EnabledOnly"), new Color(0.6f, 1f, 0.6f), _enabledOnly, () => { _enabledOnly = !_enabledOnly; Rebuild(); });

        // 2 段目: 並べ替えとタグの絞り込み (タグは 1 つだけ選ぶ・もう一度押すと外す)。陣営の札が無い時はこの 1 段だけ
        var chips2 = new Stack(true) { Gap = 0.07f, Align = Align.Start };
        (TeamFromOutside ? chips2 : chips).Add(enabledOnly);
        chips2.Add(Chip(new Text("RolePage.Sort" + _sort), SortColor, false, () => { _sort = (_sort + 1) % SortCount; Rebuild(); }));
        for (int f = 1; f <= TagCount; f++)
        {
            int tag = f;
            chips2.Add(Chip(new Text("RolePage.Tag" + f), TagColor(f), _filter == f, () => { _filter = _filter == tag ? 0 : tag; Rebuild(); }));
        }
        if (SettingNotice.FreshCount > 0)
            chips2.Add(Chip(new Text("RolePage.FreshOnly"), FreshColor, _freshOnly, () => { _freshOnly = !_freshOnly; Rebuild(); }));

        // カードは 2 列 (左から右へ・2 枚で 1 段)
        var cards = new Stack(false) { Gap = CardGap, Width = CardW };
        float half = (CardW - CardGap) / 2f;
        Stack line = null;
        int n = 0;
        foreach (var sec in Visible())
        {
            if (n % 2 == 0)
            {
                line = new Stack(true) { Gap = CardGap, Align = Align.Start };
                cards.Add(line);
            }
            line.Add(Card(sec, half, true));
            n++;
        }
        if (n == 0) cards.Add(new Label(new Text("RoleMenu.NoMatchingRoles"), 1.6f) { Color = SubText, Width = CardW, TextAlign = TextAlignmentOptions.Center });

        float listH = TeamFromOutside ? H - ChipH - 0.12f : H - ChipH * 2f - 0.07f - 0.12f;
        _list = new ScrollView(new Vector2(ListW, listH), cards) { Tag = _tag + ":" + _team + (_enabledOnly ? "+" : "") + _filter + _sort };
        _sideH = listH;
        _side = new Holder(new Vector2(SideW, listH), SideContent());
        var head = TeamFromOutside ? chips2 : new Stack(false, chips, chips2) { Gap = 0.07f, Align = Align.Start };
        return new Stack(false, head, new Stack(true, _list, _side) { Gap = 0.15f }) { Gap = 0.12f, Width = W };
    }

    private Button Chip(string text, Color c, bool on, Action click)
    {
        // 陣営の札が無い時は札を 1 段に詰めるので、字と余白を小さく
        var label = new Label(text, TeamFromOutside ? 1.2f : 1.45f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = on ? new Color(0.08f, 0.08f, 0.1f) : c };
        var b = new Button(label, click) { Size = new Vector2(0f, ChipH), MinSize = new Vector2(TeamFromOutside ? 0.6f : 0.75f, ChipH), Padding = new Vector2(TeamFromOutside ? 0.09f : 0.14f, 0.04f), Color = c };
        if (on) b.Fill = c;
        return b;
    }

    // 今の陣営の札・「出る役職だけ」で見せる物 (役職とアドオン・陣営に置いた設定のまとまり)
    // ---- 並べ替えとタグ ----

    private const int SortCount = 3;   // 0 = 標準・1 = 出現率の高い順・2 = 名前
    private const int TagCount = 4;    // 1 = お気に入り・2 = キル役・3 = 地形を変える・4 = 本編の役職
    private static readonly Color SortColor = new(0.78f, 0.78f, 0.84f);

    private static Color TagColor(int tag) => tag switch
    {
        1 => new Color(1f, 0.6f, 0.8f),
        2 => new Color(1f, 0.45f, 0.4f),
        3 => new Color(1f, 0.7f, 0.35f),
        _ => new Color(0.75f, 0.85f, 1f),
    };

    private static bool HasTag(Registry.Section sec, int tag)
    {
        var a = sec.Role;
        if (a == null) return false;
        var rb = a as RoleBase;
        return tag switch
        {
            1 => RoleFavorites.Has(a),
            2 => rb != null && rb.CanKill,
            3 => rb != null && rb.TerrainPermit.Any,
            4 => a is Roles.Builtin.VanillaRole,
            _ => true,
        };
    }

    // 要約と 2 枚目に出すタグの名前 (お気に入りは別の印なので入れない)
    private static string TagLine(Registry.Section sec)
    {
        var sb = new System.Text.StringBuilder();
        for (int t = 2; t <= TagCount; t++)
        {
            if (!HasTag(sec, t)) continue;
            if (sb.Length > 0) sb.Append('・');
            sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(TagColor(t))}>{new Text("RolePage.Tag" + t)}</color>");
        }
        return sb.ToString();
    }

    private List<Registry.Section> Visible()
    {
        var list = new List<Registry.Section>(VisibleInOrder());
        if (_sort == 0 || list.Count < 2) return list;
        var order = new Dictionary<Registry.Section, int>();
        for (int i = 0; i < list.Count; i++) order[list[i]] = i;
        list.Sort((x, y) =>
        {
            int c = 0;
            if (_sort == 1) c = (y.Role?.Chance.Value ?? -1).CompareTo(x.Role?.Chance.Value ?? -1);
            else if (_sort == 2) c = string.CompareOrdinal(SortName(x), SortName(y));
            return c != 0 ? c : order[x].CompareTo(order[y]);
        });
        return list;
    }

    // 名前の並びは読み (無ければ名前) をひらがなに揃えて比べる (検索と同じ揃え方)
    private static string SortName(Registry.Section sec) => RoleSearch.Normalize(sec.Role?.Reading ?? sec.Title.ToString());

    // 凡例: カードの縁の色の見本 3 つと、名前の印の説明 (「更新」の印の説明は、印が付いている時だけ)
    private Widget Legend()
    {
        var swatches = new Stack(true) { Gap = 0.05f, Align = Align.Start };
        float itemW = (SideW - 0.1f) / 3f;
        (string key, Color c)[] items = { ("RolePage.SwatchOff", Gray), ("RolePage.SwatchChance", Color.white), ("RolePage.SwatchAlways", Gold) };
        foreach (var (key, c) in items)
            swatches.Add(new Stack(true,
                new Box(null) { Size = new Vector2(0.32f, 0.24f), Color = c },
                new Label(new Text(key), 1.05f) { Color = SubText, Width = itemW - 0.37f, MinFontSize = 0.8f }) { Gap = 0.05f });
        var col = new Stack(false, swatches) { Gap = 0.04f, Width = SideW, Align = Align.Start };
        col.Add(new Label(new Text("RolePage.LegendMarks"), 1.05f) { Color = SubText, Width = SideW, Align = Align.Start });
        if (SettingNotice.FreshCount > 0) col.Add(new Label(new Text("RolePage.LegendFreshLine"), 1.05f) { Color = SubText, Width = SideW, Align = Align.Start });
        swatches.Align = Align.Start;
        return col;
    }

    private IEnumerable<Registry.Section> VisibleInOrder()
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
                if (FreshOnly && !Fresh(sec)) continue;
                if (_filter != 0 && !HasTag(sec, _filter)) continue;
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

    // oneLine = 2 列のカード (名前の下に「×1  50%」)
    private static string CardValues(Assignable a, bool oneLine = false)
    {
        if (a == null) return "";
        string count = $"×{a.Count.Display()}";
        if (a.Count.Changed) count = $"<color=#{ChangedHex}>{count}</color>";
        return oneLine ? $"{count}  {a.Chance.Display()}" : $"{count}\n{a.Chance.Display()}";
    }

    private static readonly string ChangedHex = ColorUtility.ToHtmlStringRGB(OptEditor.ChangedColor);

    // 細かい設定を既定から変えてあるか。出現率は既定が 0 (出ない) なので縁の色で分かる → 数えない
    private static bool DetailChanged(Registry.Section sec)
    {
        foreach (var opt in SectionOpts(sec))
            if (opt != sec.Role?.Chance && opt.Changed) return true;
        return false;
    }

    // ホストが直前に変えた項目の字 (客の側)
    public static readonly Color FreshColor = new(0.4f, 0.88f, 1f, 1f);
    private static readonly string FreshHex = ColorUtility.ToHtmlStringRGB(FreshColor);
    private static readonly Text FreshMark = new("RolePage.FreshMark");
    private static readonly string FavHex = ColorUtility.ToHtmlStringRGB(TagColor(1));
    private const string FavMark = "★";

    private static bool Fresh(Registry.Section sec)
    {
        foreach (var opt in sec.Opts)
            if (opt.Fresh) return true;
        return false;
    }

    // カードの名前 (細かい設定を変えてあれば *・ホストが直前に変えていれば「更新」)
    private static string CardName(Registry.Section sec)
    {
        string s = DetailChanged(sec) ? $"{sec.Title} <color=#{ChangedHex}>*</color>" : sec.Title.ToString();
        if (RoleFavorites.Has(sec.Role)) s = $"<color=#{FavHex}>{FavMark}</color> {s}";
        return Fresh(sec) ? $"{s} <size=70%><color=#{FreshHex}>{FreshMark}</color></size>" : s;
    }

    // 「変わった所だけ」の札を出すか (受け取った変更がある間だけ)
    private bool FreshOnly => _freshOnly && SettingNotice.FreshCount > 0;

    private static string ShortText(Registry.Section sec)
    {
        var a = sec.Role;
        if (a == null) return "";
        return RoleDoc.Has(a) ? new Text(a.Id + ".Summary").ToString() : a.Description.ToString();
    }

    private static readonly Color IconOff = new(1f, 1f, 1f, 0.35f);

    private static Sprite Icon(Assignable a) => RoleArt.Of(a);

    // narrow = 2 列の一覧のカード (絵の枠+名前+人数と出現率の 1 行・説明は右の欄だけ)。検索の結果は幅いっぱいで説明つき
    private Widget Card(Registry.Section sec, float width, bool narrow = false)
    {
        var a = sec.Role;
        bool on = a == null || Enabled(a);
        var title = TitleColor(sec);
        float pad = narrow ? 0.09f : 0.14f;
        Label name, values;
        Widget body;
        if (narrow)
        {
            float textW = width - pad * 2f - IconW - 0.06f;
            name = new Label(CardName(sec), 1.6f) { MenuFont = true, Width = textW, MinFontSize = 1.05f, Color = on ? title : Color.Lerp(title, Gray, 0.55f), Align = Align.Start };
            values = new Label(CardValues(a, true), 1.15f) { Width = textW, Bold = true, Color = on ? Color.white : Gray, Align = Align.Start };
            var text = new Stack(false, name) { Gap = 0f, Width = textW, Align = Align.Start };
            if (a != null) text.Add(values);
            var icon = Icon(a);
            Widget art = icon ? new Picture(icon, IconW, IconW) { Fit = true, Color = on ? Color.white : IconOff } : new Spacer(IconW, IconW);
            body = new Stack(true, art, text) { Gap = 0.06f };
        }
        else
        {
            name = new Label(CardName(sec), 1.9f) { MenuFont = true, Width = width - 1.0f, Color = on ? title : Color.Lerp(title, Gray, 0.55f), Align = Align.Start };
            var desc = new Label(ShortText(sec), 1.15f) { Width = width - 1.0f, Color = on ? SubText : Gray, Align = Align.Start };
            values = new Label(CardValues(a), 1.35f) { Width = 0.6f, TextAlign = TextAlignmentOptions.Right, Bold = true, Color = on ? Color.white : Gray };
            var text = new Stack(false, name) { Gap = 0.01f, Width = width - 0.95f };
            if (desc.Content.Length > 0) text.Add(desc);
            body = new Stack(true, text, values) { Gap = 0.1f };
        }
        var card = new Button(body, () => OnCard(sec))
        {
            Size = new Vector2(width, 0f),
            MinSize = new Vector2(width, 0.55f),
            Padding = new Vector2(pad, 0.07f),
            Color = CardColor(sec),
            // 選んだカードは中を薄く塗る (選び直しは塗りの色だけ書き換える)
            Fill = narrow && SideEdit ? SelectFill(sec) : null,
            OnHover = () => { if (!Touch) ShowSide(sec); },
            OnUnhover = () => { if (!Touch && _hover == sec) ShowSide(null); },
        };
        _cards.Add((sec, card, values, name, narrow));
        return card;
    }

    private Color SelectFill(Registry.Section sec) => sec == _open ? Color.Lerp(Color.black, TitleColor(sec), 0.28f) : Color.black;

    private void OnCard(Registry.Section sec)
    {
        if (SideEdit && sec.Role != null)
        {
            // もう一度押すと閉じる
            Select(_open == sec ? null : sec);
            return;
        }
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

    // 当てた物の要約を右に出す (右の欄で編集している間は編集のまま)
    private void ShowSide(Registry.Section sec)
    {
        _hover = sec;
        if (SideEdit && _open != null) return;
        RefreshSide();
    }

    private void RefreshSide()
    {
        if (_side?.Screen == null || !_side.Screen.Alive) return;
        _sideList?.Remember();
        _sideList = null;
        _editor.Clear();
        _resetBtn = _undoBtn = null;
        _side.Screen.Set(SideContent());
    }

    // 右の欄で編集する物を選ぶ (null = 選びを外して、まとめて変える札へ)
    private void Select(Registry.Section sec)
    {
        _open = sec;
        _picked = null;
        foreach (var (s, card, _, _, narrow) in _cards)
            if (narrow && card.FillRenderer) card.FillRenderer.color = SelectFill(s);
        RefreshSide();
    }

    private Widget SideContent()
    {
        if (SideEdit && _open != null) return SideEditor(_open);
        if (_hover != null) return Summary(_hover);
        if (_settings && Editable) return Bulk();
        return new Stack(false,
            new Label(new Text(Touch ? "RoleDictionary.HintTouch" : "RoleDictionary.Hint"), 1.3f) { Color = SubText, Width = SideW, Align = Align.Start },
            Legend(),
            new Spacer(0.1f, 0.05f),
            new Button(new Text("RoleDictionary.CopyCode"), () => SettingNotice.Toast(SettingCode.Copy()), 1.35f) { Size = new Vector2(SideW, 0.4f) }) { Gap = 0.12f, Width = SideW };
    }

    // ---- 右の欄の見出し: 陣営の色の地に大きな絵と名前 (左上に操作のボタン) ----

    private const float HeroH = 0.98f, HeroArt = 0.8f;

    private Widget Hero(Color c, Widget art, string title, string sub, Widget tools = null)
    {
        var hero = new Layers(SideW, HeroH);
        hero.Add(new Box(null) { Size = new Vector2(SideW, HeroH), Color = Color.Lerp(c, Color.black, 0.5f), Fill = Color.Lerp(Color.black, c, 0.2f) }, 0f, 0f);
        if (art != null) hero.Add(art, 0.08f, (HeroH - HeroArt) / 2f, true);
        float textW = SideW - HeroArt - 0.3f;
        var text = new Stack(false, new Label(title, 1.9f) { MenuFont = true, Outline = 0.2f, Color = c, Width = textW, MinFontSize = 1.2f, Align = Align.Start }) { Gap = 0f, Width = textW };
        if (!string.IsNullOrEmpty(sub)) text.Add(new Label(sub, 1.05f) { Color = SubText, Width = textW, Align = Align.Start });
        hero.Add(text, 0.14f, 0.06f, false, true);
        if (tools != null) hero.Add(tools, 0.1f, 0.08f);
        return hero;
    }

    // 役職の絵 (無ければ陣営のクルーの絵)
    private static Widget RoleArtPic(Registry.Section sec)
    {
        var icon = Icon(sec.Role);
        if (icon) return new Picture(icon, HeroArt, HeroArt) { Fit = true };
        int k = KindIndex(sec);
        return k >= 0 ? PosePic(k) : null;
    }

    // 陣営のクルーの絵 (メインメニューの絵を陣営の色で)
    private static Widget PosePic(int kind)
    {
        var sp = CrewArt.Pose(SettingsMenu.KindPose(kind));
        var mat = sp ? CrewArt.Material(RoleMenu.KindColor(kind)) : null;
        return mat ? new Picture(sp, HeroArt * 1.1f, HeroArt) { Fit = true, Material = mat } : null;
    }

    private static int KindIndex(Registry.Section sec)
    {
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
            foreach (var s in Registry.SectionsOf(RoleMenu.KindTab(k)))
                if (s == sec) return k;
        return -1;
    }

    // 見出しの下の字: 陣営の名前とタグ
    private static string HeroSub(Registry.Section sec)
    {
        int k = KindIndex(sec);
        string team = k >= 0 ? RoleMenu.KindName(k).ToString() : "";
        string tags = TagLine(sec);
        return tags.Length > 0 ? $"{team}・{tags}" : team;
    }

    private Widget Summary(Registry.Section sec)
    {
        var col = new Stack(false) { Gap = 0.03f, Width = SideW };
        col.Add(Hero(TitleColor(sec), RoleArtPic(sec), sec.Title.ToString(), HeroSub(sec)));
        col.Add(new Spacer(0.1f, 0.03f));
        string s = ShortText(sec);
        if (s.Length > 0) col.Add(new Label(s, 1.2f) { Color = SubText, Width = SideW, Align = Align.Start });
        col.Add(new Spacer(0.1f, 0.06f));
        foreach (var opt in SectionOpts(sec))
        {
            if (!opt.Shown) continue;
            col.Add(new Label($"{opt.Label}: {ValueText(opt)}", 1.25f) { Width = SideW, Align = Align.Start, Color = opt.Fresh ? FreshColor : Color.white });
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
        string head = _team < 0 ? new Text("RolePage.BulkAll") : new Text("RolePage.BulkTeam").With("team", team);
        if (_team >= 0) col.Add(Hero(RoleMenu.KindColor(_team), PosePic(_team), team, head));
        else col.Add(new Label(head, 1.6f) { Bold = true, Width = SideW, Align = Align.Start });
        col.Add(new Label(new Text("RolePage.EnabledBig").With("n", EnabledIn()).With("count", CountIn()), 3.2f) { Bold = true, Width = SideW, Align = Align.Start });
        col.Add(Legend());
        float third = (SideW - 0.16f) / 3f;
        col.Add(new Stack(true,
            new Button("0%", () => Apply(a => a.Chance.SetValue(0)), 1.4f) { Size = new Vector2(third, 0.4f) },
            new Button("100%", () => Apply(a => a.Chance.SetValue(100)), 1.4f) { Size = new Vector2(third, 0.4f) },
            new Button(new Text("RoleMenu.Undo"), Undo, 1.3f) { Size = new Vector2(third, 0.4f), Color = OptHistory.Any ? Color.white : OptEditor.Dim }) { Gap = 0.08f });
        col.Add(BulkStepper(new Text("RolePage.ChanceTo").With("pct", _bulkChance + "%"),
            () => { _bulkChance = Math.Max(0, _bulkChance - 10); ShowSide(null); },
            () => { _bulkChance = Math.Min(100, _bulkChance + 10); ShowSide(null); },
            () => Apply(a => a.Chance.SetValue(_bulkChance))));
        col.Add(BulkStepper(new Text("RolePage.CountTo").With("n", _bulkCount),
            () => { _bulkCount = Math.Max(1, _bulkCount - 1); ShowSide(null); },
            () => { _bulkCount = Math.Min(MaxBulkCount(), _bulkCount + 1); ShowSide(null); },
            () => Apply(a => a.Count.SetValue(_bulkCount))));
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
        if (SideEdit) RefreshSide();
        else if (_open != null || _tokens.Length > 0) Rebuild();
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
        if (SideEdit) RefreshSide();
        else Rebuild();
    }

    // 右の欄の 0% / 100% (1 回の取り消しで戻る)
    private void SetChance(Registry.Section sec, int value)
    {
        var opt = sec.Role?.Chance;
        if (opt == null || !Editable) return;
        int before = opt.Index;
        if (!opt.SetValue(value)) return;
        OptHistory.Push(opt, before);
        OptEditor.AfterStep(opt);
        AfterEdit();
        RefreshSide();
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
        // 「変わった所だけ」の札が出る/消える時は一覧ごと作り直す
        if (SideEdit && _open != null && _freshChip == SettingNotice.FreshCount > 0)
        {
            // 右の欄で編集中は一覧を作り直さず、カードの字と右の欄だけ
            RefreshCards();
            RefreshSide();
        }
        else if (_open != null || _tokens.Length > 0 || _freshChip != SettingNotice.FreshCount > 0) Rebuild();
        else RefreshCards();
    }

    public void RefreshCards()
    {
        foreach (var (sec, card, values, name, narrow) in _cards)
        {
            var a = sec.Role;
            if (a == null) continue;
            bool on = Enabled(a);
            card.SetColor(CardColor(sec));
            // 字はゲーム側へ写すたびに費用が掛かるので、変わったカードだけ書く (Content = 今出している字)
            if (values.Built)
            {
                string v = CardValues(a, narrow);
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
        var head = new Stack(true, back) { Gap = 0.2f, Align = Align.Start };
        // 一覧の今の並び (陣営・絞り込み・並べ替え) で前後の物へ。端は反対の端へ回る。
        // 今の一覧に無い物 (別の陣営の札の時に外から開いた等) はその陣営の並び。検索の結果から開いた時は出さない
        if (Neighbors(sec) is var (prev, next))
            head.Add(new Stack(true,
                new Button("◀", () => OpenSection(prev), 1.5f) { Size = new Vector2(0.45f, 0.5f) },
                new Button("▶", () => OpenSection(next), 1.5f) { Size = new Vector2(0.45f, 0.5f) }) { Gap = 0.05f });
        head.Add(title);
        var extra = HeadExtra?.Invoke(sec);
        if (extra != null) head.Add(extra);
        if (sec.Role != null) head.Add(FavButton(sec, 0.5f));

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
            var r = _editor.Row(opt, edit, indent, opt.Fresh ? FreshColor : (Color?)null);
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

    // 一覧の今の並び (陣営・絞り込み・並べ替え) での前後の物。端は反対の端へ回る。
    // 今の一覧に無い物 (別の陣営の札の時に外から開いた等) はその陣営の並び。検索の結果から開いた時は無し
    private (Registry.Section prev, Registry.Section next)? Neighbors(Registry.Section sec)
    {
        var order = _tokens.Length > 0 ? null : Visible();
        int at = order?.IndexOf(sec) ?? -1;
        if (at < 0 && order != null)
        {
            order = KindOf(sec);
            at = order?.IndexOf(sec) ?? -1;
        }
        if (at < 0 || order.Count < 2) return null;
        return (order[(at + order.Count - 1) % order.Count], order[(at + 1) % order.Count]);
    }

    // お気に入り (自分の手元だけ)。押すと札の色で塗る
    private Button FavButton(Registry.Section sec, float size)
    {
        bool fav = RoleFavorites.Has(sec.Role);
        var c = TagColor(1);
        var label = new Label(FavMark, size * 3.6f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = fav ? new Color(0.08f, 0.08f, 0.1f) : c };
        var b = new Button(label, () =>
        {
            RoleFavorites.Toggle(sec.Role);
            if (SideEdit)
            {
                RefreshCards();
                RefreshSide();
            }
            else Rebuild();
        })
        { Size = new Vector2(size, size), Padding = Vector2.zero, Color = c };
        if (fav) b.Fill = c;
        return b;
    }

    // ---- 右の欄で編集 (設定画面の一覧) ----

    private Widget SideEditor(Registry.Section sec)
    {
        bool edit = Editable;
        const float btn = 0.36f, gap = 0.05f;
        // 見出しの左上: 前後の役職・お気に入り・閉じる
        var tools = new Stack(true) { Gap = gap };
        if (Neighbors(sec) is var (prev, next))
        {
            tools.Add(new Button("◀", () => Select(prev), 1.3f) { Size = new Vector2(btn, btn), Padding = Vector2.zero });
            tools.Add(new Button("▶", () => Select(next), 1.3f) { Size = new Vector2(btn, btn), Padding = Vector2.zero });
        }
        if (sec.Role != null) tools.Add(FavButton(sec, btn));
        tools.Add(new Button("×", () => Select(null), 1.5f) { Size = new Vector2(btn, btn), Padding = Vector2.zero });

        var col = new Stack(false) { Gap = 0.05f, Width = SideW };
        col.Add(Hero(TitleColor(sec), RoleArtPic(sec), sec.Title.ToString(), HeroSub(sec), tools));
        string s = ShortText(sec);
        if (s.Length > 0) col.Add(new Label(s, 1.1f) { Color = SubText, Width = SideW, Align = Align.Start });

        // 項目の行 (多い時はこの中だけスクロール)。人数と出現率は陣営の色の枠にまとめ、0% / 100% のボタンを添える
        float rowW = SideW - 0.18f;
        var rows = new Stack(false) { Gap = 0.06f, Width = rowW };
        var opts = new List<Opt>(SectionOpts(sec));
        _editor.Watch(opts);
        var a = sec.Role;
        Stack core = null;
        float coreW = rowW - 0.1f;
        foreach (var opt in opts)
        {
            if (!opt.Shown) continue;
            bool inCore = a != null && (opt == a.Count || opt == a.Chance);
            var r = _editor.NarrowRow(opt, edit, inCore ? coreW : rowW, Depth(opt) * 0.2f, opt.Fresh ? FreshColor : (Color?)null);
            r.Align = Align.Start;
            if (!inCore) { rows.Add(r); continue; }
            if (core == null)
            {
                core = new Stack(false) { Gap = 0.06f, Width = coreW };
                rows.Add(new Box(core) { Size = new Vector2(rowW, 0f), Padding = new Vector2(0.05f, 0.07f), Color = Color.Lerp(TitleColor(sec), Color.black, 0.25f) });
            }
            core.Add(r);
        }
        if (core != null && edit && a.Chance != null)
        {
            float qw = (coreW - 0.08f) / 2f;
            core.Add(new Stack(true,
                new Button("0%", () => SetChance(sec, 0), 1.3f) { Size = new Vector2(qw, 0.32f) },
                new Button("100%", () => SetChance(sec, 100), 1.3f) { Size = new Vector2(qw, 0.32f) }) { Gap = 0.08f });
        }
        if (sec.Role is RoleBase rb && rb.Excluded != null)
        {
            if (edit) rows.Add(ExclSection(rb, rowW, 2));
            else rows.Add(new Label($"{new Text("Registry.NotWith")}: {ExclSummary(rb)}", OptEditor.NarrowFont) { Bold = true, Width = rowW, Align = Align.Start });
        }
        _editor.Refresh();

        // 下の段: 初期値に戻す・元に戻す・説明を見る (役職辞典)
        const float barH = 0.38f;
        var bar = new Stack(true) { Gap = gap, Align = Align.Start };
        var extra = HeadExtra?.Invoke(sec) as Box;
        int n = (edit ? 2 : 0) + (extra != null ? 1 : 0);
        float bw = n > 0 ? (SideW - gap * (n - 1)) / n : 0f;
        if (edit)
        {
            _resetBtn = new Button(new Text("OptEditor.Reset"), () => ResetSection(sec), 1.2f) { Size = new Vector2(bw, barH), Color = ResetColor(sec) };
            _undoBtn = new Button(new Text("RoleMenu.Undo"), Undo, 1.2f) { Size = new Vector2(bw, barH), Color = UndoColor };
            bar.Add(_resetBtn).Add(_undoBtn);
        }
        if (extra != null)
        {
            extra.Size = new Vector2(bw, barH);
            extra.MinSize = Vector2.zero;
            if (extra.Content is Label el)
            {
                el.FontSize = 1.2f;
                el.Width = bw - 0.12f;
                el.MinFontSize = 0.9f;
            }
            bar.Add(extra);
        }

        // 見出しと説明の高さは字の量で変わるので、行の欄の高さは大まかに見積もる
        float used = HeroH + 0.05f + (s.Length > 0 ? 0.42f : 0f) + (n > 0 ? barH + 0.1f : 0f);
        // 値を変えて作り直しても同じ所を見せる (役職ごとの位置)
        _sideList = new ScrollView(new Vector2(SideW, Math.Max(_sideH - used, 1f)), rows) { Tag = _tag + ":side:" + (sec.Role?.Id ?? sec.Title.ToString()) };
        col.Add(_sideList);
        if (n > 0) col.Add(bar);
        return col;
    }

    private static List<Registry.Section> KindOf(Registry.Section sec)
    {
        for (int k = 0; k <= RoleMenu.AddonKind; k++)
        {
            var list = new List<Registry.Section>(Registry.SectionsOf(RoleMenu.KindTab(k)));
            if (list.Contains(sec)) return list;
        }
        return null;
    }

    private static int Depth(Opt opt)
    {
        int d = 0;
        for (var p = opt.Parent; p != null && d < 4; p = p.Parent) d++;
        return d;
    }

    // 一緒に出ない役職: 札を押すと両方の役職の選びを揃えて切り替える (片方だけ残ると、外したのに選んだままに見えるため)
    private Widget ExclSection(RoleBase me) => ExclSection(me, W - 0.3f, 3);

    private Widget ExclSection(RoleBase me, float width, int columns)
    {
        float w = (width - 0.08f * (columns - 1)) / columns;
        bool narrow = columns < 3;
        var col = new Stack(false) { Gap = 0.06f, Width = width, Align = Align.Start };
        col.Add(new Spacer(0.1f, 0.1f));
        col.Add(new Label(new Text("Registry.NotWith"), narrow ? OptEditor.NarrowFont : OptEditor.FontSize) { Bold = true, Align = Align.Start });
        col.Add(new Label(new Text("RoleMenu.NotWithHint"), narrow ? 1.05f : 1.15f) { Color = SubText, Width = width - 0.1f, Align = Align.Start });
        if (Editable) col.Add(ExclBulk(me, width, narrow));
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

    // まとめて選ぶ札: 全部・陣営ごと。その範囲が全部選ばれていれば外し、そうでなければ全部選ぶ
    private Widget ExclBulk(RoleBase me, float width, bool narrow)
    {
        int columns = narrow ? 2 : 4;
        float w = (width - 0.08f * (columns - 1)) / columns;
        var col = new Stack(false) { Gap = 0.06f, Align = Align.Start };
        Stack row = null;
        for (int k = -1; k < 3; k++)
        {
            if ((k + 1) % columns == 0) { row = new Stack(true) { Gap = 0.08f, Align = Align.Start }; col.Add(row); }
            int kind = k;
            Color c = k < 0 ? Color.white : RoleMenu.KindColor(k);
            var text = k < 0 ? new Text("RoleMenu.All") : RoleMenu.KindName(k);
            row.Add(new Button(new Label(text, 1.3f) { TextAlign = TextAlignmentOptions.Center, Color = c, Width = w - 0.15f, MinFontSize = 0.9f }, () =>
            {
                if (!Editable) return;
                bool all = true;
                foreach (var other in ExclCandidates(me))
                    if ((kind < 0 || (int)other.Team == kind) && !AssignPlan.FixedPair(me, other) && !AssignPlan.PickedPair(me, other)) { all = false; break; }
                foreach (var other in ExclCandidates(me))
                {
                    if ((kind >= 0 && (int)other.Team != kind) || AssignPlan.FixedPair(me, other)) continue;
                    me.Excluded.Set(other.Order, !all);
                    other.Excluded.Set(me.Order, !all);
                }
                Changed?.Invoke();
                if (narrow) RefreshSide();
                else Rebuild();
            })
            { Size = new Vector2(w, 0.36f), Color = ChipOff });
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
        var label = new Label(text, 1.35f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = on ? c : Gray, Width = w - 0.2f, MinFontSize = 1f };
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
        var picked = new List<string>();
        var rest = new List<string>();
        foreach (var other in ExclCandidates(me))
        {
            if (AssignPlan.FixedPair(me, other) || AssignPlan.PickedPair(me, other)) picked.Add(other.Name.ToString());
            else rest.Add(other.Name.ToString());
        }
        return RoleSetOpt.Summary(picked, rest, 4);
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
            col.Add(new Label(_settings ? $"{new Text("RoleMenu.NothingMatches").With("q", _queryText)}\n{Tr.Get("RoleMenu.SearchClearHint")}" : new Text("RoleMenu.NoMatchingRoles").ToString(), 1.6f) { Color = SubText, Width = W - 0.4f, TextAlign = TextAlignmentOptions.Center });
        else
            col.Children.Insert(0, new Label(SearchSummary(), 1.3f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
        _editor.Watch(all);
        _editor.Refresh();
        _list = new ScrollView(new Vector2(W, H), col);
        return _list;
    }

    // 検索の結果の頁の先頭に出す件数 (設定画面では本編/MRP/役職ごとの内訳と戻り方も)
    private string SearchSummary()
    {
        int total = _hits[0] + _hits[1] + _hits[2];
        var sb = new System.Text.StringBuilder(new Text("RoleMenu.SearchCount").With("q", _queryText).With("total", total));
        if (!_settings) return sb.ToString();
        Text[] names = { VanillaGame.TabName, SettingsMenu.TabName(false), RoleMenu.TabName };
        string sep = "\n";
        for (int i = 0; i < 3; i++)
        {
            if (_hits[i] == 0) continue;
            sb.Append(sep).Append(new Text("RoleMenu.SearchTabCount").With("tab", names[i]).With("n", _hits[i]));
            sep = Tr.Get("RoleMenu.SearchSeparator");
        }
        sb.Append('\n').Append(Tr.Get("RoleMenu.SearchClearHint"));
        return sb.ToString();
    }

    // ---- 毎フレーム (開いている間だけ) ----

    // カードに当てている間の右クリック = 出現率を 0% と 100% で切り替える (設定画面で値を変えられる時だけ)
    public void Tick()
    {
        if (Touch || !SideEdit || _hover?.Role == null) return;
        if (!Input.GetMouseButtonDown(1) || !Editable) return;
        var c = _hover.Role.Chance;
        OptHistory.Push(c, c.Index);
        c.SetIndex(c.Index > 0 ? 0 : c.Count - 1);
        Roles.TeamSizes.OnEdited(c);
        AfterEdit();
        if (_open == null || _open == _hover) RefreshSide();
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
                if (SideEdit && _side != null) Select(a.Length > 1 ? Find(a[1]) : null);
                else
                {
                    _open = a.Length > 1 ? Find(a[1]) : null;
                    Rebuild();
                }
                break;
            case "sidescroll":
                if (_sideList?.Scroller != null && a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ss))
                    _sideList.Scroller.ScrollPercentY(ss);
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
            case "sort":
                _sort = a.Length > 1 && int.TryParse(a[1], out int so) ? Math.Clamp(so, 0, SortCount - 1) : 0;
                Rebuild();
                break;
            case "tag":
                _filter = a.Length > 1 && int.TryParse(a[1], out int tg) ? Math.Clamp(tg, 0, TagCount) : 0;
                Rebuild();
                break;
            case "fav":
                if (a.Length > 1 && Find(a[1])?.Role is { } fr) RoleFavorites.Toggle(fr);
                Rebuild();
                break;
            case "rebuild":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Rebuild();
                return $"ms={sw.Elapsed.TotalMilliseconds:0.0} slots={UiActions.Census()}";
            }
        }
        var sb = new System.Text.StringBuilder($"team={_team} only={_enabledOnly} edit={Editable} open={_open?.Role?.Id ?? _open?.Title.ToString() ?? "-"} hover={_hover?.Role?.Id ?? "-"} undo={(OptHistory.Any ? 1 : 0)} hits={_hits[0]}/{_hits[1]}/{_hits[2]} slots={UiActions.Census()} cards:");
        foreach (var (sec, _, values, _, _) in _cards)
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
