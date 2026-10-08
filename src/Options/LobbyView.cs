// Based on https://github.com/waffle-ful/Aeterna-End-K-not Patches/LobbyViewSettingsPanePatch.cs and https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System;
using AmongUs.GameOptions;
using HarmonyLib;
using MoreRolesPlus.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// ロビーの「設定を見る」画面 (客も開ける) に MRP のタブを 1 つ足し、MRP の設定を一覧で見せる。
// - 行は本編の閲覧用の行 (infoPanelOrigin) を複製して本編と同じ並び (2 列) に置く。
// - 作った物は本編の settingsInfo に入れる。消すのは本編 (タブを替えた時・画面を閉じた時)。
internal static class LobbyView
{
    // 本編の StringNames に無い値をタブの印に使う
    internal const StringNames MrpTab = (StringNames)0x4D5250;
    private const string ButtonName = "MrpViewTabButton";
    private const int MaskLayer = 61;

    // 本編の閲覧画面の並び (本編の定数と同じ)
    private const float StartY = 1.44f, LeftX = -8.95f, RightX = -3f, RowStepY = 0.85f, HeaderX = -9.77f, HeaderStepY = 1.05f;

    private static readonly Tab[] TabOrder = { Tab.General, Tab.Crew, Tab.Impostor, Tab.Neutral, Tab.Addon };

    // ボタンに渡す処理は 1 つだけ作って使い回す (il2cpp 側へ渡すたびに解放されない参照が増えるため)。何も捕まえない
    private static UnityAction _click;

    public static void OnAwake(LobbyViewSettingsPane pane)
    {
        Lang.Refresh();
        var task = pane.taskTabButton;
        var roles = pane.rolesTabButton;
        var btn = Object.Instantiate(roles, roles.transform.parent);
        btn.name = ButtonName;
        btn.transform.localPosition = roles.transform.localPosition + (roles.transform.localPosition - task.transform.localPosition);
        var label = btn.buttonText;
        var tr = label.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        label.text = "More Roles Plus";
        btn.OnMouseOver = new UnityEvent();
        btn.OnMouseOut = new UnityEvent();
        btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        btn.OnClick.AddListener(_click ??= (UnityAction)Click);
        btn.SelectButton(false);
        pane.ControllerSelectable.Add(btn);
    }

    private static void Click()
    {
        var pane = Pane();
        if (pane) pane.ChangeTab(MrpTab);
    }

    internal static LobbyViewSettingsPane Pane() =>
        Vanilla.LobbyInfo is { } info ? info.LobbyViewSettingsPane : null;

    // 本編がタブを描き直した後に呼ばれる。ChangeTab と RefreshTab の両方から来うるので、描いてあれば何もしない
    public static void AfterRefresh(LobbyViewSettingsPane pane)
    {
        bool ours = pane.currentTab == MrpTab;
        var btn = pane.rolesTabButton.transform.parent.Find(ButtonName);
        if (btn) btn.GetComponent<PassiveButton>().SelectButton(ours);
        if (!ours || pane.settingsInfo.Count > 0) return;

        pane.taskTabButton.SelectButton(false);
        pane.rolesTabButton.SelectButton(false);
        try { Draw(pane); }
        catch (Exception e) { Plugin.Logger.LogError($"lobby view: {e}"); }
    }

    private static void Draw(LobbyViewSettingsPane pane)
    {
        float y = StartY;
        var client = AmongUsClient.Instance;
        if (client && !client.AmHost && !OptionSync.HoldsHostValues)
        {
            Header(pane, ref y, new Text("ホストの設定をまだ受け取っていません", "Host settings have not arrived yet"), null);
            Finish(pane, y);
            return;
        }

        foreach (var tab in TabOrder)
        {
            foreach (var sec in Registry.SectionsOf(tab))
            {
                Header(pane, ref y, sec.Title, sec.Color);
                // 出ない役職は出現率だけ見せる (役職が増えても一覧が長くなりすぎないように)
                bool off = sec.Role != null && sec.Role.Chance.Value == 0;
                int col = 0;
                foreach (var opt in sec.Opts)
                {
                    if (off && opt != sec.Role.Chance) continue;
                    if (col == 2) { y -= RowStepY; col = 0; }
                    Row(pane, opt, col == 0 ? LeftX : RightX, y);
                    col++;
                }
                y -= RowStepY;
            }
        }
        Finish(pane, y);
    }

