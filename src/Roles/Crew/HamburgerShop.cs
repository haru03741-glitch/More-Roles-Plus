// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/HamburgerShop.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// ハンバーガー屋 (クルー): 能力の無いクルー (元の mod ではタスクがハンバーガー作りに替わる。MRP ではタスクは替えない)
public sealed class HamburgerShop : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#D2691E";
    public override Text Name => new("HamburgerShop.Name");
    public override string Reading => "はんばーがーや";
    public override Text Blurb => new("HamburgerShop.Blurb");
    public override string Origin => "SNR";

}
