// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/Worshiper.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 崇拝者 (クルーの姿・インポスターの味方): いつでも自殺できる
public sealed class Worshiper : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("Worshiper.Name");
    public override string Reading => "すうはいしゃ";
    public override Text Blurb => new("Worshiper.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Worshiper.Suicide", "snr_suicide") };
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt Vent = new("Worshiper.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("Worshiper.ImpostorVision", false);
    public static readonly BoolOpt KnowImpostors = new("Worshiper.KnowImpostors", false);
    public static readonly IntOpt NeededTasks = new("Worshiper.NeededTasks", 6, 0, 30) { When = () => KnowImpostors };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    public static readonly FloatOpt SuicideCooldown = new("Worshiper.Cooldown", 30f, 0f, 60f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Worshiper.Suicide"), "snr_suicide")
        {
            Cooldown = () => SuicideCooldown,
            OnUse = () => { PortKit.Suicide(); return true; },
        });
    }
}
