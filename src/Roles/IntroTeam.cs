using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 始まりの「クルーメイト / インポスター」の画面を、第三陣営とマッドメイトの人には専用の物にする。
// 並ぶのは自分だけ (仲間は見せない)・題と帯の色と下の一言を陣営の物に
internal static class IntroTeam
{
    private enum Kind { None, Neutral, Madmate }

    private static readonly Color MadColor = new(1f, 0.1f, 0.1f, 1f);

    private static Kind Local(out RoleBase role)
    {
        role = RoleState.Local;
        if (role == null || role.IsVanilla || role.HiddenWhileAlive) return Kind.None;
        if (role.Team == Team.Neutral) return Kind.Neutral;
        if (role.Team == Team.Madmate || role.WinsWithTeam == Team.Impostor && role.Team != Team.Impostor) return Kind.Madmate;
        return Kind.None;
    }

    // BeginCrewmate / BeginImpostor の前 (並べる人はこの中で作られる): 自分だけにする。
    // 渡された一覧をその場で書き換える (引数の差し替えは本編へ届かない)。ShowTeam の時点ではまだ役職が届いていないことがある
    internal static void FilterTeam(Il2CppSystem.Collections.Generic.List<PlayerControl> team)
    {
        if (team == null || Local(out _) == Kind.None) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        team.Clear();
        team.Add(lp);
    }

    // BeginCrewmate / BeginImpostor の後: 題・色・一言を差し替える
    internal static void Apply(IntroCutscene intro)
    {
        var kind = Local(out var role);
        if (kind == Kind.None || !intro) return;
        Color color = kind == Kind.Madmate ? MadColor : RoleDisplay.ParseColor(role.Color);
        intro.TeamTitle.text = new Text(kind == Kind.Madmate ? "IntroTeam.Madmate" : "IntroTeam.Neutral").ToString();
        intro.TeamTitle.color = color;
        if (intro.BackgroundBar) intro.BackgroundBar.material.SetColor("_Color", color);
        if (intro.ImpostorText)
        {
            intro.ImpostorText.gameObject.SetActive(true);
            intro.ImpostorText.text = new Text(kind == Kind.Madmate ? "IntroTeam.MadmateSub" : "IntroTeam.NeutralSub").ToString();
        }
    }
}

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.BeginCrewmate))]
internal static class IntroBeginCrewmatePatch
{
    public static void Prefix(Il2CppSystem.Collections.Generic.List<PlayerControl> teamToDisplay) => IntroTeam.FilterTeam(teamToDisplay);

    public static void Postfix(IntroCutscene __instance) => IntroTeam.Apply(__instance);
}

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.BeginImpostor))]
internal static class IntroBeginImpostorPatch
{
    public static void Prefix(Il2CppSystem.Collections.Generic.List<PlayerControl> yourTeam) => IntroTeam.FilterTeam(yourTeam);

    public static void Postfix(IntroCutscene __instance) => IntroTeam.Apply(__instance);
}
