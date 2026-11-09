// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Sheriff.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// シェリフ (クルー): 「執行」で近くの人を倒す。倒してよい陣営でなければ自分が死ぬ
public sealed class Sheriff : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#FFFF00";
    public override Text Name => new("Sheriff.Name");
    public override string Reading => "しぇりふ";
    public override Text Blurb => new("Sheriff.Blurb");
    public override string Origin => "SM";
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
        Shot(user, target);
        if (!AmongUsClient.Instance.AmHost) return;
        bool ok = Judge(target);
        int mode = SuicideMode.Value;
        if (ok || mode == 2) PortKit.HostKill(user, target, PortKit.Harm.Bullet);
        if (mode == 1 || (mode == 0 && !ok)) PortKit.HostSuicide(user);
    }

    // 全員の画面で: 銃口が光り、弾の筋が相手へ走って、当たった所に火花と音
    private static void Shot(PlayerControl user, PlayerControl target)
    {
        if (!user || !target || MeetingHud.Instance) return;
        bool seen = user.Visible && !user.inVent;
        Vector2 a = (Vector2)user.transform.position + new Vector2(target.transform.position.x < user.transform.position.x ? -0.3f : 0.3f, 0.05f);
        Vector2 b = (Vector2)target.transform.position + new Vector2(0f, 0.05f);
        if (seen) ShotKit.Beam(a, b, new UnityEngine.Color(1f, 0.9f, 0.45f, 0.85f), 0.035f, 0.08f);
        var dir = (b - a).normalized;
        float za = RoleSparks.DepthAt(user.GetTruePosition().y) - 0.01f, zb = RoleSparks.DepthAt(target.GetTruePosition().y) - 0.01f;
        if (seen)
        {
            RoleSparks.Spawn(new Vector3(a.x, a.y, za), Vector3.zero, new UnityEngine.Color(1f, 0.95f, 0.6f, 1f), 0.08f, 0.25f, 0.4f);
            for (int i = 0; i < 6; i++)
                RoleSparks.Spawn(new Vector3(a.x, a.y, za), (Vector3)(dir * Random.Range(1.5f, 3f) + Random.insideUnitCircle * 0.8f),
                    new UnityEngine.Color(1f, 0.75f, 0.3f, 1f), 0.15f, 0.04f, 0.01f);
        }
        if (target.Visible && !target.inVent)
            for (int i = 0; i < 10; i++)
                RoleSparks.Spawn(new Vector3(b.x, b.y, zb), (Vector3)(dir * Random.Range(0.5f, 2f) + Random.insideUnitCircle * 1.5f),
                    i % 2 == 0 ? new UnityEngine.Color(1f, 0.85f, 0.4f, 1f) : new UnityEngine.Color(0.85f, 0.1f, 0.1f, 1f), 0.3f, 0.05f, 0.01f, 3f);
        GunSound.Flesh(b.x, b.y);
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
