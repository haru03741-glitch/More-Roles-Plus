namespace MoreRolesPlus.Roles.Neutral;

// 見本の役職 (第三陣営・相乗り): 誰が勝っても、最後まで生きていれば一緒に勝つ
public sealed class Survivor : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#FFE64D";
    public override Text Name => new("生存者", "Survivor");
    public override Text Blurb => new("最後まで生き延びよう", "Stay alive until the end");

    public override bool AlsoWins(GameResult result) => Player && !Player.Data.IsDead;
}
