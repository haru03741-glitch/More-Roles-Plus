// Based on the in-game settings view (F1) of https://github.com/TheOtherRolesAU/TheOtherRoles (GPL-3.0)
using System;
using System.Text;
using AmongUs.GameOptions;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 試合中に F1 (PC) で部屋の設定を画面いっぱいに出す。もう一度 F1 で消す。
// 左の列 = 本編の設定・残りの列 = MRP の設定 (/n と同じ文)。開くたびに文を作り直し、開いている間は何もしない
internal static class SettingsOverlay
{
    private const int Columns = 3, Order = 1000;
    private static GameObject _root;

    public static bool IsOpen => _root && _root.activeSelf;

    public static void Toggle()
    {
        if (IsOpen) { Hide(); return; }
        try { Show(); }
        catch (Exception e) { Plugin.Logger.LogError($"settings overlay: {e}"); Hide(); }
    }

    public static void Hide()
    {
        if (_root) Object.Destroy(_root);
        _root = null;
    }

    private static void Show()
    {
        var hud = Vanilla.Hud;
        var cam = Camera.main;
        if (!hud || !cam || !hud.TaskPanel || !hud.TaskPanel.taskText) return;
        Hide();
        float h = cam.orthographicSize, w = h * cam.aspect;

        _root = new GameObject("MrpSettingsOverlay") { layer = hud.gameObject.layer };
        _root.transform.SetParent(hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, -150f);
        // 右上のボタンなどは描く順番 (sortingOrder) が上がっているので、こちらはさらに上にする
        var back = MenuArt.Panel(_root.transform, "Back", 0.3f, new Vector2(w * 2f - 0.4f, h * 2f - 0.4f), Vector3.zero, new Color(0.05f, 0.05f, 0.07f, 0.95f), null);
        back.sortingOrder = Order;

        // 列ごとの文: 0 = 本編・1〜 = MRP を行数でおおむね半分ずつ
        var cols = new string[Columns];
        var opts = GameOptionsManager.Instance != null ? GameOptionsManager.Instance.CurrentGameOptions : null;
        int players = GameData.Instance ? GameData.Instance.PlayerCount : 0;
        cols[0] = (Lang.IsJapanese ? "<b>本編の設定</b>\n" : "<b>Game settings</b>\n") + (opts != null ? opts.ToHudString(players) : "");
        var mrp = Commands.InfoCommands.Now("").Split('\n');
        int per = (mrp.Length + Columns - 2) / (Columns - 1);
        for (int c = 1; c < Columns; c++)
        {
            var sb = new StringBuilder();
            if (c == 1) sb.Append(Lang.IsJapanese ? "<b>MRP の設定</b>\n" : "<b>More Roles Plus</b>\n");
            int from = (c - 1) * per;
            for (int i = from; i < Math.Min(mrp.Length, from + per); i++) sb.Append(mrp[i]).Append('\n');
            cols[c] = sb.ToString();
        }

        float colW = (w * 2f - 0.8f) / Columns;
        for (int c = 0; c < Columns; c++)
        {
            var tmp = Object.Instantiate(hud.TaskPanel.taskText, _root.transform);
            tmp.name = "Column" + c;
            tmp.gameObject.layer = _root.layer;
            if (tmp.font) tmp.fontSharedMaterial = tmp.font.material; // 作業一覧の切り抜きを外す
            tmp.transform.localPosition = new Vector3(-w + 0.4f + colW * c, h - 0.35f, -1f);
            tmp.transform.localScale = Vector3.one;
            tmp.rectTransform.pivot = new Vector2(0f, 1f);
            tmp.rectTransform.sizeDelta = new Vector2(colW - 0.15f, h * 2f - 0.7f);
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = true;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.8f;
            tmp.fontSizeMax = 1.5f;
            tmp.color = Color.white;
            tmp.richText = true;
            tmp.text = cols[c];
            tmp.sortingOrder = Order + 1;
        }
    }
}
