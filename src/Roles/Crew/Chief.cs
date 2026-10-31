// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Chief.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 村長 (クルー): 「任命」で近くの人をシェリフにする。相手がインポスターなら自分が死ぬ。
// 任命されたシェリフの設定はシェリフの設定のまま
public sealed class Chief : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("Chief.Name");
    public override string Reading => "そんちょう";
    public override Text Blurb => new("Chief.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Chief.Appoint", "snr_appoint") };

    public static readonly FloatOpt AppointCooldown = new("Chief.Cooldown", 30f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt SeeSheriff = new("Chief.SeeSheriff", false);
    public static readonly BoolOpt AppointNeutral = new("Chief.AppointNeutral", true);
    public static readonly BoolOpt AppointNeutralKiller = new("Chief.AppointNeutralKiller", true) { When = () => AppointNeutral };

    private byte _appointed = 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Chief.Appoint"), "snr_appoint")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => AppointCooldown,
            MaxUses = () => 1,
            CanTarget = (_, t) => AppointNeutral ? AppointNeutralKiller || RoleState.Of(t) is not { IsKiller: true } : !PortKit.IsNeutral(t),
            OnUsed = (user, target) =>
            {
                _appointed = target.PlayerId;
                if (!AmongUsClient.Instance.AmHost) return;
                if (PortKit.IsImpostor(target)) PortKit.HostSuicide(user);
                else RoleState.Change(target, RoleState.Proto<Sheriff>());
            },
        });
    }

    public override string NameMark(PlayerControl seen) =>
        SeeSheriff && seen.PlayerId == _appointed && RoleState.Of(seen) is Sheriff ? "<color=#FFFF00>★</color>" : null;
}
