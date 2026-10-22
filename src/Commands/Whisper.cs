// Based on the whisper (/w) of https://github.com/Gurge44/EndlessHostRoles and the nameplate buttons of
// https://github.com/TheOtherRolesAU/TheOtherRoles (cloning the vote cancel button) (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Commands;

[Settings(Tab.General, "WhisperSettings.Title", order: -70)]
public static class WhisperSettings
{
    public static readonly BoolOpt Enabled = new("WhisperSettings.Enabled", true);
    public static readonly BoolOpt Announce = new("WhisperSettings.Announce", true);
}

// 耳打ち: 相手の端末にだけ文を送る。使えるのはロビーと会議中 (会議では生きている人同士だけ)。
// 相手を選ぶ: 会議は名札の吹き出しのボタン・ロビーはチャット欄の横の吹き出しのボタン → 名前の一覧。
// 相手を選ぶとチャット欄の次の送信が耳打ちになる (空で送ると取りやめ)。設定によっては「誰が誰に耳打ちしたか」だけ全員に出す
internal static class Whisper
{
    private const byte None = 255;
    private static readonly Color Tint = new(0.78f, 0.6f, 1f, 1f);
    internal static byte Target = None;

    private static readonly RemoteCall<(byte to, string text)> Msg = new("Whisper.Msg", Route.Anyone,
        (w, v) => { w.Write(v.to); w.Write(v.text); }, r => (r.ReadByte(), r.ReadString()), (sender, v) => Receive(sender, v.to, v.text), perSecond: 5);

    private static readonly RemoteCall<byte> Told = new("Whisper.Told", Route.Anyone,
        (w, to) => w.Write(to), r => r.ReadByte(), (sender, to) => ReceiveTold(sender, to), perSecond: 5);

    private static bool InLobby => !ShipStatus.Instance && Vanilla.StartManager;

    // 今 from から to へ耳打ちしてよいか
    private static bool Allowed(PlayerControl from, PlayerControl to)
    {
        if (!WhisperSettings.Enabled || !from || !to || from.PlayerId == to.PlayerId || from.Data == null || to.Data == null) return false;
        if (InLobby) return true;
        return MeetingHud.Instance && !from.Data.IsDead && !to.Data.IsDead && !from.Data.Disconnected && !to.Data.Disconnected;
    }

    private static PlayerControl Player(byte id)
    {
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
            if (all[i] && all[i].PlayerId == id) return all[i];
        return null;
    }

    private static string Name(PlayerControl p) => Plain(p && p.Data != null ? p.Data.PlayerName : "?");

    // 文や名前の < > は全角にする (色や見出しの装飾を混ぜて偽の耳打ちの表示を作れないように)
    internal static string Plain(string s) => (s ?? "").Replace('<', '＜').Replace('>', '＞');

    private static string _restore; // 送れなかった文 (入力欄へ戻す)

    // 相手を選んだ (会議の名札・ロビーの一覧)。チャット欄を開いて次の送信を耳打ちにする
    public static void Choose(byte id)
    {
        var to = Player(id);
        var me = PlayerControl.LocalPlayer;
        var chat = Vanilla.Chat;
        if (!Allowed(me, to))
        {
            if (chat) DevChat.Notice(chat, new Text("Whisper.CantWhisper"));
            return;
        }
        Target = id;
        if (!chat) return;
        if (!chat.IsOpenOrOpening) chat.Toggle();
        DevChat.Notice(chat, new Text("Whisper.WhisperingTo").With("name", Name(to)));
        WhisperList.Hide();
    }

