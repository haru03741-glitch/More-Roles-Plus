// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/BlackSanta.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ブラックサンタ (クルーの姿・インポスターの味方): 「プレゼント」でインポスターをランダムなインポスターの役職にする。相手がインポスターでなければ自分が死ぬ
public sealed class BlackSanta : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("BlackSanta.Name");
    public override string Reading => "ぶらっくさんた";
    public override Text Blurb => new("BlackSanta.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("BlackSanta.Gift", "snr_present") };
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt Vent = new("BlackSanta.CanVent", true);
    public static readonly BoolOpt ImpostorSight = new("BlackSanta.ImpostorVision", true);
    public static readonly BoolOpt KnowImpostors = new("BlackSanta.KnowImpostors", true);
    public static readonly IntOpt NeededTasks = new("BlackSanta.NeededTasks", 1, 0, 30) { When = () => KnowImpostors };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    public static readonly FloatOpt GiftCooldown = new("BlackSanta.Cooldown", 25f, 0f, 180f, 2.5f, "s");
    public static readonly IntOpt Gifts = new("BlackSanta.Uses", 1, 1, 15);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("BlackSanta.Gift"), "snr_present")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => GiftCooldown,
            MaxUses = () => Gifts,
            OnUsed = (user, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                if (!PortKit.IsImpostor(target)) { PortKit.HostSuicide(user); return; }
                var pool = Options.Registry.Roles.FindAll(r => r.Team == Team.Impostor && !r.IsVanilla && !r.MadeByOthers
                    && r.WinsWithTeam == null && r.WinsWithFaction == null && r is not BlackSanta && r.GetType() != RoleState.Of(target)?.GetType());
                if (pool.Count > 0) RoleState.Change(target, pool[Random.Range(0, pool.Count)]);
            },
        });
    }
}
