using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// 見本の役職 (インポスター): キルの待ち時間が短い
public sealed class Quickdraw : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#E04040";
    public override Text Name => new("Quickdraw.Name");
    public override string Reading => "はやうち";
    public override Text Blurb => new("Quickdraw.Blurb");

    public static readonly FloatOpt KillCooldown = new("Quickdraw.KillCooldown", 15f, 2.5f, 60f, 2.5f, "s");

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;
}
