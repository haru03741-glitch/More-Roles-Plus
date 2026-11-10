// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/Madmate.cs, Roles/Ability/MadmateAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// マッドメイト (クルーの姿・インポスターの味方): インポスターが勝つと一緒に勝つ。生死は問わない。
// 設定でタスクを決めた数だけ終えるとインポスターが誰か分かる
public sealed class Madmate : RoleBase
{
    public override Team BaseTeam => Team.Madmate;
    internal const string MadColor = "#FF1919";
    public override string Color => MadColor;
    public override Text Name => new("Madmate.Name");
    public override string Reading => "まっどめいと";
    public override Text Blurb => new("Madmate.Blurb");
    public override string Origin => "libhalt";
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt KnowImpostors = new("Madmate.KnowImpostors", false);
    public static readonly IntOpt NeededTasks = new("Madmate.NeededTasks", 6, 0, 30) { When = () => KnowImpostors };
    // 原作の専用タスク (MadmateIsSpecialTasks / MadmateSpecialTasks): 分かるためのタスクの数を共通・ショート・ロングで決める
    public static readonly BoolOpt SpecialTasks = new("Madmate.SpecialTasks", false) { When = () => KnowImpostors };
    public static readonly IntOpt SpecialCommon = new("Madmate.SpecialCommon", 1, 0, 15) { When = () => KnowImpostors && SpecialTasks };
    public static readonly IntOpt SpecialShort = new("Madmate.SpecialShort", 1, 0, 15) { When = () => KnowImpostors && SpecialTasks };
    public static readonly IntOpt SpecialLong = new("Madmate.SpecialLong", 1, 0, 15) { When = () => KnowImpostors && SpecialTasks };
    // 原作どおり既定は直せない (通信・停電・原子炉と酸素)。マッドメイトとして配られた役職にも効く
    public static readonly BoolOpt CannotFixComms = new("Madmate.CannotFixComms", true);
    public static readonly BoolOpt CannotFixLights = new("Madmate.CannotFixLights", true);
    public static readonly BoolOpt CannotFixReactor = new("Madmate.CannotFixReactor", true);
    public static readonly BoolOpt Vent = new("Madmate.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("Madmate.ImpostorVision", false);
    // クルーの役職 (CanBeMadmate) をマッドメイトとしても配る。出る確率と人数はその役職の設定に出る
    public static readonly BoolOpt MaddenRoles = new("Madmate.MaddenRoles", false);

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius) => ApplyVision(ref radius);

    public override void OnAssigned()
    {
        if (IsLocal && KnowImpostors && SpecialTasks)
            PortKit.After(0.5f, () =>
            {
                if (RoleState.Local == this) SnrKit.AssignOwnTasks(SpecialCommon, SpecialShort, SpecialLong);
            });
    }

    // その破壊工作の修理 (コンソール) をマッドメイトの設定で止めるか (Rules/RepairLimit.cs が呼ぶ)
    internal static bool BlocksRepair(TaskTypes t) => t switch
    {
        TaskTypes.FixComms => CannotFixComms.Value,
        TaskTypes.FixLights => CannotFixLights.Value,
        TaskTypes.ResetReactor or TaskTypes.StopCharles or TaskTypes.ResetSeismic or TaskTypes.RestoreOxy => CannotFixReactor.Value,
        _ => false,
    };

    public override string NameMark(PlayerControl seen) => Mark(Player, seen);

    // マッドメイトとして配られた役職 (RoleBase.Madden) と共通
    internal static void ApplyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    internal static string Mark(PlayerControl me, PlayerControl seen) =>
        KnowImpostors && Known(me) && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    // 原作の complete >= Min(必要数, 全タスク数)。タスクが 1 つも無い時は必要数が 0 の時だけ
    private static bool Known(PlayerControl me)
    {
        if (!me || me.Data == null || me.Data.Tasks == null) return false;
        int all = me.Data.Tasks.Count;
        if (all <= 0) return NeededTasks <= 0;
        return PortKit.TasksDone(me) >= System.Math.Min(NeededTasks.Value, all);
    }
}
