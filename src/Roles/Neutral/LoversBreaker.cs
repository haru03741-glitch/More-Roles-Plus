// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/LoversBreaker.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 爆ぜ師 (第三陣営): 「爆破」でラバーズを倒す。決まった数のラバーズを倒してラバーズが全滅すると単独で勝つ。
// ラバーズ以外を爆破しようとすると自分が死ぬ
public sealed class LoversBreaker : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#C71585";
    public override Text Name => new("LoversBreaker.Name");
    public override string Reading => "はぜし";
    public override Text Blurb => new("LoversBreaker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("LoversBreaker.Blast", "snr_bomb") };
    public static readonly FloatOpt BlastCooldown = new("LoversBreaker.Cooldown", 25f, 2.5f, 120f, 2.5f, "s");
    public static readonly IntOpt NeededBlasts = new("LoversBreaker.NeededBlasts", 2, 1, 10);

    private int _blasts;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("LoversBreaker.Blast"), "snr_bomb")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => BlastCooldown,
            OnUsed = (user, target) =>
            {
                if (PortKit.IsLover(target)) _blasts++;
                if (!AmongUsClient.Instance.AmHost) return;
                if (PortKit.IsLover(target)) PortKit.HostKill(user, target);
                else PortKit.HostSuicide(user);
            },
        });
    }

    [HostOnly]
    private void OnMurdered(PlayerMurderedEvent e) => PortKit.After(0.3f, Check);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => PortKit.After(0.3f, Check);

    private void Check()
    {
        if (_blasts < NeededBlasts || !PortKit.Alive(Player)) return;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && PortKit.IsLover(p)) return;
        GameEnd.Win(this);
    }
}
