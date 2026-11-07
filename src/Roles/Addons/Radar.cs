// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Radar.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Town Of Us (TOU)
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// レーダー (アドオン): 一番近い生きている人への矢印が出る
public sealed class Radar : AddonBase
{
    public override string Color => "#0B336B";
    public override Text Name => new("Radar.Name");
    public override string Reading => "れーだー";
    public override Text Blurb => new("Radar.Blurb");
    public override string Origin => "Uchu, TOU";

    private WorldArrow _arrow;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        _arrow ??= new WorldArrow(Lifespan, new Color32(11, 51, 107, 255));
        PlayerControl best = null;
        float bd = float.MaxValue;
        if (PortKit.Alive(Player) && !MeetingHud.Instance)
        {
            Vector2 me = Player.GetTruePosition();
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || p.PlayerId == PlayerId) continue;
                float d = Vector2.Distance(me, p.GetTruePosition());
                if (d < bd) { bd = d; best = p; }
            }
        }
        _arrow.SetActive(best);
        if (best) _arrow.Update(best.transform.position);
    }
}
