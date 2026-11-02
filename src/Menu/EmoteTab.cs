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
// - タブの並びの右端に「エモート」のタブを足す。4 つのエモートを札で並べ、押すと右の見本の人がそのエモートをする
internal static class EmoteTab
{
    private const string TabName = "MrpEmotesTab";
    private static readonly string[] Names = { "Emote.Wave", "Emote.Jump", "Emote.Clap", "Emote.Beckon" };

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
        }
        catch (Exception e) { Plugin.Logger.LogError($"emote tab: {e}"); }
    }

    private static void Forget()
    {
        _menu = null;
        _panel = null;
        _tabBg = null;
        _open = false;
        UiActions.Release(Slots);
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
    private static (bool, Color) Look(PlayerCustomizationMenu menu, bool selected)
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

    private static SpriteRenderer Bg(TabButton t)
    {
        var bgT = FindDeep(TabRoot(t), "Tab Background");
        return bgT ? bgT.GetComponent<SpriteRenderer>() : null;
    }

    // 「〇〇Tab」(タブの並び「Tabs」の子)
    private static Transform TabRoot(TabButton t)
    {
        if (t == null || !t.Button) return null;
        var tr = t.Button.transform;
        while (tr.parent && tr.parent.name != "Tabs") tr = tr.parent;
        return tr.parent ? tr : null;
    }

    private static void Apply(SpriteRenderer sr, (bool On, Color C) look)
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
        _panel.SetActive(true);
        _open = true;
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

    // ── 中身: 4 つのエモートの札 ───────────────────────────────

    private const float CardW = 1.35f, CardH = 1.3f, GapX = 0.15f, GapY = 0.2f;

    private static void BuildPanel(PlayerCustomizationMenu menu)
    {
        var colorTab = menu.Tabs[0].Tab;
        if (!colorTab) return;
        _panel = new GameObject("MrpEmotePanel") { layer = colorTab.gameObject.layer };
        _panel.transform.SetParent(colorTab.transform.parent, false);
        _panel.transform.localPosition = colorTab.transform.localPosition;
        _panel.SetActive(false);

        // 字のひな形は右の品名 (色のタブの字は並びの枠で切り抜く材質なので、枠の外が薄くなる)
        var proto = menu.itemName;
        if (proto)
        {
            var title = Label(proto, _panel.transform, new Text("Emote.Tab").ToString(), new Vector3(-1.95f, 2.15f, -1f), 5f, TextAlignmentOptions.Left, new Vector2(4f, 1f));
            title.rectTransform.pivot = new Vector2(0f, 0.5f);
        }

        float x0 = -1.95f + CardW * 0.5f, y0 = 1.25f;
        for (byte k = 0; k < Names.Length && k < EmoteArt.Count; k++)
        {
            byte kind = k;
            var pos = new Vector3(x0 + (k % 2) * (CardW + GapX), y0 - (k / 2) * (CardH + GapY), -1f);
            var card = MenuArt.Panel(_panel.transform, "Card" + k, 0.12f, new Vector2(CardW, CardH), pos, new Color(0.22f, 0.24f, 0.3f, 1f), null);
            var col = card.gameObject.AddComponent<BoxCollider2D>();
            col.size = new Vector2(CardW, CardH);
            var pb = card.gameObject.AddComponent<PassiveButton>();
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnMouseOver = new UnityEngine.Events.UnityEvent();
            pb.OnMouseOut = new UnityEngine.Events.UnityEvent();
            var cardSr = card;
            pb.OnMouseOver.AddListener(UiActions.Take(() => { if (cardSr) cardSr.color = new Color(0.3f, 0.45f, 0.5f, 1f); }, Slots));
            pb.OnMouseOut.AddListener(UiActions.Take(() => { if (cardSr) cardSr.color = new Color(0.22f, 0.24f, 0.3f, 1f); }, Slots));
            pb.OnClick.AddListener(UiActions.Take(() => Preview(kind), Slots));

            var sprite = EmoteArt.Icon(kind);
            var mat = Mat(menu);
            if (sprite && mat)
            {
                var go = new GameObject("Icon") { layer = _panel.layer };
                go.transform.SetParent(card.transform, false);
                const float s = 0.55f;
                var c = EmoteArt.IconCenter(kind);
                go.transform.localPosition = new Vector3(-c.x * s, 0.15f - c.y * s, -0.01f);
                go.transform.localScale = new Vector3(s, s, 1f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sharedMaterial = mat;
                sr.sprite = sprite;
                sr.sortingOrder = card.sortingOrder;
                PlayerMaterial.SetColors(ColorId(), sr);
            }
            if (proto)
            {
                Label(proto, card.transform, new Text(Names[kind]).ToString(), new Vector3(0f, -0.45f, -0.02f), 2.4f, TextAlignmentOptions.Center, new Vector2(CardW, 0.4f));
                // 試合の中で出すキー (エモートの輪を開いて 1〜4)
                Label(proto, card.transform, (kind + 1).ToString(), new Vector3(CardW * 0.5f - 0.17f, CardH * 0.5f - 0.17f, -0.02f), 2.2f, TextAlignmentOptions.Center, new Vector2(0.3f, 0.3f)).color = new Color(1f, 0.85f, 0.3f, 1f);
            }
        }
        if (proto)
        {
            string key = KeyHints.KeyName(Keybinds.Key(Keybinds.Slot.Emote));
            var hint = Label(proto, _panel.transform, new Text("Emote.TabHint").With("key", key).ToString(), new Vector3(-1.95f, y0 - 1.5f * CardH - GapY - 0.12f, -1f), 1.7f, TextAlignmentOptions.TopLeft, new Vector2(3.6f, 1f));
            hint.rectTransform.pivot = new Vector2(0f, 1f);
            hint.enableWordWrapping = true;
            hint.color = new Color(0.8f, 0.8f, 0.8f, 1f);
        }
    }

    private static void Preview(byte kind)
    {
        var menu = _menu;
        if (!menu || !menu.PreviewArea) return;
        EmoteArt.Preview(menu.PreviewArea, ColorId(), kind, Mat(menu));
        if (menu.itemName) menu.itemName.text = new Text(Names[kind]).ToString();
    }

    // 人の色を塗る材質。メインメニューには試合の部品 (死体のひな形) が無いので、見本の人の体の材質を借りる
    private static Material Mat(PlayerCustomizationMenu menu)
    {
        var mat = FxHands.PlayerMat();
        if (mat) return mat;
        var pp = menu ? menu.PreviewArea : null;
        var body = pp && pp.cosmetics && pp.cosmetics.currentBodySprite != null ? pp.cosmetics.currentBodySprite.BodySprite : null;
        return body ? body.sharedMaterial : null;
    }

    // 自分の色 (メインメニューでは保存されている着せ替えの色)
    private static int ColorId()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.Data != null) return lp.Data.DefaultOutfit.ColorId;
        var data = AmongUs.Data.DataManager.Player;
        return data != null && data.Customization != null ? data.Customization.Color : 0;
    }

    private static TextMeshPro Label(TextMeshPro proto, Transform parent, string text, Vector3 pos, float size, TextAlignmentOptions align, Vector2 box)
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

    private static Transform FindDeep(Transform t, string name)
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
