using System;
using System.Collections.Generic;
using HarmonyLib;

namespace MoreRolesPlus.Roles;

// ---- 起きる前 (ホストの端末でだけ起きる・受け手が止めたり書き換えたりできる) ----

// ホストがキルを通す前 (本編のインポスターのキルも、インポスター以外のキル役のキルも)。PlayerId = 狙われた人。
// Cancel するとキルは起きず、キルした人の待ち時間もそのまま
public sealed class BeforeMurderEvent : CancelableEvent, IPlayerEvent
{
    public PlayerControl Killer { get; internal set; }
    public PlayerControl Target { get; internal set; }
    public byte PlayerId => Target.PlayerId;

    // 誰も受けていなければ作らずに済ませる。断った理由を返す (通したら null)
    internal static string Ask(PlayerControl killer, PlayerControl target)
    {
        if (!Events<BeforeMurderEvent>.Any) return null;
        return Events<BeforeMurderEvent>.Run(new BeforeMurderEvent { Killer = killer, Target = target }).Reason;
    }
}

// 会議の票 1 つ。Target = 入れた先の番号 (スキップは VoteTallyEvent.Skip)
public readonly struct Vote
{
    public readonly byte Voter, Target;
    public Vote(byte voter, byte target) { Voter = voter; Target = target; }
    public override string ToString() => $"{Voter}>{(Target == VoteTallyEvent.Skip ? "skip" : Target.ToString())}";
}

// ホストが会議の票を数える時 (追放する人を決める前)。Votes = 数える票 1 つずつで、重み (ModifyVoteWeight) は
// 同じ票を並べた数で入っている。書き換えると追放される人と、結果の画面の票の印がそれに従う。ホストの端末でだけ起きる
public sealed class VoteTallyEvent : GameEvent
{
    public const byte Skip = 253;

    public readonly List<Vote> Votes;
    internal VoteTallyEvent(List<Vote> votes) => Votes = votes;

    // その人の投票先 (入れていなければ 255)
    public byte TargetOf(byte voter)
    {
        foreach (var v in Votes) if (v.Voter == voter) return v.Target;
        return 255;
    }

    // その人の票の数
    public int WeightOf(byte voter)
    {
        int n = 0;
        foreach (var v in Votes) if (v.Voter == voter) n++;
        return n;
    }

    public void Add(byte voter, byte target) => Votes.Add(new Vote(voter, target));

    // その人の票を全部消す。消した数を返す
    public int Remove(byte voter) => Votes.RemoveAll(v => v.Voter == voter);

    // その人の票を全部 target へ向け直す (代わりに入れる)。入れていなかった人は 1 票足す
    public void Redirect(byte voter, byte target)
    {
        int n = Remove(voter);
        for (int i = Math.Max(n, 1); i > 0; i--) Add(voter, target);
    }

    // その人の票の数を weight にする (投票先はそのまま・入れていない人には何もしない)
    public void SetWeight(byte voter, int weight)
    {
        byte target = TargetOf(voter);
        if (target == 255) return;
        Remove(voter);
        for (int i = 0; i < weight; i++) Add(voter, target);
    }
}

// ---- 試合で起きた事 (どれも全員の端末で起きる) ----

// 誰かが倒された。PlayerId = 倒された人 ([OnlyMine] なら自分が倒された時)
public sealed class PlayerMurderedEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Killer { get; internal set; }
    public PlayerControl Target { get; internal set; }
    public byte PlayerId => Target.PlayerId;
}

// 会議で追放された
public sealed class PlayerExiledEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Player { get; internal set; }
    public byte PlayerId => Player.PlayerId;
}

// 会議が始まった (投票の画面が出た)
public sealed class MeetingStartEvent : GameEvent { }

// 会議が終わって歩けるようになった。Exiled = 追放された人 (いなければ null)
public sealed class MeetingEndEvent : GameEvent
{
    public NetworkedPlayerInfo Exiled { get; internal set; }
}

// 試合が終わった。Result = 第三陣営がいる試合で配られた結果 (本編の判定のまま終わった試合は null)
public sealed class GameEndEvent : GameEvent
{
    public GameResult Result { get; internal set; }
}

// 会議が呼ばれた (死体の通報か緊急ボタン・会議の画面が出る前)。Reporter = 呼んだ人。
// Body = 通報された死体の人 (緊急ボタンなら null)。PlayerId = 呼んだ人
public sealed class MeetingCalledEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Reporter { get; internal set; }
    public NetworkedPlayerInfo Body { get; internal set; }
    public bool IsEmergency => Body == null;
    public byte PlayerId => Reporter.PlayerId;
}

// タスクを 1 つ終えた。TaskId = 終えたタスクの番号 (Player.myTasks の PlayerTask.Id)
public sealed class TaskCompletedEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Player { get; internal set; }
    public uint TaskId { get; internal set; }
    public byte PlayerId => Player.PlayerId;
}

// ベントに入った (入る動きの始まり)。VentId = 入ったベントの番号
public sealed class VentEnteredEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Player { get; internal set; }
    public int VentId { get; internal set; }
    public byte PlayerId => Player.PlayerId;
}

// ベントから出た (出る動きの始まり)。VentId = 出たベントの番号
public sealed class VentExitedEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Player { get; internal set; }
    public int VentId { get; internal set; }
    public byte PlayerId => Player.PlayerId;
}

