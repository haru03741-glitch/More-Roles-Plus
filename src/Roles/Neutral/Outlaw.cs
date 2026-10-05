using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// 見本の役職 (第三陣営・キル役): クルーの姿のままキルボタンを持ち、最後の一人になれば勝ち
public sealed class Outlaw : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#8C6239";
    public override Text Name => new("無法者", "Outlaw");
    public override Text Blurb => new("全員を倒して最後に残れ", "Be the last one standing");
    public override bool IsKiller => true;

    public static readonly FloatOpt KillCooldown = new("キルの待ち時間", "Kill Cooldown", 25f, 2.5f, 60f, 2.5f, "s");

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;
}
