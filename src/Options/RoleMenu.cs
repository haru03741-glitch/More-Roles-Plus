using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Builtin;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 設定画面の「役職」タブ。全陣営の役職を 1 本の列に並べる。
// - 役職の行 = 本編の役職設定の行 (人数 ± と出現率 ± が 1 行) の複製。その場で値を変えられる。
// - 役職名を押すと、真下にその役職の細かい設定が開く (別のページへ移らない・スクロール位置はそのまま)。
// - 上の固定の帯 = 陣営で絞る / 出る役職だけ。
// - 設定画面の検索 (左の欄・SettingsMenu) の結果もこのタブに出す。検索の間は本編のゲーム設定と MRP 全般の項目も
//   見出しごとに並べ、役職名のほか細かい設定の名前にも当たる (当たった役職は開いて、当たった行を色で示す)。
// - 絞り込みや開け閉めでは行を作り直さず、表示と位置だけを変える。
internal static class RoleMenu
{
    // 1920x1080 の画面で 1 単位 = 180px。RoleLift = 本編の役職の行は数値の行より絵が 0.25 下に出るので、その分上げる
    private const float ChipHeight = 0.36f, Gap = 0.1f, Side = 0.15f;
    private const float RowGap = 0.08f;
    private const float BandHeight = 0.06f + ChipHeight * 3f + RowGap * 2f + 0.1f; // 上の固定の帯の高さ (絞り込みの段 + まとめて変える段 2 つ)
    private const float RepeatEvery = 0.07f; // ± を押したままにした時に繰り返す間隔 (秒)
    private const long HoldDelayMs = 400;    // 押してから繰り返し始めるまで
    private const float HeaderX = -0.903f, OptX = -0.05f, RoleX = -0.53f, RoleLift = 0.25f, Z = -2f;
    private const float HeaderStep = 0.55f, RoleStep = 0.55f, OptStep = 0.45f, GroupGap = 0.12f;
    private const int MaskLayer = 20;

    // 行の板 (役職の行の座標で)。左端 -2.02・右端 3.86 = 名前の帯の左端から ± の右端まで
    private const float CardL = -2.02f, CardR = 3.86f, CardH = 0.46f, CardY = -0.296f, Edge = 0.025f, Drop = 0.04f;
    private const float SubL = -1.72f, SubH = 0.38f;  // 細かい設定の行の板 (左端を一段下げる・右端は役職の板と揃える)
    private const float OptTitleW = 1.8f;              // 細かい設定の名前の幅 (名前の左端から値の欄の左端まで)

    private static readonly Color Highlight = new(1f, 0.88f, 0.4f, 1f);
    private static readonly Color Ink = new(0.04f, 0.04f, 0.06f, 1f);        // 輪郭
    private static readonly Color CardBase = new(0.125f, 0.137f, 0.176f, 1f); // 板の地
    private static readonly Color CardOff = new(0.098f, 0.106f, 0.13f, 1f);   // 出ない役職の板
    private static readonly Team[] Teams = { Team.Crew, Team.Impostor, Team.Neutral };
    private const int AddonKind = 3;  // Group.Kind と絞り込みの番号: 0〜2 = Team、3 = アドオン
    private static readonly Color AddonColor = new(0.72f, 0.5f, 0.95f);

    // 開き直しても残す状態 (検索語だけは開くたびに空から)
    private static int _team = -1;  // -1 = 全部
    private static bool _enabledOnly;
    private static readonly HashSet<string> Expanded = new();

    // まとめて変える前の出現率と人数 (「元に戻す」で 1 回分だけ戻す)。画面を閉じたら捨てる
    private static List<(Assignable role, int chance, int count)> _undo;

    // まとめて変える値 (± で選ぶ)。開き直しても残す
    private static int _bulkChance = 50, _bulkCount = 1;

    // まとめて変える札の番号
    private const int ActZero = 0, ActFull = 1, ActUndo = 2, ActChanceTo = 3, ActCountTo = 4,
        ActChanceDown = 5, ActChanceUp = 6, ActCountDown = 7, ActCountUp = 8;

    // 一緒に出ない役職の札 (開いた役職の下に並ぶ)。見出しの行 (細かい設定の行と同じ高さ) を押すと札が開く
    private const int ExclColumns = 4;
    private const float ExclChipH = 0.3f, ExclGap = 0.07f, ExclPad = 0.12f, ExclHeadH = SubH;
    private static readonly HashSet<string> ExclOpen = new();  // 札を開いている役職 (開き直しても残す)

    // ホイール・右クリックの相手 = 指が乗っている役職の行と所 (0 = 名前・1 = 人数の ±・2 = 出現率の ±)
    private static RoleRow _hoverRow;
    private static int _hoverZone;
    private static float _wheelSpeed = -1f;  // 乗っている間は一覧のホイールを止める (元の速さ)
    private static long _pressAt;            // ± を押した時刻 (ms)
    private static readonly bool Touch = OperatingSystem.IsAndroid();

    private sealed class OptRow
    {
        public Opt Opt;
        public OptionBehaviour Row;
        public string Hay;  // 項目の名前 (当たった行の色に使う)
        public string Find; // 探す対象 (検索だけの組では見出しの名前も入れる)
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
        public ExclPanel Excl;  // 最初に開いた時に作る

        // 一緒に出ない役職を選べる (幽霊になってから付く役職とアドオンは選べない)
        public RoleBase Picker => Role is RoleBase rb && rb.Excluded != null ? rb : null;
        public bool HasDetail => Opts.Count > 0 || Picker != null;
    }

    private sealed class ExclPanel
    {
        public GameObject Root;
        public float FullHeight;  // 札を開いた時の高さ (閉じている時は ExclHeadH)
        public SpriteRenderer Fill, Edge;
        public Color FillColor;
        public TextMeshPro Title, Chevron;
        public string TitleText;  // 書いた見出しの文 (変わった時だけ書く)
        public bool Open, Hover;
        public readonly List<ExclChip> Chips = new();
    }

    private sealed class ExclChip
    {
        public RoleBase Other;
        public PassiveButton Button;
        public SpriteRenderer Fill, Edge;
        public TextMeshPro Label;
        public bool Hover;
    }

