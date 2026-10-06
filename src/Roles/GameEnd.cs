using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 試合の結果。陣営の勝ちなら Team、第三陣営の役職の勝ちなら Role が入る (誰も勝たない時は両方 null)
public sealed class GameResult
{
    public Team? Team { get; internal set; }
    public RoleBase Role { get; internal set; }
    public bool Draw { get; internal set; } // 廃村 (ホストが打ち切った・勝者なし)
    public readonly HashSet<byte> Winners = new();
}

// 第三陣営がいる試合の勝敗。全員が MRP を入れている前提なので、ホストが勝者を決めて 1 通で配り、
// 各端末は終了画面の勝者を差し替えるだけ。第三陣営がいない試合は本編の判定のまま (何も足さない)。
//
// 生き残りの数え方: インポスター / 第三陣営のキル役 (役職ごとに 1 陣営) / それ以外 (クルーとキルしない第三陣営)。
// - キル役がいない: 本編と同じ (インポスター ≥ 残り でインポスター・インポスター 0 でクルー)
// - キル役がいる: インポスターが全滅し、キル役が 1 種類だけ残り、残りがその人数以下ならその役職の勝ち。
//   それまではインポスターの人数勝ちもクルーの全滅勝ちも起きない
public static class GameEnd
{
    private const int CheckIntervalMs = 250;
    private static readonly SystemTypes[] CriticalSystems = { SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.HeliSabotage };

    private static GameResult _pending;   // ホストが決めた結果 (RpcEndGame の前置で配る)
    private static bool _announced;
    internal static GameResult Last;       // 配られた結果 (終了画面で使う)
    internal static bool LocalWon;
    internal static bool LocalNeutral;
    private static string _localColor;   // 終了画面では役職の情報が消えているので、結果を受けた時に写す
    private static long _nextCheckMs;
    private static IntPtr _ship;
    private static LifeSuppSystemType _lifeSupp;
    private static ICriticalSabotage _critical;
    private static readonly Dictionary<string, int> Killers = new();
    internal static readonly HashSet<byte> TestKillers = new(); // テスト用: キル役として数える人
    internal static int Blocked;                                 // 止めた本編の終わりの数 (確認用)

    // 役職から: role の持ち主だけの勝ちで試合を終わらせる。ホストの端末で呼ばれた時だけ効く
    public static void Win(RoleBase role)
    {
        if (role == null || !AmongUsClient.Instance.AmHost || _pending != null) return;
        var r = new GameResult { Role = role };
        r.Winners.Add(role.PlayerId);
        End(r, GameOverReason.ImpostorsByKill);
    }

    internal static void Reset()
    {
        _pending = null;
        _announced = false;
        Last = null;
        LocalWon = LocalNeutral = false;
        _ship = IntPtr.Zero;
        _lifeSupp = null;
        _critical = null;
    }

    // 廃村: 勝者なしで試合を打ち切る (ホストのみ)
    internal static bool Abort()
    {
        if (!AmongUsClient.Instance.AmHost || !GameData.Instance || !ShipStatus.Instance || _pending != null) return false;
        End(new GameResult { Draw = true }, GameOverReason.ImpostorsByKill);
        return true;
    }

    // 結果がもう決まっている (MRP が決めた終わりの途中)
    internal static bool Decided => _pending != null;

    internal static void End(GameResult r, GameOverReason reason)
    {
        _pending = r;
        GameManager.Instance.RpcEndGame(reason, false);
    }

    // ---- ホスト: 終了判定 (本編の CheckEndCriteria の代わり。第三陣営がいる試合だけ) ----

    internal static void Check()
    {
        // 毎 FixedUpdate 呼ばれるので、本編への問い合わせは間引きを通った後だけ
        long now = Environment.TickCount64;
        if (_pending != null || now < _nextCheckMs) return;
        _nextCheckMs = now + CheckIntervalMs;
        if (!AmongUsClient.Instance.AmHost || TutorialManager.InstanceExists || !GameData.Instance || !ShipStatus.Instance) return;

        if (SabotageExpired())
        {
            End(TeamResult(Team.Impostor), GameOverReason.ImpostorsBySabotage);
            return;
        }
        if (GameManager.Instance.CheckTaskCompletion())
        {
            End(TeamResult(Team.Crew), GameOverReason.CrewmatesByTask);
            return;
        }
        var r = Decide(out var reason, out _);
        if (r != null) End(r, reason);
    }

