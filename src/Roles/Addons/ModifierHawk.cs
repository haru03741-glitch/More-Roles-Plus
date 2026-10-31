// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/ModifierHawk.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// モディファイアホーク (アドオン): 「ホークアイ」でしばらく視点を引いて広く見渡せる
public sealed class ModifierHawk : AddonBase
{
    public override string Color => "#226B39";
    public override Text Name => new("ModifierHawk.Name");
    public override string Reading => "もでぃふぁいあほーく";
    public override Text Blurb => new("ModifierHawk.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt EyeCooldown = new("ModifierHawk.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt EyeDuration = new("ModifierHawk.Duration", 10f, 1f, 60f, 0.5f, "s");
    public static readonly FloatOpt Magnification = new("ModifierHawk.Magnification", 2f, 1.25f, 10f, 0.25f, "x");
    public static readonly BoolOpt CannotWalk = new("ModifierHawk.CannotWalk", true);

    private bool _active;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ModifierHawk.Eye"), "snr_hawk")
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
