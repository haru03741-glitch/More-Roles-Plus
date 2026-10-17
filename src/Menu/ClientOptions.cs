// Based on https://github.com/tukasa0001/TownOfHost pull/1265 (ClientOptionItem), as carried by
// https://github.com/Gurge44/EndlessHostRoles and https://github.com/waffle-ful/Aeterna-End-K-not (GPL-3.0)
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 自分の端末だけに効く ON/OFF (見た目・操作の好み)。本編の設定画面 (Esc) に「MRP の設定」ボタンを足し、押すと並べた板が開く。
// 値は BepInEx の設定ファイル ([Client] 節) に残る。部屋の設定 (ホストが決めて全員に配る物) とは別
internal static class ClientOptions
{
    private sealed class Item
    {
        public ConfigEntry<bool> Entry;
        public Text Label;
        public Action Changed;
        public SpriteRenderer Back;
        public ButtonRolloverHandler Rollover;
    }

    private static readonly List<Item> Items = new();
    private static ConfigFile _cfg;

    public static ConfigEntry<bool> DarkTheme { get; private set; }
    public static ConfigEntry<bool> DarkMeeting { get; private set; }
    public static ConfigEntry<bool> EndSummary { get; private set; }

    public static void Bind(ConfigFile cfg)
    {
        _cfg = cfg;
        DarkTheme = Add("DarkTheme", false, new Text("チャットを暗い色にする", "Dark chat"), ChatTheme.Refresh);
        EndSummary = Add("EndSummary", true, new Text("試合の終わりに全員の役職を出す", "Show everyone's role at game end"), null);
        DarkMeeting = Add("DarkMeeting", false, new Text("会議の画面を暗い色にする", "Dark meeting screen"), null); // 次の会議から
    }

    private static ConfigEntry<bool> Add(string key, bool def, Text label, Action changed)
    {
        var e = _cfg.Bind("Client", key, def, label.En);
        Items.Add(new Item { Entry = e, Label = label, Changed = changed });
        return e;
    }

    private static readonly Color On = new Color32(0, 165, 255, 255);
    private static readonly Color Off = new Color32(77, 77, 77, 255);

    private static SpriteRenderer _panel; // 開いた板 (本編の設定画面の子なので画面と一緒に消える)

    // 押した時の処理は番号ごとに 1 つだけ作って使い回す (il2cpp 側へ渡すたびに解放されない参照が増えるため)。何も捕まえない
    private static readonly Dictionary<int, UnityAction> Clicks = new();
    private static UnityAction _open, _close;

