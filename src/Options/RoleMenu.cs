using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 設定画面の「役職」タブ。全陣営の役職を 1 本の列に並べる。
// - 役職の行 = 本編の役職設定の行 (人数 ± と出現率 ± が 1 行) の複製。その場で値を変えられる。
// - 役職名を押すと、真下にその役職の細かい設定が開く (別のページへ移らない・スクロール位置はそのまま)。
// - 上の固定の帯 = 検索欄 / 陣営で絞る / 出る役職だけ。検索は打つたびに絞り込み、
//   役職名のほか細かい設定の名前にも当たる (当たった役職は開いて、当たった行を色で示す)。
// - 絞り込みや開け閉めでは行を作り直さず、表示と位置だけを変える。
internal static class RoleMenu
{
    // 1920x1080 の画面で 1 単位 = 180px。RoleLift = 本編の役職の行は数値の行より絵が 0.25 下に出るので、その分上げる
    private const float BandHeight = 1.0f;      // 上の固定の帯の高さ
    private const float SearchHeight = 0.42f, ChipHeight = 0.36f, Gap = 0.1f, Side = 0.15f, CloseClear = 0.3f;
    private const float HeaderX = -0.903f, OptX = -0.05f, RoleX = -0.53f, RoleLift = 0.25f, Z = -2f;
    private const float HeaderStep = 0.55f, RoleStep = 0.55f, OptStep = 0.45f, GroupGap = 0.12f;
    private const int MaskLayer = 20;

    // 行の板 (役職の行の座標で)。左端 -2.02・右端 3.86 = 名前の帯の左端から ± の右端まで
    private const float CardL = -2.02f, CardR = 3.86f, CardH = 0.46f, CardY = -0.296f, Edge = 0.025f, Drop = 0.04f;
    private const float SubL = -1.72f, SubH = 0.38f;  // 細かい設定の行の板 (左端を一段下げる・右端は役職の板と揃える)

    private static readonly Color Highlight = new(1f, 0.88f, 0.4f, 1f);
    private static readonly Color Ink = new(0.04f, 0.04f, 0.06f, 1f);        // 輪郭
    private static readonly Color CardBase = new(0.125f, 0.137f, 0.176f, 1f); // 板の地
    private static readonly Color CardOff = new(0.098f, 0.106f, 0.13f, 1f);   // 出ない役職の板
    private static readonly Team[] Teams = { Team.Crew, Team.Impostor, Team.Neutral };

    // 開き直しても残す状態 (検索語だけは開くたびに空から)
    private static int _team = -1;  // -1 = 全部
    private static bool _enabledOnly;
    private static readonly HashSet<string> Expanded = new();

    private sealed class OptRow
    {
        public Opt Opt;
        public OptionBehaviour Row;
        public string Hay;
    }

    private sealed class RoleRow
    {
        public Assignable Role;
        public RoleOptionSetting Row;
        public string Hay, AllHay;
        public readonly List<OptRow> Opts = new();
        public SpriteRenderer Fill, Accent;  // 行の板・左の色の印
        public TextMeshPro Chevron;
        public bool Hover;
    }

    private sealed class Group
    {
        public Team Team;
        public CategoryHeaderMasked Header;
        public readonly List<OptRow> Plain = new();   // 役職でない設定のまとまり ([Settings] でこの陣営に置いたもの)
        public readonly List<RoleRow> Roles = new();
    }

    private static readonly List<Group> Groups = new();
    private static readonly Dictionary<IntPtr, RoleRow> ByRow = new();
    private static readonly Dictionary<string, RoleRow> ById = new();
    private static readonly List<(PassiveButton btn, int team, bool only, Color color)> Chips = new();
    private static GameOptionsMenu _page;
    private static CategoryHeaderMasked _empty;
    private static SearchBox _search;
    private static string[] _tokens = Array.Empty<string>();
    private static float _topY;
    private static Assignable _described;  // 左の説明の欄に出している役職

    public static bool TryRow(Il2CppObjectBase row, out Assignable role)
    {
        if (ByRow.Count > 0 && ByRow.TryGetValue(row.Pointer, out var r)) { role = r.Role; return true; }
        role = null;
        return false;
    }

    public static Text TabName => new("役職", "Roles");

    public static Text DefaultDescription =>
        new($"役職の人数と出現率。名前を押すと細かい設定が開きます。\n出る役職: {EnabledCount()} / {Registry.Roles.Count}",
            $"Role count and chance. Press a name to open its settings.\nEnabled: {EnabledCount()} / {Registry.Roles.Count}");

