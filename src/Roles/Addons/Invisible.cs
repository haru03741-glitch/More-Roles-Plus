// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Invisible.cs (role idea and logic; UchuAddon allows porting them, no assets used)
namespace MoreRolesPlus.Roles.Addons;

// インビジブル (アドオン): いつもインポスター以外から見えない。本人は付いていることに気付かない (死ぬと分かる)
public sealed class Invisible : AddonBase
{
    public override string Color => "#7DF2FF";
    public override Text Name => new("Invisible.Name");
    public override string Reading => "いんびじぶる";
    public override Text Blurb => new("Invisible.Blurb");
    public override string Origin => "Uchu";

    public override bool HiddenFromSelf => true;

    public override void OnAssigned() => UchuKit.Hidden.Set(PlayerId, () => Player && Player.Data != null && !Player.Data.IsDead, () => false); // 本人には普通に見える (気付かない)
}
