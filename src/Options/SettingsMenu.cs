using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MoreRolesPlus.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// Based on https://github.com/waffle-ful/Aeterna-End-K-not Patches/GameOptionsMenuPatch.cs (GPL-3.0)
// 本編の「ゲーム設定」画面に MRP のタブ (全般・役職) を足す。役職のタブの中身は RoleMenu。
// - タブは本編のタブを複製して作り、行も本編の行の部品 (チェックボックス / 数値) を複製して並べる。
// - 行の操作 (± / チェック) は本編の処理の前で横取りして MRP の設定項目を書き換える。
//   本編の行の OnValueChanged には何も入れない (管理側のデリゲートを il2cpp の欄に入れると、
//   画面を閉じても行ごと解放されなくなるため)。
// - 値は画面を閉じた時に保存し、全員へ送る。
// - タブのボタンの下に検索欄を 1 つ置く。打つと全部のタブ (本編のゲーム設定・MRP 全般・役職) から探し、
//   結果は役職のタブに見出しごとに並べる (RoleMenu)。空にすると検索の前に開いていたタブへ戻る。
internal static class SettingsMenu
{
    private const int FirstTab = 3; // 本編のタブは 0〜2

    // 左の列 (設定画面の座標)。X は本編のタブのボタンと同じ所 (絵はボタンの位置から左へずれて描かれる)
    private const float CatX = -2.96f, CatTopY = 1.6f, CatStepY = 0.46f;
    private const float CatScaleX = 1f, CatScaleY = 0.78f;
    private const float CompactX = 0.48f, CompactY = 0.8f;
    // 検索欄は板の上の縁より上・右上の本編のボタンの列に掛からない所
    private const float SearchX = -1.2f, SearchY = 2.6f, SearchW = 3.6f;

    // 左の列のボタンの番号 (陣営は CatKind0 + RoleMenu の陣営の番号)
    private const int CatPresets = 0, CatGame = 1, CatGeneral = 2, CatKind0 = 3;
    internal static readonly Color GameCatColor = new(0.82f, 0.84f, 0.88f), GeneralCatColor = new(1f, 0.55f, 0.75f);
    private static readonly List<(PassiveButton Btn, int Cat)> Cats = new();

    private sealed class Page
    {
        public bool Roles;  // false = 全般のタブ
        public bool Presets; // プリセットのタブ
        public GameOptionsMenu Menu;
    }

    private static readonly List<Page> Pages = new();
    private static readonly Dictionary<IntPtr, int> MenuToPage = new();
    private static GameOptionsMenu _template;
    private static SearchBox _search;
    private static bool _searching, _leavingSearch;
    private static int _currentTab = 1, _returnTab = 1;

    public static string SearchQuery => _search != null ? _search.Query : "";
    // 試験用: MRP のタブのボタンを押す (OnClick をそのまま呼ぶ)
    internal static string ClickCat(int cat)
    {
        var hit = Cats.Find(c => c.Cat == cat);
        if (!hit.Btn) return "no button";
        hit.Btn.OnClick.Invoke();
        return "ok";
    }

    internal static string ClickTabButton(int page)
    {
        if (page < 0 || page >= Pages.Count) return "no page";
        int cat = Pages[page].Presets ? CatPresets : Pages[page].Roles ? CatKind0 + RolePage.Team : CatGeneral;
        var hit = Cats.Find(c => c.Cat == cat);
        if (!hit.Btn) return "no button";
        hit.Btn.OnClick.Invoke();
        return Pages[page].Menu && Pages[page].Menu.gameObject.activeSelf ? "shown" : "not shown";
    }

    // 試験用: 開いている MRP のタブ
    internal static Transform OpenPage()
    {
        foreach (var p in Pages)
            if (p.Menu && p.Menu.gameObject.activeSelf) return p.Menu.transform;
        return null;
    }

    public static bool IsOurMenu(GameOptionsMenu m, out int page) => MenuToPage.TryGetValue(m.Pointer, out page);

