using System;
using System.Collections.Generic;
using Assets.InnerNet;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// MRP ショップ: MRP コイン (非公式のゲーム内通貨) でエモートと会議のスタンプを買う。
// 本編のショップの画面をそのまま使う: 上の帯の最後に MRP のタブ、豆の数の左に MRP コインの数 (本編の豆の欄を写す)、
// 品は本編の品の札 (本編の作る関数で作ってから中身を差し替える)、押すと本編の購入確認の画面に出し、買うと本編の「購入中」の窓で結果を出す。
// 値段・持っているか・買えるかは全部サーバーが決める (ここは並べて「買う」を送るだけ)
internal static class MrpShop
{
    private const string TabName = "MrpShopTab", WalletName = "MrpCoin Wallet";

    private static StoreMenu _store;
    private static SpriteRenderer _tabBg;
    private static (bool On, Color C) _sel, _unsel;
    private static TextMeshPro _walletText;
    private static bool _open, _layoutPending;
    private static int _seen = -1;

    private sealed class Card
    {
        public PurchaseButton Btn;
        public MrpAccount.Item Item;
        public string Name;
        public bool Emote;
        public byte Kind;
    }

    private static readonly List<Card> Cards = new();
    private static readonly List<int> Slots = new();

    // ── 本編のショップを開いた時: タブとコインの欄を足す ─────────────────────────

    internal static void Ensure(StoreMenu store)
    {
        if (!store) return;
        if (_store != store) Forget();
        _store = store;
        MrpAccount.Refresh();
        var bean = store.beanAmount;
        var pod = bean ? bean.transform.parent : null;
        var wallet = pod ? pod.parent : null;
        var row = wallet ? wallet.parent : null;
        if (!row) return;
        if (!row.Find(TabName)) BuildTab(store, row);
        if (!wallet.Find(WalletName)) BuildWallet(bean, pod, wallet);
        // 帯の並べ直しは本編が画面の比で位置を決めた後 (次のフレーム) に
        _layoutPending = true;
        _seen = -1;
    }

    private static void Forget()
    {
        Leave();
        _store = null;
        _tabBg = null;
        _walletText = null;
        _layoutPending = false;
        Original.Clear();
    }

    private static void BuildTab(StoreMenu store, Transform row)
    {
        var last = row.Find("CubesTab");
        if (!last) return;
        var tab = Object.Instantiate(last.gameObject, row);
        tab.name = TabName;
        var newDot = EmoteTab.FindDeep(tab.transform, "NewItemIcon");
        if (newDot) newDot.gameObject.SetActive(false);

        // 絵: 持ち物の画面のエモートのタブと同じ手 (自分の色)
        var iconT = EmoteTab.FindDeep(tab.transform, "Icon");
        var icon = iconT ? iconT.GetComponent<SpriteRenderer>() : null;
        var hand = EmoteArt.Icon(EmoteArt.Wave);
        var mat = Mat(false);
        if (icon && hand && mat)
        {
            icon.sprite = hand;
            icon.sharedMaterial = mat;
            float k = 0.42f;
            var c = EmoteArt.IconCenter(EmoteArt.Wave);
            icon.transform.localScale = new Vector3(k, k, 1f);
            icon.transform.localPosition = new Vector3(-c.x * k, -c.y * k, icon.transform.localPosition.z);
            PlayerMaterial.SetColors(EmoteTab.ColorId(), icon);
        }

        var bgT = EmoteTab.FindDeep(tab.transform, "Tab Background");
        _tabBg = bgT ? bgT.GetComponent<SpriteRenderer>() : null;
        if (bgT)
        {
            // 本編のタブの印 (どのタブか) を外し、押した時は MRP のタブを開く
            var mark = bgT.GetComponent<StoreMenuTabButton>();
            if (mark) Object.Destroy(mark);
            var pb = bgT.GetComponent<PassiveButton>();
            if (pb)
            {
                pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                _onTab ??= (UnityAction)OpenTab;
                pb.OnClick.AddListener(_onTab);
            }
        }
        _unsel = Look(store, false);
        _sel = Look(store, true);
        EmoteTab.Apply(_tabBg, _unsel);
    }

    private static UnityAction _onTab;

    // 本編のタブの台の見た目 (選ばれている物か、いない物)
    private static (bool, Color) Look(StoreMenu store, bool selected)
    {
        var cur = store.selectedTabObject;
        foreach (var t in store.Tabs)
        {
            if (t == null || !t.Button) continue;
            bool isSel = cur != null && t.Pointer == cur.Pointer;
            if (isSel == selected) return (t.Button.enabled, t.Button.color);
        }
        return (selected, Color.white);
    }

