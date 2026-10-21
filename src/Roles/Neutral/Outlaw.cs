namespace MoreRolesPlus.Roles.Neutral;

// 見本の役職 (第三陣営・キル役): クルーの姿のままキルボタンを持ち、最後の一人になれば勝ち。
// キルのクールダウン・視界・キルの距離は第三陣営のキル役の共通設定 (NeutralKillSettings)
public sealed class Outlaw : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#8C6239";
    public override Text Name => new("Outlaw.Name");
    public override string Reading => "むほうもの";
    public override Text Blurb => new("Outlaw.Blurb");
    public override bool IsKiller => true;
}
