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
    public static readonly BoolOpt Vent = new("Madmate.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("Madmate.ImpostorVision", false);
    // クルーの役職 (CanBeMadmate) をマッドメイトとしても配る。出る確率と人数はその役職の設定に出る
    public static readonly BoolOpt MaddenRoles = new("Madmate.MaddenRoles", false);

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius) => ApplyVision(ref radius);

    public override string NameMark(PlayerControl seen) => Mark(Player, seen);

    // マッドメイトとして配られた役職 (RoleBase.Madden) と共通
    internal static void ApplyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    internal static string Mark(PlayerControl me, PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(me) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;
}
