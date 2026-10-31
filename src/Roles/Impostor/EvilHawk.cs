// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilHawk.cs, Roles/Ability/HawkAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// イビルホーク (インポスター): 「ホークアイ」でしばらく視点を引いて広く見渡せる (設定で、その間は歩けない)
public sealed class EvilHawk : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilHawk.Name");
    public override string Reading => "いびるほーく";
    public override Text Blurb => new("EvilHawk.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilHawk.Eye", "snr_hawk") };

    public static readonly FloatOpt EyeCooldown = new("EvilHawk.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt EyeDuration = new("EvilHawk.Duration", 10f, 1f, 60f, 0.5f, "s");
    public static readonly FloatOpt Magnification = new("EvilHawk.Magnification", 2f, 1.25f, 10f, 0.25f, "x");
    public static readonly BoolOpt CannotWalk = new("EvilHawk.CannotWalk", true);

    private bool _active;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilHawk.Eye"), "snr_hawk")
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
