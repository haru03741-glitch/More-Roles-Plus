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
internal static class SettingsMenu
{
    private const int FirstTab = 3; // 本編のタブは 0〜2

    // タブの並び (1920x1080 の画面での px)。本編の「ゲーム設定」ボタンの中心が基準
    private const float GameButtonX = 370f, GameButtonY = 760f;
    private const float LeftColumnX = 240f, RightColumnX = 474f, FirstRowY = 640f, RowStepY = 95f;
    private const float CompactX = 0.48f, CompactY = 0.8f;

    private sealed class Page
    {
        public bool Roles;  // false = 全般のタブ
        public PassiveButton Button;
        public GameOptionsMenu Menu;
    }

    private static readonly List<Page> Pages = new();
    private static readonly Dictionary<IntPtr, int> MenuToPage = new();
    private static readonly Dictionary<IntPtr, Opt> Rows = new();
    private static GameOptionsMenu _template;
    private static FloatGameSetting _numberData;
    private static CheckboxGameSetting _checkData;

    public static bool TryRow(Il2CppObjectBase row, out Opt opt)
    {
        if (Rows.Count == 0) { opt = null; return false; }
        return Rows.TryGetValue(row.Pointer, out opt);
    }

    // 試験用: MRP のタブのボタンを押す (OnClick をそのまま呼ぶ)
    internal static string ClickTabButton(int page)
    {
        if (page < 0 || page >= Pages.Count || !Pages[page].Button) return "no button";
        Pages[page].Button.OnClick.Invoke();
        return Pages[page].Menu && Pages[page].Menu.gameObject.activeSelf ? "shown" : "not shown";
    }

    // 試験用: 開いているタブの n 行目の ± / チェックを押したのと同じことをする
    internal static string PressRow(int page, int row, int delta)
    {
        if (page < 0 || page >= Pages.Count || !Pages[page].Menu) return "no page";
        var children = Pages[page].Menu.Children;
        if (children == null || row < 0 || row >= children.Count) return "no row";
        var b = children[row];
        var num = b.TryCast<NumberOption>();
        if (num != null) { if (delta > 0) num.Increase(); else num.Decrease(); return num.ValueText.text; }
        var tog = b.TryCast<ToggleOption>();
        if (tog != null) { tog.Toggle(); return tog.CheckMark.enabled ? "checked" : "unchecked"; }
        return "unknown row";
    }

    // 試験用: 開いている MRP のタブ
    internal static Transform OpenPage()
    {
        foreach (var p in Pages)
            if (p.Menu && p.Menu.gameObject.activeSelf) return p.Menu.transform;
        return null;
    }

    public static bool IsOurMenu(GameOptionsMenu m, out int page) => MenuToPage.TryGetValue(m.Pointer, out page);

    private static Text TabName(bool roles) => roles ? RoleMenu.TabName : new Text("MRP 全般", "MRP");

    private static Color TabColor(bool roles) => roles ? new Color(1f, 0.8f, 0.35f) : new Color(0.75f, 0.6f, 1f);

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
        Rows.Clear();
        RoleMenu.Forget();
        if (!_template) return;

        // 左の列を 2 列に詰める: 左 = 本編の 3 つ、右 = MRP のタブ。
        // 位置は本編の 2 つのボタンの間隔 (1920x1080 の画面で 113px) を物差しにして px で決める
        var gameBtn = menu.GameSettingsButton;
        Vector3 origin = gameBtn.transform.localPosition;
        float unit = Mathf.Abs(menu.RoleSettingsButton.transform.localPosition.y - origin.y) / 113f;
        Vector3 scale = gameBtn.transform.localScale;
        Vector3 At(float px, float py) => origin + new Vector3((px - GameButtonX) * unit, (GameButtonY - py) * unit, 0f);
        PassiveButton[] vanilla = { menu.GamePresetsButton, gameBtn, menu.RoleSettingsButton };

        foreach (bool roles in new[] { false, true })
        {
            if (roles ? !RoleMenu.HasContent() : Registry.SectionsOf(Tab.General).Count == 0) continue;
            int index = Pages.Count;
            string tab = roles ? "Roles" : "General";

            var btn = Object.Instantiate(gameBtn, gameBtn.transform.parent);
            btn.name = "MrpTabButton_" + tab;
            Compact(btn, At(RightColumnX, FirstRowY + index * RowStepY), scale);
            var label = btn.GetComponentInChildren<TextMeshPro>();
            var tr = label.GetComponent<TextTranslatorTMP>();
            if (tr) Object.Destroy(tr);
            label.text = TabName(roles);
            Color c = TabColor(roles);
            btn.inactiveSprites.GetComponent<SpriteRenderer>().color = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 1f);
            btn.activeSprites.GetComponent<SpriteRenderer>().color = c;
            btn.selectedSprites.GetComponent<SpriteRenderer>().color = c;
            btn.OnMouseOver = new UnityEvent();
            btn.OnMouseOut = new UnityEvent();
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            btn.OnClick.AddListener(ClickAction(FirstTab + index));
            menu.ControllerSelectable.Add(btn);

