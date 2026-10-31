// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/NiceNekomata.cs, Roles/Ability/RevengeExileAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ナイス猫又 (クルー): 追放されると、生きている人を 1 人ランダムに道連れにする
public sealed class NiceNekomata : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF8000";
    public override Text Name => new("NiceNekomata.Name");
    public override string Reading => "ないすねこまた";
    public override Text Blurb => new("NiceNekomata.Blurb");
    public override string Origin => "SNR";

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        var pool = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p.PlayerId != PlayerId && PortKit.Alive(p) && true) pool.Add(p);
        if (pool.Count == 0) return;
        PortKit.HostVanish(Player, pool[Random.Range(0, pool.Count)]);
    }
}