    // 豆の欄 (豆の絵 + 数) を写して、絵をコインに替えて豆の左に置く
    private const float AmountW = 0.5f, WalletGap = 0.12f;

    private static void BuildWallet(TextMeshPro bean, Transform pod, Transform wallet)
    {
        var go = Object.Instantiate(pod.gameObject, wallet);
        go.name = WalletName;
        var sr = go.GetComponent<SpriteRenderer>();
        var beanSprite = sr ? sr.sprite : null;
        if (sr && beanSprite) sr.sprite = MrpCoin.SpriteSized(beanSprite.bounds.size.y);
        var amount = go.transform.Find(bean.name);
        _walletText = amount ? amount.GetComponent<TextMeshPro>() : null;
        if (!_walletText) return;
        _walletText.color = MrpCoin.Ink;
        // 数の箱を豆より短くする (左端は豆と同じ所のまま)
        var rt = _walletText.rectTransform;
        float oldW = rt.sizeDelta.x, newW = AmountW / Math.Max(pod.localScale.x * amount.localScale.x, 0.0001f);
        rt.sizeDelta = new Vector2(newW, rt.sizeDelta.y);
        amount.localPosition += new Vector3(-(oldW - newW) * rt.pivot.x * amount.localScale.x, 0f, 0f);
        // 置き場: コインの数の箱の右端を、豆の絵の左端より少し左に
        float beanHalf = beanSprite ? beanSprite.bounds.size.x * pod.localScale.x / 2f : 0.28f;
        float textLeft = (amount.localPosition.x - newW * rt.pivot.x * amount.localScale.x) * pod.localScale.x;
        float right = textLeft + AmountW;
        go.transform.localPosition = pod.localPosition + new Vector3(-beanHalf - WalletGap - right, 0f, 0f);
        UpdateWallet();
    }

    private static void UpdateWallet()
    {
        if (!_walletText) return;
        _walletText.text = MrpAccount.Ready ? MrpAccount.Coins.ToString() : "-";
    }

    // 帯の並べ直し: MRP のタブを最後のタブの後ろに足し、コインの欄の左まで収まるようにタブの間を詰める (足りる時は詰めない)
    private static void Layout()
    {
        _layoutPending = false;
        var store = _store;
        var bean = store ? store.beanAmount : null;
        var pod = bean ? bean.transform.parent : null;
        var wallet = pod ? pod.parent : null;
        var row = wallet ? wallet.parent : null;
        if (!row) return;
        var mine = row.Find(TabName);
        var home = row.Find("HomeTab");
        var cubes = row.Find("CubesTab");
        var bundle = row.Find("BundleTab");
        var coin = wallet.Find(WalletName);
        if (!mine || !home || !cubes || !bundle || !coin) return;

        // 元の位置を 1 回だけ控える (開き直しても同じ所から並べ直す)
        if (Original.Count == 0)
        {
            for (int i = 0; i < row.childCount; i++)
            {
                var c = row.GetChild(i);
                if (c == mine || c == wallet) continue;
                bool tab = c.name.EndsWith("Tab", StringComparison.Ordinal);
                if (!tab && c.name != "Header Separator") continue;
                if (c.localPosition.x < home.localPosition.x - 0.01f) continue;
                Original[c.GetInstanceID()] = (c.localPosition.x, c.localScale);
            }
        }
        float homeX = Orig(home).X;
        float cubesX = Orig(cubes).X;
        float pitch = cubesX - Orig(bundle).X;
        float mineX = cubesX + pitch;

        // コインの欄の左端 (帯の座標)
        var coinSr = coin.GetComponent<SpriteRenderer>();
        float coinHalf = coinSr && coinSr.sprite ? coinSr.sprite.bounds.size.x * coin.localScale.x / 2f : 0.3f;
        float coinLeft = wallet.localPosition.x + (coin.localPosition.x - coinHalf) * wallet.localScale.x;

        // Cubes の後ろの区切りは MRP のタブの後ろへ
        float lastEnd = mineX;
        var moves = new List<(Transform T, float X, Vector3 S, bool Tab)>();
        for (int i = 0; i < row.childCount; i++)
        {
            var c = row.GetChild(i);
            if (!Original.TryGetValue(c.GetInstanceID(), out var o)) continue;
            float x = o.X > cubesX ? o.X + pitch : o.X;
            lastEnd = Math.Max(lastEnd, x);
            moves.Add((c, x, o.Scale, c.name.EndsWith("Tab", StringComparison.Ordinal)));
        }
        moves.Add((mine, mineX, Orig(cubes).Scale, true));
        float room = coinLeft - 0.2f - homeX;
        float f = Math.Min(1f, room / Math.Max(lastEnd - homeX, 0.01f));
        float s = Math.Clamp(f * 1.15f, 0.7f, 1f);
        foreach (var m in moves)
        {
            var p = m.T.localPosition;
            m.T.localPosition = new Vector3(homeX + (m.X - homeX) * f, p.y, p.z);
            if (m.Tab) m.T.localScale = new Vector3(m.S.x * s, m.S.y * s, m.S.z);
        }
        _layoutInfo = $"f={f:0.00} s={s:0.00} room={room:0.00} coinLeft={coinLeft:0.00} last={lastEnd:0.00} home={homeX:0.00}";
    }

