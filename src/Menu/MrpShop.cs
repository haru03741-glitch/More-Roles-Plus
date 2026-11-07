using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Menu;

// MRP ショップ: MRP コイン (非公式のゲーム内通貨) でエモートと会議のスタンプを買う窓。
// 本編のショップの画面の右下に「MRP ショップ (非公式)」のボタンを置いて開く。
// 値段・持っているか・買えるかは全部サーバーが決める (ここは並べて「買う」を送るだけ)
internal static class MrpShop
{
    private const float WinW = 8.6f, WinH = 5.4f;
    private static readonly Color Gold = new(1f, 0.85f, 0.35f);
    private static readonly Color Sub = new(0.78f, 0.8f, 0.88f);
    private static readonly Color Warn = new(1f, 0.6f, 0.45f);
    private static string _status = "";
    private static bool _busy;
    private static int _seen = -1;

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_Shop";

    public static void Open()
    {
        _status = "";
        var w = UiWindow.Open("Shop", new Vector2(WinW, WinH));
        if (w == null) return;
        Build();
        MrpAccount.Refresh(() => { if (IsOpen) Build(); });
    }

    private static void Build()
    {
        var w = UiWindow.Current;
        if (w == null || !w.Root || !IsOpen) return;
        _seen = MrpAccount.Version;
        var col = new Stack(false) { Gap = 0.1f, Width = WinW - 0.4f };
        col.Add(new Label(new Text("Shop.Title"), 2.4f) { MenuFont = true, Bold = true, Color = Gold });
        // 非公式であること (本編のお金と関係ない) をはっきり出す
        col.Add(new Label(new Text("Shop.Unofficial"), 1.25f) { Color = Warn, Width = WinW - 0.5f, Align = Align.Start });
        string me = MrpAccount.Ready
            ? new Text("Shop.Me").With("coins", MrpAccount.Coins).With("tier", $"<color=#{MrpAccount.TierColorHex(MrpAccount.Tier)}>{MrpAccount.TierName(MrpAccount.Tier)}</color>").With("rating", MrpAccount.Rating).ToString()
            : new Text(MrpAccount.LastError.Length > 0 ? "Shop.Error" : "Shop.Loading").With("error", MrpAccount.LastError).ToString();
        col.Add(new Label(me, 1.6f) { Width = WinW - 0.5f, Align = Align.Start });

        var left = new Stack(false) { Gap = 0.06f, Width = 4f };
        var right = new Stack(false) { Gap = 0.06f, Width = 4f };
        left.Add(new Label(new Text("Emote.Tab"), 1.7f) { Bold = true });
        right.Add(new Label(new Text("Stamp.Tab"), 1.7f) { Bold = true });
        foreach (var item in MrpAccount.Catalog)
        {
            bool emote = item.Id.StartsWith("emote:", StringComparison.Ordinal);
            if (!int.TryParse(item.Id[(item.Id.IndexOf(':') + 1)..], out int kind)) continue;
            string name = emote
                ? (kind < Emotes.Names.Length ? new Text(Emotes.Names[kind]).ToString() : item.Id)
                : (kind < MeetingStamps.Keys.Length ? MeetingStamps.Label((byte)kind) : item.Id);
            (emote ? left : right).Add(Row(item, name));
        }
        col.Add(new Stack(true, left, right) { Gap = 0.2f, Align = Align.Start });
        if (_status.Length > 0) col.Add(new Label(_status, 1.4f) { Color = Gold });
        col.Add(new Label(new Text("Shop.Hint"), 1.2f) { Color = Sub, Width = WinW - 0.5f, Align = Align.Start });
        w.Body.Set(col);
    }

