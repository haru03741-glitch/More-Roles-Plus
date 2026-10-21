using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Commands;

[Settings(Tab.General, "TeamChatSettings.Title", order: -69)]
public static class TeamChatSettings
{
    public const int Off = 0, Meetings = 1, MeetingsAndTasks = 2;

    public static readonly ChoiceOpt Mode = new("TeamChatSettings.Mode", MeetingsAndTasks,
        new Text("TeamChatSettings.Off"), new Text("TeamChatSettings.Meetings"), new Text("TeamChatSettings.MeetingsAndTasks"));
}

// 陣営チャット: 同じ陣営の仲間 (今はインポスター同士) の端末にだけ文を送る。本編のチャットの送信は通さず MRP の電文で届ける。
// 会議中はチャット欄の横のボタンで「全員へ / 仲間へ」を切り替える。設定によってはタスク中も仲間にだけチャット欄を出し、
// その間は全員へは送れない (本編のチャットで送るとタスク中のクルーに届いてしまう)。
// 送れるのは生きている人から生きている仲間へ。読むのは陣営の全員 (死んだ仲間も読める)
internal static class TeamChat
{
    private static readonly Color Tint = new(1f, 0.25f, 0.25f, 1f);
    private static readonly Color TintOff = new(0.55f, 0.55f, 0.6f, 1f);
    private const int PendingMax = 30;

    private static readonly RemoteCall<string> Msg = new("TeamChat.Msg", Route.Anyone,
        (w, text) => w.Write(text), r => r.ReadString(), Receive, Delivery.Queued);

    private static bool _on; // 会議中に「仲間へ」を選んでいるか
    private static string _restore; // 送れなかった文 (入力欄へ戻す)
    private static readonly List<(byte from, string text)> Pending = new(); // チャット欄が隠れている間に届いた文

    private static int Mode => TeamChatSettings.Mode.Value;
    private static bool InMeeting => MeetingHud.Instance;
    // 追放の画面も含める (本編は追放の画面が消える前に HUD を出し直すので、その時にチャット欄を出す)
    private static bool InTasks => ShipStatus.Instance && !MeetingHud.Instance && !PracticeMatch.Sandbox;

    // 陣営チャットでつながる陣営 (無ければ null)。送り手と受け手で同じ判定を使う
    private static Team? TeamOf(PlayerControl p)
    {
        if (!p || p.Data == null) return null;
        var role = RoleState.Of(p);
        if (role != null) return role.HasTeamChat ? role.Team : null;
        return p.Data.Role && p.Data.Role.IsImpostor ? Team.Impostor : null;
    }

    private static bool Mates(PlayerControl a, PlayerControl b)
    {
        if (!a || !b || a.PlayerId == b.PlayerId) return false;
        var t = TeamOf(a);
        return t != null && t == TeamOf(b);
    }

    private static bool Alive(PlayerControl p) => p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

    // 自分が今、陣営チャットで送れる場面か (生きていて仲間がいて、設定がこの場面を許している)
    private static bool CanUse(PlayerControl me)
    {
        if (Mode == TeamChatSettings.Off || !Alive(me) || TeamOf(me) == null) return false;
        if (!(InMeeting || Mode == TeamChatSettings.MeetingsAndTasks && InTasks)) return false;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
            if (Alive(all[i]) && Mates(me, all[i])) return true;
        return false;
    }

    // タスク中にチャット欄を出すか (HUD を出し直す時に見る)
    public static bool ShowsInTasks => Mode == TeamChatSettings.MeetingsAndTasks && InTasks && CanUse(PlayerControl.LocalPlayer);

    // 次の送信が陣営チャットになるか (入力欄の色もこれで決める)
    public static bool Tinted
    {
        get
        {
            var me = PlayerControl.LocalPlayer;
            return me && CanUse(me) && (InTasks || _on);
        }
    }

    // チャット欄の送信 (DevChat の入口から)。陣営チャットとして扱ったら true
    public static bool TrySend(ChatController chat, string text)
    {
        if (text.StartsWith('/')) return false; // コマンドはそのまま
        var me = PlayerControl.LocalPlayer;
        // タスク中に陣営チャットとして出したチャット欄の文は本編へ流さない (流すとクルーに届く)
        bool tasks = InTasks && Alive(me) && Mode == TeamChatSettings.MeetingsAndTasks && TeamOf(me) != null;
        if (!tasks && !(_on && InMeeting)) return false;
        if (text.Length == 0) return true;
        if (!CanUse(me))
        {
            DevChat.Notice(chat, new Text("TeamChat.NoMates"));
            _restore = text;
            return true;
        }
        if (text.Length > ChatInput.Limit) text = text[..ChatInput.Limit];
        var sent = new HashSet<int>();
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (!p || p.isDummy || p.OwnerId < 0 || !Mates(me, p) || !sent.Add(p.OwnerId)) continue;
            Msg.Send(text, p.OwnerId);
        }
        chat.AddChat(me, Line(text), false);
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

    private static string Line(string text) => $"<color=#FF4040>[{new Text("TeamChat.Label")}]</color> {Whisper.Plain(text)}";