            var page = Object.Instantiate(_template, _template.transform.parent);
            page.name = "MrpTab_" + tab;
            page.gameObject.SetActive(false);
            MenuToPage[page.Pointer] = index;
            Pages.Add(new Page { Roles = roles, Button = btn, Menu = page });
        }
        // MRP のボタンは本編のボタンを複製して作るので、本編の方を縮めるのは複製の後
        for (int i = 0; i < vanilla.Length; i++) Compact(vanilla[i], At(LeftColumnX, FirstRowY + i * RowStepY), scale);
        // 本編の役職は MRP の役職タブに並ぶので、本編の役職のタブは出さない
        if (Pages.Exists(p => p.Roles)) menu.RoleSettingsButton.gameObject.SetActive(false);
    }

    // ボタンを小さくする。文字は横だけ潰れないよう縦横の縮みを揃え、はみ出す分は自動で小さくする
    private static void Compact(PassiveButton b, Vector3 pos, Vector3 scale)
    {
        b.transform.localPosition = pos;
        b.transform.localScale = new Vector3(scale.x * CompactX, scale.y * CompactY, scale.z);
        var label = b.GetComponentInChildren<TextMeshPro>();
        if (!label) return;
        const float fix = CompactY / CompactX;
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

    // true = 本編の処理も続ける
    public static bool OnChangeTab(GameSettingMenu menu, int tabNum, bool previewOnly)
    {
        bool commit = !previewOnly || Controller.currentTouchType == Controller.TouchType.Joystick;
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
            desc.text = Pages[index].Roles
                ? RoleMenu.DefaultDescription
                : new Text("More Roles Plus の設定。閉じた時に全員へ送られます。", "More Roles Plus settings. Sent to everyone when you close this menu.");
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
            if (p.Button) p.Button.SelectButton(i == index);
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
                if (Pages[index].Roles) RoleMenu.Build(m, GameSettingMenu.Instance);
                else Build(m, Tab.General);
            }
            catch (Exception e) { Plugin.Logger.LogError($"settings tab build: {e}"); }
            m.cachedData = GameOptionsManager.Instance.CurrentGameOptions;
            m.InitializeControllerNavigation();
        }
        return false;
    }

    private static void Build(GameOptionsMenu m, Tab tab)
    {
        EnsureData();
        const float rowX = 0.952f, headerX = -0.903f, z = -2f;
        float y = 2f;
        foreach (var sec in Registry.SectionsOf(tab))
        {
            var h = Object.Instantiate(m.categoryHeaderOrigin, m.settingsContainer);
            h.SetHeader(StringNames.RolesCategory, 20);
            h.Title.text = sec.Color != null ? $"<color={sec.Color}>{sec.Title}</color>" : sec.Title.ToString();
            if (sec.Color != null)
            {
                Color c = Roles.RoleDisplay.ParseColor(sec.Color);
                h.Background.color = new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 1f);
                h.Divider.color = c;
            }
            h.transform.localScale = new Vector3(0.63f, 0.63f, 1f);
            h.transform.localPosition = new Vector3(headerX, y, z);
            y -= 0.63f;

            foreach (var opt in sec.Opts)
            {
                var row = MakeRow(m, opt);
                row.transform.localPosition = new Vector3(rowX, y, z);
                m.Children.Add(row);
                y -= 0.45f;
            }
        }
        m.scrollBar.SetYBoundsMax(-y - 1.65f);
    }

    // 設定項目 1 つ分の行 (本編の数値 / チェックの行の複製)。操作は下のパッチが MRP の項目へ回す
    internal static OptionBehaviour MakeRow(GameOptionsMenu m, Opt opt)
    {
        EnsureData();
        OptionBehaviour row = opt.IsToggle
            ? Object.Instantiate(m.checkboxOrigin, m.settingsContainer)
            : Object.Instantiate(m.numberOptionOrigin, m.settingsContainer);
        row.SetClickMask(m.ButtonClickMask);
        row.SetUpFromData(opt.IsToggle ? _checkData : _numberData, 20);
        Rows[row.Pointer] = opt;
        Refresh(row, opt);
        return row;
    }

    private static void EnsureData()
    {
        if (!_numberData)
        {
            _numberData = ScriptableObject.CreateInstance<FloatGameSetting>();
            _numberData.Type = OptionTypes.Float;
            _numberData.Title = StringNames.Accept;
            _numberData.Value = 0f;
            _numberData.Increment = 1f;
            _numberData.ValidRange = new FloatRange(0f, 1000f);
            _numberData.FormatString = "";
            _numberData.ZeroIsInfinity = false;
            _numberData.SuffixType = NumberSuffixes.None;
        }
        if (!_checkData)
        {
            _checkData = ScriptableObject.CreateInstance<CheckboxGameSetting>();
            _checkData.Type = OptionTypes.Checkbox;
            _checkData.Title = StringNames.Accept;
        }
    }

    public static void Refresh(OptionBehaviour row, Opt opt)
    {
        opt.Sync();
        var num = row.TryCast<NumberOption>();
        if (num != null)
        {
            num.TitleText.text = opt.Label;
            num.ValueText.text = opt.Display();
            // 数値は端で止まるので、それ以上動かない側のボタンを灰色にする
            num.MinusBtn?.SetInteractable(opt.Index > 0);
            num.PlusBtn?.SetInteractable(opt.Index < opt.Count - 1);
            return;
        }
        var tog = row.TryCast<ToggleOption>();
        if (tog != null)
        {
            tog.TitleText.text = opt.Label;
            tog.CheckMark.enabled = opt.Index != 0;
        }
    }

    public static void OnClose()
    {
        foreach (var p in Pages)
        {
            if (p.Button) p.Button.OnClick.RemoveAllListeners();
        }
        // 閉じた画面の行は破棄され、そのアドレスは別の物に使い回されうるので、ここで全部忘れる
        Rows.Clear();
        RoleMenu.Forget();
        MenuToPage.Clear();
        Pages.Clear();
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        Registry.Save();
        OptionSync.SendAll();
        Roles.Builtin.VanillaRoles.OnMenuClosed();
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

    [HarmonyPatch(nameof(GameSettingMenu.Close)), HarmonyPrefix]
    public static void Close() => SettingsMenu.OnClose();
}