    private static int EnabledCount()
    {
        int n = 0;
        foreach (var r in Registry.Roles) if (r.Chance.Value > 0) n++;
        return n;
    }

    public static bool HasContent()
    {
        if (Registry.Roles.Count > 0) return true;
        foreach (var t in Teams)
            if (Registry.SectionsOf(TabOf(t)).Count > 0) return true;
        return false;
    }

    private static Tab TabOf(Team t) => t switch { Team.Impostor => Tab.Impostor, Team.Neutral => Tab.Neutral, _ => Tab.Crew };

    private static Text TeamName(Team t) => t switch
    {
        Team.Impostor => new Text("インポスター", "Impostor"),
        Team.Neutral => new Text("第三陣営", "Neutral"),
        _ => new Text("クルー", "Crewmate"),
    };

    private static Color TeamColor(Team t) => t switch
    {
        Team.Impostor => new Color(1f, 0.35f, 0.35f),
        Team.Neutral => new Color(1f, 0.7f, 0.25f),
        _ => new Color(0.55f, 0.85f, 1f),
    };

    public static void Forget()
    {
        if (_search != null) SearchBox.Forget(_search);
        _search = null;
        Groups.Clear();
        ByRow.Clear();
        ById.Clear();
        Chips.Clear();
        _page = null;
        _empty = null;
        _described = null;
        _tokens = Array.Empty<string>();
    }

    // ---- 組み立て ----

    public static void Build(GameOptionsMenu m, GameSettingMenu menu)
    {
        Forget();
        _page = m;
        BuildBand(m, menu);

        var origin = menu.RoleSettingsTab ? menu.RoleSettingsTab.roleOptionSettingOrigin : null;
        var roleOptions = GameOptionsManager.Instance.CurrentGameOptions.RoleOptions;
        var anyRole = RoleManager.Instance.AllRoles[0];

        foreach (var team in Teams)
        {
            var g = new Group { Team = team, Header = MakeHeader(m, TeamColor(team)) };
            foreach (var sec in Registry.SectionsOf(TabOf(team)))
            {
                if (sec.Role != null) continue;
                foreach (var opt in sec.Opts) g.Plain.Add(MakeOpt(m, opt, TeamColor(team)));
            }
            foreach (var sec in Registry.SectionsOf(TabOf(team)))
            {
                if (sec.Role == null || !origin) continue;
                var r = new RoleRow { Role = sec.Role, Hay = RoleSearch.Haystack(sec.Role.Name) + "\n" + RoleSearch.Normalize(sec.Role.Reading) };
                r.Row = Object.Instantiate(origin, m.settingsContainer);
                r.Row.name = "MrpRole_" + sec.Role.Id;
                r.Row.SetRole(roleOptions, anyRole, MaskLayer);
                r.Row.SetClickMask(m.ButtonClickMask);
                AddNameButton(r, m);
                StyleRole(r, m);
                var all = new System.Text.StringBuilder(r.Hay);
                foreach (var opt in sec.Opts)
                {
                    if (opt == sec.Role.Chance || opt == sec.Role.Count) continue;
                    var o = MakeOpt(m, opt, RoleDisplay.ParseColor(sec.Role.Color));
                    r.Opts.Add(o);
                    all.Append('\n').Append(o.Hay);
                }
                r.AllHay = all.ToString();
                ByRow[r.Row.Pointer] = r;
                ById[sec.Role.Id] = r;
                m.Children.Add(r.Row);
                foreach (var o in r.Opts) m.Children.Add(o.Row);
                RefreshRole(r);
                g.Roles.Add(r);
            }
            if (g.Plain.Count == 0 && g.Roles.Count == 0) { Object.Destroy(g.Header.gameObject); continue; }
            foreach (var o in g.Plain) m.Children.Add(o.Row);
            Groups.Add(g);
        }

        _empty = MakeHeader(m, new Color(0.6f, 0.6f, 0.6f));
        _empty.Title.text = new Text("当てはまる役職がありません", "No matching roles");
        // 並ぶ字を先にフォントへ入れておく (スクロールして初めて出た字を作る時の引っかかりを避ける)
        var chars = new System.Text.StringBuilder("0123456789/ ›");
        foreach (var g in Groups)
        {
            chars.Append(TeamName(g.Team).ToString());
            foreach (var r in g.Roles) { chars.Append(r.Role.Name); foreach (var o in r.Opts) chars.Append(o.Opt.Label.ToString()); }
            foreach (var o in g.Plain) chars.Append(o.Opt.Label.ToString());
        }
        MenuFont.Prepare(chars.ToString());
        Relayout(true);
    }

