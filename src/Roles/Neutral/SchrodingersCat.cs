// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/SchrodingersCat.cs, Roles/Ability/SchrodingersCatAbility.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Crew;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// シュレディンガーの猫 (第三陣営): 勝ち方を持たない。キルされかけると死なずに、キルした人の陣営の猫になる
// (インポスター → インポスターの猫 / キル無しの設定ならマッドの猫、ジャッカルの仲間 → ジャッカルの猫 / フレンズの猫、
// パブロフの仲間 → パブロフの猫 / フレンズの猫、クルー → クルーの猫)。それ以外の第三陣営にキルされた時は普通に死ぬ
// (設定でクルーの猫として死ぬ)。設定で、キルした人が少し後に死ぬ。追放された時はクルーの猫になる (設定)。恋人の猫は何も起きない
public sealed class SchrodingersCat : RoleBase
{
    public const string CatColor = "#7E7E7E";

    public override Team Team => Team.Neutral;
    public override string Color => CatColor;
    public override Text Name => new("SchrodingersCat.Name");
    public override string Reading => "しゅれでぃんがーのねこ";
    public override Text Blurb => new("SchrodingersCat.Blurb");
    public override string Origin => "SNR";
    public override bool TasksCount => false;

    public static readonly BoolOpt HasKill = new("SchrodingersCat.HasKill", true);
    public static readonly FloatOpt KillCooldown = new("SchrodingersCat.KillCooldown", 45f, 2.5f, 60f, 2.5f, "s") { When = () => HasKill };
    public static readonly BoolOpt VictimSuicide = new("SchrodingersCat.KillVictimSuicide", false) { When = () => HasKill };
    public static readonly FloatOpt SuicideTime = new("SchrodingersCat.SuicideTime", 15f, 0f, 60f, 2.5f, "s") { When = () => HasKill && VictimSuicide };
    public static readonly BoolOpt CrewOnExile = new("SchrodingersCat.BeCrewOnExile", false);
    public static readonly BoolOpt CrewOnOtherKill = new("SchrodingersCat.CrewOnKillByNonSpecific", false);

    public override void OnAssigned()
    {
        if (IsLocal) PortKit.After(0.5f, () => { if (RoleState.Local == this) SnrKit.AssignOwnTasks(0, 0, 0); });
    }

