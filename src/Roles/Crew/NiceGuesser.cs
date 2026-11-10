// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/NiceGuesser.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ナイスゲッサー (クルー): 会議中に人の役職を当てると、その人を撃ち抜ける。外すと自分が死ぬ
public sealed class NiceGuesser : RoleBase, IGuesser, IGuessRules
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("NiceGuesser.Name");
    public override string Reading => "ないすげっさー";
    public override Text Blurb => new("NiceGuesser.Blurb");
    public override string Origin => "TOR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("NiceGuesser.Shoot", "snr_guess") };
    public static readonly IntOpt Shots = new("NiceGuesser.Shots", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("NiceGuesser.PerMeeting", 3, 1, 15);
    public static readonly BoolOpt CannotShootFirstTurn = new("NiceGuesser.CannotShootFirstTurn", false);
    public static readonly BoolOpt CannotShootNoDead = new("NiceGuesser.CannotShootNoDead", false);
    public static readonly BoolOpt CannotShootCrewmate = new("NiceGuesser.CannotShootCrewmate", true);

    public bool NoCrewmate => CannotShootCrewmate;

    // 終わった会議の数 (最初の会議かの判定。全員の端末で数える)
    private int _turn;
    private void OnMeetingEnd(MeetingEndEvent e) => _turn++;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("NiceGuesser.Shoot"), "snr_guess")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => Shots,
            UsesPerMeeting = PerMeeting,
            SpendOnConfirm = true,
            CanTarget = (_, _) => !GuessKit.Barred(_turn, CannotShootFirstTurn, CannotShootNoDead),
            OnUsed = GuessKit.OnTarget,
        });
    }
}