    private static readonly Dictionary<int, (float X, Vector3 Scale)> Original = new();
    private static string _layoutInfo = "-";

    private static (float X, Vector3 Scale) Orig(Transform t) =>
        Original.TryGetValue(t.GetInstanceID(), out var o) ? o : (t.localPosition.x, t.localScale);

    // ── MRP のタブ ─────────────────────────────────────────

    private static void OpenTab()
    {
        var store = _store;
        if (!store) return;
        if (store.confirmMenu && store.confirmMenu.activeSelf) store.CloseConfirm();
        foreach (var t in store.Tabs)
            if (t != null) EmoteTab.Apply(t.Button, _unsel);
        EmoteTab.Apply(_tabBg, _sel);
        store.DestroySliderObjects();
        if (store.featuredMenu) store.featuredMenu.SetActive(false);
        if (store.normalMenu) store.normalMenu.SetActive(true);
        if (store.comingSoonText) store.comingSoonText.SetActive(false);
        if (store.tabHeader)
        {
            store.tabHeader.text = new Text("Shop.Title").ToString();
            // 前のタブ (フィーチャー) の見出しの写しが残るので、開いている間は隠す
            var inner = store.tabHeader.transform.parent;
            for (int i = 0; inner && i < inner.childCount; i++)
            {
                var ch = inner.GetChild(i);
                if (ch == store.tabHeader.transform || !ch.gameObject.activeSelf || !ch.name.StartsWith("Title", StringComparison.Ordinal)) continue;
                ch.gameObject.SetActive(false);
                HiddenTitles.Add(ch.gameObject);
            }
        }
        _open = true;
        BuildCards();
        Header();
        if (store.swapTabSound) SoundManager.Instance.PlaySound(store.swapTabSound, false);
        MrpAccount.Refresh();
    }

    // 本編のタブへ移った・閉じた
    private static void Leave()
    {
        if (_confirm != null) RestoreConfirm();
        if (!_open) return;
        _open = false;
        EmoteTab.Apply(_tabBg, _unsel);
        ClearCards();
        foreach (var g in HiddenTitles)
            if (g) g.SetActive(true);
        HiddenTitles.Clear();
    }

    private static readonly List<GameObject> HiddenTitles = new();

    private static void ClearCards()
    {
        UiActions.Release(Slots);
        foreach (var c in Cards)
            if (c.Btn) Object.Destroy(c.Btn.gameObject);
        Cards.Clear();
    }

    private const float RowPitch = 1.95f; // 本編の帽子のタブの段の間 (実測)
    private const float CardBelow = 1.3f; // 札の置き場から札の下端まで (実測)

    private static StoreTabButton HatsTab(StoreMenu store)
    {
        foreach (var t in store.Tabs)
            if (t != null && t.storeTab == StoreTab.Hats) return t;
        return null;
    }

