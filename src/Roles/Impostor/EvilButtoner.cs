// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilButtoner.cs, Roles/Ability/ButtonerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// イビルボタナー (インポスター): 「ポチッ」でサボタージュ中以外ならどこからでも緊急会議を開ける
public sealed class EvilButtoner : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilButtoner.Name");
    public override string Reading => "いびるぼたなー";
    public override Text Blurb => new("EvilButtoner.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilButtoner.Press", "snr_button") };

    public static readonly FloatOpt PressCooldown = new("EvilButtoner.Cooldown", 20f, 0f, 180f, 2.5f, "s");
    public static readonly IntOpt Uses = new("EvilButtoner.Uses", 1, 1, 10);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilButtoner.Press"), "snr_button")
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
