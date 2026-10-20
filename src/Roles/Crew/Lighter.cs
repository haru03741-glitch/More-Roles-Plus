using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 見本の役職 (クルー): 視界が広い
public sealed class Lighter : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#EEE5BE";
    public override Text Name => new("Lighter.Name");
    public override Text Blurb => new("Lighter.Blurb");

    public static readonly FloatOpt VisionMultiplier = new("Lighter.VisionMultiplier", 1.5f, 1f, 3f, 0.25f, "x");

    public override void ModifyVision(ref float radius) => radius *= VisionMultiplier;
}
