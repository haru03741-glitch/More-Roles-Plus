// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/SelfBomber.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 自爆魔 (インポスター): 「自爆」で自分が死ぬ代わりに、周りのクルーを全員倒す
public sealed class SelfBomber : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("SelfBomber.Name");
    public override string Reading => "じばくま";
    public override Text Blurb => new("SelfBomber.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("SelfBomber.Bomb", "snr_bomb") };
    public static readonly FloatOpt Range = new("SelfBomber.Range", 2f, 0.5f, 10f, 0.5f, "m");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("SelfBomber.Bomb"), "snr_bomb")
        {
            MaxUses = () => 1,
            OnUse = () => { AreaCall.Send(0); if (AmongUsClient.Instance.AmHost) Strike(Player); return true; },
        });
    }

    private static readonly RemoteCall<byte> AreaCall = new("SelfBomber.Strike", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is SelfBomber && !sender.AmOwner) Strike(sender); });

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
        PortKit.HostSuicide(user);
    }
}
