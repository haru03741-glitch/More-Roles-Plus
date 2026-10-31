// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/MadRaccoon.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 化け狸 (クルーの姿・インポスターの味方): 「化ける」でしばらく近くの人の姿になれる
public sealed class MadRaccoon : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("MadRaccoon.Name");
    public override string Reading => "ばけだぬき";
    public override Text Blurb => new("MadRaccoon.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("MadRaccoon.Morph", "snr_morph") };
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt Vent = new("MadRaccoon.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("MadRaccoon.ImpostorVision", false);
    public static readonly BoolOpt KnowImpostors = new("MadRaccoon.KnowImpostors", false);
    public static readonly IntOpt NeededTasks = new("MadRaccoon.NeededTasks", 6, 0, 30) { When = () => KnowImpostors };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    public static readonly FloatOpt MorphCooldown = new("MadRaccoon.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt MorphDuration = new("MadRaccoon.Duration", 15f, 2.5f, 60f, 2.5f, "s");

    private bool _morphed;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("MadRaccoon.Morph"), "snr_morph")
        {
            Target = AbilityTarget.Pick,
            Cooldown = () => MorphCooldown,
            Duration = () => MorphDuration,
            CanTarget = (u, t) => t.PlayerId != u.PlayerId,
            OnUsed = (user, target) =>
            {
                if (!user.AmOwner) return;
                _morphed = true;
                PortKit.Morph(target);
            },
            OnEnd = Revert,
        });
        Lifespan.OnRelease(Revert);
    }

    private void Revert()
    {
        if (!_morphed) return;
        _morphed = false;
        PortKit.Morph(null);
    }
}