    // チャット欄の送信 (DevChat の入口から)。耳打ちとして扱ったら true
    public static bool TrySend(ChatController chat, string text)
    {
        if (Target == None) return false;
        if (text.StartsWith('/')) return false; // コマンドはそのまま
        byte id = Target;
        Target = None;
        var to = Player(id);
        var me = PlayerControl.LocalPlayer;
        if (text.Length == 0)
        {
            DevChat.Notice(chat, new Text("Whisper.WhisperCancelled"));
            return true;
        }
        if (!Allowed(me, to) || to.OwnerId < 0)
        {
            DevChat.Notice(chat, new Text("Whisper.CantWhisperTextKept"));
            _restore = text;
            return true;
        }
        if (text.Length > ChatInput.Limit) text = text[..ChatInput.Limit];
        Msg.Send((id, text), to.OwnerId);
        if (WhisperSettings.Announce) Told.Send(id);
        chat.AddChat(me, $"<color=#C79BFF>[→ {Name(to)}]</color> {Plain(text)}", false);
        return true;
    }

    // 送信の処理が入力欄を空にした後に呼ぶ: 送れなかった文を戻す
    public static void AfterClear(FreeChatInputField field)
    {
        if (_restore == null) return;
        string s = _restore;
        _restore = null;
        if (field && field.textArea) field.textArea.SetText(s, "");
    }

    private static void Receive(PlayerControl from, byte to, string text)
    {
        var me = PlayerControl.LocalPlayer;
        if (!me || me.PlayerId != to || !Allowed(from, me) || string.IsNullOrEmpty(text)) return;
        if (text.Length > ChatInput.Limit) text = text[..ChatInput.Limit];
        var chat = Vanilla.Chat;
        if (chat) chat.AddChat(from, $"<color=#C79BFF>[{new Text("Whisper.Label")}]</color> {Plain(text)}", false);
    }

    private static void ReceiveTold(PlayerControl from, byte to)
    {
        var me = PlayerControl.LocalPlayer;
        if (!me || me.PlayerId == to || !WhisperSettings.Announce) return;
        var target = Player(to);
        if (!Allowed(from, target)) return;
        var chat = Vanilla.Chat;
        if (chat) DevChat.Notice(chat, new Text("Whisper.Announce").With("from", Name(from)).With("to", Name(target)));
    }

    // 会議が終わったら選んだ相手を忘れる
    public static void Reset() => Target = None;

    // ---- 会議: 名札に吹き出しのボタン ----

    private static readonly Dictionary<byte, UnityAction> PlateClicks = new();

    public static void AddPlateButtons(MeetingHud hud)
    {
        Reset();
        if (!WhisperSettings.Enabled || !hud) return;
        var me = PlayerControl.LocalPlayer;
        if (!me || me.Data == null || me.Data.IsDead) return;
        foreach (var pva in hud.playerStates)
        {
            if (!pva || pva.AmDead) continue;
            byte id = pva.PlayerId.Value;
            if (id == me.PlayerId) continue;
            var template = pva.Buttons ? pva.Buttons.transform.Find("CancelButton") : null;
            if (!template) continue;
            var go = Object.Instantiate(template.gameObject, pva.transform);
            go.name = "MrpWhisperButton";
            go.transform.localPosition = new Vector3(1.05f, 0.18f, -1.3f);
            go.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr) { sr.sprite = MenuArt.Bubble(); sr.color = Tint; }
            var pb = go.GetComponent<PassiveButton>();
            if (!pb) continue;
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            if (!PlateClicks.TryGetValue(id, out var a)) PlateClicks[id] = a = (UnityAction)(() => Choose(id));
            pb.OnClick.AddListener(a);
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
        }
    }

    internal static string Describe() => $"OK whisper target={(Target == None ? "-" : Target.ToString())} enabled={WhisperSettings.Enabled.Value} announce={WhisperSettings.Announce.Value}";
}

// ロビーの耳打ちの相手の一覧 (チャット欄の横の吹き出しのボタンで開く)。名前を押すとその人を選ぶ
internal static class WhisperList
{
    private static GameObject _root;
    private static PassiveButton _open;
    private static UnityAction _toggle;
    private static readonly Dictionary<int, UnityAction> Picks = new();
    private const int PerPage = 14; // 2 列 × 7 行。いちばん下の段はページ送り
    private static readonly byte[] SlotIds = new byte[PerPage];
    private static readonly List<byte> Others = new();
    private static int _page;
    private static UnityAction _prev, _next;

