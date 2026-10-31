// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/NiceHawk.cs, Roles/Ability/HawkAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ナイスホーク (クルー): 「ホークアイ」でしばらく視点を引いて広く見渡せる (設定で、その間は歩けない)
public sealed class NiceHawk : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#226B39";
    public override Text Name => new("NiceHawk.Name");
    public override string Reading => "ないすほーく";
    public override Text Blurb => new("NiceHawk.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("NiceHawk.Eye", "snr_hawk") };

    public static readonly FloatOpt EyeCooldown = new("NiceHawk.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt EyeDuration = new("NiceHawk.Duration", 10f, 1f, 60f, 0.5f, "s");
    public static readonly FloatOpt Magnification = new("NiceHawk.Magnification", 2f, 1.25f, 10f, 0.25f, "x");
    public static readonly BoolOpt CannotWalk = new("NiceHawk.CannotWalk", true);

    private bool _active;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("NiceHawk.Eye"), "snr_hawk")
        {
            Cooldown = () => EyeCooldown,
            Duration = () => EyeDuration,
            OnUse = () =>
            {
                _active = true;
                Zoom.ByRole(Magnification);
                return true;
            },
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
