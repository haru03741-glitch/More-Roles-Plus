// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/EvilNekomata.cs, Roles/Ability/RevengeExileAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビル猫又 (インポスター): 追放されると、生きている人を 1 人ランダムに道連れにする
public sealed class EvilNekomata : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilNekomata.Name");
    public override string Reading => "いびるねこまた";
    public override Text Blurb => new("EvilNekomata.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt ExcludeImpostor = new("EvilNekomata.ExcludeImpostor", false);

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        var pool = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p.PlayerId != PlayerId && PortKit.Alive(p) && !(ExcludeImpostor && PortKit.IsImpostor(p))) pool.Add(p);
        if (pool.Count == 0) return;
        PortKit.HostVanish(Player, pool[Random.Range(0, pool.Count)]);
    }
}