    public static void Hide()
    {
        if (_root) Object.Destroy(_root);
        _root = null;
    }

    // チャット欄を開いた時に、ロビーなら吹き出しのボタンを出す
    public static void OnChatOpened(ChatController chat)
    {
        bool lobby = !ShipStatus.Instance && Vanilla.StartManager && WhisperSettings.Enabled;
        if (!_open && lobby && chat.banButton && chat.banButton.MenuButton)
        {
            var src = chat.banButton.MenuButton;
            _open = Object.Instantiate(src, src.transform.parent);
            _open.name = "MrpWhisperOpen";
            // 通報のボタンの上の空いている所。中の絵は消して吹き出しを描く (ボタンの地はそのまま)
            _open.transform.localPosition = src.transform.localPosition + new Vector3(0f, 0.75f, 0f);
            foreach (var sr in _open.GetComponentsInChildren<SpriteRenderer>(true))
                if (sr.gameObject != _open.gameObject) sr.enabled = false;
            var icon = new GameObject("MrpWhisperIcon") { layer = _open.gameObject.layer };
            icon.transform.SetParent(_open.transform, false);
            icon.transform.localPosition = new Vector3(0f, 0f, -0.1f);
            // 大きさはボタンの中の単位で決める。チャット窓は開く時に小さい所から広がるので、作った時の画面の縮尺は開き具合で変わる
            const float u = 1.17f; // 隣のボタンの見た目の大きさに合わせた係数
            // 他のボタンと同じ「黒いふちの白い角丸」の地
            var edge = MenuArt.Panel(_open.transform, "MrpWhisperEdge", 0.12f * u, new Vector2(0.62f * u, 0.62f * u), new Vector3(0f, 0f, -0.05f), Color.black, null);
            var bg = MenuArt.Panel(_open.transform, "MrpWhisperBg", 0.1f * u, new Vector2(0.54f * u, 0.54f * u), new Vector3(0f, 0f, -0.07f), new Color(0.93f, 0.93f, 0.95f, 1f), null);
            float k = 0.36f / 0.3f * u; // 吹き出しはボタンの中で幅 0.36 × 係数
            icon.transform.localPosition = new Vector3(0f, 0f, -0.1f);
            icon.transform.localScale = new Vector3(k, k, 1f);
            var isr = icon.AddComponent<SpriteRenderer>();
            isr.sprite = MenuArt.Bubble();
            isr.color = new Color(0.55f, 0.35f, 0.85f, 1f);
            edge.sortingOrder = 10;
            bg.sortingOrder = 11;
            isr.sortingOrder = 12;
            _open.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            _open.OnClick.AddListener(_toggle ??= (UnityAction)Toggle);
        }
        if (_open) _open.gameObject.SetActive(lobby);
        if (!lobby) Hide();
    }

    internal static void Toggle()
    {
        if (_root) { Hide(); return; }
        _page = 0;
        Build();
    }

    internal static void Turn(int d)
    {
        int pages = (Others.Count + PerPage - 1) / PerPage;
        if (pages <= 1) return;
        _page = ((_page + d) % pages + pages) % pages;
        Hide();
        Build();
    }

