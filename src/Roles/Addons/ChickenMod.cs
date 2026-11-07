// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Chicken.cs (the modifier; role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/ykundesu/SuperNewRoles (GPL-3.0)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Crew;

namespace MoreRolesPlus.Roles.Addons;

// チキン (アドオン): 役職のチキンと同じ死に方をする。勝ちの上書きは無い
public sealed class ChickenMod : AddonBase
{
    public override string Color => "#FFEE00";
    public override Text Name => new("ChickenMod.Name");
    public override string Reading => "ちきん";
    public override Text Blurb => new("ChickenMod.Blurb");
    public override string Origin => "Uchu, SNR";

    public static readonly IntOpt VentChance = new("ChickenMod.VentChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt LadderChance = new("ChickenMod.LadderChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt ZiplineChance = new("ChickenMod.ZiplineChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt PlatformChance = new("ChickenMod.PlatformChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt DoorChance = new("ChickenMod.DoorChance", 20, 0, 100, 5, "%");
    public static readonly FloatOpt BlackoutTime = new("ChickenMod.BlackoutTime", 40f, 5f, 120f, 5f, "s");

    private readonly ChickenFear _fear = new();

    [LocalOnly]
    public override void OnFixedUpdate() => _fear.Tick(Player, VentChance, LadderChance, ZiplineChance, PlatformChance, DoorChance, BlackoutTime);
}