    internal static Text TabName(bool roles) => roles ? RoleMenu.TabName : new Text("SettingsMenu.MRP");

    internal static Color TabColor(bool roles) => roles ? new Color(1f, 0.8f, 0.35f) : new Color(0.75f, 0.6f, 1f);

    // 本編がゲーム設定タブを組み立てる前の、空のタブを型として取っておく
    public static void OnMenuEnable(GameSettingMenu menu)
    {
        if (_template) return;
        _template = Object.Instantiate(menu.GameSettingsTab, menu.GameSettingsTab.transform.parent);
        _template.name = "MrpTabTemplate";
        _template.gameObject.SetActive(false);
    }

    public static void OnMenuStart(GameSettingMenu menu)
    {
        Lang.Refresh();
        OptionSync.RestoreOwn();
        Pages.Clear();
        MenuToPage.Clear();
        RolePage.Forget();
        SettingsPages.Forget();
        PresetPage.Forget();
        if (_search != null) SearchBox.Forget(_search);
        _search = null;
        _searching = false;
        _currentTab = _returnTab = 1;
        if (!_template) return;

        // 左の列 1 本で全部の設定へ行く: プリセット / ゲーム (本編のゲーム設定) / 全般 / 陣営ごとの役職。
        // 本編のタブのボタンと、左上の題・説明の箱は出さない (この列と同じ役目か、文が多いだけなので)
        var gameBtn = menu.GameSettingsButton;
        foreach (int kind in new[] { 0, 1, 2 })
        {
            bool roles = kind == 1, presets = kind == 2;
            if (roles ? !RoleMenu.HasContent() : kind == 0 && Registry.SectionsOf(Tab.General).Count == 0) continue;
            var page = Object.Instantiate(_template, _template.transform.parent);
            page.name = "MrpTab_" + (presets ? "Presets" : roles ? "Roles" : "General");
            page.gameObject.SetActive(false);
            MenuToPage[page.Pointer] = Pages.Count;
            Pages.Add(new Page { Roles = roles, Presets = presets, Menu = page });
        }

        Cats.Clear();
        CatLabels.Clear();
        CatShown.Clear();
        CatDots.Clear();
        CatArts.Clear();
        if (Pages.Exists(p => p.Presets)) AddCat(menu, gameBtn, CatPresets, PresetPage.TabName, PresetPage.TabColor);
        AddCat(menu, gameBtn, CatGame, new Text("SettingsMenu.CatGame"), GameCatColor);
        if (Pages.Exists(p => !p.Roles && !p.Presets)) AddCat(menu, gameBtn, CatGeneral, new Text("SettingsMenu.CatGeneral"), GeneralCatColor);
        if (Pages.Exists(p => p.Roles))
            for (int k = 0; k <= RoleMenu.AddonKind; k++) AddCat(menu, gameBtn, CatKind0 + k, RoleMenu.KindName(k), RoleMenu.KindColor(k));
        RefreshCatCounts();
        foreach (var b in new[] { gameBtn, menu.RoleSettingsButton, menu.GamePresetsButton })
        {
            b.gameObject.SetActive(false);
            menu.ControllerSelectable.Remove(b);
        }
        var label = menu.transform.Find("GameSettingsLabel");
        if (label) label.gameObject.SetActive(false);
        var info = menu.transform.Find("What Is This?");
        if (info) info.gameObject.SetActive(false);

        // 検索欄: 板の上 (どの頁からでも全部を探す)
        if (Pages.Exists(p => p.Roles))
            _search = SearchBox.Create(gameBtn.transform.parent, new Vector3(SearchX, SearchY, gameBtn.transform.localPosition.z), SearchW, 0.42f,
                new Text("SettingsMenu.SearchEverything"), OnSearch);
        // 本編は開いた時にプリセットのタブを出すので、ゲーム設定のタブから始める
        menu.ChangeTab(1, false);
        var desc = menu.MenuDescriptionText;
        if (!desc) return;
        var descTr = desc.GetComponent<TextTranslatorTMP>();
        if (descTr) Object.Destroy(descTr);
        desc.text = TranslationController.Instance.GetString(StringNames.GameSettingsDescription);
    }

