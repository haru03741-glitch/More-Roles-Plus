// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Spelunker.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// スペランカー (第三陣営): とても虚弱。ベントに近づく・昇降機・はしご・ドアを開けると決まった確率で死に、通信妨害や停電が長引くと死ぬ。
// 試合の終わりまで生きていれば勝つ (設定で、勝った陣営と一緒に勝つ)
public sealed class Spelunker : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#DAA520";
    public override Text Name => new("Spelunker.Name");
    public override string Reading => "すぺらんかー";
    public override Text Blurb => new("Spelunker.Blurb");
    public override string Origin => "SNR";
    public static readonly BoolOpt SabotageDeath = new("Spelunker.SabotageDeath", true);
    public static readonly FloatOpt CommsTime = new("Spelunker.CommsTime", 20f, 5f, 120f, 2.5f, "s") { When = () => SabotageDeath };
    public static readonly FloatOpt LightsTime = new("Spelunker.LightsTime", 20f, 5f, 120f, 2.5f, "s") { When = () => SabotageDeath };
    public static readonly IntOpt VentChance = new("Spelunker.VentChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt LiftChance = new("Spelunker.LiftChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt DoorChance = new("Spelunker.DoorChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt LadderChance = new("Spelunker.LadderChance", 20, 0, 100, 5, "%");

    internal readonly SpelunkerRisk Risk = new();

    [LocalOnly]
    public override void OnFixedUpdate() =>
        Risk.Tick(Player, SabotageDeath ? CommsTime : -1f, SabotageDeath ? LightsTime : -1f, VentChance, LiftChance, LadderChance);

    [LocalOnly]
    private void OnExiled(PlayerExiledEvent e) => Risk.OnExiled();

    public static readonly BoolOpt AdditionalWin = new("Spelunker.AdditionalWin", false);

    public override bool AlsoWins(GameResult result) => AdditionalWin && PortKit.Alive(Player);

    public override bool StealsWin(GameResult result) => !AdditionalWin && PortKit.Alive(Player);
}