    private static OptRow MakeOpt(GameOptionsMenu m, Opt opt, Color c)
    {
        var row = SettingsMenu.MakeRow(m, opt);
        var o = new OptRow { Opt = opt, Row = row, Hay = RoleSearch.Haystack(opt.Label) };
        StyleOpt(o, c);
        return o;
    }

    // 陣営の見出し = 陣営の色の太字 + 左の色の印 + 右へ伸びる線。本編の見出しの帯の絵は消す
    private static CategoryHeaderMasked MakeHeader(GameOptionsMenu m, Color c)
    {
        var h = Object.Instantiate(m.categoryHeaderOrigin, m.settingsContainer);
        h.SetHeader(StringNames.RolesCategory, MaskLayer);
        h.transform.localScale = new Vector3(0.63f, 0.63f, 1f);
        if (h.Background) h.Background.enabled = false;
        if (h.Divider)
        {
            h.Divider.color = new Color(c.r, c.g, c.b, 0.55f);
        }
        var title = h.Title;
        if (title)
        {
            MenuFont.Apply(title, MaskLayer, 0.2f);
            title.enableWordWrapping = false;
            var tp = title.transform.localPosition;
            title.transform.localPosition = new Vector3(tp.x + 0.4f, tp.y, tp.z);
            // 印は見出しの座標 (0.63 倍) で置くので、世界の大きさ ÷ 0.63
            MenuArt.Panel(h.transform, "MrpHeaderMark", 0.05f, new Vector2(0.1f, 0.55f),
                new Vector3((CardL + 0.12f - HeaderX) / 0.63f, tp.y, tp.z), c, MenuArt.Masked(h.Background));
        }
        return h;
    }

    // 役職の行の見た目: 本編の名前の帯を消して、行いっぱいの角丸の板 (輪郭 + 地 + 一段の影) と左の色の印を敷く。
    // 名前は左寄せの太字。開け閉めの印は「›」を回して見せる
    private static void StyleRole(RoleRow r, GameOptionsMenu m)
    {
        var row = r.Row;
        var label = row.labelSprite;
        var mat = MenuArt.Masked(label);
        if (label) label.enabled = false;  // 押す所 (当たり判定) は残す
        var divider = row.transform.Find("Divider");
        if (divider) { var ds = divider.GetComponent<SpriteRenderer>(); if (ds) ds.color = new Color(1f, 1f, 1f, 0.12f); }

        float cx = (CardL + CardR) / 2f - RoleX, w = CardR - CardL;
        var t = row.transform;
        MenuArt.Panel(t, "MrpCardShadow", 0.12f, new Vector2(w + Edge * 2f, CardH + Edge * 2f), new Vector3(cx, CardY - Drop, 5.7f), new Color(0f, 0f, 0f, 0.55f), mat);
        MenuArt.Panel(t, "MrpCardEdge", 0.12f, new Vector2(w + Edge * 2f, CardH + Edge * 2f), new Vector3(cx, CardY, 5.45f), Ink, mat);
        r.Fill = MenuArt.Panel(t, "MrpCard", 0.1f, new Vector2(w, CardH), new Vector3(cx, CardY, 5.2f), CardBase, mat);
        r.Accent = MenuArt.Panel(t, "MrpCardMark", 0.035f, new Vector2(0.075f, 0.3f), new Vector3(CardL + 0.12f - RoleX, CardY, 3f), Color.white, mat);

        var title = row.titleText;
        if (title)
        {
            MenuFont.Apply(title, MaskLayer);
            title.alignment = TextAlignmentOptions.Left;
            title.enableWordWrapping = false;
            title.enableAutoSizing = false;
            title.fontSize = 1.6f;
            var rt = title.rectTransform;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(1.75f, CardH);
            title.transform.localPosition = new Vector3(CardL + 0.44f - RoleX, CardY + 0.01f, title.transform.localPosition.z);

            // 開け閉めの印。字は名前と同じ部品を複製して使う (切り抜きのマテリアルごと引き継ぐ)。細かい設定が無い役職では隠す
            var chev = Object.Instantiate(title, t);
            chev.name = "MrpChevron";
            chev.alignment = TextAlignmentOptions.Center;
            chev.fontSize = 1.9f;
            chev.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            chev.rectTransform.sizeDelta = new Vector2(0.2f, 0.3f);
            chev.transform.localPosition = new Vector3(CardL + 0.31f - RoleX, CardY + 0.01f, title.transform.localPosition.z);
            chev.text = "›";
            r.Chevron = chev;
        }
    }

