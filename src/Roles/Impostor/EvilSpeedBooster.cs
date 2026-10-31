// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilSpeedBooster.cs, Roles/Ability/SpeedBoosterAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// イビルスピードブースター (インポスター): 「ブースト！」でしばらく速く歩ける
public sealed class EvilSpeedBooster : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilSpeedBooster.Name");
    public override string Reading => "いびるすぴーどぶーすたー";
    public override Text Blurb => new("EvilSpeedBooster.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilSpeedBooster.Boost", "snr_boost") };

    public static readonly FloatOpt BoostCooldown = new("EvilSpeedBooster.Cooldown", 30f, 0f, 180f, 2.5f, "s");
    public static readonly FloatOpt BoostDuration = new("EvilSpeedBooster.Duration", 5f, 0.5f, 30f, 0.5f, "s");
    public static readonly FloatOpt Multiplier = new("EvilSpeedBooster.Multiplier", 2f, 1f, 5f, 0.1f, "x");

    private bool _active;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilSpeedBooster.Boost"), "snr_boost")
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