    // 左の列のボタン 1 つ (本編のゲーム設定のボタンの複製・押す音と当てた時の見た目は本編のまま)
    private static void AddCat(GameSettingMenu menu, PassiveButton proto, int cat, Text name, Color c)
    {
        var btn = Object.Instantiate(proto, proto.transform.parent);
        btn.name = "MrpCat_" + cat;
        btn.gameObject.SetActive(true);
        var z = proto.transform.localPosition.z;
        Compact(btn, new Vector3(CatX, CatTopY - Cats.Count * CatStepY, z), proto.transform.localScale, CatScaleX, CatScaleY);
        var label = btn.GetComponentInChildren<TextMeshPro>();
        var tr = label.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        label.text = name;
        btn.inactiveSprites.GetComponent<SpriteRenderer>().color = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 1f);
        btn.activeSprites.GetComponent<SpriteRenderer>().color = c;
        btn.selectedSprites.GetComponent<SpriteRenderer>().color = c;
        AddCatArt(btn, label, cat, c);
        if (cat >= CatKind0) CatLabels[cat] = (label, name.ToString());
        if (cat >= CatGame) AddCatDot(btn, cat);
        btn.OnMouseOver = new UnityEvent();
        btn.OnMouseOut = new UnityEvent();
        btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        btn.OnClick.AddListener(CatAction(cat));
        menu.ControllerSelectable.Add(btn);
        Cats.Add((btn, cat));
    }

    // 陣営のボタンの名前の右に「出る役職の数/全部の数」、初期値から変えた設定がある時は右上の角に橙の点 (値を変えるたびに書き直す)
    private static readonly Dictionary<int, (TextMeshPro Label, string Name)> CatLabels = new();
    private static readonly Dictionary<int, int> CatShown = new(); // 今出している数 (変わった陣営だけ書き直す)
    private static readonly Dictionary<int, GameObject> CatDots = new();

    private static void AddCatDot(PassiveButton btn, int cat)
    {
        var bg = btn.inactiveSprites ? btn.inactiveSprites.GetComponent<SpriteRenderer>() : null;
        if (!bg || !UI.UiAssets.Circle) return;
        var t = btn.transform;
        var b = bg.bounds;
        // 縁の角の少し内側。ボタンは縦横で縮みが違うので、丸の横を戻す
        float d = b.size.y * 0.24f;
        var pos = t.InverseTransformPoint(new Vector3(b.max.x - d * 0.55f, b.max.y - d * 0.55f, b.center.z));
        var ls = t.lossyScale;
        var dot = UI.ChangeDot.Make(t, new Vector3(pos.x, pos.y, -0.02f), btn.gameObject.layer, 1f);
        dot.transform.localScale = new Vector3(d / ls.x, d / ls.y, 1f);
        dot.sortingOrder = bg.sortingOrder + 5;
        var inner = dot.transform.GetChild(0).GetComponent<SpriteRenderer>();
        if (inner) inner.sortingOrder = bg.sortingOrder + 6;
        dot.gameObject.SetActive(false);
        CatDots[cat] = dot.gameObject;
    }

    internal static void RefreshCatCounts()
    {
        foreach (var (cat, (label, name)) in CatLabels)
        {
            if (!label) continue;
            int on = 0, all = 0;
            bool changed = false;
            foreach (var sec in Registry.SectionsOf(RoleMenu.KindTab(cat - CatKind0)))
            {
                if (!changed)
                    foreach (var opt in sec.Opts)
                        if (opt.Changed) { changed = true; break; }
                var a = sec.Role;
                if (a == null) continue;
                all++;
                if (a.Chance.Value > 0 && a.Count.Value > 0) on++;
            }
            int key = on << 16 | all << 1 | (changed ? 1 : 0);
            if (CatShown.TryGetValue(cat, out int shown) && shown == key) continue;
            CatShown[cat] = key;
            label.text = $"{name}<size=65%>  {on}/{all}</size>";
            if (CatDots.TryGetValue(cat, out var dot) && dot) dot.SetActive(changed);
        }
        // ゲームと全般: MRP の全般の項目のうち、その頁に並ぶ物 (ゲームの頁へ回した物 = OffTab) に初期値から変えた物があれば点
        bool game = false, general = false;
        foreach (var sec in Registry.SectionsOf(Tab.General))
            foreach (var opt in sec.Opts)
                if (opt.Changed && !opt.NoChangeMark)
                {
                    if (opt.OffTab) game = true;
                    else general = true;
                }
        SetCatDot(CatGame, game);
        SetCatDot(CatGeneral, general);
    }

    private static void SetCatDot(int cat, bool on)
    {
        if (CatDots.TryGetValue(cat, out var dot) && dot && dot.activeSelf != on) dot.SetActive(on);
    }

    // 左の列のボタンの左端に、その頁の色のクルーの絵 (メインメニューの絵のポーズ)。字は絵の右の残りの幅へ寄せる
    private static readonly int[] CatPose = { -1, 5, 5, 1, 2, 3, 0 }; // 番号 = CatPresets..CatKind0+AddonKind

    private static void AddCatArt(PassiveButton btn, TextMeshPro label, int cat, Color c)
    {
        int pose = cat < CatPose.Length ? CatPose[cat] : -1;
        var bg = btn.inactiveSprites.GetComponent<SpriteRenderer>();
        if (pose < 0 || !bg) return;
        var t = btn.transform;
        var b = bg.bounds;
        Vector3 min = t.InverseTransformPoint(b.min), max = t.InverseTransformPoint(b.max);
        float h = max.y - min.y;
        var ls = t.lossyScale;
        float aspect = ls.y / ls.x; // ボタンは縦横で縮みが違うので、絵の横を戻す
        float artH = h * CatArtHeight;
        var sr = UI.CrewArt.Place(t, pose, c, new Vector3(min.x + h * CatArtX * aspect, min.y + h * 0.04f, -0.01f), artH, btn.gameObject.layer, bg.sortingOrder + 1);
        if (!sr) return;
        var s = sr.transform.localScale;
        sr.transform.localScale = new Vector3(s.x * aspect, s.y, s.z);
        // 絵の左端をボタンの左の縁の少し内側へ (ポーズで横幅が違う)
        sr.transform.position += new Vector3(b.min.x + b.size.y * 0.08f - sr.bounds.min.x, 0f, 0f);
        CatArts[cat] = (sr.transform, sr.transform.localPosition, sr.transform.localScale, h * 0.07f);
        if (label.sortingOrder <= sr.sortingOrder) label.sortingOrder = sr.sortingOrder + 1;
        // 字の左の余白 = 絵の右端まで
        float right = sr.bounds.max.x;
        var rt = label.rectTransform;
        float edge = label.transform.InverseTransformPoint(new Vector3(right, b.center.y, b.center.z)).x;
        float m = edge - rt.rect.xMin;
        if (m > 0f) label.margin = new Vector4(m, 0f, 0f, 0f);
    }

    private const float CatArtHeight = 1.05f, CatArtX = 0.5f, CatArtPicked = 1.15f;
    private static readonly Dictionary<int, (Transform T, Vector3 Pos, Vector3 Scale, float Lift)> CatArts = new();

    private static UnityAction CatAction(int cat)
    {
        int key = 1000 + cat;
        if (!Clicks.TryGetValue(key, out var a)) Clicks[key] = a = (UnityAction)(() => SelectCat(cat));
        return a;
    }

    private static void SelectCat(int cat)
    {
        var menu = GameSettingMenu.Instance;
        if (!menu) return;
        int page = cat switch
        {
            CatPresets => Pages.FindIndex(p => p.Presets),
            CatGeneral => Pages.FindIndex(p => !p.Roles && !p.Presets),
            >= CatKind0 => Pages.FindIndex(p => p.Roles),
            _ => -1,
        };
        if (cat >= CatKind0) RolePage.SetTeam(cat - CatKind0);
        menu.ChangeTab(page < 0 ? 1 : FirstTab + page, false);
    }

    // 今の頁に当たる左の列のボタンだけを選んだ見た目に (-1 = どれも選ばない・検索の間)
    private static void HighlightCat(int tabNum)
    {
        int cur = -1;
        if (tabNum == 1) cur = CatGame;
        else if (tabNum >= FirstTab && tabNum - FirstTab < Pages.Count)
        {
            var p = Pages[tabNum - FirstTab];
            cur = p.Presets ? CatPresets : p.Roles ? CatKind0 + RolePage.Team : CatGeneral;
        }
        foreach (var (btn, cat) in Cats)
            if (btn) btn.SelectButton(cat == cur);
        // 選んだ頁のクルーは少し大きく持ち上げる
        foreach (var (cat, (t, pos, scale, lift)) in CatArts)
        {
            if (!t) continue;
            bool on = cat == cur;
            t.localScale = on ? scale * CatArtPicked : scale;
            t.localPosition = on ? new Vector3(pos.x, pos.y + lift, pos.z) : pos;
        }
    }

    // 左の列の一番下のボタンのすぐ下 (世界座標の左上と幅)。陣営の頁ではここに絞り込みの札を置く
    internal static bool CatFoot(out Vector3 topLeft, out float width)
    {
        topLeft = default;
        width = 0f;
        if (Cats.Count == 0) return false;
        var btn = Cats[Cats.Count - 1].Btn;
        var col = btn ? btn.GetComponent<Collider2D>() : null;
        if (!col) return false;
        var b = col.bounds;
        topLeft = new Vector3(b.min.x, b.min.y - b.size.y * 0.25f, b.center.z);
        width = b.size.x;
        return true;
    }

    // ボタンを小さくする。文字は横だけ潰れないよう縦横の縮みを揃え、はみ出す分は自動で小さくする
    private static void Compact(PassiveButton b, Vector3 pos, Vector3 scale, float sx = CompactX, float sy = CompactY)
    {
        b.transform.localPosition = pos;
        b.transform.localScale = new Vector3(scale.x * sx, scale.y * sy, scale.z);
        var label = b.GetComponentInChildren<TextMeshPro>();
        if (!label) return;
        float fix = sy / sx;
        var ls = label.transform.localScale;
        label.transform.localScale = new Vector3(ls.x * fix, ls.y, ls.z);
        var rt = label.rectTransform;
        rt.sizeDelta = new Vector2(rt.sizeDelta.x / fix, rt.sizeDelta.y);
        label.enableAutoSizing = true;
        label.fontSizeMin = 1f;
        label.fontSizeMax = label.fontSize;
        label.alignment = TextAlignmentOptions.Center;
    }

    // タブのボタンに渡す処理はタブごとに 1 つだけ作って使い回す (il2cpp 側へ渡すたびに解放されない参照が増えるため)。
    // 捕まえるのは番号だけ (ボタンや画面を捕まえると、閉じた後も解放されなくなる)
    private static readonly Dictionary<int, UnityAction> Clicks = new();

    private static UnityAction ClickAction(int tabNum)
    {
        if (!Clicks.TryGetValue(tabNum, out var a)) Clicks[tabNum] = a = (UnityAction)(() => ClickTab(tabNum));
        return a;
    }

    private static void ClickTab(int tabNum)
    {
        if (GameSettingMenu.Instance) GameSettingMenu.Instance.ChangeTab(tabNum, false);
    }

    // 検索欄の文字が変わった
    private static void OnSearch(string q)
    {
        if (_leavingSearch) return;
        var menu = GameSettingMenu.Instance;
        int roles = Pages.FindIndex(p => p.Roles);
        if (!menu || roles < 0) return;
        if (RoleSearch.Tokens(q).Length == 0)
        {
            if (!_searching) return;
            _searching = false;
            RolePage.SetQuery("");
            menu.ChangeTab(_returnTab, false);
            return;
        }
        if (!_searching)
        {
            _searching = true;
            _returnTab = _currentTab;
            menu.PresetsTab.gameObject.SetActive(false);
            menu.GameSettingsTab.gameObject.SetActive(false);
            menu.RoleSettingsTab.gameObject.SetActive(false);
            menu.GamePresetsButton.SelectButton(false);
            menu.GameSettingsButton.SelectButton(false);
            menu.RoleSettingsButton.SelectButton(false);
            ShowPage(roles);
            HighlightCat(-1);
            menu.ToggleLeftSideDarkener(true);
            menu.ToggleRightSideDarkener(false);
            RefreshAll(); // 他のタブで変えた値を結果の行へ
        }
        RolePage.SetQuery(q);
    }

    // 試験用: 検索欄に打ち込んだのと同じことをする
    internal static bool TypeSearch(string q)
    {
        if (_search == null) return false;
        _search.Type(q);
        return true;
    }

    // true = 本編の処理も続ける
    public static bool OnChangeTab(GameSettingMenu menu, int tabNum, bool previewOnly)
    {
        bool commit = !previewOnly || Controller.currentTouchType == Controller.TouchType.Joystick;
        if (commit)
        {
            // 検索の間にタブを押した: 検索をやめてそのタブへ
            if (_searching)
            {
                _searching = false;
                _leavingSearch = true;
                try { _search?.Clear(); }
                finally { _leavingSearch = false; }
                RolePage.SetQuery("");
            }
            _currentTab = tabNum;
            HighlightCat(tabNum);
            RefreshAll();
        }
        if (tabNum < FirstTab)
        {
            if (commit) ShowPage(-1);
            return true;
        }

        int index = tabNum - FirstTab;
        if (index >= Pages.Count) return false;
        if (commit)
        {
            menu.PresetsTab.gameObject.SetActive(false);
            menu.GameSettingsTab.gameObject.SetActive(false);
            menu.RoleSettingsTab.gameObject.SetActive(false);
            menu.GamePresetsButton.SelectButton(false);
            menu.GameSettingsButton.SelectButton(false);
            menu.RoleSettingsButton.SelectButton(false);
            ShowPage(index);
            var desc = menu.MenuDescriptionText;
            var tr = desc.GetComponent<TextTranslatorTMP>();
            if (tr) Object.Destroy(tr);
            desc.text = Pages[index].Presets ? PresetPage.Description
                : Pages[index].Roles
                ? RoleMenu.DefaultDescription
                : new Text("SettingsMenu.Description");
        }
        if (previewOnly)
        {
            menu.ToggleLeftSideDarkener(false);
            menu.ToggleRightSideDarkener(true);
        }
        else
        {
            menu.ToggleLeftSideDarkener(true);
            menu.ToggleRightSideDarkener(false);
        }
        return false;
    }

    private static void ShowPage(int index)
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            var p = Pages[i];
            if (p.Menu) p.Menu.gameObject.SetActive(i == index);
        }
    }

    // 複製したタブが初めて開かれた時に行を作る。true = 本編の処理も続ける
    public static bool OnInitialize(GameOptionsMenu m)
    {
        if (!IsOurMenu(m, out int index)) return true;
        if (m.Children == null || m.Children.Count == 0)
        {
            m.MapPicker.gameObject.SetActive(false);
            m.Children = new Il2CppSystem.Collections.Generic.List<OptionBehaviour>();
            try
            {
                if (Pages[index].Presets) PresetPage.Build(m, GameSettingMenu.Instance);
                else if (Pages[index].Roles) RolePage.Build(m);
                else SettingsPages.General.Build(m);
            }
            catch (Exception e) { Plugin.Logger.LogError($"settings tab build: {e}"); }
            m.cachedData = GameOptionsManager.Instance.CurrentGameOptions;
            m.InitializeControllerNavigation();
        }
        return false;
    }

    // プリセットを読み込んだ後など、値を外から変えた時に開いている行を全部描き直す
    public static void RefreshAll()
    {
        SettingsPages.RefreshFromOutside();
        RefreshCatCounts();
    }

    public static void OnClose()
    {
        foreach (var (btn, _) in Cats)
            if (btn) btn.OnClick.RemoveAllListeners();
        Cats.Clear();
        CatLabels.Clear();
        CatShown.Clear();
        CatDots.Clear();
        CatArts.Clear();
        // 閉じた画面の行は破棄され、そのアドレスは別の物に使い回されうるので、ここで全部忘れる
        PresetPage.Forget();
        RolePage.Forget();
        SettingsPages.Forget();
        if (_search != null) SearchBox.Forget(_search);
        _search = null;
        _searching = false;
        MenuToPage.Clear();
        Pages.Clear();
        CommitHostEdits();
    }

    // ホストが変えた値を保存して全員へ送る (設定画面を閉じた時・役職辞典の窓で値を変えて閉じた時)
    internal static void CommitHostEdits()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        Registry.Save();
        PresetLabel.Recheck();
        OptionSync.SendAll();
        Roles.Builtin.VanillaRoles.OnMenuClosed();
        Roles.TeamSizes.Recheck();
    }
}

