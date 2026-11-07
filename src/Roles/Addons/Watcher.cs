// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Watcher.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukinogatari/TheOtherRoles-GM (GPL-3.0)
using System.Linq;

namespace MoreRolesPlus.Roles.Addons;

// ウォッチャー (アドオン): 匿名投票でも、誰が誰に入れたかが見える
public sealed class Watcher : AddonBase
{
    public override string Color => "#5726A6";
    public override Text Name => new("Watcher.Name");
    public override string Reading => "うぉっちゃー";
    public override Text Blurb => new("Watcher.Blurb");
    public override string Origin => "Uchu, TORGM";

    internal static bool Has(PlayerControl p) => p && RoleState.AddonsOf(p.PlayerId).Any(a => a is Watcher);
}