    // 品の札: 本編の作る関数 (InsertProduct) で本編の品 (帽子 1 つ) の札として作り、中身を MRP の品に差し替える
    private static void BuildCards()
    {
        var store = _store;
        ClearCards();
        _seen = MrpAccount.Version;
        if (!store || !store.Scroller || store.allHats == null || store.allHats.Length == 0) return;
        var standIn = new IBuyable(store.allHats[0].Pointer);
        var items = new List<Card>();
        foreach (var item in MrpAccount.Catalog)
        {
            bool emote = item.Id.StartsWith("emote:", StringComparison.Ordinal);
            if (!byte.TryParse(item.Id[(item.Id.IndexOf(':') + 1)..], out byte kind)) continue;
            string name = emote
                ? (kind < Emotes.Names.Length ? new Text(Emotes.Names[kind]).ToString() : item.Id)
                : (kind < MeetingStamps.Keys.Length ? MeetingStamps.Label(kind) : item.Id);
            items.Add(new Card { Item = item, Name = name, Emote = emote, Kind = kind });
        }
        items.Sort((a, b) => a.Emote != b.Emote ? (a.Emote ? -1 : 1) : a.Kind.CompareTo(b.Kind));

        // 並べ方は本編の帽子のタブと同じ (1 段の数・横の範囲・始まりの高さはそのタブの値)
        var hats = HatsTab(store);
        int per = Math.Max(1, hats != null ? hats.NumPerRow : store.NumPerRow);
        var xr = hats != null ? hats.XRange : store.XRange;
        // 最初の段は見出しのすぐ下 (本編の帽子のタブで見出しの 0.93 下)
        float y0 = store.tabHeader ? store.tabHeader.transform.localPosition.y - 0.93f : store.YStart;
        float lastY = y0;
        for (int i = 0; i < items.Count; i++)
        {
            int col = i % per, rowN = i / per;
            float x = per > 1 ? Mathf.Lerp(xr.min, xr.max, col / (float)(per - 1)) : 0f;
            float y = y0 - rowN * RowPitch;
            lastY = y;
            var btn = store.InsertProduct(new Vector3(x, y, 0f), standIn);
            if (!btn) continue;
            items[i].Btn = btn;
            Dress(items[i]);
            Cards.Add(items[i]);
        }
        // スクロールの下端: 最後の段の札の下端が、一覧の切り抜きの下端より少し上に来るまで動かせるように
        var sc = store.Scroller;
        float minY = sc.ContentYBounds.min;
        var maskT = sc.transform.Find("Inner Mask Area");
        var maskSr = maskT ? maskT.GetComponent<SpriteRenderer>() : null;
        float viewBottom = maskSr ? maskSr.bounds.min.y : sc.transform.position.y - 2.6f;
        float innerAtMin = sc.Inner.position.y - (sc.Inner.localPosition.y - minY);
        float cardBottom = innerAtMin + lastY - CardBelow;
        sc.SetYBoundsMax(minY + Math.Max(0f, viewBottom + 0.15f - cardBottom));
        sc.ScrollToTop();
    }

    // 札の中身を MRP の品に
    private static void Dress(Card c)
    {
        var b = c.Btn;
        b.name = "Mrp_" + c.Item.Id;
        // 本編の帽子の絵を隠し、エモートの手 / スタンプの字を同じ所に
        var crew = b.HatImage ? b.HatImage.transform : null;
        var parent = crew ? crew.parent : b.transform;
        var crewSr = crew ? crew.GetComponent<SpriteRenderer>() : null;
        var at = crew ? crew.localPosition : Vector3.zero;
        if (crew) crew.gameObject.SetActive(false);
        if (c.Emote) Hand(parent, at + new Vector3(0f, 0.25f, 0f), c.Kind, crewSr, 0.5f, true);
        else if (b.PriceText) StampText(b.PriceText, parent, new Vector3(at.x, at.y + 0.3f, b.PriceText.transform.localPosition.z), c.Name, 3f, 1.5f);
        if (b.NameText) b.NameText.text = c.Name;
        if (b.LimitedTimeIcon) b.LimitedTimeIcon.enabled = false;
        if (b.LimitedTimeText) b.LimitedTimeText.gameObject.SetActive(false);
        Refresh(c);

        var pb = b.PassiveButton;
        if (pb)
        {
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            var card = c;
            pb.OnClick.AddListener(UiActions.Take(() => ShowConfirm(card), Slots));
        }
    }

    // スタンプの字 (札の幅に収まるように縮める)
    private static void StampText(TextMeshPro proto, Transform parent, Vector3 pos, string text, float size, float width)
    {
        var t = Object.Instantiate(proto.gameObject, parent).GetComponent<TextMeshPro>();
        t.name = "MrpStamp";
        t.transform.localPosition = pos;
        t.enableWordWrapping = false;
        float k = Math.Max(t.transform.localScale.x, 0.0001f);
        t.rectTransform.sizeDelta = new Vector2(width / k, 0.8f / k);
        t.margin = Vector4.zero;
        t.enableAutoSizing = true;
        t.fontSizeMin = size * 0.4f;
        t.fontSizeMax = size;
        t.fontSize = size;
        t.text = text;
    }

    // 持っているか・値段の出し方 (本編の札と同じく、持っている物は印と覆い・値札は絵 + 数)
    private static void Refresh(Card c)
    {
        var b = c.Btn;
        if (!b) return;
        bool owned = MrpAccount.Owned.Contains(c.Item.Id);
        if (b.PurchasedIcon) b.PurchasedIcon.enabled = owned;
        if (b.PurchasedOverlay) b.PurchasedOverlay.enabled = owned;
        if (b.PriceText) PriceText(b.PriceText, c, owned);
    }

