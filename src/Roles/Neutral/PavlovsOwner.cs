// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/PavlovsOwner.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// パブロフの犬の飼い主 (第三陣営): キルはできない。「しつけ」で近くの人を犬 (パブロフの犬) にする。
// 犬が生きていて、犬の陣営だけが残れば一緒に勝つ。相手がインポスターだと自分が死ぬ (設定)
public sealed class PavlovsOwner : RoleBase
{
    public const string Pack = "Pavlovs";
    public const string PackColor = "#F5DEB3";

    public override Team Team => Team.Neutral;
    public override string Color => PackColor;
    public override Text Name => new("PavlovsOwner.Name");
    public override string Reading => "ぱぶろふのいぬ(かいぬし)";
    public override Text Blurb => new("PavlovsOwner.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("PavlovsOwner.Train", "snr_sidekick") };
    public override string Faction => Pack;

    public static readonly FloatOpt TrainCooldown = new("PavlovsOwner.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt MaxDogs = new("PavlovsOwner.MaxDogs", 2, 1, 10);
    public static readonly BoolOpt SuicideOnImpostor = new("PavlovsOwner.SuicideOnImpostor", true);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("PavlovsOwner.Train"), "snr_sidekick")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => TrainCooldown,
            MaxUses = () => MaxDogs,
            CanTarget = (_, t) => !InPack(t),
            OnUsed = (user, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                if (SuicideOnImpostor && PortKit.IsImpostor(target)) PortKit.HostSuicide(user);
                else RoleState.Change(target, RoleState.Proto<PavlovsDog>());
            },
        });
    }

    public static bool InPack(PlayerControl p) => RoleState.Of(p) is { Faction: Pack };

    public static bool AnyOwnerAlive()
    {
        foreach (var r in RoleState.All)
            if (r is PavlovsOwner && PortKit.Alive(r.Player)) return true;
        return false;
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId != PlayerId && RoleState.Of(seen) is PavlovsDog ? $"<color={PackColor}>●</color>" : null;
}
