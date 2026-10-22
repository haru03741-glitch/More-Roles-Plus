// Weighted votes sent as one result entry per vote based on https://github.com/All-Of-Us-Mods/MiraAPI
// (MeetingHudPatches.EndCheck, LGPL-2.1)
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Rules;

// 会議の票の重み (Assignable.ModifyVoteWeight) と書き換え (VoteTallyEvent)。重みも受け手も無い時は本編のまま。
// 書き換えた時は、全員が投票し終えた所 (CheckForEndVoting) をホストが代わりに締める: 書き換えた後の票で数えて
// 追放する人と同票を決め、票 1 つにつき結果の一覧の 1 行を作って全員へ配る (重み 2 の人は印が 2 つ出る)。
// 本編の結果を配る関数 (RpcVotingComplete) は締めの中に埋め込まれていて前置きが呼ばれないので、締めごと代わる。
// ジャッジが覆しを積んでいる会議だけは本編に締めさせ (覆しの演出は本編にしか無い)、数えた票の数だけ書き換える
internal static class VoteTally
{
    // 数える票か (未投票 252・投票し損ね 254・死んでいる 255 は数えない)
    private static bool Counted(byte target) => target < 252 || target == VoteTallyEvent.Skip;

    private const int MaxRows = 300;
    private static List<Vote> _before, _after;  // ジャッジの会議で本編に締めさせる時の書き換え (CalculateVotes で使う)
    internal static string Last = "-";          // 確かめ用: 最後に締めた時の様子

    internal static void OnMeetingStart()
    {
        _before = _after = null;
        Last = "-";
    }

