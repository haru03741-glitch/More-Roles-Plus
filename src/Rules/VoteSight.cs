using HarmonyLib;

namespace MoreRolesPlus.Rules;

// 匿名投票でも、誰が誰に入れたかが見える人 (アドミラルの設定・ウォッチャー)。
// 本編は結果を並べる時 (PopulateResults) に「匿名投票か」を見て灰色にするので、その間だけ匿名でないと答える
internal static class VoteSight
{
    internal static bool InResults;

    internal static bool Sees(PlayerControl p) =>
        p && (Roles.Crew.Admiral.SeesVotes(p) || Roles.Addons.Watcher.Has(p));
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.PopulateResults))]
internal static class VoteSightResultsPatch
{
    public static void Prefix() => VoteSight.InResults = true;
    public static void Postfix() => VoteSight.InResults = false;
}

[HarmonyPatch(typeof(LogicOptionsNormal), nameof(LogicOptionsNormal.GetAnonymousVotes))]
internal static class VoteSightAnonymousPatch
{
    public static void Postfix(ref bool __result)
    {
        if (__result && VoteSight.InResults && VoteSight.Sees(PlayerControl.LocalPlayer)) __result = false;
    }
}
