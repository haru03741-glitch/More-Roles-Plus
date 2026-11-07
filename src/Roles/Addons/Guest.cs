// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Guest.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Linq;

namespace MoreRolesPlus.Roles.Addons;

// ゲスト (アドオン): 緊急ボタンを押せない
public sealed class Guest : AddonBase
{
    public override string Color => "#42CF90";
    public override Text Name => new("Guest.Name");
    public override string Reading => "げすと";
    public override Text Blurb => new("Guest.Blurb");
    public override string Origin => "Uchu";

    internal static bool Has(PlayerControl p) => p && RoleState.AddonsOf(p.PlayerId).Any(a => a is Guest);
}