    // 生き残りの数から勝者を決める (決まらなければ null)。info は確認用の内訳 (withInfo の時だけ作る)
    internal static GameResult Decide(out GameOverReason reason, out string info, bool withInfo = false)
    {
        reason = GameOverReason.ImpostorsByKill;
        int imp = 0, crew = 0, nk = 0;
        Killers.Clear();
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected || p.IsDead) continue;
            var role = RoleState.Of(p.PlayerId);
            string killer = TestKillers.Contains(p.PlayerId) ? "test" : role is { Team: Team.Neutral, IsKiller: true } ? role.Id : null;
            if (killer != null)
            {
                Killers.TryGetValue(killer, out int c);
                Killers[killer] = c + 1;
                nk++;
            }
            else if (IsImpostor(p)) imp++;
            else crew++;
        }
        info = !withInfo ? null : $"imp={imp} crew={crew} killers=[{string.Join(",", Killers.Select(k => $"{k.Key}:{k.Value}"))}]";

        if (nk == 0)
        {
            if (crew == 0 && imp == 0) return new GameResult();
            if (crew <= imp) return TeamResult(Team.Impostor);
            if (imp == 0)
            {
                reason = GameOverReason.CrewmatesByVote;
                return TeamResult(Team.Crew);
            }
            return null;
        }
        if (imp > 0 || crew > nk || Killers.Count != 1) return null;

        string id = Killers.Keys.First();
        var r = new GameResult { Role = Registry.Roles.FirstOrDefault(x => x.Id == id) };
        foreach (var role in RoleState.All)
        {
            if (role.Id == id) r.Winners.Add(role.PlayerId);
        }
        foreach (byte pid in TestKillers) r.Winners.Add(pid);
        return r;
    }

    private static bool IsImpostor(NetworkedPlayerInfo p)
    {
        var rb = p.Role;
        return rb && rb.IsImpostor;
    }

    // 陣営の勝ち: その陣営の全員 (死んだ人も)。第三陣営は入れない
    internal static GameResult TeamResult(Team team)
    {
        var r = new GameResult { Team = team };
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected) continue;
            var role = RoleState.Of(p.PlayerId);
            if (role != null && role.Team == Team.Neutral) continue;
            if (IsImpostor(p) == (team == Team.Impostor)) r.Winners.Add(p.PlayerId);
        }
        return r;
    }

    // 酸素・原子炉 (各マップの同じ役目の物) の時間切れ。システムの取り出しは船ごとに 1 回
    private static bool SabotageExpired()
    {
        var ship = ShipStatus.Instance;
        if (_ship != ship.Pointer)
        {
            _ship = ship.Pointer;
            _lifeSupp = null;
            _critical = null;
            var sys = ship.Systems;
            if (sys.ContainsKey(SystemTypes.LifeSupp)) _lifeSupp = sys[SystemTypes.LifeSupp].TryCast<LifeSuppSystemType>();
            foreach (var t in CriticalSystems)
            {
                if (!sys.ContainsKey(t)) continue;
                _critical = sys[t].TryCast<ICriticalSabotage>();
                break;
            }
        }
        if (_lifeSupp != null && _lifeSupp.Countdown <= 0f)
        {
            _lifeSupp.Countdown = LifeSuppSystemType.CountdownStopped;
            return true;
        }
        if (_critical != null && _critical.Countdown <= 0f)
        {
            _critical.ClearSabotage();
            return true;
        }
        return false;
    }

    // ---- ホスト: 終わる直前に結果を配る (本編の判定で終わる時もここを通る) ----

    // 自動の終わりを止めている (テスト用の noend か、人数が足りない練習の試合)
    internal static bool Held => Bridge.LobbyCommands.NoGameEnd || PracticeMatch.Active;

    // 本編が出した終わりを通すか (MRP が決めた終わりは常に通す)
    internal static bool Allows(GameOverReason reason)
    {
        if (_pending != null) return true;
        if (GameEnd.Held) return false;
        if (!RoleState.AnyNeutral) return true;
        return reason switch
        {
            // 人数での勝ちは本編の数え方 (第三陣営をクルーとして数える) なので止め、Check の判定に任せる
            GameOverReason.ImpostorsByKill or GameOverReason.ImpostorsByVote or GameOverReason.CrewmatesByVote => false,
            _ => true,
        };
    }

    internal static void Announce(GameOverReason reason)
    {
        if (_announced) return;
        _announced = true;
        var r = _pending ?? FromReason(reason);
        _pending = r;
        Plugin.Logger.LogInfo($"end reason={reason}");
        foreach (var role in RoleState.All)
        {
            if (r.Draw || r.Winners.Contains(role.PlayerId)) continue;
            try
            {
                if (role.AlsoWins(r)) r.Winners.Add(role.PlayerId);
            }
            catch (Exception e) { Plugin.Logger.LogError($"{role.Id}.AlsoWins: {e}"); }
        }

        Result.Send(r);
        Accept(r);
    }

    private static readonly RemoteCall<GameResult> Result = new("GameEnd.Result", Route.HostToAll,
        (w, r) =>
        {
            w.Write((byte)(r.Draw ? 3 : r.Team == Team.Crew ? 1 : r.Team == Team.Impostor ? 2 : 0));
            w.WritePacked(r.Role == null ? -1 : Registry.Roles.FindIndex(x => x.Id == r.Role.Id));
            w.Write((byte)r.Winners.Count);
            foreach (byte pid in r.Winners) w.Write(pid);
        },
        reader =>
        {
            var r = new GameResult();
            int team = reader.ReadByte();
            if (team == 1) r.Team = Team.Crew;
            else if (team == 2) r.Team = Team.Impostor;
            else if (team == 3) r.Draw = true;
            int idx = reader.ReadPackedInt32();
            if (idx >= 0 && idx < Registry.Roles.Count) r.Role = Registry.Roles[idx];
            int n = reader.ReadByte();
            for (int i = 0; i < n; i++) r.Winners.Add(reader.ReadByte());
            return r;
        },
        (_, r) => Accept(r));

    private static GameResult FromReason(GameOverReason reason) => reason switch
    {
        GameOverReason.CrewmatesByVote or GameOverReason.CrewmatesByTask or GameOverReason.ImpostorDisconnect
            or GameOverReason.HideAndSeek_CrewmatesByTimer => TeamResult(Team.Crew),
        _ => TeamResult(Team.Impostor),
    };

    private static void Accept(GameResult r)
    {
        Last = r;
        var lp = PlayerControl.LocalPlayer;
        LocalWon = lp && r.Winners.Contains(lp.PlayerId);
        LocalNeutral = RoleState.Local?.Team == Team.Neutral;
        _localColor = RoleState.Local?.Color;
        Plugin.Logger.LogInfo($"win: draw={r.Draw} team={r.Team?.ToString() ?? "-"} role={r.Role?.Id ?? "-"} winners=[{string.Join(",", r.Winners)}] localWon={LocalWon}");
    }

    // ---- 全員: 終了画面の勝者を差し替える ----

    // 終了画面の場面では試合の情報が消えているので、試合が終わった瞬間に勝者の写しを作っておく
    private static Il2CppSystem.Collections.Generic.List<CachedPlayerData> _winners;

    internal static void CaptureWinners()
    {
        _winners = null;
        if (Last == null || !GameData.Instance) return;
        _winners = new Il2CppSystem.Collections.Generic.List<CachedPlayerData>();
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p != null && Last.Winners.Contains(p.PlayerId)) _winners.Add(new CachedPlayerData(p));
        }
    }

    internal static void ApplyWinners()
    {
        if (Last == null || _winners == null) return;
        EndGameResult.CachedWinners = _winners;
        _winners = null;
    }

    internal static void ApplyText(EndGameManager egm)
    {
        if (Last == null || !egm) return;
        if (Last.Draw)
        {
            egm.WinText.text = new Text("廃村", "Game Aborted");
            egm.WinText.color = Color.white;
            egm.BackgroundBar.material.color = Color.gray;
            AddSubText(egm, new Text("ホストが試合を打ち切りました", "The host ended the game"), Color.gray);
            return;
        }
        if (Last.Role == null && !LocalNeutral) return; // 陣営の勝ちでクルー / インポスターから見る時は本編の表示が合っている

        var color = Last.Role != null ? RoleDisplay.ParseColor(Last.Role.Color)
            : LocalWon && _localColor != null ? RoleDisplay.ParseColor(_localColor) : Palette.ImpostorRed;
        egm.WinText.text = LocalWon ? new Text("勝利", "Victory") : new Text("敗北", "Defeat");
        egm.WinText.color = LocalWon || Last.Role != null ? color : Palette.ImpostorRed;
        if (Last.Role == null) return;

        egm.BackgroundBar.material.color = color;
        AddSubText(egm, Lang.IsJapanese ? $"{Last.Role.Name}の勝利" : $"{Last.Role.Name} Wins", color);
    }

    // 勝ち負けの文字の下に小さく 1 行足す
    private static void AddSubText(EndGameManager egm, string text, Color color)
    {
        var sub = UnityEngine.Object.Instantiate(egm.WinText.gameObject, egm.WinText.transform.parent);
        sub.name = "MrpWinner";
        sub.transform.localPosition = egm.WinText.transform.localPosition + new Vector3(0f, -0.9f, 0f);
        sub.transform.localScale = egm.WinText.transform.localScale * 0.45f;
        var tmp = sub.GetComponent<TMPro.TextMeshPro>();
        tmp.text = text;
        tmp.color = color;
    }
}

