// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Robber.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 泥棒 (インポスター): キルした相手が終えていたタスクを、全部終えていない状態に戻す
public sealed class Robber : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Robber.Name");
    public override string Reading => "どろぼう";
    public override Text Blurb => new("Robber.Blurb");
    public override string Origin => "SNR";
    // 全員の端末で同じように戻す
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        var data = e.Target.Data;
        if (data == null || data.Tasks == null) return;
        foreach (var t in data.Tasks) if (t != null) t.Complete = false;
        if (GameData.Instance) GameData.Instance.RecomputeTaskCounts();
    }
}
