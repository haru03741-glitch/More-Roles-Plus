// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Santa.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// サンタ (クルー): 「プレゼント」で近くの人をランダムなクルーの役職にする。相手がクルーでなければ自分が死ぬ
public sealed class Santa : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#E5383B";
    public override Text Name => new("Santa.Name");
    public override string Reading => "さんた";
    public override Text Blurb => new("Santa.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Santa.Gift", "snr_present") };
    public static readonly FloatOpt GiftCooldown = new("Santa.Cooldown", 25f, 0f, 180f, 2.5f, "s");
    public static readonly IntOpt Gifts = new("Santa.Uses", 1, 1, 15);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Santa.Gift"), "snr_present")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => GiftCooldown,
            MaxUses = () => Gifts,
            OnUsed = (user, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                if (!PortKit.IsCrewSide(target)) { PortKit.HostSuicide(user); return; }
                var pool = Options.Registry.Roles.FindAll(r => r.Team == Team.Crew && !r.IsVanilla && !r.MadeByOthers
                    && r.WinsWithTeam == null && r.WinsWithFaction == null && r is not Santa && r.GetType() != RoleState.Of(target)?.GetType());
                if (pool.Count > 0) RoleState.Change(target, pool[Random.Range(0, pool.Count)]);
            },
        });
    }
}
