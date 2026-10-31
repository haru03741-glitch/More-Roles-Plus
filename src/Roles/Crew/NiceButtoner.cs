// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/NiceButtoner.cs, Roles/Ability/ButtonerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// ナイスボタナー (クルー): 「ポチッ」でサボタージュ中以外ならどこからでも緊急会議を開ける
public sealed class NiceButtoner : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#00FFFF";
    public override Text Name => new("NiceButtoner.Name");
    public override string Reading => "ないすぼたなー";
    public override Text Blurb => new("NiceButtoner.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("NiceButtoner.Press", "snr_button") };

    public static readonly FloatOpt PressCooldown = new("NiceButtoner.Cooldown", 20f, 0f, 180f, 2.5f, "s");
    public static readonly IntOpt Uses = new("NiceButtoner.Uses", 1, 1, 10);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("NiceButtoner.Press"), "snr_button")
        {
            Cooldown = () => PressCooldown,
            MaxUses = () => Uses,
            OnUse = () =>
            {
                if (PortKit.AnySabotage()) return false;
                PlayerControl.LocalPlayer.CmdReportDeadBody(null);
                return true;
            },
        });
    }
}