    private static void Build()
    {
        var chat = Vanilla.Chat;
        if (!chat || !chat.backgroundImage) return;
        var me = PlayerControl.LocalPlayer;
        var parent = chat.backgroundImage.transform;
        _root = new GameObject("MrpWhisperList") { layer = chat.gameObject.layer };
        _root.transform.SetParent(parent, false);
        _root.transform.localPosition = new Vector3(0f, 0f, -20f);
        var back = MenuArt.Panel(_root.transform, "Back", 0.15f, new Vector2(4.2f, 3.6f), Vector3.zero, new Color(0.08f, 0.06f, 0.12f, 0.96f), null);
        back.sortingOrder = 500;

        Others.Clear();
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p && p.Data != null && (!me || p.PlayerId != me.PlayerId)) Others.Add(p.PlayerId);
        }
        int pages = (Others.Count + PerPage - 1) / PerPage;
        if (_page >= pages) _page = Math.Max(0, pages - 1);

        int n = 0;
        var proto = chat.freeChatField ? chat.freeChatField.charCountText : null;
        for (int k = _page * PerPage; k < Others.Count && n < SlotIds.Length; k++)
        {
            var p = Find(Others[k]);
            if (!p) continue;
            SlotIds[n] = p.PlayerId;
            float x = n % 2 == 0 ? -1.0f : 1.0f, y = 1.45f - (n / 2) * 0.42f;
            var row = MenuArt.Panel(_root.transform, "Row" + n, 0.08f, new Vector2(1.9f, 0.36f), new Vector3(x, y, -1f), new Color(0.3f, 0.2f, 0.45f, 1f), null);
            row.sortingOrder = 501;
            var col = row.gameObject.AddComponent<BoxCollider2D>();
            col.size = new Vector2(1.9f, 0.36f);
            var pb = row.gameObject.AddComponent<PassiveButton>();
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
            int slot = n;
            if (!Picks.TryGetValue(slot, out var a)) Picks[slot] = a = (UnityAction)(() => Whisper.Choose(SlotIds[slot]));
            pb.OnClick.AddListener(a);
            if (proto) Label(proto, row.transform, p.Data.PlayerName);
            n++;
        }
        if (n == 0) { Hide(); return; }
        if (pages > 1) Pager(proto, pages);
    }

    // 一覧のいちばん下の段: 「<  1/2  >」
    private static void Pager(TextMeshPro proto, int pages)
    {
        const float y = 1.45f - PerPage / 2 * 0.42f;
        _prev ??= (UnityAction)(() => Turn(-1));
        _next ??= (UnityAction)(() => Turn(1));
        PagerButton(proto, -1.0f, y, "<", _prev);
        PagerButton(proto, 1.0f, y, ">", _next);
        if (proto) Label(proto, MenuArt.Panel(_root.transform, "Page", 0.08f, new Vector2(0.9f, 0.36f), new Vector3(0f, y, -1f), new Color(0, 0, 0, 0), null).transform, $"{_page + 1}/{pages}");
    }

    private static void PagerButton(TextMeshPro proto, float x, float y, string glyph, UnityAction act)
    {
        var row = MenuArt.Panel(_root.transform, "Page" + glyph, 0.08f, new Vector2(0.6f, 0.36f), new Vector3(x, y, -1f), new Color(0.3f, 0.2f, 0.45f, 1f), null);
        row.sortingOrder = 501;
        var col = row.gameObject.AddComponent<BoxCollider2D>();
        col.size = new Vector2(0.6f, 0.36f);
        var pb = row.gameObject.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener(act);
        if (proto) Label(proto, row.transform, glyph);
    }

    private static TextMeshPro Label(TextMeshPro proto, Transform row, string text)
    {
        var t = Object.Instantiate(proto, row);
        t.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        t.transform.localScale = Vector3.one;
        t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        t.rectTransform.anchoredPosition = Vector2.zero;
        t.rectTransform.sizeDelta = new Vector2(1.8f, 0.34f);
        t.alignment = TextAlignmentOptions.Center;
        t.enableAutoSizing = true;
        t.fontSizeMin = 0.8f;
        t.fontSizeMax = 1.6f;
        t.color = Color.white;
        t.text = text;
        t.sortingOrder = 502;
        return t;
    }

    private static PlayerControl Find(byte id)
    {
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.PlayerId == id) return p;
        return null;
    }
}

[HarmonyPatch]
internal static class WhisperPatches
{
    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start)), HarmonyPostfix]
    public static void MeetingStart(MeetingHud __instance)
    {
        try { Whisper.AddPlateButtons(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"whisper: {e.Message}"); }
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy)), HarmonyPostfix]
    public static void MeetingEnd() => Whisper.Reset();

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void ChatToggle(ChatController __instance)
    {
        try { WhisperList.OnChatOpened(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"whisper list: {e.Message}"); }
    }
}
