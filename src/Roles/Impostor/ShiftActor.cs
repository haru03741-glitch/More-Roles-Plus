// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/ShiftActor.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// シフトアクター (インポスター): しばらく他の人の姿になれ、化けた相手の役職が分かる
public sealed class ShiftActor : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("ShiftActor.Name");
    public override string Reading => "しふとあくたー";
    public override Text Blurb => new("ShiftActor.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("ShiftActor.Morph", "snr_morph") };
    public static readonly FloatOpt MorphCooldown = new("ShiftActor.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt MorphDuration = new("ShiftActor.Duration", 15f, 2.5f, 60f, 2.5f, "s");

    private bool _morphed;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ShiftActor.Morph"), "snr_morph")
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
                var r = RoleState.Of(target);
                if (r != null) PortKit.Notice(new Text("ShiftActor.Role").With("name", target.Data.PlayerName).With("role", $"<color={r.Color}>{r.Name}</color>").ToString());
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
