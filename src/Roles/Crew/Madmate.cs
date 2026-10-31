// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/Madmate.cs, Roles/Ability/MadmateAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// マッドメイト (クルーの姿・インポスターの味方): インポスターが勝つと一緒に勝つ。生死は問わない。
// 設定でタスクを決めた数だけ終えるとインポスターが誰か分かる
public sealed class Madmate : RoleBase
{
    public override Team Team => Team.Madmate;
    public override string Color => "#FF1919";
    public override Text Name => new("Madmate.Name");
    public override string Reading => "まっどめいと";
    public override Text Blurb => new("Madmate.Blurb");
    public override string Origin => "SNR";
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt KnowImpostors = new("Madmate.KnowImpostors", false);
    public static readonly IntOpt NeededTasks = new("Madmate.NeededTasks", 6, 0, 30) { When = () => KnowImpostors };
    public static readonly BoolOpt Vent = new("Madmate.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("Madmate.ImpostorVision", false);

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;
}
