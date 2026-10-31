// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilHacker.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルハッカー (インポスター): 「アドミン」でいつでもどこでもアドミン (部屋ごとの人数) を見られる
public sealed class EvilHacker : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilHacker.Name");
    public override string Reading => "いびるはっかー";
    public override Text Blurb => new("EvilHacker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilHacker.Admin", "snr_admin") };
    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilHacker.Admin"), "snr_admin")
        {
            OnUse = () =>
            {
                var hud = Vanilla.Hud;
                if (!hud) return false;
                hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.CountOverlay });
                return false;
            },
        });
    }
}
