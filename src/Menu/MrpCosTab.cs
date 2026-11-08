using System;
using System.Collections.Generic;
using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 着せ替え画面 (ロビーのノートパソコン) のタブの並びの右端 (エモートのタブの右) に「MRP」のタブを足す。
// MRP だけの帽子 2 つの枠とバイザー 1 つの枠に、作ってくださった方の絵を入れる (札と右の品名に作者の名前を出す)。
// 上の枠を押して選び、下の一覧から入れる物を押す。入れている物をもう 1 度押すと外す
internal static class MrpCosTab
{
    private const string TabName = "MrpCosmeticsTab";
    private const string AfterTab = "MrpEmotesTab";

    private static readonly List<int> Slots = new();
    private static readonly List<int> ContentSlots = new();
    private static PlayerCustomizationMenu _menu;
    private static GameObject _panel, _content;
    private static SpriteRenderer _tabBg;
    private static bool _open;
    private static (bool On, Color C) _sel, _unsel;
    private static int _slot; // 0 = 帽子 1・1 = 帽子 2・2 = バイザー
    private static int _seen = -1;

    private const float Left = -1.95f;
    private static readonly Color CardColor = new(0.20f, 0.22f, 0.40f, 1f);
    private static readonly Color CardHover = new(0.30f, 0.45f, 0.62f, 1f);
    private static readonly Color CardPicked = new(0.45f, 0.38f, 0.85f, 1f);
    private static readonly Color SwapColor = new(0.16f, 0.36f, 0.34f, 1f);
    private static readonly Color Gold = new(1f, 0.85f, 0.35f, 1f);

    // FixedUpdate から (エモートのタブの後)
    internal static void Tick()
    {
        try
        {
            var menu = PlayerCustomizationMenu.Instance;
            if (!menu)
            {
                if (_menu != null) Forget();
                return;
            }
            if (_menu != menu)
            {
                if (!Build(menu)) return; // エモートのタブがまだ無い: 次の回に
                _menu = menu;
            }
            if (_open && _seen != MrpCosmetics.Version) Rebuild();
        }
        catch (Exception e) { Plugin.Logger.LogError($"mrp cos tab: {e}"); }
    }

    private static void Forget()
    {
        _menu = null;
        _panel = null;
        _content = null;
        _tabBg = null;
        _open = false;
        UiActions.Release(Slots);
        UiActions.Release(ContentSlots);
    }

    // ── タブ ─────────────────────────────────────────────────

    private static bool Build(PlayerCustomizationMenu menu)
    {
        if (menu.Tabs == null || menu.Tabs.Length == 0) return false;
        var anyTab = EmoteTab.TabRoot(menu.Tabs[menu.Tabs.Length - 1]);
        if (!anyTab) return false;
        var row = anyTab.parent;
        var prev = row.Find(AfterTab);
        if (!prev) return false;
        if (row.Find(TabName)) return true;

        var tab = UnityEngine.Object.Instantiate(prev.gameObject, row);
        tab.name = TabName;
        var lp0 = prev.localPosition;
        tab.transform.localPosition = new Vector3(lp0.x + 1f, lp0.y, lp0.z);
        var glyph = row.Find("RightGlyphContainer");
        if (glyph) glyph.localPosition += new Vector3(1f, 0f, 0f);

        // タブの絵: 恐竜の帽子 (自分の体の色で塗る)
        var icon = EmoteTab.FindDeep(tab.transform, "Icon")?.GetComponent<SpriteRenderer>();
        var item = MrpCosmetics.Find("tyrannocolor");
        var sp = MrpCosmetics.Thumb(item);
        var mat = EmoteTab.Mat(menu);
        if (icon && sp)
        {
            icon.sprite = sp;
            if (mat) icon.sharedMaterial = mat;
            var size = sp.bounds.size;
            float k = 0.62f / Mathf.Max(size.x, size.y, 0.01f);
            icon.transform.localScale = new Vector3(k, k, 1f);
            icon.transform.localPosition = new Vector3(0f, 0f, icon.transform.localPosition.z);
            PlayerMaterial.SetColors(EmoteTab.ColorId(), icon);
        }

        var bgT = EmoteTab.FindDeep(tab.transform, "Tab Background");
        _tabBg = bgT ? bgT.GetComponent<SpriteRenderer>() : null;
        var btn = bgT ? bgT.GetComponent<PassiveButton>() : null;
        if (btn)
        {
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            btn.OnClick.AddListener(UiActions.Take(Open, Slots));
        }
        _unsel = EmoteTab.Look(menu, false);
        _sel = EmoteTab.Look(menu, true);
        EmoteTab.Apply(_tabBg, _unsel);

        var colorTab = menu.Tabs[0].Tab;
        if (!colorTab) return true;
        _panel = new GameObject("MrpCosPanel") { layer = colorTab.gameObject.layer };
        _panel.transform.SetParent(colorTab.transform.parent, false);
        _panel.transform.localPosition = colorTab.transform.localPosition;
        _panel.SetActive(false);
        return true;
    }