    // 全員が投票し終えた時 (ホスト)。代わりに締めたら true
    internal static bool TryEnd(MeetingHud hud)
    {
        _before = _after = null;
        // 本編は話し合いの間でも全員が入れ終えたら締める。結果を出した後だけ締め直さない
        if (!AmongUsClient.Instance.AmHost || !hud || hud.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return false;
        var states = hud.playerStates;
        for (int i = 0; i < states.Length; i++)
            if (states[i] && !states[i].AmDead && !states[i].DidVote) return false; // 本編も締めない

        var before = new List<Vote>();
        for (int i = 0; i < states.Length; i++)
        {
            var pva = states[i];
            if (!pva || pva.AmDead) continue;
            byte t = pva.VotedForId.Value;
            if (Counted(t)) before.Add(new Vote(pva.PlayerId.Value, t));
        }
        var after = new List<Vote>(before);
        bool weighted = ApplyWeights(after);
        if (!weighted && !Events<VoteTallyEvent>.Any)
        {
            Last = $"vanilla votes={Join(before)}";
            return false;
        }
        if (Events<VoteTallyEvent>.Any)
        {
            Events<VoteTallyEvent>.Run(new VoteTallyEvent(after));
            // 受け手が足した票のうち、いない人の票・数えない先 (252 など)・死んだ人への票は捨てる
            after.RemoveAll(v => !Valid(v));
        }
        if (Same(before, after))
        {
            Last = $"unchanged votes={Join(before)}";
            return false;
        }
        Last = $"changed before={Join(before)} after={Join(after)}";

        if (hud.judgeOverrulesQueue != null && hud.judgeOverrulesQueue.Count > 0)
        {
            _before = before;
            _after = after;
            Last += " judge=vanilla";
            Plugin.Logger.LogInfo($"vote tally {Last}");
            return false;
        }

        // 一番多い 1 人を追放 (並んだら同票で誰も出ない・スキップが一番なら誰も出ない)
        var counts = new Dictionary<byte, int>();
        foreach (var v in after) counts[v.Target] = (counts.TryGetValue(v.Target, out int n) ? n : 0) + 1;
        byte top = 255;
        int best = int.MinValue;
        bool tie = true;
        foreach (var (key, n) in counts)
        {
            if (n > best) { top = key; best = n; tie = false; }
            else if (n == best) tie = true;
        }
        NetworkedPlayerInfo exiled = null;
        if (!tie && top < 252)
        {
            var info = GameData.Instance.GetPlayerById(top);
            if (info != null && !info.IsDead) exiled = info;
        }

        // 結果の一覧: 数える票を入れなかった人 (死んでいる・未投票) は本編と同じ 1 行、票は 1 つ 1 行
        var voters = new HashSet<byte>();
        foreach (var v in before) voters.Add(v.Voter);
        var rows = new List<MeetingHud.VoterState>(states.Length + after.Count);
        for (int i = 0; i < states.Length; i++)
        {
            var pva = states[i];
            if (!pva) continue;
            byte id = pva.PlayerId.Value;
            if (voters.Contains(id)) continue;
            rows.Add(new MeetingHud.VoterState { VoterId = id, VotedForId = pva.VotedForId.Value });
        }
        foreach (var v in after) rows.Add(new MeetingHud.VoterState { VoterId = v.Voter, VotedForId = v.Target });
        // 公式サーバーは 1 通が約 1000 バイトを超えると切る (1 行 2 バイト)。多すぎる時は同じ人の同じ先への行を 1 行にまとめる
        // (追放と同票は数え直した票で決めたまま・結果の画面の印の数だけ減る)
        if (rows.Count > MaxRows)
        {
            var seen = new HashSet<int>();
            rows.RemoveAll(r => !seen.Add(r.VoterId << 8 | r.VotedForId));
        }
        var sent = new Il2CppStructArray<MeetingHud.VoterState>((long)rows.Count);
        for (int i = 0; i < rows.Count; i++) sent[i] = rows[i];

        Last += $" exiled={(exiled != null ? exiled.PlayerId : 255)} tie={tie} rows={rows.Count}";
        Plugin.Logger.LogInfo($"vote tally {Last}");
        hud.RpcVotingComplete(sent, exiled, tie, false, 0);
        return true;
    }

    // 本編が票を数えた直後 (ジャッジの会議で本編に締めさせた時だけ・ホスト)
    internal static void AfterCalculate(Il2CppSystem.Collections.Generic.Dictionary<byte, int> counts)
    {
        var before = _before;
        var after = _after;
        _before = _after = null;
        if (before == null || counts == null) return;
        foreach (var v in before) Add(counts, v.Target, -1);
        foreach (var v in after) Add(counts, v.Target, 1);
    }

    // 役職とアドオンの重みで同じ票を並べる。どれかの重みが 1 でなければ true
    private static bool ApplyWeights(List<Vote> votes)
    {
        if (!RoleState.Any) return false;
        bool changed = false;
        for (int i = votes.Count - 1; i >= 0; i--)
        {
            var v = votes[i];
            int w = 1;
            RoleState.Of(v.Voter)?.ModifyVoteWeight(ref w);
            var addons = RoleState.AddonsOf(v.Voter);
            for (int k = 0; k < addons.Count; k++) addons[k].ModifyVoteWeight(ref w);
            if (w == 1) continue;
            changed = true;
            w = System.Math.Clamp(w, 0, 15);
            votes.RemoveAt(i);
            for (int k = 0; k < w; k++) votes.Insert(i, v);
        }
        return changed;
    }

    private static void Add(Il2CppSystem.Collections.Generic.Dictionary<byte, int> counts, byte key, int delta)
    {
        if (!Counted(key)) return;
        int n = (counts.ContainsKey(key) ? counts[key] : 0) + delta;
        if (n > 0) counts[key] = n;
        else counts.Remove(key);
    }

    private static bool Valid(Vote v)
    {
        if (!Counted(v.Target)) return false;
        var voter = GameData.Instance.GetPlayerById(v.Voter);
        if (voter == null || voter.Disconnected) return false;
        if (v.Target == VoteTallyEvent.Skip) return true;
        var target = GameData.Instance.GetPlayerById(v.Target);
        return target != null && !target.IsDead && !target.Disconnected;
    }

    private static bool Same(List<Vote> a, List<Vote> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Voter != b[i].Voter || a[i].Target != b[i].Target) return false;
        return true;
    }

    private static string Join(List<Vote> votes)
    {
        if (votes.Count == 0) return "none";
        var sb = new StringBuilder();
        foreach (var v in votes) sb.Append(sb.Length == 0 ? "" : ",").Append(v);
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
internal static class VoteTallyEndPatch
{
    public static bool Prefix(MeetingHud __instance)
    {
        try { return !VoteTally.TryEnd(__instance); }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"vote tally: {e}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CalculateVotes))]
internal static class VoteTallyCalculatePatch
{
    public static void Postfix(Il2CppSystem.Collections.Generic.Dictionary<byte, int> __result)
    {
        try { VoteTally.AfterCalculate(__result); }
        catch (System.Exception e) { Plugin.Logger.LogError($"vote tally: {e}"); }
    }
}