[HarmonyPatch(typeof(GameSettingMenu))]
internal static class GameSettingMenuPatch
{
    [HarmonyPatch("OnEnable"), HarmonyPrefix]
    public static void OnEnable(GameSettingMenu __instance) => SettingsMenu.OnMenuEnable(__instance);

    [HarmonyPatch("Start"), HarmonyPostfix]
    public static void Start(GameSettingMenu __instance)
    {
        try { SettingsMenu.OnMenuStart(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"settings menu: {e}"); }
    }

    [HarmonyPatch(nameof(GameSettingMenu.ChangeTab)), HarmonyPrefix]
    public static bool ChangeTab(GameSettingMenu __instance, [HarmonyArgument(0)] int tabNum, [HarmonyArgument(1)] bool previewOnly) =>
        SettingsMenu.OnChangeTab(__instance, tabNum, previewOnly);

    // 設定画面の上に自前の窓がある時の Esc は窓だけを閉じる (設定画面も自分で Esc を見て閉じるため)
    [HarmonyPatch(nameof(GameSettingMenu.Close)), HarmonyPrefix]
    public static bool Close()
    {
        if (UI.UiWindow.EscapeTaken()) return false;
        SettingsMenu.OnClose();
        return true;
    }
}

[HarmonyPatch(typeof(GameOptionsMenu))]
internal static class GameOptionsMenuPatch
{
    [HarmonyPatch("Initialize"), HarmonyPrefix]
    public static bool Initialize(GameOptionsMenu __instance) => SettingsMenu.OnInitialize(__instance);

    [HarmonyPatch("CreateSettings"), HarmonyPrefix]
    public static bool CreateSettings(GameOptionsMenu __instance) => !SettingsMenu.IsOurMenu(__instance, out _);

    // 本編のゲーム設定のタブ: 本編の行は隠し、本編の設定と MRP のゲームのルールを自前の頁で並べる
    [HarmonyPatch("CreateSettings"), HarmonyPostfix]
    public static void CreateSettingsPost(GameOptionsMenu __instance)
    {
        if (SettingsMenu.IsOurMenu(__instance, out _)) return;
        try { SettingsPages.Game.Build(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"game page: {e}"); }
    }
}