[HarmonyPatch(typeof(GameOptionsMenu))]
internal static class GameOptionsMenuPatch
{
    [HarmonyPatch("Initialize"), HarmonyPrefix]
    public static bool Initialize(GameOptionsMenu __instance) => SettingsMenu.OnInitialize(__instance);

    [HarmonyPatch("CreateSettings"), HarmonyPrefix]
    public static bool CreateSettings(GameOptionsMenu __instance) => !SettingsMenu.IsOurMenu(__instance, out _);
}

// MRP の行は本編の値の処理を通さず、MRP の設定項目を直接書き換える
[HarmonyPatch(typeof(NumberOption))]
internal static class NumberOptionPatch
{
    [HarmonyPatch(nameof(NumberOption.Initialize)), HarmonyPrefix]
    public static bool Initialize(NumberOption __instance) => !SettingsMenu.TryRow(__instance, out _);

    [HarmonyPatch("FixedUpdate"), HarmonyPrefix]
    public static bool FixedUpdate(NumberOption __instance) => !SettingsMenu.TryRow(__instance, out _);

    [HarmonyPatch(nameof(NumberOption.Increase)), HarmonyPrefix]
    public static bool Increase(NumberOption __instance) => Step(__instance, 1);

    [HarmonyPatch(nameof(NumberOption.Decrease)), HarmonyPrefix]
    public static bool Decrease(NumberOption __instance) => Step(__instance, -1);

    private static bool Step(NumberOption row, int delta)
    {
        if (!SettingsMenu.TryRow(row, out var opt)) return true;
        opt.Step(delta);
        SettingsMenu.Refresh(row, opt);
        return false;
    }
}

[HarmonyPatch(typeof(ToggleOption))]
internal static class ToggleOptionPatch
{
    [HarmonyPatch(nameof(ToggleOption.Initialize)), HarmonyPrefix]
    public static bool Initialize(ToggleOption __instance) => !SettingsMenu.TryRow(__instance, out _);

    [HarmonyPatch("FixedUpdate"), HarmonyPrefix]
    public static bool FixedUpdate(ToggleOption __instance) => !SettingsMenu.TryRow(__instance, out _);

    [HarmonyPatch(nameof(ToggleOption.Toggle)), HarmonyPrefix]
    public static bool Toggle(ToggleOption __instance)
    {
        if (!SettingsMenu.TryRow(__instance, out var opt)) return true;
        opt.Step(1);
        __instance.CheckMark.enabled = opt.Index != 0;
        return false;
    }
}
