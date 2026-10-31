// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Jackal.cs, Roles/Ability/JSidekickAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// ジャッカル (第三陣営・キル役): キルボタンを持ち、ジャッカルの仲間だけが残れば勝ち。
// 1 度だけ「サイドキック」で近くの人を仲間 (サイドキック / ジャッカルフレンズ) にできる。
// 視界とキルの距離は第三陣営のキル役の共通設定
public sealed class Jackal : RoleBase
{
    public const string Pack = "Jackal";
    public const string PackColor = "#00B4EB";

    public override Team Team => Team.Neutral;
    public override string Color => PackColor;
    public override Text Name => new("Jackal.Name");
    public override string Reading => "じゃっかる";
    public override Text Blurb => new("Jackal.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Jackal.Kill"), new("Jackal.Sidekick", "snr_sidekick") };
    public override bool IsKiller => true;
    public override string Faction => Pack;

    public static readonly FloatOpt KillCooldown = new("Jackal.KillCooldown", 25f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt Vent = new("Jackal.CanVent", true);
    public static readonly BoolOpt CanSidekick = new("Jackal.CanSidekick", true);
    public static readonly FloatOpt SidekickCooldown = new("Jackal.SidekickCooldown", 30f, 0f, 120f, 2.5f, "s") { When = () => CanSidekick };
    public static readonly BoolOpt Infinite = new("Jackal.Infinite", false) { When = () => CanSidekick };

    // サイドキックから上がったジャッカル (Infinite でなければもう仲間を作れない)。全員の端末で同じように付ける
    internal static readonly HashSet<byte> Promoted = new();

    public override bool? CanVent => Vent;

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    public override void OnAssigned()
    {
        if (!CanSidekick || (Promoted.Contains(PlayerId) && !Infinite)) return;
        AddAbility(new Ability(new Text("Jackal.Sidekick"), "snr_sidekick")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SidekickCooldown,
            MaxUses = () => 1,
            CanTarget = (_, t) => !InPack(t),
            OnUsed = (_, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                RoleState.Change(target, RoleState.Proto<Sidekick>());
            },
        });
    }

    // ジャッカルの仲間 (サイドキック・フレンズを含む)
    public static bool InPack(PlayerControl p) => RoleState.Of(p) is { } r && (r.Faction == Pack || r.WinsWithFaction == Pack);

    // ジャッカルが仲間を倒すキルは通さない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (InPack(e.Target)) e.Cancel("jackal pack");
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId != PlayerId && InPack(seen) ? $"<color={PackColor}>◆</color>" : null;

    // 生きているジャッカルがいなければ、生きているサイドキックをジャッカルに上げる。全員の端末で呼ぶ (Change はホストだけが配る)
    internal static void PromoteIfLeaderless()
    {
        foreach (var r in RoleState.All)
            if (r is Jackal && PortKit.Alive(r.Player)) return;
        foreach (var r in RoleState.All)
        {
            if (r is not Sidekick || !PortKit.Alive(r.Player)) continue;
            if (Promoted.Count == 0) RoleState.Match.OnRelease(Promoted.Clear);
            Promoted.Add(r.PlayerId);
            if (AmongUsClient.Instance.AmHost) RoleState.Change(r.Player, RoleState.Proto<Jackal>());
            return; // 上げるのは 1 人だけ
        }
    }
}
