// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/ElectionCommissioner.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// 選挙管理委員 (クルー): 匿名投票の設定でも、自分の画面では誰が誰に入れたかが見える
public sealed class ElectionCommissioner : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#E8C547";
    public override Text Name => new("ElectionCommissioner.Name");
    public override string Reading => "せんきょかんりいいん";
    public override Text Blurb => new("ElectionCommissioner.Blurb");
    public override string Origin => "SNR";
}

// 匿名投票かを本編が聞く所。自分が選挙管理委員なら、自分の画面でだけ匿名でないと答える
[HarmonyLib.HarmonyPatch(typeof(LogicOptionsNormal), nameof(LogicOptionsNormal.GetAnonymousVotes))]
internal static class ElectionCommissionerVotesPatch
{
    public static void Postfix(ref bool __result)
    {
        if (__result && (RoleState.Local is ElectionCommissioner || RoleState.Local is Neutral.God && Neutral.God.SeeVote)) __result = false;
    }
}
