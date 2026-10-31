// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilDoorr.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルドアー (インポスター): 「ドアーー!!」で目の前のドアを開け閉めする (ドアを開け閉めできる船だけ)
public sealed class EvilDoorr : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilDoorr.Name");
    public override string Reading => "いびるどあー";
    public override Text Blurb => new("EvilDoorr.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilDoorr.Door", "snr_door") };
    public static readonly FloatOpt DoorCooldown = new("EvilDoorr.Cooldown", 10f, 0f, 60f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilDoorr.Door"), "snr_door")
        {
            Cooldown = () => DoorCooldown,
            OnUse = () => Toggle(Player),
        });
    }

    // 目の前 (1.5m 以内で一番近い) のドアを開け閉めする。ドアの無い船・届く所に無い時は false
    private static bool Toggle(PlayerControl me)
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllDoors == null || !ship.Systems.ContainsKey(SystemTypes.Doors)) return false;
        OpenableDoor best = null;
        float bestD = 1.5f;
        foreach (var d in ship.AllDoors)
        {
            if (!d) continue;
            float dist = Vector2.Distance(me.GetTruePosition(), d.transform.position);
            if (dist < bestD) { best = d; bestD = dist; }
        }
        if (!best) return false;
        if (best.IsOpen) ship.RpcCloseDoorsOfType(best.Room);
        else ship.RpcUpdateSystem(SystemTypes.Doors, (byte)(best.Id | 64));
        return true;
    }
}