    // 細かい設定の行: 本編の名前の帯を消して、一段下げた角丸の板 (輪郭 + 役職の色を少し混ぜた地) を敷く。名前は左寄せ。
    // 行は縮めて置かれるので、板の大きさと位置は行の座標 (÷ 縮尺) で書く
    private static void StyleOpt(OptRow o, Color c)
    {
        if (!o.Row) return;
        var t = o.Row.transform;
        float k = 1f / t.localScale.x;
        var bg = t.Find("LabelBackground");
        var bgSprite = bg ? bg.GetComponent<SpriteRenderer>() : null;
        var mat = MenuArt.Masked(bgSprite);
        if (bgSprite) bgSprite.enabled = false;
        float y = bg ? bg.localPosition.y : 0f;
        float w = CardR - SubL, cx = ((SubL + CardR) / 2f - OptX) * k;
        MenuArt.Panel(t, "MrpOptEdge", 0.09f * k, new Vector2((w + Edge * 2f) * k, (SubH + Edge * 2f) * k), new Vector3(cx, y, 3.2f), Ink, mat);
        MenuArt.Panel(t, "MrpOpt", 0.08f * k, new Vector2(w * k, SubH * k), new Vector3(cx, y, 3f), Mix(CardOff, c, 0.1f), mat);

        var title = OptTitle(o);
        if (!title) return;
        MenuFont.Apply(title, MaskLayer);
        title.alignment = TextAlignmentOptions.Left;
        title.enableWordWrapping = false;
        title.enableAutoSizing = false;
        title.fontSize = 2.4f;
        title.color = OptText;
        var rt = title.rectTransform;
        rt.pivot = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(2.6f * k, SubH * k);
        title.transform.localPosition = new Vector3((SubL + 0.2f - OptX) * k, y + 0.01f * k, title.transform.localPosition.z);
    }

    private static readonly Color OptText = new(0.86f, 0.88f, 0.93f, 1f);

    private static TextMeshPro OptTitle(OptRow o)
    {
        var num = o.Row.TryCast<NumberOption>();
        if (num != null) return num.TitleText;
        var tog = o.Row.TryCast<ToggleOption>();
        return tog != null ? tog.TitleText : null;
    }

    private static Color Mix(Color a, Color b, float t) =>
        new(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);

    // 役職名の帯を押せるようにする (押すと細かい設定を開け閉めする)
    private static void AddNameButton(RoleRow r, GameOptionsMenu m)
    {
        var label = r.Row.labelSprite;
        if (!label) return;
        var go = label.gameObject;
        var col = go.AddComponent<BoxCollider2D>();
        col.size = label.drawMode != SpriteDrawMode.Simple ? label.size : (Vector2)label.sprite.bounds.size;
        var pb = go.AddComponent<PassiveButton>();
        pb.Colliders = new Collider2D[] { col };
        pb.ClickMask = m.ButtonClickMask;
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnClick.AddListener(Cached(NameClicks, r.Role.Id, id => () => OnNameClick(id)));
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOver.AddListener(Cached(NameOvers, r.Role.Id, id => () => OnNameHover(id, true)));
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOut.AddListener(Cached(NameOuts, r.Role.Id, id => () => OnNameHover(id, false)));
    }

