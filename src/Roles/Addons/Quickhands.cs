using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// 見本のアドオン (付く相手を絞る): キルの待ち時間が短くなる。キルできる人にしか付かない
public sealed class Quickhands : AddonBase
{
    public override string Color => "#FF7A59";
    public override Text Name => new("早業", "Quickhands");
    public override string Reading => "はやわざ";
    public override Text Blurb => new("次のキルまでの時間が短い", "Strike again sooner");

    public static readonly IntOpt Reduction = new("短くする割合", "Cooldown Reduction", 25, 5, 50, 5, "%");

    // インポスターか、キルボタンを使える役職の人だけ (本編の役職のままの人はインポスターの時だけ)
    public override bool CanAttach(Team team, RoleBase role) =>
        base.CanAttach(team, role) && (team == Team.Impostor || (role != null && role.CanKill));

    public override void ModifyKillCooldown(ref float seconds) => seconds *= 1f - Reduction.Value / 100f;
}