    // ホスト: キルされかけたら陣営を決める
    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Target || e.Target.PlayerId != PlayerId || !e.Killer || e.Killer.PlayerId == PlayerId || PortKit.IsLover(Player)) return;
        var killer = e.Killer;
        var kr = RoleState.Of(killer);
        RoleBase proto;
        if (PortKit.IsImpostor(killer)) proto = HasKill ? RoleState.Proto<SchrodingersCatImpostor>() : RoleState.Proto<SchrodingersCatMadmate>();
        else if (kr is { Faction: Jackal.Pack } || kr is { WinsWithFaction: Jackal.Pack, CanKill: true })
            proto = HasKill ? RoleState.Proto<SchrodingersCatJackal>() : RoleState.Proto<SchrodingersCatFriends>();
        else if (kr is { Faction: PavlovsOwner.Pack } || kr is { WinsWithFaction: PavlovsOwner.Pack, CanKill: true })
            proto = HasKill ? RoleState.Proto<SchrodingersCatPavlovs>() : RoleState.Proto<SchrodingersCatPavlovFriends>();
        else if (kr == null || kr.Team == Team.Crew) proto = RoleState.Proto<SchrodingersCatCrew>();
        else
        {
            // それ以外の第三陣営: 普通に死ぬ (設定でクルーの猫として)
            if (CrewOnOtherKill)
            {
                var me = Player;
                PortKit.After(0.1f, () => { if (me && RoleState.Of(me) is SchrodingersCat) RoleState.Change(me, RoleState.Proto<SchrodingersCatCrew>()); });
            }
            return;
        }
        e.Cancel("schrodinger");
        FakeCall.Send((killer.PlayerId, PlayerId));
        Fake(killer.PlayerId, PlayerId);
        RoleState.Change(Player, proto);
        if (HasKill && VictimSuicide && proto is not SchrodingersCatCrew) ScheduleSuicide(killer);
    }

    // ホスト: 追放されたらクルーの猫になる (設定)
    [HostOnly]
    private void OnExiled(PlayerExiledEvent e)
    {
        if (!CrewOnExile || !e.Player || e.Player.PlayerId != PlayerId) return;
        var me = Player;
        PortKit.After(0.1f, () => { if (me && RoleState.Of(me) is SchrodingersCat) RoleState.Change(me, RoleState.Proto<SchrodingersCatCrew>()); });
    }

    private static readonly RemoteCall<(byte killer, byte cat)> FakeCall = new("SchrodingersCat.Fake", Route.HostToAll,
        (w, m) => { w.Write(m.killer); w.Write(m.cat); }, r => (r.ReadByte(), r.ReadByte()), (_, m) => Fake(m.killer, m.cat));

    // 全員の端末で: 猫の画面ではキルされたように見せ、キルした人は猫の所へ寄って待ち時間が戻る
    private static void Fake(byte killerId, byte catId)
    {
        var killer = PortKit.ById(killerId);
        var cat = PortKit.ById(catId);
        if (!killer || !cat) return;
        if (cat.AmOwner)
        {
            if (cat.MyPhysics && cat.MyPhysics.body) cat.MyPhysics.body.velocity = Vector2.zero;
            var hud = Vanilla.Hud;
            if (hud && hud.KillOverlay) hud.KillOverlay.ShowKillAnimation(killer.Data, cat.Data);
        }
        if (killer.AmOwner)
        {
            if (Vector2.Distance(killer.GetTruePosition(), cat.GetTruePosition()) <= 3.5f) Teleport.Self(cat.transform.position);
            killer.SetKillTimer(GameManager.Instance.LogicOptions.GetKillCooldown());
        }
    }

    // ホスト: キルした人を少し後 (会議が始まればその時) に死なせる
    private static void ScheduleSuicide(PlayerControl killer)
    {
        float left = SuicideTime;
        NoticeCall.Send(killer.PlayerId);
        if (killer.AmOwner) StatusEffects.ShowFor("SchrodingersCatSuicide", new Text("SchrodingersCat.SuicideText"), CatColor, left);
        System.Action step = null;
        step = () =>
        {
            if (!PortKit.Alive(killer)) return;
            left -= 0.1f;
            if (MeetingHud.Instance) { PortKit.HostVanish(killer, killer); return; }
            if (left <= 0f) { PortKit.HostSuicide(killer); return; }
            PortKit.After(0.1f, step);
        };
        step();
    }

    private static readonly RemoteCall<byte> NoticeCall = new("SchrodingersCat.Notice", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (_, id) =>
        {
            var p = PortKit.ById(id);
            if (p && p.AmOwner) StatusEffects.ShowFor("SchrodingersCatSuicide", new Text("SchrodingersCat.SuicideText"), CatColor, SuicideTime);
        });
}

// 陣営が決まった猫の共通
public abstract class SchrodingersCatBase : RoleBase
{
    public override Team Team => Team.Neutral;
    public override Text Name => new("SchrodingersCat.Name");
    public override Text Blurb => new("SchrodingersCat.Blurb");
    public override string Origin => "SNR";
    public override bool MadeByOthers => true;
    public override bool TasksCount => false;

    protected abstract string TeamColor { get; }
    protected abstract string TeamKey { get; }
    public override string Color => TeamColor;
    public override HelpSection[] HelpSections => new HelpSection[] { new(TeamKey) };

    // 仲間か (お互いに名前の印で分かる)
    protected abstract bool IsMate(PlayerControl p);

    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId != PlayerId && IsMate(seen) ? $"<color={TeamColor}>●</color>" : null;

    public override string MarkSeenBy(PlayerControl viewer) =>
        viewer && viewer.PlayerId != PlayerId && IsMate(viewer) ? $"<color={TeamColor}>猫</color>" : null;
}

// キルできる猫の共通: 仲間以外をキルできる
public abstract class SchrodingersCatKiller : SchrodingersCatBase
{
    public override bool IsKiller => true;
    public override void ModifyKillCooldown(ref float seconds) => seconds = SchrodingersCat.KillCooldown;

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId && e.Target && e.Target != e.Killer && IsMate(e.Target)) e.Cancel("cat mate");
    }
}

public sealed class SchrodingersCatCrew : SchrodingersCatBase
{
    public override string Reading => "しゅれでぃんがーのねこくるー";
    public override Team Team => Team.Crew;
    protected override string TeamColor => "#8CFFFF";
    protected override string TeamKey => "SchrodingersCat.TeamCrew";
    protected override bool IsMate(PlayerControl p) => false;
}