    // 上の固定の帯。スクロールする範囲 (マスク・つかむ所) を帯の高さだけ下から詰める
    private static void BuildBand(GameOptionsMenu m, GameSettingMenu menu)
    {
        var scroller = m.scrollBar;
        var maskArea = scroller.transform.Find("MaskArea");
        var maskBg = scroller.transform.Find("MaskBg");
        var hitbox = scroller.transform.Find("Hitbox");
        var areaSprite = maskArea ? maskArea.GetComponent<SpriteRenderer>() : null;
        if (!areaSprite) { _topY = 2f; return; }

        var b = areaSprite.bounds;
        Vector3 min = m.transform.InverseTransformPoint(b.min), max = m.transform.InverseTransformPoint(b.max);
        float left = min.x, right = max.x, top = max.y, height = max.y - min.y;

        foreach (var t in new[] { maskArea, maskBg })
        {
            if (!t) continue;
            var s = t.localScale;
            float k = (height - BandHeight) / height;
            t.localScale = new Vector3(s.x, s.y * k, s.z);
            t.localPosition -= new Vector3(0f, BandHeight / 2f, 0f);
        }
        var hit = hitbox ? hitbox.GetComponent<BoxCollider2D>() : null;
        if (hit)
        {
            hit.size = new Vector2(hit.size.x, hit.size.y - BandHeight);
            hit.offset = new Vector2(hit.offset.x, hit.offset.y - BandHeight / 2f);
        }
        _topY = 2f - BandHeight;

        float width = right - left - Side * 2f;
        float x0 = left + Side;
        float searchW = width - CloseClear; // 右上の閉じるボタンに掛からないように
        _search = SearchBox.Create(m.transform, new Vector3(x0 + searchW / 2f, top - 0.06f - SearchHeight / 2f, -5f), searchW, SearchHeight,
            new Text("役職・設定の名前で検索", "Search roles and settings"), OnQuery);

        // 陣営で絞るボタン (全部・クルー・インポスター・第三陣営) と「出る役職だけ」
        var chipDefs = new List<(Text label, Color color, int team, bool only)>
        {
            (new Text("全部", "All"), new Color(0.75f, 0.6f, 1f), -1, false),
        };
        foreach (var t in Teams) chipDefs.Add((TeamName(t), TeamColor(t), (int)t, false));
        chipDefs.Add((new Text("出る役職だけ", "Enabled only"), new Color(0.6f, 0.9f, 0.55f), 0, true));
        float cw = (width - Gap * (chipDefs.Count - 1)) / chipDefs.Count;
        float cy = top - 0.06f - SearchHeight - 0.08f - ChipHeight / 2f;
        for (int i = 0; i < chipDefs.Count; i++)
        {
            var d = chipDefs[i];
            var chip = MakeChip(menu.GameSettingsButton, m.transform, new Vector3(x0 + cw / 2f + i * (cw + Gap), cy, -5f), cw, ChipHeight, d.label, d.color);
            if (!chip) continue;
            string key = d.only ? "only" : "team" + d.team;
            int team = d.team;
            bool only = d.only;
            chip.OnClick.AddListener(Cached(ChipClicks, key, _ => () => OnChip(team, only)));
            Chips.Add((chip, d.team, d.only, d.color));
        }
        RefreshChips();
    }

