// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Lighter.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ライター (クルー): 「点灯」でしばらく視界が広がる。停電でも狭まらない。
// MRP には別のライター (Lighter) があるので、型の名前は SnrLighter
public sealed class SnrLighter : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#EEE5BE";
    public override Text Name => new("SnrLighter.Name");
    public override string Reading => "らいたー";
    public override Text Blurb => new("SnrLighter.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("SnrLighter.Light", "snr_light") };

    public static readonly FloatOpt LightCooldown = new("SnrLighter.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt LightDuration = new("SnrLighter.Duration", 10f, 2.5f, 20f, 2.5f, "s");
    public static readonly FloatOpt Radius = new("SnrLighter.Radius", 2.5f, 0.1f, 10f, 0.1f, "x");

    private bool _lit;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("SnrLighter.Light"), "snr_light")
        {
            Cooldown = () => LightCooldown,
            Duration = () => LightDuration,
            OnUse = () => _lit = true,
            OnEnd = () => _lit = false,
        });
        Lifespan.OnRelease(() => _lit = false);
    }

    public override void ModifyVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (_lit && ship) radius = ship.MaxLightRadius * Radius;
    }
}
