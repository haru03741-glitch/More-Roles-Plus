// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Samurai.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 侍 (インポスター): 1 試合に 1 度だけ「必殺技」で周りのクルーを全員斬る
public sealed class Samurai : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Samurai.Name");
    public override string Reading => "さむらい";
    public override Text Blurb => new("Samurai.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Samurai.Slash", "snr_slash") };
    public static readonly FloatOpt Range = new("Samurai.Range", 2f, 0.5f, 10f, 0.5f, "m");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Samurai.Slash"), "snr_slash")
        {
            MaxUses = () => 1,
            OnUse = () => { AreaCall.Send(0); if (AmongUsClient.Instance.AmHost) Strike(Player); return true; },
        });
    }

    private static readonly RemoteCall<byte> AreaCall = new("Samurai.Strike", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Samurai && !sender.AmOwner) Strike(sender); });

    // ホストの端末で: 周りの人を倒す
    private static void Strike(PlayerControl user)
    {
        if (!PortKit.Alive(user)) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == user.PlayerId || PortKit.IsImpostor(p)) continue;
            if (Targeting.Unreachable(user, p, Range) != null) continue;
            PortKit.HostKill(user, p);
        }
        
    }
}
