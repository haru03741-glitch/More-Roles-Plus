using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 着せ替え画面 (ロビーのノートパソコン):
// - 右上の段のボタン (試合の情報・役職辞典・バグ報告・チャット・設定・フレンド) がタブの並びに重なるので、開いている間は隠す
// - タブの並びの右端に「エモート」のタブを足す。エモートの扇の 4 つの枠・会議のスタンプの 8 つの枠に入れる物を選ぶ (押すと右の見本の人がそのエモートをする)
internal static class EmoteTab
{
    private const string TabName = "MrpEmotesTab";

    private static readonly List<GameObject> Hidden = new();
    private static readonly List<int> Slots = new();
    private static PlayerCustomizationMenu _menu;
    private static GameObject _panel;
    private static SpriteRenderer _tabBg;
    private static bool _open;
    private static (bool On, Color C) _sel, _unsel;

    // FixedUpdate から
    internal static void Tick()
    {
        try
        {
            var menu = PlayerCustomizationMenu.Instance;
            if (!menu)
            {
                ShowHud();
                if (_menu != null) Forget();
                return;
            }
            HideHud();
            if (_menu != menu)
            {
                Forget();
                _menu = menu;
                Build(menu);
            }
            Watch();
        }
        catch (Exception e) { Plugin.Logger.LogError($"emote tab: {e}"); }
    }

    private static void Forget()
    {
        _menu = null;
        _panel = null;
        _tabBg = null;
        _open = false;
        _content = null;
        UiActions.Release(Slots);
        UiActions.Release(ContentSlots);
        EmoteArt.Stop(EmoteArt.PreviewId);
    }

    // ── 右上の段を隠す ─────────────────────────────────────────

