using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// 見本のアドオン (値を変える問い合わせ): 視界が広くなる
public sealed class Hawkeye : AddonBase
{
    public override string Color => "#E8B84A";
    public override Text Name => new("Hawkeye.Name");
    public override string Reading => "たかのめ";
    public override Text Blurb => new("Hawkeye.Blurb");

    public static readonly IntOpt VisionPercent = new("Hawkeye.VisionPercent", 125, 110, 200, 5, "%");

    public override void ModifyVision(ref float radius) => radius *= VisionPercent.Value / 100f;
}
