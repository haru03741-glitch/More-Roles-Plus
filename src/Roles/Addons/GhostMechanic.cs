// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Ghost/GhostMechanic.cs (GPL-3.0)
// SuperNewRoles/Roles/Ability/RepairSabotageAbility.cs
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// ゴーストメカニック (幽霊役職・クルー側): 死んだクルーに付く。「修理」で起きているサボタージュを全部すぐに直す
public sealed class GhostMechanic : AddonBase
{
    public override string Color => "#8A8E99";
    public override Text Name => new("GhostMechanic.Name");
    public override string Reading => "ごーすとめかにっく";
    public override Text Blurb => new("GhostMechanic.Blurb");
    public override string Origin => "SNR";
    public override bool Ghost => true;

    public override bool CanAttach(Team team, RoleBase role) => team != Team.Impostor;

    public static readonly FloatOpt RepairCooldown = new("GhostMechanic.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt CannotFixMixup = new("GhostMechanic.CannotFixMixup", true);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("GhostMechanic.Repair"), "snr_repair")
        {
            Ghost = true,
            Cooldown = () => RepairCooldown,
            OnUse = () =>
            {
                if (!SnrKit.SabotageActive(!CannotFixMixup)) return false;
                SnrKit.FixSabotage(CannotFixMixup);
                return true;
            },
        });
    }
}