    // 絞り込みのボタン = 角の丸い札。本編のボタンを複製し、押した時の切り替え (3 つの絵) はそのまま使って絵だけ差し替える。
    // 選ばれていない = 暗い地 + 色の輪郭 + 色の字 / 指を乗せる = 地に色が少し乗る / 選ばれている = 色の地 + 暗い字
    private static PassiveButton MakeChip(PassiveButton template, Transform parent, Vector3 pos, float w, float h, Text text, Color c)
    {
        if (!template) return null;
        var btn = Object.Instantiate(template, parent);
        btn.name = "MrpChip";
        var t = btn.transform;
        // 親の座標で 1 単位 = 1 単位にする (本編のボタンは縦横で違う倍率が掛かっている)
        t.localScale = Vector3.one;
        t.localPosition = pos;
        float radius = h / 2f;
        var col = btn.GetComponent<BoxCollider2D>();
        if (col) { col.size = new Vector2(w, h); col.offset = Vector2.zero; }

        var dark = new Color(0.11f, 0.12f, 0.15f, 1f);
        void Skin(GameObject go, Color fill)
        {
            if (!go) return;
            go.transform.localPosition = new Vector3(0f, 0f, go.transform.localPosition.z);
            go.transform.localScale = Vector3.one;
            var sr = go.GetComponent<SpriteRenderer>();
            if (!sr) return;
            sr.sprite = MenuArt.Round(radius - Edge);
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(w - Edge * 2f, h - Edge * 2f);
            sr.color = fill;
        }
        Skin(btn.inactiveSprites, dark);
        Skin(btn.activeSprites, Mix(dark, c, 0.3f));
        Skin(btn.selectedSprites, c);
        // 輪郭は 3 つの絵の後ろに 1 枚だけ (色は RefreshChips で変える)
        var edge = MenuArt.Panel(t, "MrpChipEdge", radius, new Vector2(w, h), new Vector3(0f, 0f, 0.5f), c, null);
        MenuArt.Panel(t, "MrpChipShadow", radius, new Vector2(w, h), new Vector3(0f, -Drop, 0.6f), new Color(0f, 0f, 0f, 0.5f), null);
        edge.sortingOrder = -1;

        var label = btn.GetComponentInChildren<TextMeshPro>(true);
        if (label)
        {
            var tr = label.GetComponent<TextTranslatorTMP>();
            if (tr) Object.Destroy(tr);
            // 字は札の真ん中へ (本編のボタンでは字が絵の入れ物の子にあることがあるので、親をボタンに付け替える)
            label.transform.SetParent(t, false);
            label.transform.localScale = Vector3.one;
            label.transform.localPosition = new Vector3(0f, 0.005f, -1f);
            MenuFont.Apply(label);
            var rt = label.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w - 0.16f, h);
            label.enableWordWrapping = false;
            label.enableAutoSizing = true;
            label.fontSizeMin = 0.8f;
            label.fontSizeMax = 1.5f;
            label.alignment = TextAlignmentOptions.Center;
            label.text = text;
        }
        // 本編の絵の入れ物で 3 つの絵以外の物 (字の位置合わせ用の絵) は消す
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var go = sr.gameObject;
            if (go == btn.inactiveSprites || go == btn.activeSprites || go == btn.selectedSprites || sr == edge || go.name == "MrpChipShadow") continue;
            sr.enabled = false;
        }
        btn.OnMouseOver = new UnityEvent();
        btn.OnMouseOut = new UnityEvent();
        btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        return btn;
    }

    // il2cpp 側へ渡す処理は、押す所ごとに 1 つだけ作って使い回す (渡すたびに解放されない参照が増えるため)。
    // 捕まえるのは名前と番号だけ
    private static readonly Dictionary<string, UnityAction> NameClicks = new(), NameOvers = new(), NameOuts = new(), ChipClicks = new();

    private static UnityAction Cached(Dictionary<string, UnityAction> cache, string key, Func<string, Action> make)
    {
        if (!cache.TryGetValue(key, out var a)) cache[key] = a = (UnityAction)make(key);
        return a;
    }

    // ---- 操作 ----

    private static void OnQuery(string q)
    {
        _tokens = RoleSearch.Tokens(q);
        Relayout(true);
    }

    private static void OnChip(int team, bool only)
    {
        if (only) _enabledOnly = !_enabledOnly;
        else _team = team;
        RefreshChips();
        Relayout(true);
    }

    private static void RefreshChips()
    {
        foreach (var (btn, team, only, c) in Chips)
        {
            if (!btn) continue;
            bool on = only ? _enabledOnly : _team == team;
            btn.SelectButton(on);
            var label = btn.GetComponentInChildren<TextMeshPro>(true);
            if (label) label.color = on ? new Color(0.08f, 0.08f, 0.1f, 1f) : Mix(c, Color.white, 0.2f);
            var edge = btn.transform.Find("MrpChipEdge");
            var sr = edge ? edge.GetComponent<SpriteRenderer>() : null;
            if (sr) sr.color = on ? Mix(c, Color.white, 0.45f) : new Color(c.r * 0.75f, c.g * 0.75f, c.b * 0.75f, 1f);
        }
    }

    private static void OnNameClick(string id)
    {
        if (!ById.TryGetValue(id, out var r)) return;
        if (r.Opts.Count > 0 && _tokens.Length == 0)
        {
            if (!Expanded.Remove(id)) Expanded.Add(id);
            Relayout(false);
        }
        Describe(r.Role);
    }

    private static void OnNameHover(string id, bool over)
    {
        if (!ById.TryGetValue(id, out var r)) return;
        r.Hover = over;
        if (r.Fill) r.Fill.color = CardColor(r);
    }

    private static void Describe(Assignable role)
    {
        _described = role;
        var menu = GameSettingMenu.Instance;
        if (!menu || !menu.MenuDescriptionText) return;
        menu.MenuDescriptionText.text = $"{role.ColoredName}\n{role.Description}";
    }

    // 人数・出現率の ± (本編の処理の代わり)。端で止まる
    public static void Step(RoleOptionSetting row, bool chance, int delta)
    {
        if (!ByRow.TryGetValue(row.Pointer, out var r)) return;
        (chance ? r.Role.Chance : r.Role.Count).Step(delta);
        RefreshRole(r);
        // 「出る役職だけ」で 0% にした行は、その場では消さない (押し間違いをすぐ戻せるように)。次に並べ直す時に消える
        RefreshHeaders();
        // 役職の説明を見ている間はそのまま (数値を変えるたびに説明が消えないように)
        if (_described != null) return;
        var menu = GameSettingMenu.Instance;
        if (menu && menu.MenuDescriptionText) menu.MenuDescriptionText.text = DefaultDescription;
    }

    // 板の地: 出る役職は役職の色を少し混ぜる・出ない役職は暗く・指を乗せると明るく
    private static Color CardColor(RoleRow r)
    {
        Color c = RoleDisplay.ParseColor(r.Role.Color);
        bool off = r.Role.Chance.Value == 0;
        var bg = off ? Mix(CardOff, c, 0.06f) : Mix(CardBase, c, 0.16f);
        return r.Hover ? Mix(bg, c, 0.14f) : bg;
    }

    private static void RefreshRole(RoleRow r)
    {
        var row = r.Row;
        if (!row) return;
        var role = r.Role;
        bool off = role.Chance.Value == 0;
        bool hit = _tokens.Length > 0 && RoleSearch.AnyIn(_tokens, r.Hay);
        Color c = RoleDisplay.ParseColor(role.Color);
        row.titleText.text = role.Name;
        row.titleText.color = hit ? Highlight : off ? new Color(0.62f, 0.63f, 0.68f, 1f) : Mix(c, Color.white, 0.3f);
        if (r.Fill) r.Fill.color = CardColor(r);
        if (r.Accent) r.Accent.color = off ? new Color(c.r * 0.45f, c.g * 0.45f, c.b * 0.45f, 1f) : c;
        if (r.Chevron)
        {
            bool has = r.Opts.Count > 0;
            if (r.Chevron.gameObject.activeSelf != has) r.Chevron.gameObject.SetActive(has);
            r.Chevron.transform.localEulerAngles = new Vector3(0f, 0f, IsOpen(r) ? -90f : 0f);
            r.Chevron.color = row.titleText.color;
        }
        row.countText.text = role.Count.Display();
        row.chanceText.text = role.Chance.Display();
        row.chanceText.color = role.Chance.Value >= 100 ? Highlight : off ? new Color(0.6f, 0.6f, 0.6f, 1f) : Color.white;
        row.CountMinusBtn?.SetInteractable(role.Count.Index > 0);
        row.CountPlusBtn?.SetInteractable(role.Count.Index < role.Count.Count - 1);
        row.ChanceMinusBtn?.SetInteractable(role.Chance.Index > 0);
        row.ChancePlusBtn?.SetInteractable(role.Chance.Index < role.Chance.Count - 1);
    }

    private static bool IsOpen(RoleRow r)
    {
        if (r.Opts.Count == 0) return false;
        if (_tokens.Length == 0) return Expanded.Contains(r.Role.Id);
        foreach (var o in r.Opts)
            if (RoleSearch.AnyIn(_tokens, o.Hay)) return true;
        return false;
    }

    private static bool Matches(string hay)
    {
        foreach (var t in _tokens)
            if (!hay.Contains(t, StringComparison.Ordinal)) return false;
        return true;
    }

    private static void RefreshHeaders()
    {
        foreach (var g in Groups)
        {
            if (!g.Header) continue;
            int on = 0;
            foreach (var r in g.Roles) if (r.Role.Chance.Value > 0) on++;
            string name = $"<color=#{ColorUtility.ToHtmlStringRGB(TeamColor(g.Team))}>{TeamName(g.Team)}</color>";
            g.Header.Title.text = g.Roles.Count > 0 ? $"{name}  <size=70%>{on} / {g.Roles.Count}</size>" : name;
        }
    }

    // 見せる行と位置を決め直す。行は作り直さない
    private static void Relayout(bool toTop)
    {
        if (!_page) return;
        bool searching = _tokens.Length > 0;
        float y = _topY;
        bool any = false;

        foreach (var g in Groups)
        {
            bool teamShown = searching || _team < 0 || _team == (int)g.Team;
            int shown = 0;
            foreach (var o in g.Plain) if (teamShown && (!searching || Matches(o.Hay))) shown++;
            foreach (var r in g.Roles) if (teamShown && RoleShown(r, searching)) shown++;

            bool groupOn = shown > 0;
            if (g.Header)
            {
                g.Header.gameObject.SetActive(groupOn);
                if (groupOn)
                {
                    g.Header.transform.localPosition = new Vector3(HeaderX, y, Z);
                    y -= HeaderStep;
                }
            }
            foreach (var o in g.Plain)
            {
                bool on = groupOn && (!searching || Matches(o.Hay));
                Place(o, on, ref y, searching);
            }
            foreach (var r in g.Roles)
            {
                bool on = groupOn && RoleShown(r, searching);
                if (r.Row) r.Row.gameObject.SetActive(on);
                if (on)
                {
                    r.Row.transform.localPosition = new Vector3(RoleX, y + RoleLift, Z);
                    y -= RoleStep;
                }
                bool open = on && IsOpen(r);
                foreach (var o in r.Opts) Place(o, open, ref y, searching);
                RefreshRole(r);
            }
            if (groupOn) { y -= GroupGap; any = true; }
        }

        if (_empty)
        {
            _empty.gameObject.SetActive(!any);
            if (!any) { _empty.transform.localPosition = new Vector3(HeaderX, y, Z); y -= HeaderStep; }
        }
        RefreshHeaders();
        _page.scrollBar.SetYBoundsMax(Mathf.Max(0f, -y - 1.65f));
        if (toTop) _page.scrollBar.ScrollPercentY(0f);
    }

    private static bool RoleShown(RoleRow r, bool searching)
    {
        if (searching) return Matches(r.AllHay);
        return !_enabledOnly || r.Role.Chance.Value > 0;
    }

    private static void Place(OptRow o, bool on, ref float y, bool searching)
    {
        if (!o.Row) return;
        o.Row.gameObject.SetActive(on);
        if (!on) return;
        o.Row.transform.localPosition = new Vector3(OptX, y, Z);
        y -= OptStep;
        bool hit = searching && RoleSearch.AnyIn(_tokens, o.Hay);
        var title = OptTitle(o);
        if (title) title.color = hit ? Highlight : OptText;
    }

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        if (!_page) return "role tab not built";
        switch (a.Length > 0 ? a[0] : "dump")
        {
            case "q":
                if (_search == null) return "no search box";
                _search.Type(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "");
                break;
            case "team":
                OnChip(a.Length > 1 && int.TryParse(a[1], out int t) ? t : -1, false);
                break;
            case "only":
                OnChip(0, true);
                break;
            case "scroll":
                if (a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sp))
                    _page.scrollBar.ScrollPercentY(sp);
                break;
            case "open":
                if (a.Length > 1) OnNameClick(a[1]);
                break;
            case "press":
                // press <Id> <count|chance> <+1|-1>
                if (a.Length < 4 || !ById.TryGetValue(a[1], out var pr) || !int.TryParse(a[3], out int d)) return "press <Id> <count|chance> <+1|-1>";
                if (a[2] == "count") { if (d > 0) pr.Row.IncreaseCount(); else pr.Row.DecreaseCount(); }
                else { if (d > 0) pr.Row.IncreaseChance(); else pr.Row.DecreaseChance(); }
                break;
        }
        var sb = new System.Text.StringBuilder();
        sb.Append($"search={(_search != null ? "'" + _search.Query + "'" : "none")} team={_team} only={_enabledOnly} rows:");
        foreach (var g in Groups)
        {
            if (g.Header && g.Header.gameObject.activeSelf) sb.Append($" [{g.Team}]");
            foreach (var o in g.Plain) if (o.Row && o.Row.gameObject.activeSelf) sb.Append($" {o.Opt.Key}");
            foreach (var r in g.Roles)
            {
                if (!r.Row || !r.Row.gameObject.activeSelf) continue;
                sb.Append($" {r.Role.Id}({r.Row.countText.text},{r.Row.chanceText.text}{(IsOpen(r) ? ",open" : "")})");
                foreach (var o in r.Opts) if (o.Row && o.Row.gameObject.activeSelf) sb.Append($" >{o.Opt.Key.Substring(o.Opt.Key.IndexOf('.') + 1)}");
            }
        }
        if (_empty && _empty.gameObject.activeSelf) sb.Append(" (empty)");
        return sb.ToString();
    }
}