    private sealed class Group
    {
        public int Kind;          // 陣営の組: 0〜2 = Team、3 = アドオン。検索だけの組は -1
        public Text Title;
        public Color Color;
        public int Area;          // 検索の件数のまとめ: 0 = ゲーム設定・1 = MRP 全般・2 = 役職
        public bool SearchOnly;   // 検索している間だけ出す (本編のゲーム設定・MRP 全般)
        public CategoryHeaderMasked Header;
        public readonly List<OptRow> Plain = new();   // 役職でない設定のまとまり ([Settings] でこの陣営に置いたもの)
        public readonly List<RoleRow> Roles = new();
    }

    private static readonly List<Group> Groups = new();
    private static readonly Dictionary<IntPtr, RoleRow> ByRow = new();
    private static readonly Dictionary<string, RoleRow> ById = new();
    private static readonly List<(PassiveButton btn, int team, bool only, Color color)> Chips = new();
    private static readonly List<(PassiveButton btn, int action, Color color)> Actions = new();  // まとめて変える札 (0 = 0%・1 = 100%・2 = 元に戻す)
    private static GameOptionsMenu _page;
    private static CategoryHeaderMasked _empty;
    private static string[] _tokens = Array.Empty<string>();
    private static float _topY;
    private static Assignable _described;  // 左の説明の欄に出している役職

    public static bool TryRow(Il2CppObjectBase row, out Assignable role)
    {
        if (ByRow.Count > 0 && ByRow.TryGetValue(row.Pointer, out var r)) { role = r.Role; return true; }
        role = null;
        return false;
    }

    public static Text TabName => new("RoleMenu.TabName");

    public static Text DefaultDescription =>
        new Text("RoleMenu.Description").With("enabledCount", EnabledCount()).With("count", Registry.Roles.Count);

    private static int EnabledCount()
    {
        int n = 0;
        foreach (var r in Registry.Roles) if (r.Chance.Value > 0) n++;
        return n;
    }

    public static bool HasContent()
    {
        if (Registry.Roles.Count > 0 || Registry.Addons.Count > 0) return true;
        foreach (var t in Teams)
            if (Registry.SectionsOf(TabOf(t)).Count > 0) return true;
        return false;
    }

    private static Tab TabOf(Team t) => t switch { Team.Impostor => Tab.Impostor, Team.Neutral => Tab.Neutral, _ => Tab.Crew };

    private static Text TeamName(Team t) => t switch
    {
        Team.Impostor => new Text("RoleMenu.Impostor"),
        Team.Neutral => new Text("RoleMenu.Neutral"),
        _ => new Text("RoleMenu.Crewmate"),
    };

    private static Color TeamColor(Team t) => t switch
    {
        Team.Impostor => new Color(1f, 0.35f, 0.35f),
        Team.Neutral => new Color(1f, 0.7f, 0.25f),
        _ => new Color(0.55f, 0.85f, 1f),
    };

    private static Tab KindTab(int k) => k == AddonKind ? Tab.Addon : TabOf((Team)k);
    private static Text KindName(int k) => k == AddonKind ? new Text("RoleMenu.Addons") : TeamName((Team)k);
    private static Color KindColor(int k) => k == AddonKind ? AddonColor : TeamColor((Team)k);

    public static void Forget()
    {
        Groups.Clear();
        ByRow.Clear();
        ById.Clear();
        Chips.Clear();
        Actions.Clear();
        _undo = null;
        _hoverRow = null;
        // 同じ画面で作り直す時に、止めたホイールを戻す
        if (_wheelSpeed >= 0f && _page && _page.scrollBar) _page.scrollBar.ScrollWheelSpeed = _wheelSpeed;
        _wheelSpeed = -1f;
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
        VanillaRoles.EnsureSettings();
        BuildBand(m, menu);

        var origin = menu.RoleSettingsTab ? menu.RoleSettingsTab.roleOptionSettingOrigin : null;
        var roleOptions = GameOptionsManager.Instance.CurrentGameOptions.RoleOptions;
        var anyRole = RoleManager.Instance.AllRoles[0];

        // 検索の時だけ出る組: 本編のゲーム設定 (見出しごと)・マップのバリエーション・ゲーム設定に足した組・MRP 全般 (見出しごと)
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
            AddSearchGroup(m, Join(VanillaGame.TabName, sec.Title), GameColor, 0, opts);
        }
        AddSearchGroup(m, Join(VanillaGame.TabName, MapVariants.Title), GameColor, 0, new Opt[] { MapSettings.Mirror });
        foreach (var g in GameTabRows.All()) AddSearchGroup(m, Join(VanillaGame.TabName, g.Title), GameColor, 0, g.Opts);
        foreach (var sec in Registry.SectionsOf(Tab.General))
        {
            var opts = new List<Opt>();
            foreach (var opt in sec.Opts) if (!opt.OffTab) opts.Add(opt);
            AddSearchGroup(m, Join(SettingsMenu.TabName(false), sec.Title), SettingsMenu.TabColor(false), 1, opts);
        }