    private static void Open()
    {
        var menu = _menu;
        if (!menu || !_panel) return;
        EmoteTab.Close();
        var sel = EmoteTab.Look(menu, true);
        if (sel.Item1 != _unsel.On || sel.Item2 != _unsel.C) _sel = sel;
        foreach (var t in menu.Tabs)
        {
            if (t.Tab) t.Tab.gameObject.SetActive(false);
            EmoteTab.Apply(EmoteTab.Bg(t), _unsel);
        }
        EmoteTab.Apply(_tabBg, _sel);
        if (menu.cubeArea) menu.cubeArea.gameObject.SetActive(false);
        if (menu.cosmicubeMenu) menu.cosmicubeMenu.gameObject.SetActive(false);
        if (menu.nameplateMaskArea) menu.nameplateMaskArea.gameObject.SetActive(false);
        if (menu.PreviewArea) menu.PreviewArea.gameObject.SetActive(true);
        MrpCosmetics.PreviewVanillaHat = MrpCosmetics.PreviewVanillaVisor = false;
        _panel.SetActive(true);
        _open = true;
        if (menu.equipButton) menu.equipButton.gameObject.SetActive(false);
        if (menu.equippedText) menu.equippedText.gameObject.SetActive(false);
        Rebuild();
        if (menu.swapTabSound) SoundManager.Instance.PlaySound(menu.swapTabSound, false);
    }

    // 付けるのは札を押した時なので、本編が毎フレーム出し直す「装備」「装備中」を後から隠す
    internal static void AfterUpdate(PlayerCustomizationMenu menu)
    {
        if (!_open || menu != _menu) return;
        if (menu.equipButton && menu.equipButton.gameObject.activeSelf) menu.equipButton.gameObject.SetActive(false);
        if (menu.equippedText && menu.equippedText.gameObject.activeSelf) menu.equippedText.gameObject.SetActive(false);
    }

    // 本編のタブかエモートのタブが開かれた
    internal static void Close()
    {
        if (!_open) return;
        _open = false;
        if (_panel) _panel.SetActive(false);
        EmoteTab.Apply(_tabBg, _unsel);
    }

    // ── 中身 ─────────────────────────────────────────────────

    private static MrpCosmetics.Item InSlot(int slot) => slot switch
    {
        0 => MrpCosmetics.MyHat,
        1 => MrpCosmetics.MyHat2,
        _ => MrpCosmetics.MyVisor,
    };