    private static void HideHud()
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        Hide(hud.SettingsButton ? hud.SettingsButton.transform.parent : null, hud);
        Hide(hud.Chat ? hud.Chat.chatButton : null, hud);
        var friends = hud.transform.Find("Friends List Button");
        Hide(friends, hud);
    }

    private static void Hide(Component c, HudManager hud)
    {
        if (!c || c.transform == hud.transform) return;
        var go = c.gameObject;
        if (!go.activeSelf) return;
        go.SetActive(false);
        if (!Hidden.Contains(go)) Hidden.Add(go);
    }

    private static void ShowHud()
    {
        if (Hidden.Count == 0) return;
        foreach (var go in Hidden)
            if (go) go.SetActive(true);
        Hidden.Clear();
    }

    // ── タブ ─────────────────────────────────────────────────

    private static void Build(PlayerCustomizationMenu menu)
    {
        if (menu.Tabs == null || menu.Tabs.Length == 0) return;
        var lastTab = TabRoot(menu.Tabs[menu.Tabs.Length - 1]);
        if (!lastTab || lastTab.parent.Find(TabName)) return;
        var row = lastTab.parent;

        var tab = UnityEngine.Object.Instantiate(lastTab.gameObject, row);
        tab.name = TabName;
        var lp0 = lastTab.localPosition;
        tab.transform.localPosition = new Vector3(lp0.x + 1f, lp0.y, lp0.z);
        // 右のコントローラーの印を 1 つ右へ
        var glyph = row.Find("RightGlyphContainer");
        if (glyph) glyph.localPosition += new Vector3(1f, 0f, 0f);

        var icon = FindDeep(tab.transform, "Icon")?.GetComponent<SpriteRenderer>();
        var hand = EmoteArt.Icon(EmoteArt.Wave);
        var mat = Mat(menu);
        if (icon && hand && mat)
        {
            icon.sprite = hand;
            icon.sharedMaterial = mat;
            float k = 0.42f;
            var c = EmoteArt.IconCenter(EmoteArt.Wave);
            icon.transform.localScale = new Vector3(k, k, 1f);
            icon.transform.localPosition = new Vector3(-c.x * k, -c.y * k, icon.transform.localPosition.z);
            PlayerMaterial.SetColors(ColorId(), icon);
        }

        var bgT = FindDeep(tab.transform, "Tab Background");
        _tabBg = bgT ? bgT.GetComponent<SpriteRenderer>() : null;
        var btn = bgT ? bgT.GetComponent<PassiveButton>() : null;
        if (btn)
        {
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            btn.OnClick.AddListener(UiActions.Take(Open, Slots));
        }
        // 見た目は「選ばれていない」の状態から始める (写したタブが選ばれていた時の色のまま残さない)
        _unsel = Look(menu, false);
        _sel = Look(menu, true);
        Apply(_tabBg, _unsel);
        BuildPanel(menu);
    }

    // 本編のタブの台の見た目 (選ばれている物か、いない物)
    internal static (bool, Color) Look(PlayerCustomizationMenu menu, bool selected)
    {
        foreach (var t in menu.Tabs)
        {
            var bg = Bg(t);
            if (!bg || !t.Tab) continue;
            bool isSel = t.Tab.gameObject.activeSelf;
            if (isSel == selected) return (bg.enabled, bg.color);
        }
        return (selected, Color.white);
    }

    internal static SpriteRenderer Bg(TabButton t)
    {
        var bgT = FindDeep(TabRoot(t), "Tab Background");
        return bgT ? bgT.GetComponent<SpriteRenderer>() : null;
    }

    // 「〇〇Tab」(タブの並び「Tabs」の子)
    internal static Transform TabRoot(TabButton t)
    {
        if (t == null || !t.Button) return null;
        var tr = t.Button.transform;
        while (tr.parent && tr.parent.name != "Tabs") tr = tr.parent;
        return tr.parent ? tr : null;
    }

    internal static void Apply(SpriteRenderer sr, (bool On, Color C) look)
    {
        if (!sr) return;
        sr.enabled = look.On;
        sr.color = look.C;
    }

    private static void Open()
    {
        var menu = _menu;
        if (!menu || !_panel) return;
        var sel = Look(menu, true);
        if (sel.Item1 != _unsel.On || sel.Item2 != _unsel.C) _sel = sel;
        foreach (var t in menu.Tabs)
        {
            if (t.Tab) t.Tab.gameObject.SetActive(false);
            Apply(Bg(t), _unsel);
        }
        Apply(_tabBg, _sel);
        // キューブのタブの右の見本 (キューブの絵と「有効化」) を片付け、人の見本を出す
        if (menu.cubeArea) menu.cubeArea.gameObject.SetActive(false);
        if (menu.cosmicubeMenu) menu.cosmicubeMenu.gameObject.SetActive(false);
        if (menu.nameplateMaskArea) menu.nameplateMaskArea.gameObject.SetActive(false);
        if (menu.PreviewArea) menu.PreviewArea.gameObject.SetActive(true);
        MrpCosTab.Close();
        _panel.SetActive(true);
        _open = true;
        MrpCosmetics.PreviewVanillaHat = MrpCosmetics.PreviewVanillaVisor = false;
        if (menu.equipButton) menu.equipButton.gameObject.SetActive(false);
        if (menu.equippedText) menu.equippedText.gameObject.SetActive(false);
        if (menu.itemName) menu.itemName.text = new Text("Emote.Tab").ToString();
        if (menu.swapTabSound) SoundManager.Instance.PlaySound(menu.swapTabSound, false);
    }

    // エモートのタブには装備するという事が無いので、本編が毎フレーム出し直す「装備」「装備中」を後から隠す
    internal static void AfterUpdate(PlayerCustomizationMenu menu)
    {
        if (!_open || menu != _menu) return;
        if (menu.equipButton && menu.equipButton.gameObject.activeSelf) menu.equipButton.gameObject.SetActive(false);
        if (menu.equippedText && menu.equippedText.gameObject.activeSelf) menu.equippedText.gameObject.SetActive(false);
    }

    // 本編のタブが開かれた
    internal static void Close()
    {
        if (!_open) return;
        _open = false;
        if (_panel) _panel.SetActive(false);
        Apply(_tabBg, _unsel);
        EmoteArt.Stop(EmoteArt.PreviewId);
    }

    // ── 中身: エモート 4 つの枠・会議のスタンプ 8 つの枠と、入れられる物の一覧 ─────────────
    //   上の 2 つのボタンで「エモート」「スタンプ」を切り替える。枠を押して選び、下の一覧から入れる物を押す。
    //   買っていない物 (ショップで MRP コインで買う物) は暗く出し、押すと見本だけ出す

    private const float CardW = 0.85f, CardH = 0.8f, Gap = 0.1f, Left = -1.95f;
    private static readonly Color CardColor = new(0.20f, 0.22f, 0.40f, 1f);
    private static readonly Color CardHover = new(0.30f, 0.45f, 0.62f, 1f);
    private static readonly Color CardPicked = new(0.45f, 0.38f, 0.85f, 1f);
    private static readonly Color Locked = new(0.13f, 0.13f, 0.18f, 1f);
    private static readonly List<int> ContentSlots = new();
    private static GameObject _content;
    private static bool _stampMode;
    private static int _slot;
    private static int _seenAccount = -1, _seenLoadout = -1;

    private static void BuildPanel(PlayerCustomizationMenu menu)
    {
        var colorTab = menu.Tabs[0].Tab;
        if (!colorTab) return;
        _panel = new GameObject("MrpEmotePanel") { layer = colorTab.gameObject.layer };
        _panel.transform.SetParent(colorTab.transform.parent, false);
        _panel.transform.localPosition = colorTab.transform.localPosition;
        _panel.SetActive(false);
        Net.MrpAccount.Refresh();
        Rebuild();
    }

    // 毎フレーム (開いている間): 買った・枠を替えたら描き直す
    internal static void Watch()
    {
        if (!_open || !_panel) return;
        if (_seenAccount != Net.MrpAccount.Version || _seenLoadout != Loadout.Version) Rebuild();
    }

    private static void Rebuild()
    {
        var menu = _menu;
        if (!_panel || !menu) return;
        _seenAccount = Net.MrpAccount.Version;
        _seenLoadout = Loadout.Version;
        UiActions.Release(ContentSlots);
        if (_content) UnityEngine.Object.Destroy(_content);
        _content = new GameObject("Content") { layer = _panel.layer };
        _content.transform.SetParent(_panel.transform, false);
        var root = _content.transform;
        // 字のひな形は右の品名 (色のタブの字は並びの枠で切り抜く材質なので、枠の外が薄くなる)
        var proto = menu.itemName;
        if (!proto) return;

        // 見出しと MRP コイン
        var title = Label(proto, root, new Text("Emote.Tab").ToString(), new Vector3(Left, 2.2f, -1f), 4.2f, TextAlignmentOptions.Left, new Vector2(3f, 0.7f));
        title.rectTransform.pivot = new Vector2(0f, 0.5f);
        string coins = Net.MrpAccount.Ready
            ? new Text("Shop.CoinsShort").With("coins", Net.MrpAccount.Coins).ToString()
            : new Text("Shop.Offline").ToString();
        var coin = Label(proto, root, coins, new Vector3(Left + 3.75f, 2.2f, -1f), 1.6f, TextAlignmentOptions.Right, new Vector2(2.4f, 0.5f));
        coin.rectTransform.pivot = new Vector2(1f, 0.5f);
        coin.color = new Color(1f, 0.85f, 0.35f, 1f);

        // 切り替え
        Toggle(proto, root, new Text("Emote.Tab").ToString(), new Vector3(Left + 0.9f, 1.68f, -1f), !_stampMode, () => { _stampMode = false; _slot = 0; Rebuild(); });
        Toggle(proto, root, new Text("Stamp.Tab").ToString(), new Vector3(Left + 2.75f, 1.68f, -1f), _stampMode, () => { _stampMode = true; _slot = 0; Rebuild(); });

        if (_stampMode) BuildStamps(proto, root);
        else BuildEmotes(menu, proto, root);
    }

    private static void BuildEmotes(PlayerCustomizationMenu menu, TextMeshPro proto, Transform root)
    {
        // 枠 (扇の 1〜4)
        Caption(proto, root, new Text("Loadout.Slots").ToString(), 1.25f);
        for (int i = 0; i < Loadout.EmoteSlots; i++)
        {
            int slot = i;
            byte kind = Loadout.Emote(slot);
            var pos = new Vector3(Left + CardW * 0.5f + i * (CardW + Gap), 0.72f, -1f);
            var card = Card(root, "Slot" + i, pos, new Vector2(CardW, CardH), slot == _slot ? CardPicked : CardColor, () => { _slot = slot; Preview(kind); Rebuild(); });
            Hand(menu, card.transform, kind, 0.36f, 0.1f, true);
            Label(proto, card.transform, (slot + 1).ToString(), new Vector3(CardW * 0.5f - 0.13f, CardH * 0.5f - 0.13f, -0.02f), 1.8f, TextAlignmentOptions.Center, new Vector2(0.3f, 0.3f)).color = new Color(1f, 0.85f, 0.3f, 1f);
            Label(proto, card.transform, new Text(Emotes.Names[kind]).ToString(), new Vector3(0f, -0.29f, -0.02f), 1.25f, TextAlignmentOptions.Center, new Vector2(CardW, 0.25f));
        }

        // 一覧 (押すと選んだ枠に入れる)
        Caption(proto, root, new Text("Loadout.Pick").With("n", _slot + 1).ToString(), 0.2f);
        for (int k = 0; k < EmoteArt.Count; k++)
        {
            byte kind = (byte)k;
            bool owned = Net.MrpAccount.OwnsEmote(kind);
            var pos = new Vector3(Left + CardW * 0.5f + (k % 4) * (CardW + Gap), -0.33f - (k / 4) * (CardH + Gap), -1f);
            var card = Card(root, "Emote" + k, pos, new Vector2(CardW, CardH), owned ? CardColor : Locked, () =>
            {
                Preview(kind);
                if (Net.MrpAccount.OwnsEmote(kind)) Loadout.SetEmote(_slot, kind);
            });
            Hand(menu, card.transform, kind, 0.36f, 0.1f, owned);
            Label(proto, card.transform, owned ? new Text(Emotes.Names[kind]).ToString() : PriceText("emote:" + kind), new Vector3(0f, -0.29f, -0.02f), 1.2f, TextAlignmentOptions.Center, new Vector2(CardW, 0.25f))
                .color = owned ? Color.white : new Color(1f, 0.85f, 0.35f, 1f);
        }
    }

    private static void BuildStamps(TextMeshPro proto, Transform root)
    {
        const float w = 0.85f, h = 0.4f;
        // 枠 (会議の札の 1〜8)
        Caption(proto, root, new Text("Loadout.StampSlots").ToString(), 1.25f);
        for (int i = 0; i < Loadout.StampSlots; i++)
        {
            int slot = i;
            byte kind = Loadout.Stamp(slot);
            var pos = new Vector3(Left + w * 0.5f + (i % 4) * (w + Gap), 0.88f - (i / 4) * (h + Gap), -1f);
            var card = Card(root, "Slot" + i, pos, new Vector2(w, h), slot == _slot ? CardPicked : CardColor, () => { _slot = slot; ShowStamp(kind); Rebuild(); });
            Label(proto, card.transform, MeetingStamps.Label(kind), new Vector3(0f, 0f, -0.02f), 1.3f, TextAlignmentOptions.Center, new Vector2(w, h));
            Label(proto, card.transform, (slot + 1).ToString(), new Vector3(-w * 0.5f + 0.09f, h * 0.5f - 0.09f, -0.02f), 1.1f, TextAlignmentOptions.Center, new Vector2(0.2f, 0.2f)).color = new Color(1f, 0.85f, 0.3f, 1f);
        }
        Caption(proto, root, new Text("Loadout.Pick").With("n", _slot + 1).ToString(), -0.1f);
        for (int k = 0; k < MeetingStamps.Count; k++)
        {
            byte kind = (byte)k;
            bool owned = Net.MrpAccount.OwnsStamp(kind);
            var pos = new Vector3(Left + w * 0.5f + (k % 4) * (w + Gap), -0.5f - (k / 4) * (h + Gap), -1f);
            var card = Card(root, "Stamp" + k, pos, new Vector2(w, h), owned ? CardColor : Locked, () =>
            {
                ShowStamp(kind);
                if (Net.MrpAccount.OwnsStamp(kind)) Loadout.SetStamp(_slot, kind);
            });
            Label(proto, card.transform, owned ? MeetingStamps.Label(kind) : PriceText("stamp:" + kind), new Vector3(0f, 0f, -0.02f), 1.2f, TextAlignmentOptions.Center, new Vector2(w, h))
                .color = owned ? Color.white : new Color(1f, 0.85f, 0.35f, 1f);
        }
    }

    // 買っていない物の札の字: 値段 (ランクで買えない時は段の名前)
    private static string PriceText(string id)
    {
        var item = Net.MrpAccount.Catalog.Find(c => c.Id == id);
        if (item == null) return new Text("Shop.Locked").ToString();
        if (Net.MrpAccount.Tier < item.MinTier) return new Text("Shop.NeedTier").With("tier", Net.MrpAccount.TierName(item.MinTier)).ToString();
        return new Text("Shop.PriceShort").With("price", item.Price).ToString();
    }

    private static void ShowStamp(byte kind)
    {
        if (_menu && _menu.itemName) _menu.itemName.text = MeetingStamps.Label(kind);
    }

    private static void Caption(TextMeshPro proto, Transform root, string text, float y)
    {
        var t = Label(proto, root, text, new Vector3(Left, y, -1f), 1.5f, TextAlignmentOptions.Left, new Vector2(3.8f, 0.3f));
        t.rectTransform.pivot = new Vector2(0f, 0.5f);
        t.color = new Color(0.75f, 0.8f, 0.95f, 1f);
    }

    private static void Toggle(TextMeshPro proto, Transform root, string text, Vector3 pos, bool on, Action click)
    {
        var card = Card(root, "Toggle", pos, new Vector2(1.75f, 0.38f), on ? CardPicked : CardColor, click);
        Label(proto, card.transform, text, new Vector3(0f, 0f, -0.02f), 1.7f, TextAlignmentOptions.Center, new Vector2(1.75f, 0.38f));
    }

    private static SpriteRenderer Card(Transform root, string name, Vector3 pos, Vector2 size, Color color, Action click)
    {
        var card = MenuArt.Panel(root, name, 0.1f, size, pos, color, null);
        var col = card.gameObject.AddComponent<BoxCollider2D>();
        col.size = size;
        var pb = card.gameObject.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEngine.Events.UnityEvent();
        pb.OnMouseOut = new UnityEngine.Events.UnityEvent();
        var sr = card;
        pb.OnMouseOver.AddListener(UiActions.Take(() => { if (sr) sr.color = color == Locked ? new Color(0.2f, 0.2f, 0.27f, 1f) : CardHover; }, ContentSlots));
        pb.OnMouseOut.AddListener(UiActions.Take(() => { if (sr) sr.color = color; }, ContentSlots));
        pb.OnClick.AddListener(UiActions.Take(click, ContentSlots));
        return card;
    }

    private static void Hand(PlayerCustomizationMenu menu, Transform card, byte kind, float s, float y, bool owned)
    {
        var sprite = EmoteArt.Icon(kind);
        var mat = Mat(menu);
        if (!sprite || !mat) return;
        var go = new GameObject("Icon") { layer = card.gameObject.layer };
        go.transform.SetParent(card, false);
        var c = EmoteArt.IconCenter(kind);
        go.transform.localPosition = new Vector3(-c.x * s, y - c.y * s, -0.01f);
        go.transform.localScale = new Vector3(s, s, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sprite = sprite;
        sr.sortingOrder = card.GetComponent<SpriteRenderer>()?.sortingOrder ?? 0;
        PlayerMaterial.SetColors(ColorId(), sr);
        if (!owned) sr.color = new Color(0.4f, 0.4f, 0.4f, 0.8f);
    }

    private static void Preview(byte kind)
    {
        var menu = _menu;
        if (!menu || !menu.PreviewArea) return;
        EmoteArt.Preview(menu.PreviewArea, ColorId(), kind, Mat(menu));
        if (menu.itemName) menu.itemName.text = new Text(Emotes.Names[kind]).ToString();
    }

    // 人の色を塗る材質。メインメニューには試合の部品 (死体のひな形) が無いので、見本の人の体の材質を借りる
    internal static Material Mat(PlayerCustomizationMenu menu)
    {
        var mat = FxHands.PlayerMat();
        if (mat) return mat;
        var pp = menu ? menu.PreviewArea : null;
        var body = pp && pp.cosmetics && pp.cosmetics.currentBodySprite != null ? pp.cosmetics.currentBodySprite.BodySprite : null;
        return body ? body.sharedMaterial : null;
    }

    // 自分の色 (メインメニューでは保存されている着せ替えの色)
    internal static int ColorId()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.Data != null) return lp.Data.DefaultOutfit.ColorId;
        var data = AmongUs.Data.DataManager.Player;
        return data != null && data.Customization != null ? data.Customization.Color : 0;
    }

    internal static TextMeshPro Label(TextMeshPro proto, Transform parent, string text, Vector3 pos, float size, TextAlignmentOptions align, Vector2 box)
    {
        var go = UnityEngine.Object.Instantiate(proto.gameObject, parent);
        go.name = "Text";
        foreach (var tr in go.GetComponents<TextTranslatorTMP>()) UnityEngine.Object.Destroy(tr);
        for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        go.transform.localPosition = pos;
        go.transform.localScale = Vector3.one;
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.enableAutoSizing = false;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = box;
        tmp.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        tmp.text = text;
        return tmp;
    }

    internal static Transform FindDeep(Transform t, string name)
    {
        if (!t) return null;
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var hit = FindDeep(t.GetChild(i), name);
            if (hit) return hit;
        }
        return null;
    }

    // 確認用: エモートのタブを開き、kind >= 0 ならそのエモートの見本を出す
    internal static void Test(int kind)
    {
        Open();
        if (kind >= 0 && kind < EmoteArt.Count) Preview((byte)kind);
    }

    internal static string Describe() => $"emotetab menu={(bool)_menu} panel={(bool)_panel} open={_open} hidden={Hidden.Count}";
}

[HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.Update))]
internal static class EmoteTabUpdatePatch
{
    public static void Postfix(PlayerCustomizationMenu __instance) => EmoteTab.AfterUpdate(__instance);
}

[HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.OpenTab))]
internal static class EmoteTabOpenTabPatch
{
    public static void Prefix() => EmoteTab.Close();
}
