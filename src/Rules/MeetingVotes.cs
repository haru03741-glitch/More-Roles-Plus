// Based on the skip-vote and no-vote-is-self-vote meeting options in https://github.com/TheOtherRolesAU/TheOtherRoles
// and https://github.com/AU-Avengers/TOU-Mira (GPL-3.0)
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// 会議の票のルール (GameRules の行)。
// - スキップできない: スキップの札を全員の画面で 0 倍にする (当たり判定も 0 になるので押せない)。
//   時間切れで投票しなかった人は本編どおりスキップになる (自分に入れた扱いと組み合わせるとスキップは 0 になる)
// - 投票しなかった人は自分に入れた扱い: 時間切れで本編が未投票の人をスキップにする直前に、ホストが生きている未投票の人の
//   票を本人へ入れる。本編の時間切れの処理は Update の中に埋め込まれていて ForceSkipAll のパッチは呼ばれないので、
//   会議中だけ動く Update の前置きで「このフレームで締め切りを越える」時に 1 回だけ入れる。
//   票の関数は PlayerId (構造体) を引数に取るのでそこには当てず、ホストがダミーの票に使っている CmdCastVote(byte, byte) を使う
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

    private static bool _selfVoted; // この会議で入れたか

    internal static void OnMeetingStart(MeetingHud hud)
    {
        _selfVoted = false;
        if (!GameRules.NoSkip || !hud || !hud.SkipVoteButton) return;
        hud.SkipVoteButton.transform.localScale = Vector3.zero;
        // 結果の画面の「投票をスキップした」の見出しも (誰もスキップできないので)
        if (hud.SkippedVoting) hud.SkippedVoting.transform.localScale = Vector3.zero;
    }

    // MeetingHud.Update の前 (会議中の毎フレーム)。締め切りを越えるフレームだけ動く
    internal static void BeforeUpdate(MeetingHud hud)
    {
        if (_selfVoted || !GameRules.SelfVoteIfNone || !AmongUsClient.Instance.AmHost) return;
        if (hud.state is not (MeetingHud.MeetingStates.NotVoted or MeetingHud.MeetingStates.Voted)) return;
        var opts = GameOptionsManager.Instance.CurrentGameOptions;
        int voting = opts.GetInt(AmongUs.GameOptions.Int32OptionNames.VotingTime);
        if (voting <= 0) return; // 投票の時間が無制限
        float limit = opts.GetInt(AmongUs.GameOptions.Int32OptionNames.DiscussionTime) + voting;
        if (hud.discussionTimer + Time.deltaTime < limit) return;
        BeforeForceSkip(hud);
    }

    internal static void BeforeForceSkip(MeetingHud hud)
    {
        if (_selfVoted || !GameRules.SelfVoteIfNone || !AmongUsClient.Instance.AmHost || !hud) return;
        _selfVoted = true;
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

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
internal static class MeetingVotesUpdatePatch
{
    public static void Prefix(MeetingHud __instance)
    {
        try { MeetingVotes.BeforeUpdate(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogError($"meeting self vote: {e}"); }
    }
}

// 時間切れを直接起こした時 (dev の votetimeout など)
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
