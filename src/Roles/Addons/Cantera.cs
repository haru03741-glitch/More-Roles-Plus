// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Ghost/Cantera.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// カンテラ (幽霊役職・クルー側): 死んだクルーに付く。「灯火」で誰かに (壁越しでも) 取り憑き、しばらくその人の視界を
// 船の一番明るい時の何倍かにする (会議が終わると解ける)
public sealed class Cantera : AddonBase
{
    public override string Color => "#FFEDC2";
    public override Text Name => new("Cantera.Name");
    public override string Reading => "かんてら";
    public override Text Blurb => new("Cantera.Blurb");
    public override string Origin => "SNR";
    public override bool Ghost => true;

    public override bool CanAttach(Team team, RoleBase role) => team != Team.Impostor;

    public static readonly FloatOpt LightCooldown = new("Cantera.Cooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt LightDuration = new("Cantera.Duration", 5f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt LightVision = new("Cantera.Vision", 1.5f, 0.25f, 3f, 0.25f, "x");
    public static readonly BoolOpt LimitUses = new("Cantera.LimitUses", false);
    public static readonly IntOpt MaxUses = new("Cantera.MaxUses", 3, 1, 15) { When = () => LimitUses };

    private static int _stamp;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Cantera.Light"), "snr_light")
        {
            Ghost = true,
            Target = AbilityTarget.Player,
            IgnoreWalls = true,
            Cooldown = () => LightCooldown,
            Duration = () => LightDuration,
            MaxUses = () => LimitUses ? MaxUses : 0,
            OnUsed = (_, target) =>
            {
                if (!target.AmOwner || !ShipStatus.Instance) return;
                int stamp = ++_stamp;
                SnrKit.FixedVision = ShipStatus.Instance.MaxLightRadius * LightVision;
                StatusEffects.ShowFor("Cantera", new Text("Cantera.Lit"), Color, LightDuration);
                PortKit.After(LightDuration, () => { if (stamp == _stamp) SnrKit.FixedVision = -1f; });
            },
        });
        Lifespan.OnRelease(() => _stamp++);
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        _stamp++;
        if (SnrKit.FixedVision >= 0f && !Revenant_LocalHaunted) SnrKit.FixedVision = -1f;
    }

    private static bool Revenant_LocalHaunted => Impostor.Revenant.LocalHaunted;
}
