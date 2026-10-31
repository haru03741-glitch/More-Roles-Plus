// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Necromancer.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// ネクロマンサー (インポスター): 幽霊が見え、「呪縛」で幽霊をレヴェナントにする。レヴェナントの居場所は矢印で分かる
public sealed class Necromancer : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Necromancer.Name");
    public override string Reading => "ねくろまんさー";
    public override Text Blurb => new("Necromancer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Necromancer.Curse", "snr_necro") };

    public static readonly FloatOpt CurseCooldown = new("Necromancer.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt MaxRevenants = new("Necromancer.MaxRevenants", 1, 1, 15);
    public static readonly IntOpt RequiredTasks = new("Necromancer.RevenantRequiredTasks", 3, 0, 15);
    public static readonly IntOpt HauntDuration = new("Necromancer.RevenantHauntDuration", 5, 1, 15, 1, "s");
    public static readonly FloatOpt HauntVision = new("Necromancer.RevenantHauntVision", 0.1f, 0f, 1f, 0.1f, "x");
    public static readonly BoolOpt CannotReportWhileHaunted = new("Necromancer.RevenantCannotReport", true);

    private readonly Dictionary<byte, WorldArrow> _arrows = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Necromancer.Curse"), "snr_necro")
        {
            Target = AbilityTarget.Player,
            TargetDead = true,
            Cooldown = () => CurseCooldown,
            MaxUses = () => MaxRevenants,
            CanTarget = (_, t) => RoleState.Of(t) is not Revenant,
            OnUsed = (_, target) =>
            {
                if (AmongUsClient.Instance.AmHost) RoleState.Change(target, RoleState.Proto<Revenant>());
            },
        });
    }

    // 自分の端末で: 幽霊を見えるようにし、レヴェナントへの矢印を出す
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!PortKit.Alive(Player))
        {
            foreach (var a in _arrows.Values) a.SetActive(false);
            return;
        }
        if (!MeetingHud.Instance) SnrKit.ShowGhosts();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null) continue;
            bool rev = RoleState.Of(p) is Revenant && !p.Data.Disconnected;
            if (!_arrows.TryGetValue(p.PlayerId, out var arrow))
            {
                if (!rev) continue;
                arrow = _arrows[p.PlayerId] = new WorldArrow(Lifespan, UnityEngine.Color.red);
            }
            arrow.SetActive(rev && !MeetingHud.Instance);
            if (rev) arrow.Update(p.transform.position);
        }
    }
}
