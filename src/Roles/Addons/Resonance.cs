// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Resonance.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Town Of Us Mira (TOU-Mira)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// レゾナンス (アドオン・インポスターだけ): 他のインポスターがキルすると画面が赤く光る (設定で殺された人の色)
public sealed class Resonance : AddonBase
{
    public override string Color => "#FF1919";
    public override Text Name => new("Resonance.Name");
    public override string Reading => "れぞなんす";
    public override Text Blurb => new("Resonance.Blurb");
    public override string Origin => "Uchu, TOUMira";

    protected internal override bool DefaultOnCrew => false;
    protected internal override bool DefaultOnMadmate => false;
    protected internal override bool DefaultOnNeutral => false;

    public static readonly BoolOpt IsColorFlash = new("Resonance.IsColorFlash", false);

    [LocalOnly]
    private void OnPlayerMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || !e.Target || e.Killer.PlayerId == PlayerId || !PortKit.IsImpostor(e.Killer) || !PortKit.Alive(Player)) return;
        UnityEngine.Color c = IsColorFlash && e.Target.Data != null
            ? Palette.PlayerColors[e.Target.Data.DefaultOutfit.ColorId % Palette.PlayerColors.Length]
            : new UnityEngine.Color32(255, 25, 25, 255);
        PortKit.Flash(c);
    }
}