    private static void Rebuild()
    {
        var menu = _menu;
        if (!_panel || !menu) return;
        _seen = MrpCosmetics.Version;
        UiActions.Release(ContentSlots);
        if (_content) UnityEngine.Object.Destroy(_content);
        _content = new GameObject("Content") { layer = _panel.layer };
        _content.transform.SetParent(_panel.transform, false);
        var root = _content.transform;
        var proto = menu.itemName;
        if (!proto) return;

        var title = EmoteTab.Label(proto, root, new Text("Cos.Tab").ToString(), new Vector3(Left, 2.2f, -1f), 4.2f, TextAlignmentOptions.Left, new Vector2(3.8f, 0.7f));
        title.rectTransform.pivot = new Vector2(0f, 0.5f);

        // 枠 (帽子 1・帽子 2・バイザー)
        Caption(proto, root, new Text("Cos.Slots").ToString(), 1.68f);
        const float sw = 1.18f, sh = 0.82f, sg = 0.07f;
        string[] slotKeys = { "Cos.Slot.Hat1", "Cos.Slot.Hat2", "Cos.Slot.Visor" };
        for (int i = 0; i < 3; i++)
        {
            int slot = i;
            var item = InSlot(slot);
            var pos = new Vector3(Left + sw * 0.5f + i * (sw + sg), 1.07f, -1f);
            var card = Card(root, "Slot" + i, pos, new Vector2(sw, sh), slot == _slot ? CardPicked : CardColor, () => { _slot = slot; Show(InSlot(slot)); Rebuild(); });
            var head = EmoteTab.Label(proto, card.transform, new Text(slotKeys[i]).ToString(), new Vector3(0f, sh * 0.5f - 0.12f, -0.02f), 1.1f, TextAlignmentOptions.Center, new Vector2(sw, 0.2f));
            head.color = new Color(0.75f, 0.8f, 0.95f, 1f);
            if (item == null)
            {
                EmoteTab.Label(proto, card.transform, new Text("Cos.None").ToString(), new Vector3(0f, -0.05f, -0.02f), 1.3f, TextAlignmentOptions.Center, new Vector2(sw, 0.3f));
                continue;
            }
            Thumb(menu, card.transform, item, sw - 0.3f, 0.34f, 0.0f);
            Fit(EmoteTab.Label(proto, card.transform, new Text(item.NameKey).ToString(), new Vector3(0f, -0.28f, -0.02f), 1.0f, TextAlignmentOptions.Center, new Vector2(sw - 0.08f, 0.18f)), 0.55f);
        }

        // 重ねる順 (左が奥・右が手前)。札の間の「入替」で隣どうしを入れ替える。札を押すとその枠を選ぶ
        var fl = EmoteTab.Label(proto, root, new Text("Cos.Order").ToString(), new Vector3(Left, 0.42f, -1f), 1.3f, TextAlignmentOptions.Left, new Vector2(0.95f, 0.3f));
        fl.rectTransform.pivot = new Vector2(0f, 0.5f);
        fl.color = new Color(0.75f, 0.8f, 0.95f, 1f);
        Fit(fl, 0.6f);
        string[] names = { "Cos.Slot.Hat1", "Cos.Slot.Hat2", "Cos.Slot.Visor" };
        var order = MrpCosmetics.MyOrder;
        const float fw = 0.62f, bw = 0.36f, fg = 0.03f;
        float x = Left + 1.0f;
        for (int p = 0; p < 3; p++)
        {
            int slot = order[p];
            var pos = new Vector3(x + fw * 0.5f, 0.42f, -1f);
            var b = Card(root, "Order" + p, pos, new Vector2(fw, 0.3f), slot == _slot ? CardPicked : CardColor, () => { _slot = slot; Show(InSlot(slot)); Rebuild(); });
            Fit(EmoteTab.Label(proto, b.transform, new Text(names[slot]).ToString(), new Vector3(0f, 0f, -0.02f), 1.2f, TextAlignmentOptions.Center, new Vector2(fw - 0.06f, 0.26f)), 0.6f);
            x += fw + fg;
            if (p < 2)
            {
                int at = p;
                var sb = Card(root, "Swap" + p, new Vector3(x + bw * 0.5f, 0.42f, -1f), new Vector2(bw, 0.26f), SwapColor, () => { MrpCosmetics.Swap(at); Rebuild(); });
                Fit(EmoteTab.Label(proto, sb.transform, new Text("Cos.Swap").ToString(), new Vector3(0f, 0f, -0.02f), 1.0f, TextAlignmentOptions.Center, new Vector2(bw - 0.04f, 0.22f)), 0.5f);
                x += bw + fg;
            }
        }

        // 一覧
        bool visor = _slot == 2;
        Caption(proto, root, visor ? new Text("Cos.PickVisor").ToString() : new Text("Cos.PickHat").With("n", _slot + 1).ToString(), 0.08f);
        var items = new List<MrpCosmetics.Item> { null };
        foreach (var it in MrpCosmetics.All)
            if (it.Kind == (visor ? MrpCosmetics.Kind.Visor : MrpCosmetics.Kind.Hat)) items.Add(it);
        var mine = InSlot(_slot);
        var other = _slot == 0 ? MrpCosmetics.MyHat2 : _slot == 1 ? MrpCosmetics.MyHat : null;
        const float w = 0.68f, h = 0.82f, gap = 0.07f;
        const int perRow = 5;
        float top = -0.05f - h * 0.5f;
        for (int k = 0; k < items.Count; k++)
        {
            var item = items[k];
            var pos = new Vector3(Left + w * 0.5f + (k % perRow) * (w + gap), top - (k / perRow) * (h + gap), -1f);
            int slot = _slot;
            var color = item != null && item == mine ? CardPicked : item != null && item == other ? new Color(0.30f, 0.30f, 0.50f, 1f) : CardColor;
            var card = Card(root, "Cos" + k, pos, new Vector2(w, h), color, () =>
            {
                MrpCosmetics.EquipSlot(slot, InSlot(slot) == item ? null : item);
                Show(item);
            });
            if (item == null)
            {
                EmoteTab.Label(proto, card.transform, new Text("Cos.None").ToString(), new Vector3(0f, 0.05f, -0.02f), 1.3f, TextAlignmentOptions.Center, new Vector2(w, 0.3f));
                continue;
            }
            Thumb(menu, card.transform, item, w - 0.12f, 0.42f, 0.14f);
            Fit(EmoteTab.Label(proto, card.transform, new Text(item.NameKey).ToString(), new Vector3(0f, -0.2f, -0.02f), 0.95f, TextAlignmentOptions.Center, new Vector2(w - 0.06f, 0.18f)), 0.55f);
            Fit(EmoteTab.Label(proto, card.transform, new Text("Cos.By").With("name", item.Author).ToString(), new Vector3(0f, -0.33f, -0.02f), 0.85f, TextAlignmentOptions.Center, new Vector2(w - 0.06f, 0.15f)), 0.55f).color = Gold;
        }
        Show(mine);
    }

