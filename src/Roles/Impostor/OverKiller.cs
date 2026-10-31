// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/OverKiller.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// オーバーキラー (インポスター): キルした相手の死体が、周りにいくつも増える
public sealed class OverKiller : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("OverKiller.Name");
    public override string Reading => "おーばーきらー";
    public override Text Blurb => new("OverKiller.Blurb");
    public override string Origin => "SNR";
    public static readonly IntOpt ExtraBodies = new("OverKiller.ExtraBodies", 3, 1, 10);

    // 全員の端末で: 死体を写して周りに置く
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        var id = e.Target.PlayerId;
        PortKit.After(0.1f, () =>
        {
            foreach (var b in Object.FindObjectsOfType<DeadBody>())
            {
                if (!b || b.ParentId != id) continue;
                for (int i = 0; i < ExtraBodies; i++)
                {
                    var copy = Object.Instantiate(b.gameObject, b.transform.parent);
                    var off = Random.insideUnitCircle * 0.6f;
                    copy.transform.position = b.transform.position + new Vector3(off.x, off.y, 0f);
                }
                break;
            }
        });
    }
}
