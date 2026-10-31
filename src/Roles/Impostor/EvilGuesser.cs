// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilGuesser.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// イビルゲッサー (インポスター): 会議中に人の役職を当てると、その人を撃ち抜ける。外すと自分が死ぬ
public sealed class EvilGuesser : RoleBase, IGuesser
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilGuesser.Name");
    public override string Reading => "いびるげっさー";
    public override Text Blurb => new("EvilGuesser.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilGuesser.Shoot", "snr_guess") };
    public static readonly IntOpt Shots = new("EvilGuesser.Shots", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("EvilGuesser.PerMeeting", 1, 1, 15);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilGuesser.Shoot"), "snr_guess")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => Shots,
            UsesPerMeeting = PerMeeting,
            OnUsed = GuessKit.OnTarget,
        });
    }
}