        for (int kind = 0; kind <= AddonKind; kind++)
        {
            var g = new Group { Kind = kind, Title = KindName(kind), Color = KindColor(kind), Area = 2, Header = MakeHeader(m, KindColor(kind)) };
            foreach (var sec in Registry.SectionsOf(KindTab(kind)))
            {
                if (sec.Role != null) continue;
                foreach (var opt in sec.Opts) if (!opt.OffTab) g.Plain.Add(MakeOpt(m, opt, KindColor(kind)));
            }
            foreach (var sec in Registry.SectionsOf(KindTab(kind)))
            {
                if (sec.Role == null || !origin) continue;
                var r = new RoleRow { Role = sec.Role, Hay = RoleSearch.Haystack(sec.Role.Name) + "\n" + RoleSearch.Normalize(sec.Role.Reading) };
                r.Row = Object.Instantiate(origin, m.settingsContainer);
                r.Row.name = "MrpRole_" + sec.Role.Id;
                r.Row.SetRole(roleOptions, anyRole, MaskLayer);
                r.Row.SetClickMask(m.ButtonClickMask);
                AddNameButton(r, m);
                SetupSteppers(r);
                StyleRole(r, m);
                var all = new System.Text.StringBuilder(r.Hay);
                foreach (var opt in sec.Opts)
                {
                    if (opt == sec.Role.Chance || opt == sec.Role.Count || opt.OffTab) continue;
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
        // 並ぶ字を先にフォントへ入れておく (スクロールして初めて出た字を作る時の引っかかりを避ける)
        var chars = new System.Text.StringBuilder("0123456789/ ›");
        foreach (var g in Groups)
        {
            chars.Append(g.Title.ToString());
            foreach (var r in g.Roles) { chars.Append(r.Role.Name); foreach (var o in r.Opts) chars.Append(o.Opt.Label.ToString()); }
            foreach (var o in g.Plain) chars.Append(o.Opt.Label.ToString());
        }
        MenuFont.Prepare(chars.ToString());
        Relayout(true);
    }

    private static OptRow MakeOpt(GameOptionsMenu m, Opt opt, Color c, string context = null)
    {
        var row = SettingsMenu.MakeRow(m, opt);
        var hay = RoleSearch.Haystack(opt.Label);
        var o = new OptRow { Opt = opt, Row = row, Hay = hay, Find = context == null ? hay : hay + "\n" + context };
        StyleOpt(o, c);
        return o;
    }

    private static readonly Color GameColor = new(0.5f, 0.85f, 0.75f);

    private static Text Join(Text a, Text b) => new Text("RoleMenu.Breadcrumb").With("a", a).With("b", b);

    private static void AddSearchGroup(GameOptionsMenu m, Text title, Color c, int area, IEnumerable<Opt> opts)
    {
        var g = new Group { Kind = -1, Title = title, Color = c, Area = area, SearchOnly = true };
        string context = RoleSearch.Haystack(title);
        foreach (var opt in opts) g.Plain.Add(MakeOpt(m, opt, c, context));
        if (g.Plain.Count == 0) return;
        g.Header = MakeHeader(m, c);
        foreach (var o in g.Plain) m.Children.Add(o.Row);
        Groups.Add(g);
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
        // 長い名前は値の欄 (− やチェック) の手前に収まるまで文字を小さくする
        title.enableAutoSizing = true;
        title.fontSizeMin = 1.4f;
        title.fontSizeMax = 2.4f;
        title.color = OptText;
        var rt = title.rectTransform;
        rt.pivot = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(OptTitleW * k, SubH * k);
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

    // 人数・出現率の ±: 押したままで繰り返す (本編のボタンの繰り返しを使う)・指が乗っている所を覚える (ホイールと右クリック用)
    private static void SetupSteppers(RoleRow r)
    {
        var row = r.Row;
        var btns = new[] { row.CountMinusBtn, row.CountPlusBtn, row.ChanceMinusBtn, row.ChancePlusBtn };
        for (int i = 0; i < btns.Length; i++)
        {
            var b = btns[i];
            if (!b) continue;
            b.OnRepeat = true;
            b.RepeatDuration = RepeatEvery;
            string key = r.Role.Id + "|" + (i < 2 ? 1 : 2);
            b.OnMouseOver ??= new UnityEvent();
            b.OnMouseOver.AddListener(Cached(NameOvers, key, k => () => OnZoneHover(k, true)));
            b.OnMouseOut ??= new UnityEvent();
            b.OnMouseOut.AddListener(Cached(NameOuts, key, k => () => OnZoneHover(k, false)));
        }
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

        // 縦のスクロールバー (つまみが動く範囲と溝) が帯に掛かる分だけ下げる
        float barTop = top - BandHeight - 0.04f;
        var bar = scroller.ScrollbarY;
        if (bar && bar.graphic)
        {
            var yb = scroller.ScrollbarYBounds;
            float half = m.transform.InverseTransformPoint(bar.graphic.bounds.max).y - bar.transform.localPosition.y;
            float d = yb.max + half - barTop;
            if (d > 0f)
            {
                scroller.ScrollbarYBounds = new FloatRange(yb.min, yb.max - d);
                bar.transform.localPosition -= new Vector3(0f, Math.Max(0f, bar.transform.localPosition.y + half - barTop), 0f);
            }
            var track = bar.trackGraphic ? bar.trackGraphic.transform : null;
            if (track)
            {
                var s = track.localScale;
                float over = track.localPosition.y + s.y / 2f - barTop;
                if (over > 0f && over < s.y)
                {
                    track.localScale = new Vector3(s.x, s.y - over, s.z);
                    track.localPosition -= new Vector3(0f, over / 2f, 0f);
                }
            }
        }

        float width = right - left - Side * 2f;
        float x0 = left + Side;

        // 陣営で絞るボタン (全部・クルー・インポスター・第三陣営) と「出る役職だけ」
        var chipDefs = new List<(Text label, Color color, int team, bool only)>
        {
            (new Text("RoleMenu.All"), new Color(0.75f, 0.6f, 1f), -1, false),
        };
        foreach (var t in Teams) chipDefs.Add((TeamName(t), TeamColor(t), (int)t, false));
        if (Registry.Addons.Count > 0) chipDefs.Add((KindName(AddonKind), AddonColor, AddonKind, false));
        chipDefs.Add((new Text("RoleMenu.EnabledOnly"), new Color(0.6f, 0.9f, 0.55f), 0, true));
        float cw = (width - Gap * (chipDefs.Count - 1)) / chipDefs.Count;
        float cy = top - 0.06f - ChipHeight / 2f;
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

        // 2 段目 = 絞り込みで選んでいる陣営 (全部なら全役職とアドオン) の出現率をまとめて変える (± で選んだ値 / 0% / 100%)。
        // 3 段目 = 人数をまとめて変える (± で選んだ値) と、まとめて変える前へ戻す
        void Act(int action, float x, float y, float w, Color c)
        {
            var chip = MakeChip(menu.GameSettingsButton, m.transform, new Vector3(x, y, -5f), w, ChipHeight, ActionName(action), c);
            if (!chip) return;
            chip.OnClick.AddListener(Cached(ChipClicks, "act" + action, _ => () => OnAction(action)));
            Actions.Add((chip, action, c));
        }
        float aw = cw * 1.5f, sw = ChipHeight, edgeR = x0 + width;
        float y2 = cy - ChipHeight - RowGap, y3 = y2 - ChipHeight - RowGap;
        float pickR = edgeR - aw * 2f - Gap * 2f;               // ± で選ぶ札の右端 (右に 0% / 100% の 2 枚)
        float pickW = pickR - x0 - (sw + Gap * 0.5f) * 2f;      // 値の札の幅 (左右に ± の丸)
        float pickX = x0 + sw + Gap * 0.5f + pickW / 2f;
        var chanceColor = new Color(1f, 0.85f, 0.35f);
        var countColor = new Color(0.45f, 0.9f, 0.8f);
        Act(ActChanceDown, x0 + sw / 2f, y2, sw, chanceColor);
        Act(ActChanceTo, pickX, y2, pickW, chanceColor);
        Act(ActChanceUp, pickR - sw / 2f, y2, sw, chanceColor);
        Act(ActZero, edgeR - aw * 1.5f - Gap, y2, aw, new Color(0.62f, 0.64f, 0.7f));
        Act(ActFull, edgeR - aw / 2f, y2, aw, chanceColor);
        Act(ActCountDown, x0 + sw / 2f, y3, sw, countColor);
        Act(ActCountTo, pickX, y3, pickW, countColor);
        Act(ActCountUp, pickR - sw / 2f, y3, sw, countColor);
        Act(ActUndo, edgeR - aw - Gap / 2f, y3, aw * 2f + Gap, new Color(0.55f, 0.8f, 1f));
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

    // 設定画面の検索欄の文字が変わった (空 = 検索をやめた)
    public static void SetQuery(string q)
    {
        _tokens = RoleSearch.Tokens(q);
        // 検索は全部のタブから探すので、陣営で絞る札は隠す
        foreach (var (btn, _, _, _) in Chips)
            if (btn && btn.gameObject.activeSelf == _tokens.Length > 0) btn.gameObject.SetActive(_tokens.Length == 0);
        foreach (var (btn, _, _) in Actions)
            if (btn && btn.gameObject.activeSelf == _tokens.Length > 0) btn.gameObject.SetActive(_tokens.Length == 0);
        Relayout(true);
    }

    // 検索の結果の件数 (左の説明の欄に出す)
    public static Text SearchSummary(string q)
    {
        int[] n = new int[3];
        foreach (var g in Groups)
        {
            if (!g.Header || !g.Header.gameObject.activeSelf) continue;
            foreach (var o in g.Plain) if (o.Row && o.Row.gameObject.activeSelf) n[g.Area]++;
            foreach (var r in g.Roles) if (r.Row && r.Row.gameObject.activeSelf) n[g.Area]++;
        }
        int total = n[0] + n[1] + n[2];
        if (total == 0) return new Text("RoleMenu.NothingMatches").With("q", q);
        var sb = new System.Text.StringBuilder(new Text("RoleMenu.SearchCount").With("q", q).With("total", total));
        Text[] names = { VanillaGame.TabName, SettingsMenu.TabName(false), TabName };
        string sep = "\n";
        for (int i = 0; i < 3; i++)
        {
            if (n[i] == 0) continue;
            sb.Append(sep).Append(new Text("RoleMenu.SearchTabCount").With("tab", names[i]).With("n", n[i]));
            sep = Tr.Get("RoleMenu.SearchSeparator");
        }
        sb.Append('\n').Append(Tr.Get("RoleMenu.SearchClearHint"));
        return Text.Raw(sb.ToString());
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
        foreach (var (btn, action, c) in Actions)
        {
            if (!btn) continue;
            bool usable = action switch
            {
                ActUndo => _undo != null,
                ActChanceDown => _bulkChance > 0,
                ActChanceUp => _bulkChance < 100,
                ActCountDown => _bulkCount > 1,
                ActCountUp => _bulkCount < MaxBulkCount(),
                _ => true,
            };
            var label = btn.GetComponentInChildren<TextMeshPro>(true);
            if (label)
            {
                label.text = ActionName(action);
                label.color = usable ? Mix(c, Color.white, 0.2f) : new Color(0.42f, 0.43f, 0.47f, 1f);
            }
            var edge = btn.transform.Find("MrpChipEdge");
            var sr = edge ? edge.GetComponent<SpriteRenderer>() : null;
            if (sr) sr.color = usable ? new Color(c.r * 0.75f, c.g * 0.75f, c.b * 0.75f, 1f) : new Color(0.3f, 0.31f, 0.34f, 1f);
        }
    }

    private static Text ActionName(int action)
    {
        switch (action)
        {
            case ActUndo: return new Text("RoleMenu.Undo");
            case ActChanceDown or ActCountDown: return Text.Raw("-");
            case ActChanceUp or ActCountUp: return Text.Raw("+");
            case ActChanceTo:
                return (_team < 0 ? new Text("RoleMenu.AllChanceTo") : new Text("RoleMenu.TeamChanceTo").With("team", KindName(_team)))
                    .With("pct", _bulkChance + "%");
            case ActCountTo:
                return (_team < 0 ? new Text("RoleMenu.AllCountTo") : new Text("RoleMenu.TeamCountTo").With("team", KindName(_team)))
                    .With("n", _bulkCount);
        }
        string pct = action == ActZero ? "0%" : "100%";
        if (_team < 0) return new Text("RoleMenu.AllPercent").With("pct", pct);
        return new Text("RoleMenu.TeamPercent").With("team", KindName(_team)).With("pct", pct);
    }

    // 人数の ± の上限 (選んでいる陣営の役職の人数の上限のうち最大)
    private static int MaxBulkCount()
    {
        int max = 1;
        foreach (var g in Groups)
        {
            if (g.SearchOnly || (_team >= 0 && g.Kind != _team)) continue;
            foreach (var r in g.Roles) max = Math.Max(max, r.Role.Count.Max);
        }
        return max;
    }

    // まとめて変える札: 0% / 100% / ± で選んだ出現率 / ± で選んだ人数 = 選んでいる陣営の役職を全部その値に
    // (役職ごとの範囲に収める・変える前を 1 回分覚える)・元に戻す = 覚えた値へ戻す・± = まとめて変える値を選ぶ
    private static void OnAction(int action)
    {
        switch (action)
        {
            case ActUndo:
                if (_undo == null) return;
                foreach (var (role, chance, count) in _undo)
                {
                    role.Chance.SetIndex(chance);
                    role.Count.SetIndex(count);
                }
                _undo = null;
                break;
            case ActChanceDown or ActChanceUp:
                _bulkChance = Math.Clamp(_bulkChance + (action == ActChanceUp ? 10 : -10), 0, 100);
                break;
            case ActCountDown or ActCountUp:
                _bulkCount = Math.Clamp(_bulkCount + (action == ActCountUp ? 1 : -1), 1, MaxBulkCount());
                break;
            default:
                var before = new List<(Assignable, int, int)>();
                foreach (var g in Groups)
                {
                    if (g.SearchOnly || (_team >= 0 && g.Kind != _team)) continue;
                    foreach (var r in g.Roles)
                    {
                        var role = r.Role;
                        int chance = role.Chance.Index, count = role.Count.Index;
                        bool changed = action switch
                        {
                            ActZero => role.Chance.SetValue(0),
                            ActFull => role.Chance.SetValue(100),
                            ActChanceTo => role.Chance.SetValue(_bulkChance),
                            _ => role.Count.SetValue(_bulkCount),
                        };
                        if (changed) before.Add((role, chance, count));
                    }
                }
                if (before.Count == 0) return;  // 変わる物が無い時は前の「元に戻す」を残す
                _undo = before;
                break;
        }
        RefreshChips();
        Relayout(false);
        ShowDefault();
    }

    // 指が乗った所 (key = 役職の Id|所)。外れた時は同じ所の時だけ忘れる (隣のボタンへ移った時の順番の入れ替わりに備える)
    private static void OnZoneHover(string key, bool over)
    {
        int bar = key.LastIndexOf('|');
        if (!ById.TryGetValue(key.Substring(0, bar), out var r)) return;
        int zone = key[bar + 1] - '0';
        if (over) { _hoverRow = r; _hoverZone = zone; }
        else if (_hoverRow == r && _hoverZone == zone) _hoverRow = null;
    }

    // 設定画面が開いている間だけ (閉じると _page が null)。指が役職の行に乗っている間、
    // ホイール = ± の上だけ人数か出現率を 1 刻み・右クリック = 出現率を 0% と 100% で切り替える (名前の上でも)
    // 名前の帯は広く、スクロールで下から来た行に乗ると一覧が止まってしまうので、ホイールは取らない
    public static void Tick()
    {
        // スマホにはホイールも右クリックも無い (指を離しても「乗っている」が外れないので、一覧のホイールを止めたままにしない)
        if (Touch || _page is null || !_page) return;
        var scroller = _page.scrollBar;
        bool over = _hoverRow != null && _hoverRow.Row && _hoverRow.Row.gameObject.activeInHierarchy;
        bool wheelOver = over && _hoverZone != 0;
        if (scroller && wheelOver != _wheelSpeed >= 0f)
        {
            if (wheelOver) { _wheelSpeed = scroller.ScrollWheelSpeed; scroller.ScrollWheelSpeed = 0f; }
            else { scroller.ScrollWheelSpeed = _wheelSpeed; _wheelSpeed = -1f; }
        }
        if (!over) return;
        var r = _hoverRow;
        if (Input.GetMouseButtonDown(1))
        {
            var c = r.Role.Chance;
            c.SetIndex(c.Index > 0 ? 0 : c.Count - 1);
            AfterStep(r);
            return;
        }
        if (!wheelOver) return;
        float wheel = Input.mouseScrollDelta.y;
        if (wheel == 0f) return;
        (_hoverZone == 1 ? r.Role.Count : r.Role.Chance).Step(wheel > 0f ? 1 : -1);
        AfterStep(r);
    }

    private static void OnNameClick(string id)
    {
        if (!ById.TryGetValue(id, out var r)) return;
        if (r.HasDetail && _tokens.Length == 0)
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
        if (over) { _hoverRow = r; _hoverZone = 0; }
        else if (_hoverRow == r && _hoverZone == 0) _hoverRow = null;
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
        // 本編の繰り返しは押した直後から始まるので、押してからしばらくの繰り返しは捨てる (1 回押しで 1 つだけ進むように)
        long now = Environment.TickCount64;
        if (Input.GetMouseButtonDown(0)) _pressAt = now;
        else if (Input.GetMouseButton(0) && now - _pressAt < HoldDelayMs) return;
        (chance ? r.Role.Chance : r.Role.Count).Step(delta);
        AfterStep(r);
    }

    private static void AfterStep(RoleRow r)
    {
        RefreshRole(r);
        // 「出る役職だけ」で 0% にした行は、その場では消さない (押し間違いをすぐ戻せるように)。次に並べ直す時に消える
        RefreshHeaders();
        ShowDefault();
    }

    private static void ShowDefault()
    {
        // 役職の説明・検索の件数を見ている間はそのまま (数値を変えるたびに説明が消えないように)
        if (_described != null || _tokens.Length > 0) return;
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
            bool has = r.HasDetail;
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
        if (!r.HasDetail) return false;
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
            string name = $"<color=#{ColorUtility.ToHtmlStringRGB(g.Color)}>{g.Title}</color>";
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
            bool teamShown = g.SearchOnly ? searching : searching || _team < 0 || _team == g.Kind;
            int shown = 0;
            foreach (var o in g.Plain) if (teamShown && (!searching || Matches(o.Find))) shown++;
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
                bool on = groupOn && (!searching || Matches(o.Find));
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
                PlaceExcl(r, open && !searching, ref y);
                RefreshRole(r);
            }
            if (groupOn) { y -= GroupGap; any = true; }
        }

        if (_empty)
        {
            _empty.Title.text = searching ? new Text("RoleMenu.NothingMatchesShort") : new Text("RoleMenu.NoMatchingRoles");
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

    // ---- 一緒に出ない役職 ----

    private static readonly Color ChipDark = new(0.11f, 0.12f, 0.15f, 1f);
    private static readonly Color ChipInk = new(0.08f, 0.08f, 0.1f, 1f);

    // 開いた役職の細かい設定の下に置く (検索の間は出さない)。初めて開いた時に作り、以後は出し入れと位置だけ
    private static void PlaceExcl(RoleRow r, bool on, ref float y)
    {
        if (r.Picker == null) return;
        if (!on)
        {
            if (r.Excl != null && r.Excl.Root && r.Excl.Root.activeSelf) r.Excl.Root.SetActive(false);
            // 隠すと指が離れた知らせが来ないので、次に出す時は明るい地を残さない
            if (r.Excl != null) r.Excl.Hover = false;
            return;
        }
        r.Excl ??= BuildExcl(r);
        var p = r.Excl;
        if (p == null || !p.Root) return;
        if (!p.Root.activeSelf) p.Root.SetActive(true);
        bool open = ExclOpen.Contains(r.Role.Id);
        if (open != p.Open) SetExclShape(p, open);
        // 板の上端を細かい設定の行の板の上端と同じ所に
        p.Root.transform.localPosition = new Vector3(0f, y + SubH / 2f, Z);
        y -= (p.Open ? p.FullHeight : ExclHeadH) + (OptStep - SubH);
        RefreshExcl(r);
    }

    // 札の開け閉め: 板の高さを変え、札を出し入れし、印を回す (作り直さない)
    private static void SetExclShape(ExclPanel p, bool open)
    {
        p.Open = open;
        float h = open ? p.FullHeight : ExclHeadH;
        if (p.Edge)
        {
            p.Edge.size = new Vector2(p.Edge.size.x, h + Edge * 2f);
            p.Edge.transform.localPosition = new Vector3(p.Edge.transform.localPosition.x, -h / 2f, p.Edge.transform.localPosition.z);
        }
        if (p.Fill)
        {
            p.Fill.size = new Vector2(p.Fill.size.x, h);
            p.Fill.transform.localPosition = new Vector3(p.Fill.transform.localPosition.x, -h / 2f, p.Fill.transform.localPosition.z);
        }
        foreach (var chip in p.Chips)
            if (chip.Button) chip.Button.gameObject.SetActive(open);
        if (p.Chevron) p.Chevron.transform.localEulerAngles = new Vector3(0f, 0f, open ? -90f : 0f);
    }

    // 細かい設定の行と同じ板 (輪郭 + 役職の色を少し混ぜた地) に、見出しと他の役職の札を 4 列で並べる。
    // 座標は役職の行と同じ (板の左端 SubL・右端 CardR)。板と字は一覧の切り抜きに合わせる
    private static ExclPanel BuildExcl(RoleRow r)
    {
        var me = r.Picker;
        var proto = r.Row ? r.Row.titleText : null;
        if (!_page || !proto) return null;
        var others = new List<RoleBase>();
        foreach (var g in Groups)
        {
            if (g.SearchOnly || g.Kind == AddonKind) continue;
            foreach (var o in g.Roles)
                if (o.Picker is { } rb && rb != me) others.Add(rb);
        }

        var root = new GameObject("MrpNotWith_" + me.Id);
        root.layer = _page.settingsContainer.gameObject.layer;
        root.transform.SetParent(_page.settingsContainer, false);
        var t = root.transform;
        var p = new ExclPanel { Root = root };
        int rows = (others.Count + ExclColumns - 1) / ExclColumns;
        p.FullHeight = ExclHeadH + rows * ExclChipH + Math.Max(rows - 1, 0) * ExclGap + ExclPad;

        // 板は開いた時の高さで作り、SetExclShape で今の高さに合わせる
        var mat = MenuArt.Masked(r.Row.labelSprite);
        float w = CardR - SubL, cx = (SubL + CardR) / 2f;
        p.FillColor = Mix(CardOff, RoleDisplay.ParseColor(me.Color), 0.1f);
        p.Edge = MenuArt.Panel(t, "MrpOptEdge", 0.09f, new Vector2(w + Edge * 2f, p.FullHeight + Edge * 2f), new Vector3(cx, -p.FullHeight / 2f, 3.2f), Ink, mat);
        p.Fill = MenuArt.Panel(t, "MrpOpt", 0.08f, new Vector2(w, p.FullHeight), new Vector3(cx, -p.FullHeight / 2f, 3f), p.FillColor, mat);

        // 見出しの行 = 開け閉めの印 + 名前 + (閉じている時は選んだ役職・開いている時は説明)。行の全体が押せる
        var chev = MakeLabel(proto, t, "MrpChevron", new Vector3(SubL + 0.17f, -ExclHeadH / 2f + 0.01f, 0f), 0.2f, 0.3f);
        chev.alignment = TextAlignmentOptions.Center;
        chev.enableAutoSizing = false;
        chev.fontSize = 1.6f;
        chev.color = OptText;
        chev.text = "›";
        p.Chevron = chev;
        var title = MakeLabel(proto, t, "MrpNotWithTitle", new Vector3(SubL + 0.32f, -ExclHeadH / 2f, 0f), w - 0.5f, ExclHeadH);
        title.alignment = TextAlignmentOptions.Left;
        title.rectTransform.pivot = new Vector2(0f, 0.5f);
        title.enableAutoSizing = false;
        title.fontSize = 1.3f;
        title.color = OptText;
        p.Title = title;

        var head = new GameObject("MrpNotWithHead");
        head.layer = root.layer;
        head.transform.SetParent(t, false);
        head.transform.localPosition = new Vector3(cx, -ExclHeadH / 2f, 0f);
        var headHit = head.AddComponent<BoxCollider2D>();
        headHit.size = new Vector2(w, ExclHeadH);
        var headBtn = head.AddComponent<PassiveButton>();
        headBtn.Colliders = new Collider2D[] { headHit };
        headBtn.ClickMask = _page.ButtonClickMask;
        string headKey = "xh|" + me.Id;
        headBtn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        headBtn.OnClick.AddListener(Cached(NameClicks, headKey, _ => () => OnExclHead(me.Id)));
        headBtn.OnMouseOver = new UnityEvent();
        headBtn.OnMouseOver.AddListener(Cached(NameOvers, headKey, _ => () => OnExclHeadHover(me.Id, true)));
        headBtn.OnMouseOut = new UnityEvent();
        headBtn.OnMouseOut.AddListener(Cached(NameOuts, headKey, _ => () => OnExclHeadHover(me.Id, false)));

        float chipW = (w - ExclPad * 2f - ExclGap * (ExclColumns - 1)) / ExclColumns;
        for (int i = 0; i < others.Count; i++)
        {
            var other = others[i];
            int col = i % ExclColumns, row = i / ExclColumns;
            var go = new GameObject("MrpNotWithChip");
            go.layer = root.layer;
            go.transform.SetParent(t, false);
            go.transform.localPosition = new Vector3(SubL + ExclPad + chipW / 2f + col * (chipW + ExclGap),
                -ExclHeadH - ExclChipH / 2f - row * (ExclChipH + ExclGap), 0f);
            var chip = new ExclChip { Other = other };
            chip.Edge = MenuArt.Panel(go.transform, "MrpChipEdge", ExclChipH / 2f, new Vector2(chipW, ExclChipH), new Vector3(0f, 0f, 2.6f), Color.white, mat);
            chip.Fill = MenuArt.Panel(go.transform, "MrpChip", ExclChipH / 2f - Edge, new Vector2(chipW - Edge * 2f, ExclChipH - Edge * 2f), new Vector3(0f, 0f, 2.4f), ChipDark, mat);
            chip.Label = MakeLabel(proto, go.transform, "MrpChipText", new Vector3(0f, 0.005f, 0f), chipW - 0.14f, ExclChipH);
            chip.Label.alignment = TextAlignmentOptions.Center;
            chip.Label.enableAutoSizing = true;
            chip.Label.fontSizeMin = 0.8f;
            chip.Label.fontSizeMax = 1.25f;
            chip.Label.text = AssignPlan.FixedPair(me, other) ? new Text("RoleMenu.FixedPair").With("name", other.Name) : other.Name;

            var hit = go.AddComponent<BoxCollider2D>();
            hit.size = new Vector2(chipW, ExclChipH);
            var pb = go.AddComponent<PassiveButton>();
            pb.Colliders = new Collider2D[] { hit };
            pb.ClickMask = _page.ButtonClickMask;
            string key = "x|" + me.Id + "|" + other.Id;
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnClick.AddListener(Cached(NameClicks, key, k => () => OnExclClick(k)));
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOver.AddListener(Cached(NameOvers, key, k => () => OnExclHover(k, true)));
            pb.OnMouseOut = new UnityEvent();
            pb.OnMouseOut.AddListener(Cached(NameOuts, key, k => () => OnExclHover(k, false)));
            chip.Button = pb;
            p.Chips.Add(chip);
        }
        SetExclShape(p, ExclOpen.Contains(me.Id));
        return p;
    }

    // 役職名の字の部品を複製して使う (一覧の切り抜きのマテリアルごと引き継ぐ)
    private static TextMeshPro MakeLabel(TextMeshPro proto, Transform parent, string name, Vector3 pos, float w, float h)
    {
        var label = Object.Instantiate(proto, parent);
        label.name = name;
        label.enableWordWrapping = false;
        label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        label.rectTransform.sizeDelta = new Vector2(w, h);
        label.transform.localPosition = pos;
        return label;
    }

    // 札の見た目: 選んでいない = 暗い地 + 色の輪郭 + 色の字 / 選んでいる = 色の地 + 暗い字 / 役職の作りで決まった組 = 少し暗い色の地 (押しても変わらない)
    private static void RefreshExcl(RoleRow r)
    {
        var me = r.Picker;
        var p = r.Excl;
        if (me == null || p == null) return;
        if (p.Fill) p.Fill.color = p.Hover ? Mix(p.FillColor, RoleDisplay.ParseColor(me.Color), 0.12f) : p.FillColor;
        if (p.Title)
        {
            string text = $"{new Text("Registry.NotWith")}  <size=75%><color=#8E94A3>{(p.Open ? new Text("RoleMenu.NotWithHint").ToString() : ExclSummary(me, p))}</color></size>";
            if (p.TitleText != text)
            {
                MenuFont.Prepare(text);
                p.Title.text = p.TitleText = text;
            }
        }
        if (!p.Open) return;
        foreach (var chip in p.Chips) RefreshChip(me, chip);
    }

    // 閉じている時の見出しの続き: 選んだ役職 (役職の作りで決まった組も) をロビーの一覧と同じ書き方で
    private static string ExclSummary(RoleBase me, ExclPanel p)
    {
        string first = null;
        int n = 0;
        foreach (var chip in p.Chips)
        {
            if (!AssignPlan.FixedPair(me, chip.Other) && !AssignPlan.PickedPair(me, chip.Other)) continue;
            first ??= chip.Other.Name.ToString();
            n++;
        }
        if (n == 0) return Tr.Get("RoleSetOpt.None");
        return n > 1 ? new Text("RoleSetOpt.More").With("name", first).With("n", n - 1) : first;
    }

    private static void OnExclHead(string id)
    {
        if (!ById.TryGetValue(id, out var r) || r.Excl == null) return;
        if (!ExclOpen.Remove(id)) ExclOpen.Add(id);
        Relayout(false);
    }

    private static void OnExclHeadHover(string id, bool over)
    {
        if (!ById.TryGetValue(id, out var r) || r.Excl == null) return;
        r.Excl.Hover = over;
        RefreshExcl(r);
    }

    private static void RefreshChip(RoleBase me, ExclChip chip)
    {
        if (!chip.Fill) return;
        var c = RoleDisplay.ParseColor(chip.Other.Color);
        bool locked = AssignPlan.FixedPair(me, chip.Other);
        bool on = locked || AssignPlan.PickedPair(me, chip.Other);
        chip.Fill.color = on ? (locked ? Mix(ChipDark, c, 0.55f) : c) : chip.Hover ? Mix(ChipDark, c, 0.3f) : ChipDark;
        chip.Edge.color = on ? Mix(c, Color.white, 0.45f) : new Color(c.r * 0.75f, c.g * 0.75f, c.b * 0.75f, 1f);
        chip.Label.color = on ? ChipInk : Mix(c, Color.white, 0.2f);
    }

    private static bool TryChip(string key, out RoleRow row, out ExclChip chip)
    {
        chip = null;
        int a = key.IndexOf('|'), b = key.LastIndexOf('|');
        if (!ById.TryGetValue(key.Substring(a + 1, b - a - 1), out row) || row.Excl == null) return false;
        string other = key.Substring(b + 1);
        foreach (var c in row.Excl.Chips)
            if (c.Other.Id == other) { chip = c; return true; }
        return false;
    }

    // 札を押した: 両方の役職の選びを揃えて切り替える (片方だけ残ると、外したのに選んだままに見えるため)
    private static void OnExclClick(string key)
    {
        if (!TryChip(key, out var row, out var chip)) return;
        var me = row.Picker;
        var other = chip.Other;
        if (me == null || other.Excluded == null || AssignPlan.FixedPair(me, other)) return;
        bool on = !AssignPlan.PickedPair(me, other);
        me.Excluded.Set(other.Order, on);
        other.Excluded.Set(me.Order, on);
        RefreshExcl(row);
        if (ById.TryGetValue(other.Id, out var theirs)) RefreshExcl(theirs);
    }

    private static void OnExclHover(string key, bool over)
    {
        if (!TryChip(key, out var row, out var chip)) return;
        chip.Hover = over;
        if (row.Picker != null) RefreshChip(row.Picker, chip);
    }

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        // 検索は役職のタブが未だ作られていなくても打てる (打つと役職のタブが開いて作られる)
        if (a.Length > 0 && a[0] == "q" && !SettingsMenu.TypeSearch(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "")) return "no search box";
        if (!_page) return "role tab not built";
        switch (a.Length > 0 ? a[0] : "dump")
        {
            case "q":
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
            case "step":
                // step <Id> <細かい設定の番号> <+1|-1> (開いた役職の行の ± を押すのと同じ)
                if (a.Length < 4 || !ById.TryGetValue(a[1], out var sr) || !int.TryParse(a[2], out int si) || si < 0 || si >= sr.Opts.Count || !int.TryParse(a[3], out int sd))
                    return "step <Id> <n> <+1|-1>";
                var srow = sr.Opts[si].Row;
                var snum = srow.TryCast<NumberOption>();
                if (snum != null) { if (sd > 0) snum.Increase(); else snum.Decrease(); }
                else srow.TryCast<ToggleOption>()?.Toggle();
                break;
            case "act":
                // act <番号> (まとめて変える札を押すのと同じ: 0 = 0% / 1 = 100% / 2 = 元に戻す / 3 = 選んだ出現率 / 4 = 選んだ人数 / 5,6 = 出現率の -,+ / 7,8 = 人数の -,+)
                if (a.Length > 1 && int.TryParse(a[1], out int act))
                    foreach (var (btn, n, _) in Actions)
                        if (n == act && btn) btn.OnClick.Invoke();
                break;
            case "excl":
                // excl <Id> <相手の Id> (役職を開いて、一緒に出ない役職の札を押すのと同じ)
                if (a.Length < 3 || !ById.TryGetValue(a[1], out var er) || er.Picker == null) return "excl <Id> <相手の Id>";
                if (!Expanded.Contains(er.Role.Id)) OnNameClick(er.Role.Id);
                if (!ExclOpen.Contains(er.Role.Id)) OnExclHead(er.Role.Id);
                ExclChip ec = null;
                if (er.Excl != null) foreach (var c in er.Excl.Chips) if (c.Other.Id == a[2]) ec = c;
                if (ec == null) return "no chip " + a[2];
                ec.Button.OnClick.Invoke();
                break;
            case "exclhead":
                // exclhead <Id> (開いた役職の「一緒に出ない役職」の見出しを押すのと同じ)
                if (a.Length < 2 || !ById.TryGetValue(a[1], out var hr) || hr.Picker == null) return "exclhead <Id>";
                if (!Expanded.Contains(hr.Role.Id)) OnNameClick(hr.Role.Id);
                if (hr.Excl == null) return "no panel";
                var head = hr.Excl.Root.transform.Find("MrpNotWithHead");
                var hb = head ? head.GetComponent<PassiveButton>() : null;
                if (hb) hb.OnClick.Invoke();
                break;
            case "press":
                // press <Id> <count|chance> <+1|-1>
                if (a.Length < 4 || !ById.TryGetValue(a[1], out var pr) || !int.TryParse(a[3], out int d)) return "press <Id> <count|chance> <+1|-1>";
                if (a[2] == "count") { if (d > 0) pr.Row.IncreaseCount(); else pr.Row.DecreaseCount(); }
                else { if (d > 0) pr.Row.IncreaseChance(); else pr.Row.DecreaseChance(); }
                break;
            case "hover":
                // hover <Id> <0|1|2> / hover off (指が名前・人数の ±・出現率の ± に乗ったのと同じ。一覧のホイールの速さは次のフレームで変わる)
                if (a.Length > 1 && a[1] == "off") { _hoverRow = null; break; }
                if (a.Length < 3 || !ById.TryGetValue(a[1], out var vr) || !int.TryParse(a[2], out int vz)) return "hover <Id> <0|1|2> | hover off";
                if (vz == 0) OnNameHover(vr.Role.Id, true);
                else OnZoneHover(vr.Role.Id + "|" + vz, true);
                break;
        }
        var sb = new System.Text.StringBuilder();
        sb.Append($"search='{SettingsMenu.SearchQuery}' team={_team} only={_enabledOnly} undo={_undo?.Count ?? -1} bulk={_bulkChance}%/{_bulkCount} hover={(_hoverRow != null ? _hoverRow.Role.Id + "|" + _hoverZone : "-")} wheel={(_page.scrollBar ? _page.scrollBar.ScrollWheelSpeed : -1f):0.##} rows:");
        foreach (var g in Groups)
        {
            if (g.Header && g.Header.gameObject.activeSelf) sb.Append($" [{(g.SearchOnly ? g.Title.Builtin(false) : g.Kind == AddonKind ? "Addon" : ((Team)g.Kind).ToString())}]");
            foreach (var o in g.Plain) if (o.Row && o.Row.gameObject.activeSelf) sb.Append($" {o.Opt.Key}");
            foreach (var r in g.Roles)
            {
                if (!r.Row || !r.Row.gameObject.activeSelf) continue;
                sb.Append($" {r.Role.Id}({r.Row.countText.text},{r.Row.chanceText.text}{(IsOpen(r) ? ",open" : "")})");
                foreach (var o in r.Opts) if (o.Row && o.Row.gameObject.activeSelf) sb.Append($" >{o.Opt.Key.Substring(o.Opt.Key.IndexOf('.') + 1)}={o.Opt.Display()}");
                if (r.Excl != null && r.Excl.Root && r.Excl.Root.activeSelf)
                {
                    sb.Append(r.Excl.Open ? " x+=[" : " x-=[");
                    foreach (var c in r.Excl.Chips)
                    {
                        bool fixedPair = AssignPlan.FixedPair(r.Picker, c.Other);
                        if (fixedPair || AssignPlan.PickedPair(r.Picker, c.Other)) sb.Append(c.Other.Id).Append(fixedPair ? "* " : " ");
                    }
                    sb.Append(']');
                }
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
