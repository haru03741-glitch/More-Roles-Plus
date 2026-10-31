// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Sheriff.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// シェリフ (クルー): 「執行」で近くの人を倒す。倒してよい陣営でなければ自分が死ぬ
public sealed class Sheriff : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("Sheriff.Name");
    public override string Reading => "しぇりふ";
    public override Text Blurb => new("Sheriff.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Sheriff.Execute", "snr_execute") };

    public static readonly FloatOpt KillCooldown = new("Sheriff.KillCooldown", 25f, 0f, 60f, 2.5f, "s");
    public static readonly IntOpt MaxKills = new("Sheriff.MaxKills", 1, 1, 10);
    public static readonly ChoiceOpt SuicideMode = new("Sheriff.SuicideMode", 0,
        new Text("Sheriff.SuicideMode.Default"), new Text("Sheriff.SuicideMode.AlwaysSuicide"), new Text("Sheriff.SuicideMode.AlwaysKill"));
    public static readonly BoolOpt KillImpostor = new("Sheriff.KillImpostor", true);
    public static readonly BoolOpt KillMad = new("Sheriff.KillMad", true);
    public static readonly BoolOpt KillNeutral = new("Sheriff.KillNeutral", true);
    public static readonly BoolOpt KillFriends = new("Sheriff.KillFriends", true);
    public static readonly BoolOpt KillLovers = new("Sheriff.KillLovers", true);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Sheriff.Execute"), "snr_execute")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => KillCooldown,
            MaxUses = () => MaxKills,
            OnUsed = Execute,
        });
    }

    // ホストの端末で、狙った人を倒してよいかを決めて倒す / 自決する
    internal static void Execute(PlayerControl user, PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        bool ok = Judge(target);
        int mode = SuicideMode.Value;
        if (ok || mode == 2) PortKit.HostKill(user, target);
        if (mode == 1 || (mode == 0 && !ok)) PortKit.HostSuicide(user);
    }

    // 倒してよい相手か (設定の陣営)
    internal static bool Judge(PlayerControl target)
    {
        if (PortKit.LooksImpostor(target)) return KillImpostor;
        if (PortKit.IsNeutral(target)) return KillNeutral;
        if (PortKit.IsMad(target)) return KillMad;
        if (PortKit.IsFriend(target)) return KillFriends;
        if (PortKit.IsLover(target)) return KillLovers;
        return false;
    }
}
