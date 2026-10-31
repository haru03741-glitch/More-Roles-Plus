// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/DoubleKiller.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ダブルキラー (インポスター): 本編のキルボタンとは別に、待ち時間が独立したもう 1 つのキルボタンを持つ
public sealed class DoubleKiller : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("DoubleKiller.Name");
    public override string Reading => "だぶるきらー";
    public override Text Blurb => new("DoubleKiller.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("DoubleKiller.Kill", "snr_kill2") };
    public static readonly FloatOpt SecondCooldown = new("DoubleKiller.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("DoubleKiller.Kill"), "snr_kill2")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SecondCooldown,
            CanTarget = (_, t) => !PortKit.IsImpostor(t),
            OnUsed = (user, target) => PortKit.HostKill(user, target),
        });
    }
}
