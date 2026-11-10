// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilGuesser.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// イビルゲッサー (インポスター): 会議中に人の役職を当てると、その人を撃ち抜ける。外すと自分が死ぬ
public sealed class EvilGuesser : RoleBase, IGuesser, IGuessRules
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilGuesser.Name");
    public override string Reading => "いびるげっさー";
    public override Text Blurb => new("EvilGuesser.Blurb");
    public override string Origin => "TOR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilGuesser.Shoot", "snr_guess") };
    public static readonly IntOpt Shots = new("EvilGuesser.Shots", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("EvilGuesser.PerMeeting", 3, 1, 15);
    public static readonly BoolOpt CannotShootFirstTurn = new("EvilGuesser.CannotShootFirstTurn", false);
    public static readonly BoolOpt CannotShootNoDead = new("EvilGuesser.CannotShootNoDead", false);
    public static readonly BoolOpt CannotShootCrewmate = new("EvilGuesser.CannotShootCrewmate", true);

    public bool NoCrewmate => CannotShootCrewmate;

    // 終わった会議の数 (最初の会議かの判定。全員の端末で数える)
    private int _turn;
    private void OnMeetingEnd(MeetingEndEvent e) => _turn++;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilGuesser.Shoot"), "snr_guess")
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
