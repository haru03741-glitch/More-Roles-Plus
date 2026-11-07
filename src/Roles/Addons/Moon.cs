// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Moon.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Town Of Host K (TOHK)
namespace MoreRolesPlus.Roles.Addons;

// ムーン (アドオン): 停電で視界が狭まらない
public sealed class Moon : AddonBase
{
    public override string Color => "#FFFF14";
    public override Text Name => new("Moon.Name");
    public override string Reading => "むーん";
    public override Text Blurb => new("Moon.Blurb");
    public override string Origin => "Uchu, TOHK";

    public override void ModifyVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !PortKit.LightsOut()) return;
        bool imp = Player && Player.Data != null && Player.Data.Role && Player.Data.Role.IsImpostor;
        var o = GameOptionsManager.Instance.CurrentGameOptions;
        float full = ship.MaxLightRadius * o.GetFloat(imp ? AmongUs.GameOptions.FloatOptionNames.ImpostorLightMod : AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
        if (radius < full) radius = full;
    }
}
