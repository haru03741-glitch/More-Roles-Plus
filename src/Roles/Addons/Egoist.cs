// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Egoist.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Town Of Us Mira (TOU-Mira)
namespace MoreRolesPlus.Roles.Addons;

// エゴイスト (アドオン・クルーだけ): クルーが負ければ勝ち、クルーが勝つと負ける。タスクはクルーの勝ちに数えない
public sealed class Egoist : AddonBase
{
    public override string Color => "#AA20C9";
    public override Text Name => new("Egoist.Name");
    public override string Reading => "えごいすと";
    public override Text Blurb => new("Egoist.Blurb");
    public override string Origin => "Uchu, TOUMira";

    protected internal override bool DefaultOnImpostor => false;
    protected internal override bool DefaultOnMadmate => false;
    protected internal override bool DefaultOnNeutral => false;

    // クルーにだけ付く
    public override bool CanAttach(Team team, RoleBase role) => team == Team.Crew && base.CanAttach(team, role);

    public override bool AffectsWin => true;
    public override bool TasksCount => false;

    private static bool CrewWon(GameResult r) => r.Team == Team.Crew;

    public override bool AlsoWins(GameResult r) => !r.Draw && !CrewWon(r);
    public override bool LosesWin(GameResult r) => CrewWon(r);
}