    private static void Header(LobbyViewSettingsPane pane, ref float y, Text title, string color)
    {
        var h = Object.Instantiate(pane.categoryHeaderOrigin, pane.settingsContainer);
        h.SetHeader(StringNames.Accept, MaskLayer);
        h.Title.text = color != null ? $"<color={color}>{title}</color>" : title.ToString();
        if (color != null)
        {
            Color c = Roles.RoleDisplay.ParseColor(color);
            h.Background.color = new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 1f);
            h.Divider.color = c;
        }
        h.transform.localScale = Vector3.one;
        h.transform.localPosition = new Vector3(HeaderX, y, -2f);
        pane.settingsInfo.Add(h.gameObject);
        y -= HeaderStepY;
    }

    private static void Row(LobbyViewSettingsPane pane, Opt opt, float x, float y)
    {
        var p = Object.Instantiate(pane.infoPanelOrigin, pane.settingsContainer);
        p.transform.localScale = Vector3.one;
        p.transform.localPosition = new Vector3(x, y, -2f);
        if (opt is BoolOpt b) p.SetInfoCheckbox(StringNames.Accept, MaskLayer, b.Value);
        else p.SetInfo(StringNames.Accept, opt.Display(), MaskLayer);
        p.titleText.text = opt.Label;
        pane.settingsInfo.Add(p.gameObject);
    }

    private static void Finish(LobbyViewSettingsPane pane, float y)
    {
        pane.scrollBar.SetYBoundsMax(-y - 2f);
        pane.scrollBar.ScrollToTop();
    }

    // ホストの設定が届いた時、MRP のタブを開いていれば描き直す
    public static void OnOptionsReceived()
    {
        var pane = Pane();
        if (pane && pane.isActiveAndEnabled && pane.currentTab == MrpTab) pane.RefreshTab();
    }

    // 試験用: 閲覧画面を開いてタブのボタンを押し (0 = 概要・1 = 役職・2 = MRP)、並んだ行の文字を返す
    internal static string Open(int tab, int max)
    {
        var info = Vanilla.LobbyInfo;
        var gsm = Vanilla.StartManager;
        if (!info || !gsm) return "no lobby";
        var pane = info.LobbyViewSettingsPane;
        if (!pane.isActiveAndEnabled)
        {
            // 本編の「見る」ボタンと同じ経路で開く (開くまでは画面の中身が作られていない)
            var view = AmongUsClient.Instance.AmHost ? gsm.HostViewButton : gsm.ClientViewButton;
            view.OnClick.Invoke();
            return "opening (send again)";
        }
        var btn = tab switch
        {
            0 => pane.taskTabButton,
            1 => pane.rolesTabButton,
            _ => pane.rolesTabButton.transform.parent.Find(ButtonName)?.GetComponent<PassiveButton>(),
        };
        if (!btn) return "no button";
        btn.OnClick.Invoke();
        var sb = new System.Text.StringBuilder();
        sb.Append("tab=").Append(pane.currentTab == MrpTab ? "MRP" : pane.currentTab.ToString());
        sb.Append(" items=").Append(pane.settingsInfo.Count);
        for (int i = 0; i < pane.settingsInfo.Count && i < max; i++)
        {
            var go = pane.settingsInfo[i];
            foreach (var t in go.GetComponentsInChildren<TextMeshPro>())
                if (!string.IsNullOrEmpty(t.text)) sb.Append(" | ").Append(t.text);
        }
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(LobbyViewSettingsPane))]
internal static class LobbyViewSettingsPanePatch
{
    [HarmonyPatch("Awake"), HarmonyPostfix]
    public static void Awake(LobbyViewSettingsPane __instance)
    {
        try { LobbyView.OnAwake(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"lobby view tab: {e}"); }
    }

    // MRP のタブの時は本編の一覧を描かない
    [HarmonyPatch("DrawNormalTab"), HarmonyPrefix]
    public static bool DrawNormalTab(LobbyViewSettingsPane __instance) => __instance.currentTab != LobbyView.MrpTab;

    [HarmonyPatch(nameof(LobbyViewSettingsPane.ChangeTab)), HarmonyPostfix]
    public static void ChangeTab(LobbyViewSettingsPane __instance) => LobbyView.AfterRefresh(__instance);

    [HarmonyPatch(nameof(LobbyViewSettingsPane.RefreshTab)), HarmonyPostfix]
    public static void RefreshTab(LobbyViewSettingsPane __instance) => LobbyView.AfterRefresh(__instance);
}