    private static void PriceText(TextMeshPro t, Card c, bool owned)
    {
        if (owned) t.text = new Text("Shop.Owned").ToString();
        else if (MrpAccount.Tier < c.Item.MinTier) t.text = new Text("Shop.NeedTier").With("tier", MrpAccount.TierName(c.Item.MinTier)).ToString();
        else
        {
            t.text = $"{ChatIcons.Tag(MrpCoin.Sprite, 1.25f)} {c.Item.Price}";
            ChatIcons.Apply(t);
        }
    }

    // 人の色を塗る材質 (本編の着せ替えの材質。masked = スクロール欄の枠で切り抜く方)
    private static Material Mat(bool masked)
    {
        if (HatManager.InstanceExists)
        {
            var hm = HatManager.Instance;
            var m = masked ? hm.MaskedPlayerMaterial : hm.PlayerMaterial;
            if (m) return m;
        }
        return EmoteTab.Mat(_store ? _store.dressMeUp : null);
    }

    private static readonly Dictionary<int, Material> MaskedMats = new();

    // 人の色を塗る切り抜きの材質を、like の材質と同じ切り抜きの値で
    private static Material MaskedAt(SpriteRenderer like)
    {
        var src = like ? like.sharedMaterial : null;
        var baseMat = Mat(true);
        if (!src || !baseMat || !src.HasProperty(MaskLayer)) return baseMat;
        int layer = Mathf.RoundToInt(src.GetFloat(MaskLayer));
        if (MaskedMats.TryGetValue(layer, out var m) && m) return m;
        m = new Material(baseMat) { name = "MrpMaskedPlayer" + layer, hideFlags = HideFlags.DontUnloadUnusedAsset };
        m.SetFloat(MaskLayer, layer);
        MaskedMats[layer] = m;
        return m;
    }

    private const string MaskLayer = "_MaskLayer";

