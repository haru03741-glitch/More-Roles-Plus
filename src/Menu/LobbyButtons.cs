using System;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// ロビーの板の「ゲーム設定」: 「見る」を出さない。ホストは「編集」だけを真ん中に置き、
// 参加者 (本編は横長の「見る」1 つだけ) は見出しごと隠す。本編は毎フレーム出し入れするので後から直す
[HarmonyPatch(typeof(LobbyInfoPane), nameof(LobbyInfoPane.Update))]
internal static class LobbyButtons
{
    // 「編集」の置き場所: 「編集」と「見る」の真ん中 (ボタンの絵は支点から右へずれて描かれているので、支点を同じだけ動かす)
    private const float EditX = -1.015f;

    public static void Postfix(LobbyInfoPane __instance)
    {
        try { Sync(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"lobby buttons: {e.Message}"); }
    }

    private static void Sync(LobbyInfoPane pane)
    {
        var group = pane ? Keybinds.FindDeep(pane.transform, "GameSettingsButtons") : null;
        if (!group) return;
        var host = group.Find("Host Buttons");
        var client = group.Find("Client Buttons");
        if (host)
        {
            Hide(host.Find("View"));
            var edit = host.Find("Edit");
            if (edit && Mathf.Abs(edit.localPosition.x - EditX) > 0.001f)
                edit.localPosition = new Vector3(EditX, edit.localPosition.y, edit.localPosition.z);
        }
        Hide(client);
        // 参加者には押せる物が無いので見出しも隠す
        var header = group.Find("ButtonSettingsHeader");
        bool show = host && host.gameObject.activeSelf;
        if (header && header.gameObject.activeSelf != show) header.gameObject.SetActive(show);
    }

    private static void Hide(Transform t)
    {
        if (t && t.gameObject.activeSelf) t.gameObject.SetActive(false);
    }
}
