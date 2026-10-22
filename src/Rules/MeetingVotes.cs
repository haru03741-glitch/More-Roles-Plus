// Based on the skip-vote and no-vote-is-self-vote meeting options in https://github.com/TheOtherRolesAU/TheOtherRoles
// and https://github.com/AU-Avengers/TOU-Mira (GPL-3.0)
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// 会議の票のルール (GameRules の行)。
// - スキップできない: スキップの札を全員の画面で 0 倍にする (当たり判定も 0 になるので押せない)。
//   時間切れで投票しなかった人は本編どおりスキップになる (自分に入れた扱いと組み合わせるとスキップは 0 になる)
// - 投票しなかった人は自分に入れた扱い: 時間切れで本編が未投票の人をスキップにする (ForceSkipAll) 直前に、
//   ホストが生きている未投票の人の票を本人へ入れる。票の関数は PlayerId (構造体) を引数に取るのでそこには当てず、
//   引数の無い ForceSkipAll と、ホストがダミーの票に使っている CmdCastVote(byte, byte) を使う
internal static class MeetingVotes
{
    // CmdCastVote の 2 つ目は投票先の番号でなく名札の並び (playerStates) の添字。番号に欠けがある部屋では一致しない。
    // スキップ (253) などの印はそのまま返す
    internal static byte SuspectIndex(MeetingHud hud, byte playerId)
    {
        if (!hud || playerId >= 252) return playerId;
        var states = hud.playerStates;
        for (int i = 0; i < states.Length; i++)
            if (states[i] && states[i].PlayerId.Value == playerId) return (byte)i;
        return playerId;
    }

    internal static void OnMeetingStart(MeetingHud hud)
    {
        if (!GameRules.NoSkip || !hud || !hud.SkipVoteButton) return;
        hud.SkipVoteButton.transform.localScale = Vector3.zero;
        // 結果の画面の「投票をスキップした」の見出しも (誰もスキップできないので)
        if (hud.SkippedVoting) hud.SkippedVoting.transform.localScale = Vector3.zero;
    }

    internal static void BeforeForceSkip(MeetingHud hud)
    {
        if (!GameRules.SelfVoteIfNone || !AmongUsClient.Instance.AmHost || !hud) return;
        int n = 0;
        var states = hud.playerStates;
        for (int i = 0; i < states.Length; i++)
        {
            var pva = states[i];
            if (!pva || pva.AmDead || pva.DidVote) continue;
            hud.CmdCastVote(pva.PlayerId.Value, (byte)i);
            n++;
        }
        if (n > 0) Plugin.Logger.LogInfo($"meeting: {n} player(s) without a vote voted for themselves");
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class MeetingVotesStartPatch
{
    public static void Postfix(MeetingHud __instance)
    {
        try { MeetingVotes.OnMeetingStart(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogError($"meeting votes: {e}"); }
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.ForceSkipAll))]
internal static class MeetingVotesForceSkipPatch
{
    public static void Prefix(MeetingHud __instance)
    {
        // ここで落ちると時間切れの処理ごと止まって会議が終わらない
        try { MeetingVotes.BeforeForceSkip(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogError($"meeting self vote: {e}"); }
    }
}