    private static void Show(MrpCosmetics.Item item)
    {
        if (!_menu || !_menu.itemName) return;
        _menu.itemName.text = item == null ? new Text("Cos.Tab").ToString()
            : new Text(item.NameKey).ToString() + "  <size=70%><color=#FFD95A>" + new Text("Cos.By").With("name", item.Author).ToString() + "</color></size>";
    }

    private static void Caption(TextMeshPro proto, Transform root, string text, float y)
    {
        var t = EmoteTab.Label(proto, root, text, new Vector3(Left, y, -1f), 1.5f, TextAlignmentOptions.Left, new Vector2(3.8f, 0.3f));
        t.rectTransform.pivot = new Vector2(0f, 0.5f);
        t.color = new Color(0.75f, 0.8f, 0.95f, 1f);
    }

    // 札に入りきらない長い名前は字を小さくして収める
    private static TextMeshPro Fit(TextMeshPro t, float min)
    {
        t.enableAutoSizing = true;
        t.fontSizeMax = t.fontSize;
        t.fontSizeMin = min;
        return t;
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
        pb.OnMouseOver.AddListener(UiActions.Take(() => { if (sr) sr.color = CardHover; }, ContentSlots));
        pb.OnMouseOut.AddListener(UiActions.Take(() => { if (sr) sr.color = color; }, ContentSlots));
        pb.OnClick.AddListener(UiActions.Take(click, ContentSlots));
        return card;
    }

    private static void Thumb(PlayerCustomizationMenu menu, Transform card, MrpCosmetics.Item item, float maxW, float maxH, float y)
    {
        var sp = MrpCosmetics.Thumb(item);
        if (!sp) return;
        var go = new GameObject("Thumb") { layer = card.gameObject.layer };
        go.transform.SetParent(card, false);
        var size = sp.bounds.size;
        float s = Mathf.Min(maxW / Mathf.Max(size.x, 0.01f), maxH / Mathf.Max(size.y, 0.01f));
        go.transform.localPosition = new Vector3(0f, y, -0.01f);
        go.transform.localScale = new Vector3(s, s, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.sortingOrder = card.GetComponent<SpriteRenderer>()?.sortingOrder ?? 0;
        if (item.Adaptive)
        {
            var mat = EmoteTab.Mat(menu);
            if (mat) sr.sharedMaterial = mat;
            PlayerMaterial.SetColors(EmoteTab.ColorId(), sr);
        }
    }

    // 確認用: MRP のタブを開く
    internal static void Test() => Open();

    internal static string Describe() => $"costab menu={(bool)_menu} panel={(bool)_panel} open={_open} slot={_slot}";
}

[HarmonyLib.HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.Update))]
internal static class MrpCosTabUpdatePatch
{
    public static void Postfix(PlayerCustomizationMenu __instance) => MrpCosTab.AfterUpdate(__instance);
}

[HarmonyLib.HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.OpenTab))]
internal static class MrpCosTabOpenTabPatch
{
    public static void Prefix() => MrpCosTab.Close();
}
