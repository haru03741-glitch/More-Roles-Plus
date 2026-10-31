// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/MeetingSheriff.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ミーティングシェリフ (クルー): 会議中に名札のボタンで人を撃つ。倒してよい陣営でなければ自分が死ぬ。
// 会議中なので死体は残らない
public sealed class MeetingSheriff : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("MeetingSheriff.Name");
    public override string Reading => "みーてぃんぐしぇりふ";
    public override Text Blurb => new("MeetingSheriff.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("MeetingSheriff.Shoot", "snr_execute") };

    public static readonly IntOpt MaxKills = new("MeetingSheriff.MaxKills", 1, 1, 10);
    public static readonly IntOpt ShotsPerMeeting = new("MeetingSheriff.ShotsPerMeeting", 1, 1, 5);
    public static readonly ChoiceOpt SuicideMode = new("MeetingSheriff.SuicideMode", 0,
        new Text("Sheriff.SuicideMode.Default"), new Text("Sheriff.SuicideMode.AlwaysSuicide"), new Text("Sheriff.SuicideMode.AlwaysKill"));
    public static readonly BoolOpt KillImpostor = new("MeetingSheriff.KillImpostor", true);
    public static readonly BoolOpt KillMad = new("MeetingSheriff.KillMad", true);
    public static readonly BoolOpt KillNeutral = new("MeetingSheriff.KillNeutral", true);
    public static readonly BoolOpt KillFriends = new("MeetingSheriff.KillFriends", true);
    public static readonly BoolOpt KillLovers = new("MeetingSheriff.KillLovers", true);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("MeetingSheriff.Shoot"), "snr_execute")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => MaxKills,
            UsesPerMeeting = ShotsPerMeeting,
            CanTarget = (u, t) => t.PlayerId != u.PlayerId,
            OnUsed = (user, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                bool ok = Judge(target);
                int mode = SuicideMode.Value;
                if (ok || mode == 2) PortKit.HostVanish(user, target);
                if (mode == 1 || (mode == 0 && !ok)) PortKit.HostVanish(user, user);
            },
        });
    }

    private static bool Judge(PlayerControl target)
    {
        if (PortKit.LooksImpostor(target)) return KillImpostor;
        if (PortKit.IsNeutral(target)) return KillNeutral;
        if (PortKit.IsMad(target)) return KillMad;
        if (PortKit.IsFriend(target)) return KillFriends;
        if (PortKit.IsLover(target)) return KillLovers;
        return false;
    }
}
