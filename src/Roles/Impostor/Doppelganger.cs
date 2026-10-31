// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Doppelganger.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ドッペルゲンガー (インポスター): しばらく他の人の姿になれる。化けている相手をキルすると、次のキルの待ち時間が短くなる
public sealed class Doppelganger : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Doppelganger.Name");
    public override string Reading => "どっぺるげんがー";
    public override Text Blurb => new("Doppelganger.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Doppelganger.Morph", "snr_morph") };
    public static readonly FloatOpt MorphCooldown = new("Doppelganger.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt MorphDuration = new("Doppelganger.Duration", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt BonusCooldown = new("Doppelganger.BonusCooldown", 5f, 0f, 60f, 2.5f, "s");

    private byte _as = 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Doppelganger.Morph"), "snr_morph")
        {
            Target = AbilityTarget.Pick,
            Cooldown = () => MorphCooldown,
            Duration = () => MorphDuration,
            CanTarget = (u, t) => t.PlayerId != u.PlayerId,
            OnUsed = (user, target) =>
            {
                _as = target.PlayerId;
                if (user.AmOwner) PortKit.Morph(target);
            },
            OnEnd = Revert,
        });
        Lifespan.OnRelease(Revert);
    }

    private void Revert()
    {
        if (_as == 255) return;
        _as = 255;
        PortKit.Morph(null);
    }

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId != _as) return;
        PortKit.After(0.1f, () => Player.SetKillTimer(BonusCooldown));
    }
}
