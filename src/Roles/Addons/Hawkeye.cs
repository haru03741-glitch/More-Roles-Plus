using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// 見本のアドオン (値を変える問い合わせ): 視界が広くなる
public sealed class Hawkeye : AddonBase
{
    public override string Color => "#E8B84A";
    public override Text Name => new("鷹の目", "Hawkeye");
    public override string Reading => "たかのめ";
    public override Text Blurb => new("遠くまで見通せる", "See further than the rest");

    public static readonly IntOpt VisionPercent = new("視界の広さ", "Vision", 125, 110, 200, 5, "%");

    public override void ModifyVision(ref float radius) => radius *= VisionPercent.Value / 100f;
}
