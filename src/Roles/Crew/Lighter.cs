// Ported from https://github.com/TheOtherRolesAU/TheOtherRoles TheOtherRoles/Patches/ShipStatusPatch.cs, CustomOptionHolder.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ライター (クルー): 視界が広い。停電でも船の明るさの「停電中の視界」倍までしか狭まらない
// (TOR と同じく、普通のクルーの視界の明るさの割合で、停電中の視界〜点灯中の視界の間を取る)
public sealed class Lighter : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#EEE5BE";
    public override Text Name => new("Lighter.Name");
    public override Text Blurb => new("Lighter.Blurb");
    public override string Origin => "TOR";

    public static readonly FloatOpt LightsOnVision = new("Lighter.LightsOnVision", 1.5f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt LightsOffVision = new("Lighter.LightsOffVision", 0.5f, 0.25f, 5f, 0.25f, "x");

    public override void ModifyVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        // 本編のクルーの視界 = Lerp(最小, 最大, 明るさ) × クルーの視界の設定。そこから明るさの割合を取り出す
        float mod = GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
        float neutral = mod > 0f ? radius / mod : radius;
        float lit = Mathf.InverseLerp(ship.MinLightRadius, ship.MaxLightRadius, neutral);
        radius = Mathf.Lerp(ship.MaxLightRadius * LightsOffVision, ship.MaxLightRadius * LightsOnVision, lit);
    }
}