public sealed class SchrodingersCatImpostor : SchrodingersCatKiller
{
    public override string Reading => "しゅれでぃんがーのねこいんぽすたー";
    public override Team? WinsWithTeam => Team.Impostor;
    public override string Faction => null;
    public override bool? CanVent => true;
    protected override string TeamColor => "#FF1919";
    protected override string TeamKey => "SchrodingersCat.TeamImpostor";
    protected override bool IsMate(PlayerControl p) => PortKit.IsImpostor(p) || RoleState.Of(p) is SchrodingersCatImpostor;
    public override void ModifyVision(ref float radius) => PortKit.ImpostorVision(ref radius);
}

public sealed class SchrodingersCatMadmate : SchrodingersCatBase
{
    public override string Reading => "しゅれでぃんがーのねこまっど";
    public override Team? WinsWithTeam => Team.Impostor;
    public override bool? CanVent => Madmate.Vent ? true : null;
    protected override string TeamColor => "#FF1919";
    protected override string TeamKey => "SchrodingersCat.TeamMadmate";
    protected override bool IsMate(PlayerControl p) =>
        Madmate.KnowImpostors && PortKit.TasksDone(Player) >= Madmate.NeededTasks && PortKit.IsImpostor(p);
    public override string MarkSeenBy(PlayerControl viewer) => null;

    public override void ModifyVision(ref float radius)
    {
        if (Madmate.ImpostorSight) PortKit.ImpostorVision(ref radius);
    }
}

public sealed class SchrodingersCatJackal : SchrodingersCatKiller
{
    public override string Reading => "しゅれでぃんがーのねこじゃっかる";
    public override string Faction => null;
    public override string WinsWithFaction => Jackal.Pack;
    public override bool? CanVent => Jackal.Vent;
    protected override string TeamColor => Jackal.PackColor;
    protected override string TeamKey => "SchrodingersCat.TeamJackal";
    protected override bool IsMate(PlayerControl p) =>
        RoleState.Of(p) is { Faction: Jackal.Pack } or SchrodingersCatJackal;
}

public sealed class SchrodingersCatFriends : SchrodingersCatBase
{
    public override string Reading => "しゅれでぃんがーのねこふれんず";
    public override string WinsWithFaction => Jackal.Pack;
    public override bool? CanVent => JackalFriends.Vent ? true : null;
    protected override string TeamColor => Jackal.PackColor;
    protected override string TeamKey => "SchrodingersCat.TeamFriends";
    protected override bool IsMate(PlayerControl p) =>
        JackalFriends.KnowJackal && PortKit.TasksDone(Player) >= JackalFriends.NeededTasks && RoleState.Of(p) is { Faction: Jackal.Pack };
    public override string MarkSeenBy(PlayerControl viewer) => null;

    public override void ModifyVision(ref float radius)
    {
        if (JackalFriends.ImpostorSight) PortKit.ImpostorVision(ref radius);
    }
}

public sealed class SchrodingersCatPavlovs : SchrodingersCatKiller
{
    public override string Reading => "しゅれでぃんがーのねこぱぶろふ";
    public override string Faction => null;
    public override string WinsWithFaction => PavlovsOwner.Pack;
    public override bool? CanVent => PavlovsDog.Vent;
    protected override string TeamColor => PavlovsOwner.PackColor;
    protected override string TeamKey => "SchrodingersCat.TeamPavlovs";
    protected override bool IsMate(PlayerControl p) =>
        RoleState.Of(p) is { Faction: PavlovsOwner.Pack } or SchrodingersCatPavlovs;

    public override void ModifyVision(ref float radius)
    {
        if (PavlovsDog.ImpostorSight) PortKit.ImpostorVision(ref radius);
    }
}

public sealed class SchrodingersCatPavlovFriends : SchrodingersCatBase
{
    public override string Reading => "しゅれでぃんがーのねこぱぶろふふれんず";
    public override string WinsWithFaction => PavlovsOwner.Pack;
    protected override string TeamColor => PavlovsOwner.PackColor;
    protected override string TeamKey => "SchrodingersCat.TeamPavlovFriends";
    protected override bool IsMate(PlayerControl p) => false;
    public override string MarkSeenBy(PlayerControl viewer) => null;
}