[HarmonyPatch(typeof(LogicGameFlowNormal), nameof(LogicGameFlowNormal.CheckEndCriteria))]
internal static class CheckEndCriteriaPatch
{
    public static bool Prefix()
    {
        if (GameEnd.Held) return false;
        if (!RoleState.AnyNeutral) return true;
        GameEnd.Check();
        return false;
    }
}

// 本編は会議を開く前などに「死者の数で試合が終わっているか」を見て、終わっていれば会議を断る。
// 第三陣営がいる試合では本編の数え方 (第三陣営をクルーとして数える) が合わないので、MRP の判定で答える
[HarmonyPatch(typeof(LogicGameFlowNormal), nameof(LogicGameFlowNormal.IsGameOverDueToDeath))]
internal static class GameOverDueToDeathPatch
{
    public static bool Prefix(ref bool __result)
    {
        if (!RoleState.AnyNeutral && !GameEnd.Held) return true;
        __result = !GameEnd.Held && GameData.Instance && GameEnd.Decide(out _, out _) != null;
        return false;
    }
}

// 試合の終わりは必ずここを通る。第三陣営がいる試合では勝者を配ってから終わらせる。
// 本編の数え方が合わない終わりは上の問いの側で起きないようにしてあるので、ここで止めるのは取りこぼしの安全網
[HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
internal static class RpcEndGamePatch
{
    public static bool Prefix([HarmonyArgument(0)] GameOverReason endReason)
    {
        if (!AmongUsClient.Instance.AmHost) return true;
        if (!GameEnd.Allows(endReason))
        {
            if (GameEnd.Blocked++ == 0) Plugin.Logger.LogInfo($"end blocked: {endReason}");
            return false;
        }
        // 第三陣営がいない試合は本編の判定のまま。廃村など MRP が決めた終わりは常に配る
        if (!RoleState.AnyNeutral && !GameEnd.Decided) return true;
        try { GameEnd.Announce(endReason); }
        catch (Exception e) { Plugin.Logger.LogError($"win announce: {e}"); }
        return true;
    }
}

[HarmonyPatch(typeof(AmongUsClient), "OnGameEnd")]
internal static class OnGameEndPatch
{
    public static void Postfix() => GameEnd.CaptureWinners();
}

[HarmonyPatch(typeof(EndGameManager), "SetEverythingUp")]
internal static class EndScreenPatch
{
    public static void Prefix() => GameEnd.ApplyWinners();

    public static void Postfix(EndGameManager __instance)
    {
        try { GameEnd.ApplyText(__instance); }
        catch (Exception e) { Plugin.Logger.LogWarning($"end screen: {e.Message}"); }
    }
}

// 第三陣営のタスクはクルーのタスク勝利に数えない (偽のタスク)。数え直しの直前に、その人の役職の印を外す
// (死ぬと本編の役職が幽霊に入れ替わって印が戻るので、毎回外す)。
// 数えるタスクが 0 個 (クルー以外が第三陣営だけ) になると、本編は 0 ≦ 0 でタスク完了とみなして終わらせる。
// その判定は会議の前・投票の後など何か所もあり (呼び出し元に埋め込まれていてパッチが届かない所もある)、
// どれもこの合計を読むので、その時は合計を 1 (誰も終えられない分) にしておく
[HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
internal static class NeutralTaskPatch
{
    public static void Prefix()
    {
        if (!RoleState.AnyNeutral) return;
        foreach (var role in RoleState.All)
        {
            if (role.Team != Team.Neutral || !role.Player) continue;
            var rb = role.Player.Data?.Role;
            if (rb) rb.TasksCountTowardProgress = false;
        }
    }

    public static void Postfix(GameData __instance)
    {
        if (RoleState.AnyNeutral && __instance.TotalTasks == 0) __instance.TotalTasks = 1;
    }
}

