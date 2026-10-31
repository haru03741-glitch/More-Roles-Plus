// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/TheThreeLittlePigs.cs, Modules/EndGamer.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 三匹の仔豚 (第三陣営・3 人 1 組): 配られると、ほかのクルー 2 人と組になって一番目・二番目・三番目の仔豚になる。
// キルはできない。3 人とも生きて試合が終わると、その勝ちを横取りする。1 人でも欠けたら、キルできる人外が全滅した時に勝つ。
// それぞれタスクを一定の割合こなすと、一番目は一定の間隔で画面が光り (キルの待ち時間の目安)、二番目はキルを防ぎ、三番目はキルを跳ね返す
public sealed class TheThreeLittlePigs : RoleBase
{
    public const string PigColor = "#FF637B";

    public override Team Team => Team.Neutral;
    public override string Color => PigColor;
    public override Text Name => new("TheThreeLittlePigs.Name");
    public override string Reading => "さんびきのこぶた";
    public override Text Blurb => new("TheThreeLittlePigs.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("TheThreeLittlePigs.Team") };

    public static readonly BoolOpt CustomTasks = new("TheThreeLittlePigs.CustomTasks", false);
    public static readonly IntOpt CommonTasks = new("TheThreeLittlePigs.CommonTasks", 1, 0, 10) { When = () => CustomTasks };
    public static readonly IntOpt ShortTasks = new("TheThreeLittlePigs.ShortTasks", 1, 0, 20) { When = () => CustomTasks };
    public static readonly IntOpt LongTasks = new("TheThreeLittlePigs.LongTasks", 1, 0, 10) { When = () => CustomTasks };
    public static readonly IntOpt FirstPercent = new("TheThreeLittlePigs.FirstPercent", 30, 0, 100, 5, "%");
    public static readonly BoolOpt FirstCustomTimer = new("TheThreeLittlePigs.FirstCustomTimer", false);
    public static readonly FloatOpt FirstFlashTimer = new("TheThreeLittlePigs.FirstFlashTimer", 30f, 5f, 60f, 2.5f, "s") { When = () => FirstCustomTimer };
    public static readonly IntOpt SecondPercent = new("TheThreeLittlePigs.SecondPercent", 60, 0, 100, 5, "%");
    public static readonly IntOpt SecondGuards = new("TheThreeLittlePigs.SecondGuards", 1, 1, 15);
    public static readonly IntOpt ThirdPercent = new("TheThreeLittlePigs.ThirdPercent", 100, 0, 100, 5, "%");
    public static readonly IntOpt ThirdCounters = new("TheThreeLittlePigs.ThirdCounters", 1, 1, 15);

    // 組 (番号 3 つ)。全員の端末で同じ
    internal static readonly List<byte[]> Teams = new();

    // ホスト: ほかのクルー 2 人と組にして、それぞれの仔豚にする
    public override void OnAssigned()
    {
        Lifespan.OnRelease(() => { if (!RoleState.Any) Teams.Clear(); });
        if (!AmongUsClient.Instance.AmHost) return;
        var me = Player;
        PortKit.After(0.5f, () =>
        {
            if (RoleState.Of(me) is not TheThreeLittlePigs) return;
            var pool = new List<PlayerControl>();
            foreach (var p in PlayerControl.AllPlayerControls)
                if (PortKit.Alive(p) && p != me && RoleState.Of(p) is { Team: Team.Crew, MadeByOthers: false } && !PortKit.IsImpostor(p)) pool.Add(p);
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            var ids = new byte[3];
            ids[0] = me.PlayerId;
            ids[1] = pool.Count > 0 ? pool[0].PlayerId : (byte)255;
            ids[2] = pool.Count > 1 ? pool[1].PlayerId : (byte)255;
            TeamCall.Send(ids);
            Register(ids);
            RoleState.Change(me, RoleState.Proto<TheFirstLittlePig>());
            if (ids[1] != 255) RoleState.Change(PortKit.ById(ids[1]), RoleState.Proto<TheSecondLittlePig>());
            if (ids[2] != 255) RoleState.Change(PortKit.ById(ids[2]), RoleState.Proto<TheThirdLittlePig>());
        });
    }

    private static readonly RemoteCall<byte[]> TeamCall = new("TheThreeLittlePigs.Team", Route.HostToAll,
        (w, ids) => w.WriteBytesAndSize(ids), r => r.ReadBytesAndSize(), (_, ids) => Register(ids));

    private static void Register(byte[] ids)
    {
        foreach (var t in Teams) if (t[0] == ids[0]) return;
        Teams.Add(ids);
    }

    internal static byte[] TeamOf(byte id)
    {
        foreach (var t in Teams) if (System.Array.IndexOf(t, id) >= 0) return t;
        return null;
    }

    internal static int AllTasks()
    {
        if (CustomTasks && CommonTasks + ShortTasks + LongTasks > 0) return CommonTasks + ShortTasks + LongTasks;
        var o = GameOptionsManager.Instance.CurrentGameOptions;
        return o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumCommonTasks) + o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumShortTasks)
            + o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumLongTasks);
    }
}

