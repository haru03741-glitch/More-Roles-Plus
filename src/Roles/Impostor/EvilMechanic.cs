// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilMechanic.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルメカニック (インポスター): 「移設」で近くのベントの姿になり、そのベントを運べる (つながる先は変わらない)
public sealed class EvilMechanic : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilMechanic.Name");
    public override string Reading => "いびるめかにっく";
    public override Text Blurb => new("EvilMechanic.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilMechanic.Move", "snr_mechanic") };
    public static readonly FloatOpt MoveCooldown = new("EvilMechanic.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt MoveDuration = new("EvilMechanic.Duration", 10f, 2.5f, 30f, 2.5f, "s");

    internal MechanicKit Kit;

    public override void OnAssigned()
    {
        Kit = new MechanicKit(this);
        AddAbility(Kit.Make(new Text("EvilMechanic.Move"), "snr_mechanic", () => MoveCooldown, () => MoveDuration));
        Lifespan.OnRelease(() => { if (IsLocal) Kit.Stop(); MechanicKit.Hide(Player, false); });
    }

    public override void OnFixedUpdate() => Kit?.Tick();

    // 死んだら運んでいるベントを置く
    [LocalOnly]
    private void OnDeath(PlayerMurderedEvent e)
    {
        if (e.Target.PlayerId == PlayerId) Kit.Stop();
    }
}