// 地形が壊れた (結果を適用した直後)。PlayerId = 壊した人 (マップの出来事などは Terrain.TerrainApi.World = 255)。
// Kind = 爆発 / 打撃。Position = 爆心 / 叩いた点。Radius = 爆発の半径 (打撃は 0)。WallsCut = 切り取った壁の線の数 (0 = 何も抜けなかった)
public sealed class TerrainBrokenEvent : GameEvent, IPlayerEvent
{
    public byte PlayerId { get; internal set; }
    public Terrain.DamageKind Kind { get; internal set; }
    public UnityEngine.Vector2 Position { get; internal set; }
    public float Radius { get; internal set; }
    public int WallsCut { get; internal set; }
}

// ---- 本編の入口 ----

// 本編のキルの確かめ (ホストだけが呼ぶ)。本編の距離・待ち時間の確かめより前なので、受け手は「本当には起きなかった
// キル」でも呼ばれうる (断るだけにして、回数を減らす等はキルが通った後の PlayerMurderedEvent で)
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class BeforeMurderVanillaPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost || !__instance || !target) return true;
        string why;
        try { why = BeforeMurderEvent.Ask(__instance, target); }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"before murder: {e}");
            return true;
        }
        if (why == null) return true;
        Plugin.Logger.LogInfo($"kill refused {__instance.PlayerId}->{target.PlayerId}: {why}");
        return false;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class MurderEventPatch
{
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] MurderResultFlags resultFlags)
    {
        KillAbility.OnMurder(__instance);
        if (!target || (resultFlags & MurderResultFlags.Succeeded) == 0) return;
        MatchLog.OnMurder(__instance, target);
        if (RoleState.All.Count == 0) return;
        Events<PlayerMurderedEvent>.Run(new PlayerMurderedEvent { Killer = __instance, Target = target });
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
internal static class ExiledEventPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        // 船外へ吸い出された人は本編の追放と同じ死に方を借りるだけ (追放の出来事は起こさない)
        if (__instance && __instance.PlayerId == Terrain.CrewGrip.Spacing)
        {
            MatchLog.OnSpaced(__instance);
            return;
        }
        // 観戦の人を幽霊にする時も追放の処理を借りるだけ
        if (__instance && __instance.PlayerId == Spectate.Applying)
        {
            MatchLog.OnSpectate(__instance);
            return;
        }
        MatchLog.OnExiled(__instance);
        if (RoleState.All.Count == 0) return;
        Events<PlayerExiledEvent>.Run(new PlayerExiledEvent { Player = __instance });
    }
}

// 本編は通報も緊急ボタンもホストが StartMeeting を全員の端末で呼ばせる
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class MeetingCalledEventPatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target)
    {
        if (!RoleState.Any || !__instance) return;
        Events<MeetingCalledEvent>.Run(new MeetingCalledEvent { Reporter = __instance, Body = target });
    }
}

// 本人の端末でも、電文を受けた他の端末でも CompleteTask が呼ばれる
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
internal static class TaskCompletedEventPatch
{
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] uint idx)
    {
        if (!RoleState.Any || !__instance) return;
        Events<TaskCompletedEvent>.Run(new TaskCompletedEvent { Player = __instance, TaskId = idx });
    }
}

// ベントの出入りの動きは全員の端末で CoEnterVent / CoExitVent から始まる
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoEnterVent))]
internal static class VentEnteredEventPatch
{
    public static void Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] int id)
    {
        if (!RoleState.Any || !__instance) return;
        var p = __instance.myPlayer;
        if (!p) return;
        Events<VentEnteredEvent>.Run(new VentEnteredEvent { Player = p, VentId = id });
    }
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoExitVent))]
internal static class VentExitedEventPatch
{
    public static void Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] int id)
    {
        if (!RoleState.Any || !__instance) return;
        var p = __instance.myPlayer;
        if (!p) return;
        Events<VentExitedEvent>.Run(new VentExitedEvent { Player = p, VentId = id });
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class MeetingStartEventPatch
{
    public static void Postfix()
    {
        if (RoleState.All.Count == 0) return;
        MeetingEndWatch.Begin();
        Events<MeetingStartEvent>.Run(new MeetingStartEvent());
    }
}

// 会議の終わり = 会議の画面と追放の演出が両方なくなった時。会議の始まりから終わりまでの間だけ毎 FixedUpdate 見る。
// 追放の演出の後に操作を戻す本編の関数 (ExileController.ReEnableGameplay) には当てない: this を使わない小さな関数で、
// Android のビルドでは this に正しい値が来ず、パッチの中継が型を調べる所で落ちる
internal static class MeetingEndWatch
{
    private const int QuietTicks = 10; // 両方なくなってから 0.2 秒待つ (会議の画面と追放の演出の入れ替わりの隙間を拾わない)

    private static bool _active;
    private static int _quiet;
    private static NetworkedPlayerInfo _exiled;

    public static void Begin()
    {
        _active = true;
        _quiet = 0;
        _exiled = null;
    }

    public static void Reset() => _active = false;

    public static void Tick()
    {
        if (!_active) return;
        if (RoleState.All.Count == 0) { _active = false; return; }
        var exile = ExileController.Instance;
        if (MeetingHud.Instance || exile)
        {
            _quiet = 0;
            if (exile && _exiled == null) _exiled = exile.initData?.networkedPlayer;
            return;
        }
        if (++_quiet < QuietTicks) return;
        _active = false;
        Events<MeetingEndEvent>.Run(new MeetingEndEvent { Exiled = _exiled });
        _exiled = null;
    }
}
