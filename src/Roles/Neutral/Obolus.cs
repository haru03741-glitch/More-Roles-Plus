// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Obolus.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// オボルス (第三陣営・キル役): 自前のキルボタンを持つが、勝ちは数の勝負ではない。キルは 1 ターンに 1 回。
// 最初の KillShieldTurn 回の会議まではキルが封印されている。インポスター (設定でジャッカルも) を倒すと単独で勝つ。
// 開示ありなら、キルが使えるようになった時にインポスター (とジャッカル) へ「オボルスに気をつけろ」と出る
public sealed class Obolus : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#BD871A";
    public override Text Name => new("Obolus.Name");
    public override string Reading => "おぼるす";
    public override Text Blurb => new("Obolus.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Obolus.Kill", "obolus_kill") };
    public override bool IsKiller => true;
    public override bool CanKill => false;     // 本編のキルボタンは使わない
    public override string Faction => null;    // 数の勝負には入らない

    public static readonly FloatOpt KillCooldown = new("Obolus.KillCooldown", 25f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt KillShieldTurn = new("Obolus.KillShieldTurn", 2, 0, 10);
    public static readonly BoolOpt WinByJackal = new("Obolus.WinByJackal", true);
    public static readonly BoolOpt ImpostorSight = new("Obolus.ImpostorVision", true);
    public static readonly BoolOpt IgnoreLights = new("Obolus.IgnoreLights", true);
    public static readonly BoolOpt Vent = new("Obolus.CanVent", true);
    public static readonly BoolOpt Reveal = new("Obolus.Reveal", false);
    public static readonly BoolOpt RevealDeath = new("Obolus.RevealDeath", true) { When = () => Reveal };

    public override bool? CanVent => Vent;

    private int _meetings;
    private bool _revealed;

    private bool Sealed => _meetings < KillShieldTurn;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) { PortKit.ImpostorVision(ref radius); return; }
        if (!IgnoreLights) return;
        var ship = ShipStatus.Instance;
        if (ship) radius = ship.MaxLightRadius * GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
    }

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Obolus.Kill"), "obolus_kill")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => KillCooldown,
            UsesPerMeeting = 1,
            Usable = () => !Sealed, // 封印の間はボタンが暗く押せない
            OnUsed = (user, t) => { if (AmongUsClient.Instance.AmHost) UchuKit.HostAbilityKill(user, t); },
        });
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        // 封印が 0 回なら最初の会議で知らせる
        if (KillShieldTurn == 0 && _meetings == 0) RevealOnce();
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        bool was = Sealed;
        _meetings++;
        if (was && !Sealed) RevealOnce();
    }

    // 狙われる側 (インポスター・設定でジャッカル) に当たる人か
    private static bool Prey(PlayerControl p) =>
        PortKit.IsImpostor(p) || (WinByJackal && RoleState.Of(p) is Jackal or Sidekick);

    // 全員の端末で: 自分が狙われる側なら知らせる
    private void RevealOnce()
    {
        if (!Reveal || _revealed) return;
        _revealed = true;
        if (Prey(PlayerControl.LocalPlayer)) PortKit.Notice(new Text("Obolus.Warning").ToString());
    }

    private void OnPlayerMurdered(PlayerMurderedEvent e)
    {
        if (!e.Target) return;
        // オボルスが死んだ: 開示していれば狙われる側に知らせる
        if (e.Target.PlayerId == PlayerId)
        {
            if (Reveal && RevealDeath && Prey(PlayerControl.LocalPlayer)) PortKit.Notice(new Text("Obolus.Dead").ToString());
            return;
        }
        // ホスト: オボルスがインポスター (とジャッカル) を倒したら勝ち
        if (!AmongUsClient.Instance.AmHost || !e.Killer || e.Killer.PlayerId != PlayerId) return;
        if (PortKit.IsImpostor(e.Target) || (WinByJackal && RoleState.Of(e.Target) is Jackal or Sidekick))
            PortKit.After(0.3f, () => GameEnd.Win(this));
    }

    private void OnPlayerExiled(PlayerExiledEvent e)
    {
        if (e.Player && e.Player.PlayerId == PlayerId && Reveal && RevealDeath && Prey(PlayerControl.LocalPlayer))
            PortKit.Notice(new Text("Obolus.Dead").ToString());
    }
}
