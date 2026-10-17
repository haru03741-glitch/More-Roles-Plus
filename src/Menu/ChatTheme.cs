// Based on https://github.com/Gurge44/EndlessHostRoles Patches/ChatControlPatch.cs・ChatBubblePatch.cs (dark theme) and
// https://github.com/xChipseq/VanillaEnhancements Patches/MeetingPatches.cs (dark meeting screen) (GPL-3.0)
using System;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// チャットと会議の画面を暗い色にする (端末ごとの設定)。色を塗るのは、チャット欄を開いた時・吹き出しに文を入れた時・
// 会議が始まった時・設定を切り替えた時だけ (毎フレームは触らない)
internal static class ChatTheme
{
    private static readonly Color PanelDark = new(0.13f, 0.13f, 0.14f, 1f);
    private static readonly Color FieldDark = new(0.16f, 0.16f, 0.16f, 1f);
    private static readonly Color BubbleDark = new(0.1f, 0.1f, 0.1f, 1f);

    // 本編の色 (暗くする前に控えて、切った時に戻す)
    private static Color _panel = Color.white, _field = Color.white, _quick = Color.white;
    private static bool _painted;

    public static void Refresh()
    {
        var chat = Vanilla.Chat;
        if (!chat) return;
        Apply(chat);
        // 出ている吹き出しも塗り直す
        var pool = chat.chatBubblePool;
        if (!pool) return;
        var active = pool.activeChildren;
        for (int i = 0; i < active.Count; i++)
        {
            var b = active[i] ? active[i].TryCast<ChatBubble>() : null;
            if (b) Bubble(b);
        }
    }

    public static void Apply(ChatController chat)
    {
        bool dark = ClientOptions.DarkTheme.Value;
        var free = chat.freeChatField;
        var quick = chat.quickChatField;
        if (dark && !_painted)
        {
            if (chat.backgroundImage) _panel = chat.backgroundImage.color;
            if (free && free.background) _field = free.background.color;
            if (quick && quick.background) _quick = quick.background.color;
        }
        if (!dark && !_painted) return;
        _painted = dark;
        if (chat.backgroundImage) chat.backgroundImage.color = dark ? PanelDark : _panel;
        if (free)
        {
            if (free.background) free.background.color = dark ? FieldDark : _field;
            if (free.textArea && free.textArea.outputText) free.textArea.outputText.color = dark ? Color.white : Color.black;
            if (free.charCountText) free.charCountText.color = dark ? Color.white : Color.black;
        }
        if (quick)
        {
            if (quick.background) quick.background.color = dark ? FieldDark : _quick;
            if (quick.text) quick.text.color = dark ? Color.white : Color.black;
        }
    }

    // 吹き出し 1 つ。本編の白い吹き出しだけ塗る (お知らせなど色の付いた吹き出しはそのまま)。透けぐあい (死んだ人) は残す
    public static void Bubble(ChatBubble b)
    {
        var bg = b.Background;
        if (!bg) return;
        var c = bg.color;
        bool dark = ClientOptions.DarkTheme.Value;
        if (dark && c.r > 0.95f && c.g > 0.95f && c.b > 0.95f)
        {
            bg.color = new Color(BubbleDark.r, BubbleDark.g, BubbleDark.b, c.a);
            if (b.TextArea) b.TextArea.color = Color.white;
        }
        else if (!dark && c.r == BubbleDark.r && c.g == BubbleDark.g && c.b == BubbleDark.b)
        {
            bg.color = new Color(1f, 1f, 1f, c.a);
            if (b.TextArea) b.TextArea.color = Color.black;
        }
    }

    public static void Meeting(MeetingHud hud)
    {
        if (!ClientOptions.DarkMeeting.Value) return;
        var phone = hud.meetingContents ? hud.meetingContents.Find("PhoneUI/baseColor") : null;
        var sr = phone ? phone.GetComponent<SpriteRenderer>() : null;
        if (sr) sr.color = new Color(0.01f, 0.01f, 0.01f);
        if (hud.Glass) hud.Glass.color = new Color(0.7f, 0.7f, 0.7f, 0.3f);
        var skip = hud.SkipVoteButton ? hud.SkipVoteButton.GetComponent<SpriteRenderer>() : null;
        if (skip) skip.color = new Color(0.4f, 0.4f, 0.4f);
        var parts = hud.PlayerColoredParts;
        if (parts == null) return;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (!p) continue;
            p.color = new Color(0.25f, 0.25f, 0.25f);
            PlayerMaterial.SetColors(7, p);
        }
    }

    public static void VoteArea(PlayerVoteArea area)
    {
        if (ClientOptions.DarkMeeting.Value && area.Background) area.Background.color = new Color(0.1f, 0.1f, 0.1f);
    }
}

[HarmonyPatch]
internal static class ChatThemePatches
{
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void Toggle(ChatController __instance)
    {
        try { ChatTheme.Apply(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"chat theme: {e.Message}"); }
    }

    // 吹き出しに文を入れた後 (名前の色を決める SetName は色の構造体を引数に取るので、そちらには掛けない)
    [HarmonyPatch(typeof(ChatBubble), nameof(ChatBubble.SetText)), HarmonyPostfix]
    public static void SetText(ChatBubble __instance)
    {
        try { ChatTheme.Bubble(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"chat theme bubble: {e.Message}"); }
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start)), HarmonyPostfix]
    public static void MeetingStart(MeetingHud __instance)
    {
        try { ChatTheme.Meeting(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"meeting theme: {e.Message}"); }
    }

    [HarmonyPatch(typeof(PlayerVoteArea), nameof(PlayerVoteArea.SetCosmetics)), HarmonyPostfix]
    public static void VoteArea(PlayerVoteArea __instance)
    {
        try { ChatTheme.VoteArea(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"meeting theme: {e.Message}"); }
    }
}