    public static void Build(OptionsMenuBehaviour menu)
    {
        var proto = menu.DisableMouseMovement;
        if (!proto || _panel) return;
        Lang.Refresh();

        _panel = Object.Instantiate(menu.Background, menu.transform);
        _panel.name = "MrpClientOptions";
        _panel.transform.localScale = new Vector3(0.9f, 0.9f, 1f);
        _panel.transform.localPosition += Vector3.back * 8f;
        _panel.size += new Vector2(3f, 0f);
        _panel.gameObject.SetActive(false);

        var close = MakeButton(proto, _panel.transform, "Back", new Text("戻る", "Back"), out var closeBack, out _);
        close.transform.localPosition = new Vector3(2.6f, -2.3f, -6f);
        closeBack.color = Palette.DisabledGrey;
        SetClick(close, _close ??= (UnityAction)(() => { if (_panel) _panel.gameObject.SetActive(false); }));

        // 開くボタンは「ゲームを抜ける」の所に置き、抜ける・戻るのボタンは左右へ寄せる
        PassiveButton leave = null, ret = null;
        foreach (var ui in menu.ControllerSelectable)
        {
            if (!ui) continue;
            if (ui.name == "LeaveGameButton") leave = ui.GetComponent<PassiveButton>();
            else if (ui.name == "ReturnToGameButton") ret = ui.GetComponent<PassiveButton>();
        }
        var tab = proto.transform.parent.parent.parent;
        var open = MakeButton(proto, tab, "MrpClientOptionsButton", new Text("MRP の設定", "MRP Options"), out var openBack, out _);
        open.transform.localPosition = leave ? leave.transform.localPosition : new Vector3(0f, -2.4f, 1f);
        openBack.color = On;
        SetClick(open, _open ??= (UnityAction)(() => { if (_panel) _panel.gameObject.SetActive(true); }));
        if (leave) leave.transform.localPosition = new Vector3(-1.35f, -2.411f, -1f);
        if (ret) ret.transform.localPosition = new Vector3(1.35f, -2.411f, -1f);

        // 3 列で上から並べる
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            var b = MakeButton(proto, _panel.transform, "MrpClient" + it.Entry.Definition.Key, it.Label, out it.Back, out it.Rollover);
            b.transform.localPosition = new Vector3((i % 3 - 1) * 2.6f, 2.2f - 0.5f * (i / 3), -6f);
            if (!Clicks.TryGetValue(i, out var a))
            {
                int idx = i;
                Clicks[i] = a = (UnityAction)(() => Toggle(idx));
            }
            SetClick(b, a);
            Paint(it);
        }
    }

    // 本編の ON/OFF ボタンを写して、見た目の部品だけ残す (本編のボタンの処理は自分の文字を言語の切り替えで書き直すので外す)
    private static PassiveButton MakeButton(ToggleButtonBehaviour proto, Transform parent, string name, string label, out SpriteRenderer back, out ButtonRolloverHandler rollover)
    {
        var t = Object.Instantiate(proto, parent);
        t.name = name;
        back = t.Background;
        rollover = t.Rollover;
        TextMeshPro text = t.Text;
        var pb = t.GetComponent<PassiveButton>();
        Object.Destroy(t);
        if (text)
        {
            var tr = text.GetComponent<TextTranslatorTMP>();
            if (tr) Object.Destroy(tr);
            text.text = label;
        }
        return pb;
    }

    private static void SetClick(PassiveButton b, UnityAction a)
    {
        if (!b) return;
        b.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        b.OnClick.AddListener(a);
    }

    private static void Toggle(int idx)
    {
        var it = Items[idx];
        it.Entry.Value = !it.Entry.Value;
        Paint(it);
        try { it.Changed?.Invoke(); }
        catch (Exception e) { Plugin.Logger.LogError($"client option {it.Entry.Definition.Key}: {e}"); }
    }

    private static void Paint(Item it)
    {
        var c = it.Entry.Value ? On : Off;
        if (it.Back) it.Back.color = c;
        if (it.Rollover) it.Rollover.ChangeOutColor(c);
    }

    // 確認用: 一覧 / 名前で切り替え
    internal static string Describe(string key)
    {
        var hud = Vanilla.Hud;
        if (key == "menu")
        {
            if (hud && hud.GameMenu) hud.GameMenu.Open();
            else if (Object.FindObjectOfType<MainMenuManager>() is { } mm && mm.settingsButton) mm.settingsButton.OnClick.Invoke();
        }
        else if (key == "open") _open?.Invoke();
        else if (key == "close") _close?.Invoke();
        else if (key.Length > 0)
        {
            int i = Items.FindIndex(x => x.Entry.Definition.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return $"ERR client no option {key}";
            Toggle(i);
        }
        var sb = new System.Text.StringBuilder("OK client");
        foreach (var it in Items) sb.Append(' ').Append(it.Entry.Definition.Key).Append('=').Append(it.Entry.Value ? "on" : "off");
        sb.Append(" panel=").Append(_panel ? (_panel.gameObject.activeSelf ? "open" : "closed") : "none");
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
internal static class ClientOptionsPatch
{
    public static void Postfix(OptionsMenuBehaviour __instance)
    {
        try { ClientOptions.Build(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"client options: {e}"); }
    }
}
