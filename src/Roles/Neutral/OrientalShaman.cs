// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/OrientalShaman.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 陰陽師 (第三陣営): キルできる人が分かる。試合の終わりに生きていれば、勝った陣営から勝ちを奪う。
// 1 度だけ近くの人を式神 (自分の味方) にできる
public sealed class OrientalShaman : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#FFE4B5";
    public override Text Name => new("OrientalShaman.Name");
    public override string Reading => "おんみょうじ";
    public override Text Blurb => new("OrientalShaman.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("OrientalShaman.Servant", "snr_shikigami") };
    public static readonly BoolOpt Vent = new("OrientalShaman.CanVent", false);
    public static readonly BoolOpt CanServant = new("OrientalShaman.CanServant", true);
    public static readonly FloatOpt ServantCooldown = new("OrientalShaman.Cooldown", 30f, 0f, 120f, 2.5f, "s") { When = () => CanServant };

    public override bool? CanVent => Vent ? true : null;

    public override void OnAssigned()
    {
        if (!CanServant) return;
        AddAbility(new Ability(new Text("OrientalShaman.Servant"), "snr_shikigami")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => ServantCooldown,
            MaxUses = () => 1,
            OnUsed = (user, target) =>
            {
                ShermansServant.Master[target.PlayerId] = user.PlayerId;
                if (AmongUsClient.Instance.AmHost) RoleState.Change(target, RoleState.Proto<ShermansServant>());
            },
        });
        RoleState.Match.OnRelease(ShermansServant.Master.Clear);
    }

    public override bool StealsWin(GameResult result) => PortKit.Alive(Player);

    public override string NameMark(PlayerControl seen)
    {
        if (seen.PlayerId == PlayerId) return null;
        if (RoleState.Of(seen) is ShermansServant s && s.MasterId == PlayerId) return "<color=#FFE4B5>式</color>";
        return PortKit.CanKillAnyone(seen) ? "<color=#FF1919>●</color>" : null;
    }
}
