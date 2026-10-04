using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 見本の役職 (クルー): 視界が広い
public sealed class Lighter : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#EEE5BE";
    public override Text Name => new("ライター", "Lighter");
    public override Text Blurb => new("広い視界で船を見張ろう", "See further than the rest");

    public static readonly FloatOpt VisionMultiplier = new("視界の倍率", "Vision Multiplier", 1.5f, 1f, 3f, 0.25f, "x");

    public override void ModifyVision(ref float radius) => radius *= VisionMultiplier;
}
