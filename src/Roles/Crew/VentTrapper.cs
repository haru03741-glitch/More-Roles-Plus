// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/VentTrapper.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ベントトラッパー (クルー): ベントに罠を仕掛ける。仕掛けた本人以外がそのベントに入ると死ぬ (罠は 1 回で消える)
public sealed class VentTrapper : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#556B2F";
    public override Text Name => new("VentTrapper.Name");
    public override string Reading => "べんととらっぱー";
    public override Text Blurb => new("VentTrapper.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("VentTrapper.Trap", "snr_trap") };
    public static readonly FloatOpt TrapCooldown = new("VentTrapper.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt Traps = new("VentTrapper.Uses", 3, 1, 15);

    private readonly System.Collections.Generic.HashSet<int> _traps = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("VentTrapper.Trap"), "snr_trap")
        {
            Cooldown = () => TrapCooldown,
            MaxUses = () => Traps,
            OnUse = () =>
            {
                var vent = PortKit.NearestVent(Player.GetTruePosition(), 1.2f);
                if (!vent || _traps.Contains(vent.Id)) return false;
                TrapCall.Send(vent.Id);
                _traps.Add(vent.Id);
                return true;
            },
        });
    }

    private static readonly RemoteCall<int> TrapCall = new("VentTrapper.Trap", Route.Anyone,
        (w, id) => w.WritePacked(id), r => r.ReadPackedInt32(),
        (sender, id) =>
        {
            // 負の数 = 罠が消えた (ホストから)。それ以外 = 仕掛けた (本人から)
            if (id < 0) { foreach (var r in RoleState.All) if (r is VentTrapper t) t._traps.Remove(-1 - id); }
            else if (RoleState.Of(sender) is VentTrapper v) v._traps.Add(id);
        });

    [HostOnly]
    private void OnVent(VentEnteredEvent e)
    {
        if (e.Player.PlayerId == PlayerId || !_traps.Remove(e.VentId)) return;
        TrapCall.Send(-1 - e.VentId); // 消えたことを全員へ
        PortKit.HostVanish(Player, e.Player);
    }
}
