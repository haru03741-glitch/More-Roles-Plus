// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/SpeedBooster.cs, Roles/Ability/SpeedBoosterAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// スピードブースター (クルー): 「ブースト！」でしばらく速く歩ける
public sealed class SpeedBooster : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#8CFFFF";
    public override Text Name => new("SpeedBooster.Name");
    public override string Reading => "すぴーどぶーすたー";
    public override Text Blurb => new("SpeedBooster.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("SpeedBooster.Boost", "snr_boost") };

    public static readonly FloatOpt BoostCooldown = new("SpeedBooster.Cooldown", 30f, 0f, 180f, 2.5f, "s");
    public static readonly FloatOpt BoostDuration = new("SpeedBooster.Duration", 5f, 0.5f, 30f, 0.5f, "s");
    public static readonly FloatOpt Multiplier = new("SpeedBooster.Multiplier", 2f, 1f, 5f, 0.1f, "x");

    private bool _active;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("SpeedBooster.Boost"), "snr_boost")
        {
            Cooldown = () => BoostCooldown,
            Duration = () => BoostDuration,
            OnUse = () => _active = true,
            OnEnd = () => _active = false,
        });
        Lifespan.OnRelease(() => _active = false);
    }

    public override void ModifySpeed(ref float speed)
    {
        if (_active) speed *= Multiplier;
    }
}
