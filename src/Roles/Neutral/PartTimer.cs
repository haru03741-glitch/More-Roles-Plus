// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/PartTimer.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// フリーター (第三陣営): 「バイト」で人を選んで雇い主にする。雇い主が生きたまま勝つと一緒に勝つ。
// 雇い主が死ぬと、また別の人を選べる
public sealed class PartTimer : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#00FF7F";
    public override Text Name => new("PartTimer.Name");
    public override string Reading => "ふりーたー";
    public override Text Blurb => new("PartTimer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("PartTimer.Hire", "snr_parttime") };
    public static readonly FloatOpt HireCooldown = new("PartTimer.Cooldown", 20f, 0f, 120f, 2.5f, "s");

    private byte _boss = 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("PartTimer.Hire"), "snr_parttime")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => HireCooldown,
            CanTarget = (_, t) => !PortKit.Alive(PortKit.ById(_boss)),
            OnUsed = (_, target) => _boss = target.PlayerId,
        });
    }

    public override bool AlsoWins(GameResult result) =>
        PortKit.Alive(Player) && result.Winners.Contains(_boss) && PortKit.Alive(PortKit.ById(_boss));

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _boss ? "<color=#00FF7F>雇</color>" : null;
}
