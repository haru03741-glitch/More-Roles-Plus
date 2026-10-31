// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/NiceMechanic.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ナイスメカニック (クルー): 「移設」で近くのベントの姿になり、そのベントを運べる (つながる先は変わらない)
public sealed class NiceMechanic : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#526CAD";
    public override Text Name => new("NiceMechanic.Name");
    public override string Reading => "ないすめかにっく";
    public override Text Blurb => new("NiceMechanic.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("NiceMechanic.Move", "snr_mechanic") };
    public static readonly FloatOpt MoveCooldown = new("NiceMechanic.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt MoveDuration = new("NiceMechanic.Duration", 10f, 2.5f, 30f, 2.5f, "s");
    public static readonly BoolOpt UseVent = new("NiceMechanic.CanVent", true);

    public override bool? CanVent => UseVent ? true : null;

    internal MechanicKit Kit;

    public override void OnAssigned()
    {
        Kit = new MechanicKit(this);
        AddAbility(Kit.Make(new Text("NiceMechanic.Move"), "snr_mechanic", () => MoveCooldown, () => MoveDuration));
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
