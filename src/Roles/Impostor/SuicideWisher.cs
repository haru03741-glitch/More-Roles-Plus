// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/SuicideWisher.cs, Roles/Ability/SuicideWisherAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// スーサイドウィッシャー (インポスター): 「自殺」ボタンでいつでも自殺できる
public sealed class SuicideWisher : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("SuicideWisher.Name");
    public override string Reading => "すーさいどうぃっしゃー";
    public override Text Blurb => new("SuicideWisher.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("SuicideWisher.Suicide", "snr_suicide") };

    public static readonly FloatOpt SuicideCooldown = new("SuicideWisher.Cooldown", 20f, 0f, 180f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("SuicideWisher.Suicide"), "snr_suicide")
        {
            Cooldown = () => SuicideCooldown,
            OnUse = () =>
            {
                PortKit.Suicide();
                return true;
            },
        });
    }
}