    private static Widget Row(MrpAccount.Item item, string name)
    {
        bool owned = MrpAccount.Owned.Contains(item.Id);
        bool rank = MrpAccount.Tier >= item.MinTier;
        var label = new Label(name, 1.4f) { Width = 1.6f, NoWrap = true };
        Widget right;
        if (owned) right = new Label(new Text("Shop.Owned"), 1.3f) { Color = Sub, Width = 2.2f };
        else if (!rank) right = new Label(new Text("Shop.NeedTier").With("tier", MrpAccount.TierName(item.MinTier)), 1.25f) { Color = Warn, Width = 2.2f };
        else
        {
            string id = item.Id;
            bool afford = MrpAccount.Coins >= item.Price;
            right = new Button(new Text("Shop.Buy").With("price", item.Price), () => Buy(id), 1.3f)
            {
                Size = new Vector2(2.1f, 0.34f),
                Color = afford ? Gold : new Color(0.5f, 0.5f, 0.5f),
            };
        }
        return new Stack(true, label, right) { Gap = 0.1f, Align = Align.Center };
    }

    private static void Buy(string id)
    {
        if (_busy) return;
        _busy = true;
        _status = new Text("Shop.Buying").ToString();
        Build();
        MrpAccount.Buy(id, err =>
        {
            _busy = false;
            _status = err == null ? new Text("Shop.Bought").ToString()
                : new Text(err is "coins" or "rank" or "owned" or "no_item" or "auth" or "too_many" or "disabled" ? "Shop.Fail." + err : "Shop.Fail.network").ToString();
            if (IsOpen) Build();
        });
    }

    // 毎フレーム: 窓が開いている間に数が変わったら描き直す
    internal static void Watch()
    {
        if (IsOpen && _seen != MrpAccount.Version && !_busy) Build();
    }

    // ── 本編のショップの画面のボタン ─────────────────────────

    private const string ButtonName = "MrpShopButton";

    internal static void Ensure(StoreMenu store)
    {
        if (!store || store.transform.Find(ButtonName)) return;
        var go = new GameObject(ButtonName) { layer = store.gameObject.layer };
        go.transform.SetParent(store.transform, false);
        go.transform.localPosition = new Vector3(3.6f, -2.35f, -30f);
        var size = new Vector2(3.1f, 0.5f);
        var bg = new GameObject("Back") { layer = go.layer }.AddComponent<SpriteRenderer>();
        bg.transform.SetParent(go.transform, false);
        bg.sprite = MenuSkin.Button(false);
        bg.drawMode = SpriteDrawMode.Sliced;
        bg.size = size;
        var col = go.AddComponent<BoxCollider2D>();
        col.size = size;
        var text = new GameObject("Text") { layer = go.layer };
        text.transform.SetParent(go.transform, false);
        text.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        var tmp = text.AddComponent<TextMeshPro>();
        var font = FindFont(store);
        if (font) { tmp.font = font.font; tmp.fontSharedMaterial = font.fontSharedMaterial; }
        tmp.fontSize = 1.9f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = size;
        tmp.text = new Text("Shop.Open").ToString();
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        // 押した時/乗せた時の処理は 1 回だけ作って使い回す (画面ごとに管理ラムダを渡すと手放されずに残る)
        _btnBg = bg;
        _btnSize = size;
        _onOpen ??= (UnityAction)Open;
        _onOver ??= (UnityAction)(() => Hover(true));
        _onOut ??= (UnityAction)(() => Hover(false));
        pb.OnClick.AddListener(_onOpen);
        pb.OnMouseOver.AddListener(_onOver);
        pb.OnMouseOut.AddListener(_onOut);
    }

    private static SpriteRenderer _btnBg;
    private static Vector2 _btnSize;
    private static UnityAction _onOpen, _onOver, _onOut;

    private static void Hover(bool on)
    {
        if (!_btnBg) return;
        _btnBg.sprite = MenuSkin.Button(on);
        _btnBg.size = _btnSize;
    }

    private static TextMeshPro FindFont(StoreMenu store)
    {
        foreach (var t in store.GetComponentsInChildren<TextMeshPro>(true))
            if (t && t.font) return t;
        return null;
    }

    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.Open))]
    private static class StoreOpenPatch
    {
        public static void Postfix(StoreMenu __instance)
        {
            try { Ensure(__instance); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop button: {e}"); }
        }
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try { Watch(); }
            catch (Exception) { }
        }
    }
}