    private static void Receive(PlayerControl from, string text)
    {
        var me = PlayerControl.LocalPlayer;
        if (Mode == TeamChatSettings.Off || !ShipStatus.Instance || !Alive(from) || !Mates(from, me) || string.IsNullOrEmpty(text)) return;
        // 送り手と同じ場面の決まり (会議の終わりに送った文は追放の画面で届くことがある)
        if (!(InMeeting || ExileController.Instance || Mode == TeamChatSettings.MeetingsAndTasks && InTasks)) return;
        if (text.Length > ChatInput.Limit) text = text[..ChatInput.Limit];
        Plugin.Logger.LogInfo($"[teamchat] from={from.PlayerId} len={text.Length}");
        var chat = Vanilla.Chat;
        // チャット欄が隠れている間 (追放の画面など) に足すと本編の表示の処理が止まるので、出した時に足す
        if (chat && chat.gameObject.activeInHierarchy) chat.AddChat(from, Line(text), false);
        else if (Pending.Count < PendingMax) Pending.Add((from.PlayerId, text));
    }

    // 隠れている間に届いた文を足す (チャット欄を出した後に呼ぶ)
    public static void Flush()
    {
        var chat = Vanilla.Chat;
        if (Pending.Count == 0 || !chat || !chat.gameObject.activeInHierarchy) return;
        foreach (var (id, text) in Pending)
        {
            var from = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
            if (from && from.Object) chat.AddChat(from.Object, Line(text), false);
        }
        Pending.Clear();
    }

    public static void OnGameStart()
    {
        _on = false;
        _restore = null;
        Pending.Clear();
    }

    // ---- チャット欄の横の切り替えボタン ----

    private static PassiveButton _button;
    private static SpriteRenderer _icon;
    private static UnityAction _click;

    internal static void Toggle()
    {
        var chat = Vanilla.Chat;
        if (InTasks)
        {
            if (chat) DevChat.Notice(chat, new Text("TeamChat.TasksOnly"));
            return;
        }
        if (!CanUse(PlayerControl.LocalPlayer)) { _on = false; Refresh(chat); return; }
        _on = !_on;
        if (chat) DevChat.Notice(chat, new Text(_on ? "TeamChat.On" : "TeamChat.Off"));
        Refresh(chat);
    }

    // チャット欄を開いた時とボタンを押した時に、ボタンの出し入れと入力欄の色を合わせる
    public static void Refresh(ChatController chat)
    {
        if (!chat) return;
        bool show = CanUse(PlayerControl.LocalPlayer);
        if (show && !_button) Build(chat);
        if (_button) _button.gameObject.SetActive(show);
        if (_icon) _icon.color = Tinted ? Tint : TintOff;
        Menu.ChatTheme.Apply(chat);
        Flush();
    }

    // 通報のボタンの上の空いている所 (ロビーの耳打ちの一覧のボタンと同じ所。あちらはロビーだけ、こちらは試合中だけ)
    private static void Build(ChatController chat)
    {
        if (!chat.banButton || !chat.banButton.MenuButton) return;
        var src = chat.banButton.MenuButton;
        _button = Object.Instantiate(src, src.transform.parent);
        _button.name = "MrpTeamChat";
        _button.transform.localPosition = src.transform.localPosition + new Vector3(0f, 0.75f, 0f);
        foreach (var sr in _button.GetComponentsInChildren<SpriteRenderer>(true))
            if (sr.gameObject != _button.gameObject) sr.enabled = false;
        const float u = 1.17f; // 隣のボタンの見た目の大きさに合わせた係数
        var edge = MenuArt.Panel(_button.transform, "MrpTeamChatEdge", 0.12f * u, new Vector2(0.62f * u, 0.62f * u), new Vector3(0f, 0f, -0.05f), Color.black, null);
        var bg = MenuArt.Panel(_button.transform, "MrpTeamChatBg", 0.1f * u, new Vector2(0.54f * u, 0.54f * u), new Vector3(0f, 0f, -0.07f), new Color(0.93f, 0.93f, 0.95f, 1f), null);
        var icon = new GameObject("MrpTeamChatIcon") { layer = _button.gameObject.layer };
        icon.transform.SetParent(_button.transform, false);
        float k = 0.36f / 0.3f * u;
        icon.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        icon.transform.localScale = new Vector3(k, k, 1f);
        _icon = icon.AddComponent<SpriteRenderer>();
        _icon.sprite = MenuArt.Bubble();
        edge.sortingOrder = 10;
        bg.sortingOrder = 11;
        _icon.sortingOrder = 12;
        _button.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        _button.OnClick.AddListener(_click ??= (UnityAction)Toggle);
    }

    // dev: null = ボタンを押したのと同じ・true/false = 会議中の切り替えを直に決める
    internal static void BridgeSet(bool? on)
    {
        if (on == null) { Toggle(); return; }
        _on = on.Value;
        Refresh(Vanilla.Chat);
    }

    internal static string Describe()
    {
        var me = PlayerControl.LocalPlayer;
        var t = TeamOf(me);
        return $"OK teamchat mode={Mode} team={(t?.ToString() ?? "-")} on={_on} canUse={me && CanUse(me)} tinted={Tinted} showsInTasks={ShowsInTasks} pending={Pending.Count} button={(_button ? _button.gameObject.activeInHierarchy.ToString() : "-")} sent={Msg.Sent} received={Msg.Received}";
    }
}

[HarmonyPatch]
internal static class TeamChatPatches
{
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void ChatToggle(ChatController __instance)
    {
        try { TeamChat.Refresh(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"team chat: {e.Message}"); }
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start)), HarmonyPostfix]
    public static void MeetingStart()
    {
        try { TeamChat.Refresh(Vanilla.Chat); }
        catch (Exception e) { Plugin.Logger.LogError($"team chat: {e.Message}"); }
    }
}
