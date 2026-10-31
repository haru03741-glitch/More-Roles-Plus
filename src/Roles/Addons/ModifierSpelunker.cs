// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/ModifierSpelunker.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// モディファイアスペランカー (アドオン): とても虚弱。ベントに近づく・昇降機・はしご・ドアを開けると決まった確率で死に、通信妨害や停電が長引くと死ぬ
public sealed class ModifierSpelunker : AddonBase
{
    public override string Color => "#DAA520";
    public override Text Name => new("ModifierSpelunker.Name");
    public override string Reading => "もでぃふぁいあすぺらんかー";
    public override Text Blurb => new("ModifierSpelunker.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt SabotageDeath = new("ModifierSpelunker.SabotageDeath", true);
    public static readonly FloatOpt CommsTime = new("ModifierSpelunker.CommsTime", 20f, 5f, 120f, 2.5f, "s") { When = () => SabotageDeath };
    public static readonly FloatOpt LightsTime = new("ModifierSpelunker.LightsTime", 20f, 5f, 120f, 2.5f, "s") { When = () => SabotageDeath };
    public static readonly IntOpt VentChance = new("ModifierSpelunker.VentChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt LiftChance = new("ModifierSpelunker.LiftChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt DoorChance = new("ModifierSpelunker.DoorChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt LadderChance = new("ModifierSpelunker.LadderChance", 20, 0, 100, 5, "%");

    internal readonly SpelunkerRisk Risk = new();

    [LocalOnly]
    public override void OnFixedUpdate() =>
        Risk.Tick(Player, SabotageDeath ? CommsTime : -1f, SabotageDeath ? LightsTime : -1f, VentChance, LiftChance, LadderChance);

    [LocalOnly]
    private void OnExiled(PlayerExiledEvent e) => Risk.OnExiled();
}
