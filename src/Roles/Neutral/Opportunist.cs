// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Opportunist.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Neutral;

// オポチュニスト (第三陣営): 最後まで生きていれば、誰が勝っても一緒に勝つ
public sealed class Opportunist : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#7ED957";
    public override Text Name => new("Opportunist.Name");
    public override string Reading => "おぽちゅにすと";
    public override Text Blurb => new("Opportunist.Blurb");
    public override string Origin => "SNR";

    public override bool AlsoWins(GameResult result) => PortKit.Alive(Player);
}
