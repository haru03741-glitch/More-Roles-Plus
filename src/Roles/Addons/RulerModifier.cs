// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/RulerModifier.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// ルーラー (アドオン・インポスターだけ): 1 度だけ「狂わせる」で近くの人をマッドメイトにする
public sealed class RulerModifier : AddonBase
{
    public override string Color => "#FF1919";
    public override Text Name => new("RulerModifier.Name");
    public override string Reading => "るーらー";
    public override Text Blurb => new("RulerModifier.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt MadCooldown = new("RulerModifier.Cooldown", 30f, 0f, 180f, 2.5f, "s");

    public override bool CanAttach(Team team, RoleBase role) => team == Team.Impostor;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("RulerModifier.Madden"), "snr_madden")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => MadCooldown,
            MaxUses = () => 1,
            CanTarget = (_, t) => !PortKit.IsImpostor(t),
            OnUsed = (_, target) =>
            {
                if (AmongUsClient.Instance.AmHost) RoleState.Change(target, RoleState.Proto<Crew.Madmate>());
            },
        });
    }
}