// 仔豚の共通: 仲間が分かる・タスク・勝ち方
public abstract class LittlePigBase : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => TheThreeLittlePigs.PigColor;
    public override string Origin => "SNR";
    public override bool MadeByOthers => true;

    protected abstract int Percent { get; }

    protected bool TaskConditionMet =>
        PortKit.TasksDone(Player) >= Mathf.CeilToInt(TheThreeLittlePigs.AllTasks() * (Percent / 100f));

    public override void OnAssigned()
    {
        if (IsLocal && TheThreeLittlePigs.CustomTasks && TheThreeLittlePigs.CommonTasks + TheThreeLittlePigs.ShortTasks + TheThreeLittlePigs.LongTasks > 0)
            PortKit.After(0.5f, () =>
            {
                if (RoleState.Local == this)
                    SnrKit.AssignOwnTasks(TheThreeLittlePigs.CommonTasks, TheThreeLittlePigs.ShortTasks, TheThreeLittlePigs.LongTasks);
            });
    }

    public override string NameMark(PlayerControl seen)
    {
        var team = TheThreeLittlePigs.TeamOf(PlayerId);
        return seen && team != null && System.Array.IndexOf(team, seen.PlayerId) >= 0 ? $"<color={TheThreeLittlePigs.PigColor}>●</color>" : null;
    }

    // 3 人とも生きている / キルできる人外が全滅しているなら勝ちを横取りする
    public override bool StealsWin(GameResult result) => TeamWins(PlayerId);

    internal static bool TeamWins(byte id)
    {
        var team = TheThreeLittlePigs.TeamOf(id);
        if (team == null) return false;
        bool all = true, any = false;
        foreach (var m in team)
        {
            bool alive = m != 255 && PortKit.Alive(PortKit.ById(m));
            all &= alive;
            any |= alive;
        }
        if (!any) return false;
        return all || !AnyKillerAlive();
    }

    private static bool AnyKillerAlive()
    {
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p)) continue;
            if (PortKit.IsImpostor(p)) return true;
            var r = RoleState.Of(p);
            if (r is { Team: Team.Neutral } && (r.CanKill || r.Faction == Jackal.Pack || r.WinsWithFaction == Jackal.Pack)) return true;
        }
        return false;
    }

    // ホスト: 1 人でも欠けていて、キルできる人外が全滅したらその時に勝つ
    public override void OnFixedUpdate()
    {
        if (!AmongUsClient.Instance.AmHost || MeetingHud.Instance || ExileController.Instance) return;
        var team = TheThreeLittlePigs.TeamOf(PlayerId);
        if (team == null || team[0] != PlayerId && PortKit.Alive(PortKit.ById(team[0]))) return; // 組の中で 1 人だけが見る
        if (!TeamWins(PlayerId)) return;
        bool all = true;
        foreach (var m in team) all &= m != 255 && PortKit.Alive(PortKit.ById(m));
        if (all) return; // 3 人とも生きている時は試合の終わりに横取りする
        var winners = new List<byte>();
        foreach (var m in team) if (m != 255) winners.Add(m);
        GameEnd.WinWith(this, winners);
    }
}

public sealed class TheFirstLittlePig : LittlePigBase
{
    public override Text Name => new("TheFirstLittlePig.Name");
    public override string Reading => "いちばんめのこぶた";
    public override Text Blurb => new("TheFirstLittlePig.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("TheFirstLittlePig.Flash") };
    protected override int Percent => TheThreeLittlePigs.FirstPercent;

    private float _timer;

    public override void OnFixedUpdate()
    {
        base.OnFixedUpdate();
        if (!IsLocal || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance) return;
        var hud = Vanilla.Hud;
        if (hud && hud.IsIntroDisplayed) return;
        _timer -= Time.fixedDeltaTime;
        if (_timer > 0f) return;
        _timer = TheThreeLittlePigs.FirstCustomTimer ? TheThreeLittlePigs.FirstFlashTimer
            : Mathf.Max(5f, GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
        if (TaskConditionMet) PortKit.Flash(new Color32(245, 95, 71, 255), 2.5f);
    }
}

public sealed class TheSecondLittlePig : LittlePigBase
{
    public override Text Name => new("TheSecondLittlePig.Name");
    public override string Reading => "にばんめのこぶた";
    public override Text Blurb => new("TheSecondLittlePig.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("TheSecondLittlePig.Guard") };
    protected override int Percent => TheThreeLittlePigs.SecondPercent;

    private int _used;

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!e.Target || e.Target.PlayerId != PlayerId || e.Killer == e.Target || _used >= TheThreeLittlePigs.SecondGuards || !TaskConditionMet) return;
        _used++;
        e.Cancel("second little pig guard");
        PortKit.HostBlocked(e.Killer, e.Target);
    }
}

public sealed class TheThirdLittlePig : LittlePigBase
{
    public override Text Name => new("TheThirdLittlePig.Name");
    public override string Reading => "さんばんめのこぶた";
    public override Text Blurb => new("TheThirdLittlePig.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("TheThirdLittlePig.Counter") };
    protected override int Percent => TheThreeLittlePigs.ThirdPercent;

    private int _used;

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!e.Target || e.Target.PlayerId != PlayerId || e.Killer == e.Target || _used >= TheThreeLittlePigs.ThirdCounters || !TaskConditionMet) return;
        _used++;
        e.Cancel("third little pig counter");
        var killer = e.Killer;
        PortKit.HostBlocked(killer, e.Target);
        if (PortKit.Alive(killer)) PortKit.After(0.1f, () => { if (PortKit.Alive(killer)) PortKit.HostSuicide(killer); });
    }
}