    private static void Hand(Transform parent, Vector3 at, byte kind, SpriteRenderer like, float s, bool masked, Material use = null)
    {
        var sprite = EmoteArt.Icon(kind);
        var mat = use ? use : Mat(masked);
        if (!sprite || !mat) return;
        var go = new GameObject("MrpHand") { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        var c = EmoteArt.IconCenter(kind);
        go.transform.localPosition = new Vector3(at.x - c.x * s, at.y - c.y * s, at.z);
        go.transform.localScale = new Vector3(s, s, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sprite = sprite;
        if (like)
        {
            sr.sortingOrder = like.sortingOrder;
            sr.maskInteraction = like.maskInteraction;
        }
        PlayerMaterial.SetColors(EmoteTab.ColorId(), sr);
    }

    // ── 購入確認 (本編の確認の画面に MRP の品を出す) ─────────────────────────

    private static Card _confirm;
    private static Sprite _iconSprite;
    private static Vector3 _iconScale;
    private static GameObject _chipArt, _mannequin;
    private static bool _noteWasOn;
    private static string _noteText;

    // 本編の確認の画面を本編の関数で開き (札の品は代わりの帽子)、中身を MRP の品に差し替える
    private static void ShowConfirm(Card c)
    {
        var store = _store;
        if (!store || !store.confirmMenu || !c.Btn) return;
        if (_confirm != null) RestoreConfirm();
        store.ShowConfirm(c.Btn);
        _confirm = c;
        if (store.BundleName) store.BundleName.text = c.Name;
        if (store.ItemName) store.ItemName.text = c.Name;

        // 左: 見本の札の人形 (帽子をかぶった物) を隠し、エモートの手 / スタンプの字
        var inner = store.ConfirmMenuScroller ? store.ConfirmMenuScroller.Inner : null;
        // 本編は前の札を捨ててから新しい札を足す (捨てるのはフレームの終わり) ので、最後の札を使う
        Transform man = null;
        for (int i = inner ? inner.childCount - 1 : -1; i >= 0 && !man; i--)
        {
            var ch = inner.GetChild(i);
            if (ch.gameObject.activeSelf && ch.GetComponent<StoreChip>()) man = EmoteTab.FindDeep(ch, "StoreMannequin");
        }
        if (man)
        {
            _mannequin = man.gameObject;
            var holder = new GameObject("MrpChipArt") { layer = man.gameObject.layer };
            holder.transform.SetParent(man.parent, false);
            holder.transform.localPosition = man.localPosition;
            _chipArt = holder;
            // 見本の札の中は、札の台と同じ切り抜きの値 (_MaskLayer) の材質で描く
            var bg = man.parent ? man.parent.GetComponent<SpriteRenderer>() : null;
            if (c.Emote) Hand(holder.transform, new Vector3(0f, 0.15f, 0f), c.Kind, bg, 0.45f, true, MaskedAt(bg));
            else if (store.ItemName) StampText(store.ItemName, holder.transform, Vector3.zero, c.Name, 2.6f, 1.3f);
            man.gameObject.SetActive(false);
        }

        // 右: 見本の人の帽子を代わりの帽子から自分の帽子に戻し (持ち物のエモートのタブの見本と同じ姿)、エモートならそのエモートをする
        var pp = store.dressMeUp;
        if (pp)
        {
            var cust = AmongUs.Data.DataManager.Player?.Customization;
            pp.SetHat(cust != null ? cust.Hat : "hat_NoHat", EmoteTab.ColorId());
            if (c.Emote) EmoteArt.Preview(pp, EmoteTab.ColorId(), c.Kind, EmoteTab.Mat(pp));
        }

        // 買うボタン: 豆の絵をコインに、値段・持っている・ランク
        var btn = store.priceButton;
        if (!btn) return;
        if (btn.podIcon)
        {
            _iconSprite = btn.podIcon.sprite;
            _iconScale = btn.podIcon.transform.localScale;
            float h = _iconSprite ? _iconSprite.bounds.size.y * _iconScale.y : 0.34f;
            var coin = MrpCoin.Sprite;
            btn.podIcon.sprite = coin;
            float k = h / Math.Max(coin ? coin.bounds.size.y : 1f, 0.0001f);
            btn.podIcon.transform.localScale = new Vector3(k, k, 1f);
        }
        bool owned = MrpAccount.Owned.Contains(c.Item.Id);
        bool rank = MrpAccount.Tier >= c.Item.MinTier;
        if (btn.text)
            btn.text.text = owned ? new Text("Shop.Owned").ToString()
                : !rank ? new Text("Shop.NeedTier").With("tier", MrpAccount.TierName(c.Item.MinTier)).ToString()
                : c.Item.Price.ToString();
        if (owned || !rank || MrpAccount.Coins < c.Item.Price) btn.SetInactive();
        else btn.SetUseable();
        // 本編が税の注意を出す欄に、非公式の通貨であることを出す
        var note = EmoteTab.FindDeep(btn.transform, "LimitedtimeAvailabilityText");
        var noteTmp = note ? note.GetComponent<TextMeshPro>() : null;
        if (noteTmp)
        {
            _noteWasOn = note.gameObject.activeSelf;
            _noteText = noteTmp.text;
            NoteHelpers(note, false);
            noteTmp.text = new Text("Shop.UnofficialShort").ToString();
            note.gameObject.SetActive(true);
        }
    }

    // 注意の欄を本編が出し入れ・書き換えする部品を止める / 戻す
    private static void NoteHelpers(Transform note, bool on)
    {
        var hide = note.GetComponent<ConditionalHide>();
        if (hide) hide.enabled = on;
        var timer = note.GetComponent<UpdateProductTimerDisplay>();
        if (timer) timer.enabled = on;
    }

    // 確認の画面を本編の物に戻す (閉じた・本編の品の確認を開く前)
    private static void RestoreConfirm()
    {
        var store = _store;
        _confirm = null;
        EmoteArt.Stop(EmoteArt.PreviewId);
        if (_chipArt) Object.Destroy(_chipArt);
        _chipArt = null;
        if (_mannequin) _mannequin.SetActive(true);
        _mannequin = null;
        var btn = store ? store.priceButton : null;
        if (!btn) return;
        if (btn.podIcon && _iconSprite)
        {
            btn.podIcon.sprite = _iconSprite;
            btn.podIcon.transform.localScale = _iconScale;
        }
        _iconSprite = null;
        var note = EmoteTab.FindDeep(btn.transform, "LimitedtimeAvailabilityText");
        if (note && _noteText != null)
        {
            var t = note.GetComponent<TextMeshPro>();
            if (t) t.text = _noteText;
            NoteHelpers(note, true);
            note.gameObject.SetActive(_noteWasOn);
        }
        _noteText = null;
        btn.SetUseable();
    }

    private static bool _busy;

    // 本編の「買う」: MRP の品を出している時はサーバーへ送り、本編の「購入中」の窓で結果を出す
    private static void Buy()
    {
        var c = _confirm;
        var store = _store;
        if (c == null || !store || _busy) return;
        if (MrpAccount.Owned.Contains(c.Item.Id) || MrpAccount.Tier < c.Item.MinTier || MrpAccount.Coins < c.Item.Price) return;
        _busy = true;
        var modal = store.plsWaitModal;
        ShowModal(modal);
        MrpAccount.Buy(c.Item.Id, Bought);
    }

    // 本編の「購入中」の窓 (閉じている間は GameObject ごと切ってある)
    private static void ShowModal(PurchasePopUp modal)
    {
        if (!modal) return;
        modal.gameObject.SetActive(true);
        modal.Show();
    }

    // 買った結果: 本編の「購入中」の窓に結果を出し、買えたら確認を閉じる (札の「持っている」は数が変わった時に書き直る)
    private static void Bought(string err)
    {
        _busy = false;
        var store = _store;
        string msg = err == null ? new Text("Shop.Bought").ToString()
            : new Text(err is "coins" or "rank" or "owned" or "no_item" or "auth" or "too_many" or "disabled" ? "Shop.Fail." + err : "Shop.Fail.network").ToString();
        if (!store) return;
        var modal = store.plsWaitModal;
        if (modal) modal.SetResponse(err == null ? ResponseState.Success : ResponseState.Failed, msg);
        if (err == null) store.CloseConfirm();
    }

    // 毎フレーム: 数が変わったらコインの欄と札を書き直す・帯の並べ直しを 1 回
    internal static void Watch()
    {
        if (_layoutPending) Layout();
        if (_seen == MrpAccount.Version) return;
        _seen = MrpAccount.Version;
        UpdateWallet();
        if (!_open) return;
        // 品の一覧が後から届いた (タブを先に開いた) 時は札を作り直す
        if (Cards.Count != CatalogCount()) BuildCards();
        else foreach (var c in Cards) Refresh(c);
        Header();
    }

    private static int CatalogCount()
    {
        int n = 0;
        foreach (var item in MrpAccount.Catalog)
            if (item.Id.StartsWith("emote:", StringComparison.Ordinal) || item.Id.StartsWith("stamp:", StringComparison.Ordinal)) n++;
        return n;
    }

    // 見出し: 品が無い間は読み込み中 / つながっていない を出す
    private static void Header()
    {
        var store = _store;
        if (!store || !store.tabHeader) return;
        store.tabHeader.text = Cards.Count > 0 ? new Text("Shop.Title").ToString()
            : new Text(MrpAccount.LastError.Length > 0 ? "Shop.Offline" : "Shop.Loading").ToString();
    }

    // ── 確かめ用 (ブリッジ) ─────────────────────────────────

    internal static void RegisterBridge()
    {
        Bridge.TestBridge.Register("shop", "[store | inventory | tab | card <n> | buy | fakebuy ok|fail | close | scroll <0〜1> | dump] 本編のショップ・持ち物の画面を本編のボタンの道で開く / MRP のタブを開く / n 番目の札を押す / 確認の画面の買うを押す / サーバーへ送らず買った結果だけ通す / 確認を閉じる / 一覧を動かす / 帯と札の数値", (args, reply) =>
        {
            var mm = Object.FindObjectOfType<MainMenuManager>();
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string op = a.Length > 0 ? a[0] : "";
            var store = Vanilla.Store;
            switch (op)
            {
                case "store":
                    if (mm && mm.shopButton) mm.shopButton.OnClick.Invoke();
                    break;
                case "inventory":
                    if (mm && mm.inventoryButton) mm.inventoryButton.OnClick.Invoke();
                    break;
                case "tab":
                    {
                        var pb = _tabBg ? _tabBg.GetComponent<PassiveButton>() : null;
                        if (pb) pb.OnClick.Invoke();
                        break;
                    }
                case "card":
                    {
                        int n = a.Length > 1 && int.TryParse(a[1], out int k) ? k : 0;
                        if (n >= 0 && n < Cards.Count && Cards[n].Btn) Cards[n].Btn.PassiveButton.OnClick.Invoke();
                        break;
                    }
                case "buy":
                    if (store && store.priceButton && store.priceButton.button) store.priceButton.button.OnClick.Invoke();
                    break;
                case "close":
                    if (store) store.CloseConfirm();
                    break;
                case "fakebuy":
                    // サーバーへ送らずに結果だけ通す (本編の「購入中」の窓と閉じ方を見る)
                    if (_confirm != null && store)
                    {
                        _busy = true;
                        ShowModal(store.plsWaitModal);
                        Bought(a.Length > 1 && a[1] == "ok" ? null : "coins");
                    }
                    break;
                case "scroll":
                    if (store && store.Scroller) store.Scroller.ScrollPercentY(a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float q) ? q : 1f);
                    break;
                case "dump":
                    if (store)
                    {
                        reply($"STORE YStart={store.YStart} YOffset={store.YOffset} NumPerRow={store.NumPerRow} XRange=({store.XRange.min},{store.XRange.max}) bounds=({store.Scroller.ContentYBounds.min},{store.Scroller.ContentYBounds.max}) header={(store.tabHeader ? store.tabHeader.transform.parent.name + "/" + store.tabHeader.name + " " + store.tabHeader.transform.localPosition : "-")}");
                        var inner = store.Scroller.Inner;
                        for (int i = 0; i < Math.Min(inner.childCount, 4); i++)
                        {
                            var ch = inner.GetChild(i);
                            reply($"  INNER {ch.name} pos={ch.localPosition} scale={ch.localScale}");
                        }
                        reply("LAYOUT " + _layoutInfo);
                        var ev = store.priceButton && store.priceButton.button ? store.priceButton.button.OnClick : null;
                        for (int i = 0; ev != null && i < ev.GetPersistentEventCount(); i++)
                            {
                            var tg = ev.GetPersistentTarget(i);
                            reply($"BUYBTN {(tg ? tg.name : "-")} . {ev.GetPersistentMethodName(i)}");
                        }
                        var cb = Cards.Count > 0 && Cards[0].Btn ? Cards[0].Btn.PassiveButton.OnClick : null;
                        reply($"CARD listeners persistent={(cb != null ? cb.GetPersistentEventCount() : -1)}");
                    }
                    break;
            }
            reply($"OK shop menu={(bool)mm} store={(store ? store.isActiveAndEnabled.ToString() : "none")} tab={_open} cards={Cards.Count} confirm={(_confirm != null ? _confirm.Item.Id : "-")} ready={MrpAccount.Ready} coins={MrpAccount.Coins} items={MrpAccount.Catalog.Count} wallet={(_walletText ? _walletText.text : "-")}");
        });
    }

    // ── 本編の流れへの差し込み ─────────────────────────────

    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.Open))]
    private static class StoreOpenPatch
    {
        public static void Postfix(StoreMenu __instance)
        {
            try { Ensure(__instance); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop: {e}"); }
        }
    }

    // 本編のタブを開いた (MRP のタブの札を片付ける)
    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.ShowTab))]
    private static class ShowTabPatch
    {
        public static void Prefix()
        {
            try { Leave(); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop leave: {e}"); }
        }
    }

    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.Close))]
    private static class StoreClosePatch
    {
        public static void Prefix()
        {
            try { Leave(); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop close: {e}"); }
        }
    }

    // 本編の品の確認を開く前・確認を閉じた後に、確認の画面を本編の物に戻す
    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.ShowConfirm))]
    private static class ShowConfirmPatch
    {
        public static void Prefix()
        {
            try { if (_confirm != null) RestoreConfirm(); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop confirm: {e}"); }
        }
    }

    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.CloseConfirm))]
    private static class CloseConfirmPatch
    {
        public static void Postfix()
        {
            try { if (_confirm != null) RestoreConfirm(); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop confirm close: {e}"); }
        }
    }

    // MRP の品を出している時の「買う」は本編の購入へ流さない
    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.BuyProduct))]
    private static class BuyPatch
    {
        public static bool Prefix(StoreMenu __instance)
        {
            var cur = __instance.CurrentButton;
            if (_confirm == null && !(cur && IsMine(cur))) return true;
            try { Buy(); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop buy: {e}"); }
            return false;
        }
    }

    // MRP の札 (中身は代わりの帽子) を本編の購入に流さない (豆で代わりの帽子を買ってしまわないように)
    [HarmonyPatch(typeof(PurchaseButton), nameof(PurchaseButton.DoPurchase))]
    private static class DoPurchasePatch
    {
        public static bool Prefix(PurchaseButton __instance)
        {
            if (_confirm == null && !IsMine(__instance)) return true;
            try { Buy(); }
            catch (Exception e) { Plugin.Logger.LogError($"mrp shop buy: {e}"); }
            return false;
        }
    }

    private static bool IsMine(PurchaseButton b)
    {
        foreach (var c in Cards)
            if (c.Btn && c.Btn.Pointer == b.Pointer) return true;
        return false;
    }

    private static bool _watchFailed;

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try { Watch(); }
            catch (Exception e)
            {
                // 毎フレーム同じ物を出さないように 1 回だけ
                if (!_watchFailed) Plugin.Logger.LogError($"mrp shop watch: {e}");
                _watchFailed = true;
            }
        }
    }
}
