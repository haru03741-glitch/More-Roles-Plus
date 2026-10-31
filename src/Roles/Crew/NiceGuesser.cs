// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/NiceGuesser.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ナイスゲッサー (クルー): 会議中に人の役職を当てると、その人を撃ち抜ける。外すと自分が死ぬ
public sealed class NiceGuesser : RoleBase, IGuesser
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("NiceGuesser.Name");
    public override string Reading => "ないすげっさー";
    public override Text Blurb => new("NiceGuesser.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("NiceGuesser.Shoot", "snr_guess") };
    public static readonly IntOpt Shots = new("NiceGuesser.Shots", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("NiceGuesser.PerMeeting", 1, 1, 15);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("NiceGuesser.Shoot"), "snr_guess")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => Shots,
            UsesPerMeeting = PerMeeting,
            OnUsed = GuessKit.OnTarget,
        });
    }
}
