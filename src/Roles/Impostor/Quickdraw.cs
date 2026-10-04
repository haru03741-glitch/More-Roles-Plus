using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// 見本の役職 (インポスター): キルの待ち時間が短い
public sealed class Quickdraw : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#E04040";
    public override Text Name => new("早撃ち", "Quickdraw");
    public override Text Blurb => new("誰よりも早く次を仕留めろ", "Strike again before anyone else");

    public static readonly FloatOpt KillCooldown = new("キルの待ち時間", "Kill Cooldown", 15f, 2.5f, 60f, 2.5f, "s");

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;
}
