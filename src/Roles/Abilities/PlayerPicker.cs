// Opening a copy of the vanilla shapeshift menu as a player picker based on https://github.com/All-Of-Us-Mods/MiraAPI
// (CustomPlayerMenu, LGPL-2.1)
using System;
using AmongUs.GameOptions;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 人を選ぶ画面。本編の変身の画面 (ShapeshifterMinigame) を複製して開き、本編が並べた札を「選べる人」の札に作り直す。
// 見た目・閉じるボタン・15 人を超えた時のページ送り (CrowdPages) は本編の変身の画面と同じ。
// 札を押した時の処理は人の番号ごとに 1 回だけ作って使い回し (開くたびに作ると本編へ渡すたびに漏れる)、今開いている画面の受け手へ渡す
public static class PlayerPicker
{
    private static ShapeshifterMinigame _menu;
    private static Func<NetworkedPlayerInfo, bool> _filter;
    private static Action<byte> _onPick;
    private static readonly Il2CppSystem.Action[] Picks = new Il2CppSystem.Action[256];

    public static bool IsOpen => _menu;

    // 開けたら true。filter = 札を出す人 (死んだ人・自分も渡る)・onPick = 選んだ人の番号 (閉じるボタンでは呼ばない)
    public static bool Open(Func<NetworkedPlayerInfo, bool> filter, Action<byte> onPick)
    {
        if (IsOpen || Minigame.Instance || !Camera.main || !RoleManager.InstanceExists) return false;
        var role = RoleManager.Instance.GetRole(RoleTypes.Shapeshifter)?.TryCast<ShapeshifterRole>();
        if (role == null || !role.ShapeshifterMenu) return false;
        _filter = filter;
        _onPick = onPick;
        var menu = UnityEngine.Object.Instantiate(role.ShapeshifterMenu, Camera.main.transform, false);
        menu.transform.localPosition = new Vector3(0f, 0f, -50f);
        _menu = menu;
        try { menu.Begin(null); }
        catch
        {
            Close();
            throw;
        }
        return true;
    }

    public static void Close()
    {
        var menu = _menu;
        _menu = null;
        if (menu) menu.Close();
    }

    private static void Pick(byte id)
    {
        var cb = _onPick;
        Close();
        try { cb?.Invoke(id); }
        catch (Exception e) { Plugin.Logger.LogError($"player picker: {e}"); }
    }

    // 本編の Begin の後 (ページ送りより先)。本編の札を消して、選べる人の札を本編と同じ並びで作り直す
    internal static void AfterBegin(ShapeshifterMinigame menu)
    {
        if (!_menu || menu.Pointer != _menu.Pointer) return;
        var list = menu.potentialVictims;
        // 本編の札は消さずに隠すだけ (着せ替えの絵の読み込みが後から札に書くので、消すとそこで落ちる)。画面と一緒に消える
        foreach (var old in list)
            if (old) old.gameObject.SetActive(false);
        list.Clear();

        var lp = PlayerControl.LocalPlayer;
        var selectable = new Il2CppSystem.Collections.Generic.List<UiElement>();
        int n = 0;
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null || info.Disconnected || !(_filter?.Invoke(info) ?? true)) continue;
            var panel = UnityEngine.Object.Instantiate(menu.PanelPrefab, menu.transform);
            panel.transform.localPosition = new Vector3(menu.XStart + n % 3 * menu.XOffset, menu.YStart + n / 3 * menu.YOffset, -1f);
            byte id = info.PlayerId;
            panel.SetPlayer(n, info, Picks[id] ??= (Il2CppSystem.Action)(() => Pick(id)));
            // 本編と同じく、同じ陣営の色の人だけ名前をその色に
            if (lp && lp.Data != null && lp.Data.Role && info.Role)
                panel.NameText.color = lp.Data.Role.NameColor == info.Role.NameColor ? info.Role.NameColor : Color.white;
            list.Add(panel);
            selectable.Add(panel.Button);
            n++;
        }
        // コントローラーで選べる札も作り直した物に
        var cm = ControllerManager.Instance;
        if (cm)
        {
            cm.CloseOverlayMenu(menu.name);
            cm.OpenOverlayMenu(menu.name, menu.BackButton, menu.DefaultButtonSelected, selectable);
        }
    }

    // 確かめ用: 札の一覧 / その人の札を押す (本物の押下と同じ OnClick)
    internal static string Probe(int press)
    {
        if (!IsOpen) return "closed";
        var sb = new System.Text.StringBuilder("open panels=");
        var list = _menu.potentialVictims;
        sb.Append(list.Count);
        PassiveButton hit = null;
        foreach (var p in list)
        {
            if (!p) continue;
            string name = p.NameText ? p.NameText.text : "?";
            sb.Append($" [{name}{(p.gameObject.activeInHierarchy ? "" : " (hidden)")}]");
            if (press >= 0 && GameData.Instance.GetPlayerById((byte)press) is { } info && name == info.PlayerName) hit = p.Button;
        }
        if (press >= 0)
        {
            if (hit) { hit.OnClick.Invoke(); sb.Append($" pressed={press}"); }
            else sb.Append(" press: no panel");
        }
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(ShapeshifterMinigame), nameof(ShapeshifterMinigame.Begin))]
internal static class PlayerPickerBeginPatch
{
    [HarmonyPostfix, HarmonyPriority(Priority.First)] // ページ送り (CrowdPages) より先に札を作り直す
    public static void Postfix(ShapeshifterMinigame __instance)
    {
        try { PlayerPicker.AfterBegin(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"player picker: {e}"); }
    }
}
