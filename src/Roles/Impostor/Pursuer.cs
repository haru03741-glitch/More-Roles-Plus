// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Pursuer.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 追跡者 (インポスター): インポスター以外でいちばん近くの生きている人の方へ矢印が出る (自分にだけ)
public sealed class Pursuer : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Pursuer.Name");
    public override string Reading => "ついせきしゃ";
    public override Text Blurb => new("Pursuer.Blurb");
    public override string Origin => "SNR";
    private SpriteRenderer _arrow;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var lp = Player;
        PlayerControl best = null;
        float bestD = float.MaxValue;
        if (PortKit.Alive(lp) && !MeetingHud.Instance)
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || PortKit.IsImpostor(p) || p.PlayerId == lp.PlayerId) continue;
                float d = Vector2.Distance(lp.GetTruePosition(), p.GetTruePosition());
                if (d < bestD) { best = p; bestD = d; }
            }
        if (!best)
        {
            if (_arrow) _arrow.gameObject.SetActive(false);
            return;
        }
        if (!_arrow) _arrow = PortKit.Marker(Lifespan, Vector2.zero, "snr_arrowhead", 0.4f, new Color(1f, 0.1f, 0.1f));
        _arrow.gameObject.SetActive(true);
        Vector2 dir = (best.GetTruePosition() - lp.GetTruePosition()).normalized;
        Vector2 at = (Vector2)lp.transform.position + dir * 0.9f;
        _arrow.transform.position = new Vector3(at.x, at.y, lp.transform.position.z - 0.5f);
        _arrow.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
    }
}