// MRP の役職の行は本編の役職の値を通さず、MRP の出現率・人数を書き換える
[HarmonyPatch(typeof(RoleOptionSetting))]
internal static class RoleOptionSettingPatch
{
    [HarmonyPatch(nameof(RoleOptionSetting.IncreaseCount)), HarmonyPrefix]
    public static bool IncreaseCount(RoleOptionSetting __instance) => Step(__instance, false, 1);

    [HarmonyPatch(nameof(RoleOptionSetting.DecreaseCount)), HarmonyPrefix]
    public static bool DecreaseCount(RoleOptionSetting __instance) => Step(__instance, false, -1);

    [HarmonyPatch(nameof(RoleOptionSetting.IncreaseChance)), HarmonyPrefix]
    public static bool IncreaseChance(RoleOptionSetting __instance) => Step(__instance, true, 1);

    [HarmonyPatch(nameof(RoleOptionSetting.DecreaseChance)), HarmonyPrefix]
    public static bool DecreaseChance(RoleOptionSetting __instance) => Step(__instance, true, -1);

    [HarmonyPatch(nameof(RoleOptionSetting.UpdateValuesAndText)), HarmonyPrefix]
    public static bool UpdateValuesAndText(RoleOptionSetting __instance) => !RoleMenu.TryRow(__instance, out _);

    private static bool Step(RoleOptionSetting row, bool chance, int delta)
    {
        if (!RoleMenu.TryRow(row, out _)) return true;
        RoleMenu.Step(row, chance, delta);
        return false;
    }
}
