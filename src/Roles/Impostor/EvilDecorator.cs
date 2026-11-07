// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/EvilDecorator.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Crew;

namespace MoreRolesPlus.Roles.Impostor;

// イビルデコレーター (インポスター): ナイスデコレーターと同じ「装飾」を持つインポスター
public sealed class EvilDecorator : DecoratorBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilDecorator.Name");
    public override string Reading => "いびるでこれーたー";
    public override Text Blurb => new("EvilDecorator.Blurb");
    public override string Origin => "Uchu";

    public static readonly FloatOpt Cooldown = new("EvilDecorator.Cooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt Uses = new("EvilDecorator.Uses", 5, 1, 99);

    protected override float DecorateCooldown => Cooldown;
    protected override int DecorateUses => Uses;
}
