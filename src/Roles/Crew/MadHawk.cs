// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/MadHawk.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// マッドホーク (クルーの姿・インポスターの味方): 「ホークアイ」でしばらく視点を引いて広く見渡せる
public sealed class MadHawk : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("MadHawk.Name");
    public override string Reading => "まっどほーく";
    public override Text Blurb => new("MadHawk.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("MadHawk.Eye", "snr_hawk") };
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt Vent = new("MadHawk.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("MadHawk.ImpostorVision", false);
    public static readonly BoolOpt KnowImpostors = new("MadHawk.KnowImpostors", false);
    public static readonly IntOpt NeededTasks = new("MadHawk.NeededTasks", 6, 0, 30) { When = () => KnowImpostors };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    public static readonly FloatOpt EyeCooldown = new("MadHawk.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt EyeDuration = new("MadHawk.Duration", 10f, 1f, 60f, 0.5f, "s");
    public static readonly FloatOpt Magnification = new("MadHawk.Magnification", 2f, 1.25f, 10f, 0.25f, "x");
    public static readonly BoolOpt CannotWalk = new("MadHawk.CannotWalk", true);

    private bool _active;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("MadHawk.Eye"), "snr_hawk")
        {
            Cooldown = () => EyeCooldown,
            Duration = () => EyeDuration,
            OnUse = () => { _active = true; Zoom.ByRole(Magnification); return true; },
            OnEnd = Stop,
        });
        Lifespan.OnRelease(Stop);
    }

    private void Stop()
    {
        if (!_active) return;
        _active = false;
        Zoom.ByRole(1f);
    }

    public override void ModifySpeed(ref float speed)
    {
        if (_active && CannotWalk) speed = 0f;
    }
}
