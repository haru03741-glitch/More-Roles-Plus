// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/ToiletFan.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// トイレファン (クルー・エアシップだけ): 「トイレ！」でラウンジのトイレのドアを全部開ける
public sealed class ToiletFan : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#745030";
    public override Text Name => new("ToiletFan.Name");
    public override string Reading => "といれふぁん";
    public override Text Blurb => new("ToiletFan.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("ToiletFan.Open", "snr_toilet") };
    public override byte[] OnlyMaps => new byte[] { 4 };

    public static readonly FloatOpt ToiletCooldown = new("ToiletFan.Cooldown", 20f, 0f, 60f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ToiletFan.Open"), "snr_toilet")
        {
            Cooldown = () => ToiletCooldown,
            OnUse = () =>
            {
                var ship = ShipStatus.Instance;
                if (!ship || MeetingHud.Instance || !ship.Systems.ContainsKey(SystemTypes.Doors)) return false;
                foreach (byte i in new byte[] { 79, 80, 81, 82 }) ship.RpcUpdateSystem(SystemTypes.Doors, i);
                return true;
            },
        });
    }
}
