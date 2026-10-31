// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/NekoKabocha.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ネコカボチャ (インポスター): キルされるとキルした人を道連れにする。追放されると生きている人を 1 人道連れにする
public sealed class NekoKabocha : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("NekoKabocha.Name");
    public override string Reading => "ねこかぼちゃ";
    public override Text Blurb => new("NekoKabocha.Blurb");
    public override string Origin => "SNR";
    [HostOnly, OnlyMine]
    private void OnKilled(PlayerMurderedEvent e)
    {
        var killer = e.Killer;
        if (!killer || killer.PlayerId == PlayerId) return;
        PortKit.After(0.05f, () => PortKit.HostKill(Player, killer));
    }

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        var pool = new System.Collections.Generic.List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p.PlayerId != PlayerId && PortKit.Alive(p)) pool.Add(p);
        if (pool.Count > 0) PortKit.HostVanish(Player, pool[Random.Range(0, pool.Count)]);
    }
}
