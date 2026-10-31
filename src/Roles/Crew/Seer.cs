// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Seer.cs, Roles/Ability/SeerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 霊媒師 (クルー): 誰かが死ぬと画面が光り、死んだ場所に魂が見える (追放では出ない)
public sealed class Seer : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#61B26C";
    public override Text Name => new("Seer.Name");
    public override string Reading => "れいばいし";
    public override Text Blurb => new("Seer.Blurb");
    public override string Origin => "SNR";

    public static readonly ChoiceOpt Mode = new("Seer.Mode", 0,
        new Text("Seer.Mode.Both"), new Text("Seer.Mode.Flash"), new Text("Seer.Mode.Soul"));
    public static readonly BoolOpt LimitSoul = new("Seer.LimitSoul", false) { When = () => Mode.Value != 1 };
    public static readonly FloatOpt SoulDuration = new("Seer.SoulDuration", 30f, 2.5f, 60f, 2.5f, "s") { When = () => Mode.Value != 1 && LimitSoul };

    internal static readonly Color FlashColor = new Color32(97, 178, 108, 153);

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!PortKit.Alive(Player) || !e.Target) return;
        if (Mode.Value != 2) PortKit.Flash(FlashColor);
        if (Mode.Value != 1) SeerKit.Soul(Lifespan, e.Target.transform.position, null, LimitSoul, SoulDuration);
    }
}
