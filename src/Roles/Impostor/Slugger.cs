// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Slugger.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// スラッガー (インポスター): 「ハリセン」で周りの人を吹き飛ばす (死体は残らない)
public sealed class Slugger : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Slugger.Name");
    public override string Reading => "すらっがー";
    public override Text Blurb => new("Slugger.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Slugger.Swing", "snr_harisen") };
    public static readonly FloatOpt Range = new("Slugger.Range", 1.5f, 0.5f, 10f, 0.5f, "m");
    public static readonly FloatOpt SwingCooldown = new("Slugger.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Slugger.Swing"), "snr_harisen")
        {
            Cooldown = () => SwingCooldown,
            MaxUses = () => 0,
            OnUse = () => { AreaCall.Send(0); if (AmongUsClient.Instance.AmHost) Strike(Player); return true; },
        });
    }

    private static readonly RemoteCall<byte> AreaCall = new("Slugger.Strike", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Slugger && !sender.AmOwner) Strike(sender); });

    // ホストの端末で: 周りの人を倒す
    private static void Strike(PlayerControl user)
    {
        if (!PortKit.Alive(user)) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == user.PlayerId || PortKit.IsImpostor(p)) continue;
            if (Targeting.Unreachable(user, p, Range) != null) continue;
            PortKit.HostVanish(user, p);
        }
        
    }
}
