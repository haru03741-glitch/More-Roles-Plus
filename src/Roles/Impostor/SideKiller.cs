// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/SideKiller.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// サイドキラー (インポスター): 1 度だけ「侵食する」で近くの人をマッドキラーにする。自分が死ぬとマッドキラーはインポスターになる
public sealed class SideKiller : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("SideKiller.Name");
    public override string Reading => "さいどきらー";
    public override Text Blurb => new("SideKiller.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("SideKiller.Convert", "snr_sidekick") };
    public static readonly FloatOpt ConvertCooldown = new("SideKiller.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("SideKiller.Convert"), "snr_sidekick")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => ConvertCooldown,
            MaxUses = () => 1,
            CanTarget = (_, t) => !PortKit.IsImpostor(t),
            OnUsed = (user, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                RoleState.Change(target, RoleState.Proto<Crew.MadKiller>());
                Crew.MadKiller.Master[target.PlayerId] = user.PlayerId;
                Crew.MadKiller.SendMaster(target.PlayerId, user.PlayerId);
            },
        });
    }

    public override string NameMark(PlayerControl seen) =>
        RoleState.Of(seen) is Crew.MadKiller m && m.MasterId == PlayerId ? "<color=#FF1919>◇</color>" : null;
}
