using AmongUs.GameOptions;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using InnerNet;

namespace MoreRolesPlus.Roles;

// 試合の死因と前の試合の結果。全員が MRP を入れているので、殺害・追放・切断はどの端末でも同じように起きる。
// 各自の端末で記録し、/death /l /win で見る (送らない)
internal static class MatchLog
{
    public enum Kind : byte
    {
        Killed,
        Suicide,
        Exiled,
        Left,
        Spaced,
        Spectator,
    }

    public sealed class Death
    {
        public Kind Kind;
        public string Killer;     // 倒した人の名前 (倒された時だけ)
        public string KillerRole; // 倒した時の役職 (色付き)
    }

    private static readonly Dictionary<byte, Death> Deaths = new();

    // 前の試合 (無ければ null)
    public static string LastResult { get; private set; }
    public static string LastWinners { get; private set; }
    public static int ResultGen { get; private set; } // 結果を作るたびに 1 増える

    public static Death Of(byte pid) => Deaths.TryGetValue(pid, out var d) ? d : null;

    public static void Reset()
    {
        Deaths.Clear();
        RoleTrail.Clear();
        _trailOn = false;
    }

    // 試合中に役職が変わった人の移り変わり (色付きの役職名・古い順)。配り始めの途中の変化を数えないよう、
    // イントロの後の役職を起点にする。記録するのは MRP の付け直し (RoleAssigner.Apply) の後だけ:
    // 陣営をまたぐ付け直しは本編の役職が先に替わり、客では別の電文で届くので、本編の付け直しで記録すると
    // 途中の「インポスター」などが挟まる。幽霊になったことは別の欄 (GhostRoleLabel) に出すので数えない
    private const int TrailMax = 8;
    private static readonly Dictionary<byte, List<string>> RoleTrail = new();
    private static bool _trailOn;

    // 試合の終わり (CaptureResult) か次の試合のイントロ (Reset) まで、起点は取り直さない
    public static void StartTrail()
    {
        if (_trailOn) return;
        RoleTrail.Clear();
        _trailOn = true;
        var data = GameData.Instance;
        if (!data) return;
        foreach (var p in data.AllPlayers)
            if (p != null) NoteRole(p.PlayerId);
    }

    public static void NoteRole(byte pid)
    {
        if (!_trailOn) return;
        var d = GameData.Instance ? GameData.Instance.GetPlayerById(pid) : null;
        // MRP の役職の無い人は死ぬと本編の役職が幽霊に替わるので、死んだ後は見ない
        if (d == null || (d.IsDead && RoleState.Of(pid) == null)) return;
        string label = Dev.DevGod.Label(pid);
        if (label == null) return;
        if (!RoleTrail.TryGetValue(pid, out var trail)) RoleTrail[pid] = trail = new List<string>();
        if ((trail.Count == 0 || trail[^1] != label) && trail.Count < TrailMax) trail.Add(label);
    }

    // 今の役職より前の役職を「A → B → 」の形で (変わっていなければ空)
    private static string TrailPrefix(byte pid)
    {
        if (!RoleTrail.TryGetValue(pid, out var trail) || trail.Count < 2) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < trail.Count - 1; i++) sb.Append(trail[i]).Append(" → ");
        return sb.ToString();
    }

    public static void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (!target) return;
        bool self = !killer || killer.PlayerId == target.PlayerId;
        Deaths[target.PlayerId] = new Death
        {
            Kind = self ? Kind.Suicide : Kind.Killed,
            Killer = self ? null : killer.Data?.PlayerName,
            KillerRole = self ? null : Dev.DevGod.Label(killer.PlayerId),
        };
    }

    public static void OnExiled(PlayerControl p)
    {
        if (p) Deaths[p.PlayerId] = new Death { Kind = Kind.Exiled };
    }

    public static void OnSpectate(PlayerControl p)
    {
        if (p) Deaths[p.PlayerId] = new Death { Kind = Kind.Spectator };
    }

    public static void OnSpaced(PlayerControl p)
    {
        if (p) Deaths[p.PlayerId] = new Death { Kind = Kind.Spaced };
    }

    public static void OnLeft(ClientData c)
    {
        var p = c?.Character;
        if (!p || !ShipStatus.Instance || p.Data == null || p.Data.IsDead) return;
        Deaths[p.PlayerId] = new Death { Kind = Kind.Left };
    }

    public static string Describe(Death d) => d.Kind switch
    {
        Kind.Killed => new Text("MatchLog.KilledBy").With("killer", d.Killer).With("killerRole", d.KillerRole),
        Kind.Suicide => new Text("MatchLog.DiedWithNoKiller"),
        Kind.Exiled => new Text("MatchLog.Ejected"),
        Kind.Spaced => new Text("MatchLog.SuckedOutIntoSpace"),
        Kind.Spectator => new Text("MatchLog.Spectating"),
        _ => new Text("MatchLog.Disconnected"),
    };

    // 役職の名前 (色付き)。死んだ人の本編の役職は幽霊の役職に替わっているので、生きていた時の役職を出す
    private static string RoleLabel(NetworkedPlayerInfo p) => RoleLabelBase(p) + (RoleState.Of(p.PlayerId) == null && p.IsDead ? RoleDisplay.AddonSuffix(p.PlayerId) : "");

    private static string RoleLabelBase(NetworkedPlayerInfo p)
    {
        if (RoleState.Of(p.PlayerId) != null || !p.IsDead) return Dev.DevGod.Label(p.PlayerId) ?? "?";
        var alive = Dev.DevCommands.RoleWhenAlive(p);
        if (!alive.HasValue || RoleManager.IsGhostRole(alive.Value))
            alive = p.Role && p.Role.TeamType == RoleTeamTypes.Impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;
        var roles = RoleManager.Instance.AllRoles;
        for (int i = 0; i < roles.Count; i++)
        {
            var r = roles[i];
            if (r && r.Role == alive.Value)
                return $"<color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(r.TeamColor)}>{r.NiceName}</color>";
        }
        return alive.Value.ToString();
    }

    // 死んだ後に付いた特別な幽霊の役職 (守護天使など)。ただの幽霊なら null
    private static string GhostRoleLabel(NetworkedPlayerInfo p)
    {
        var r = p.Role;
        if (!p.IsDead || !r || !RoleManager.IsGhostRole(r.Role)) return null;
        if (r.Role is RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost) return null;
        return $"<color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(r.TeamColor)}>{r.NiceName}</color>";
    }

    // クルー側のタスクの進み具合 (インポスターと第三陣営の偽のタスクは数えない)
    private static string TaskLabel(NetworkedPlayerInfo p)
    {
        var tasks = p.Tasks;
        if (tasks == null || tasks.Count == 0) return null;
        var mrp = RoleState.Of(p.PlayerId);
        bool crew = mrp != null ? mrp.Team == Team.Crew : p.Role && p.Role.TeamType == RoleTeamTypes.Crewmate;
        if (!crew) return null;
        int done = 0;
        for (int i = 0; i < tasks.Count; i++)
            if (tasks[i] != null && tasks[i].Complete) done++;
        return new Text("MatchLog.Tasks").With("done", done).With("total", tasks.Count);
    }

    // 試合が終わった瞬間 (役職と勝者がまだ残っている間) に結果の文を作る
    public static void CaptureResult()
    {
        var data = GameData.Instance;
        if (!data) return;
        // MRP が勝者を決めた試合 (第三陣営・廃村) はその番号で、本編の判定のまま終わった試合は本編の勝者の名前で見る
        var last = GameEnd.Last;
        var winnerNames = new HashSet<string>();
        var wsb = new StringBuilder();
        if (last != null && last.Draw) wsb.Append(new Text("MatchLog.AbortedNoWinners").ToString());
        else if (last == null && EndGameResult.CachedWinners is { } cached)
        {
            for (int i = 0; i < cached.Count; i++) winnerNames.Add(cached[i].PlayerName);
        }
        bool Won(NetworkedPlayerInfo p) => last != null ? last.Winners.Contains(p.PlayerId) : winnerNames.Contains(p.PlayerName);

        var all = data.AllPlayers;
        if (last == null || !last.Draw)
        {
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || !Won(p)) continue;
                if (wsb.Length > 0) wsb.Append(", ");
                wsb.Append(p.PlayerName);
            }
        }
        LastWinners = wsb.Length > 0 ? wsb.ToString() : new Text("MatchLog.NoWinners");

        var sb = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || Spectate.IsWatching(p.PlayerId)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Won(p) ? "★ " : "　 ");
            sb.Append(p.PlayerName).Append(": ").Append(TrailPrefix(p.PlayerId)).Append(RoleLabel(p));
            string ghost = GhostRoleLabel(p);
            if (ghost != null) sb.Append(" → ").Append(ghost);
            // 蘇生された人の死因は残っているので、今の生死を先に見る
            var d = Of(p.PlayerId);
            if (!p.IsDead && !p.Disconnected) sb.Append(" / ").Append(new Text("MatchLog.Alive").ToString());
            else if (d != null) sb.Append(" / ").Append(Describe(d));
            else if (p.Disconnected) sb.Append(" / ").Append(new Text("MatchLog.Disconnected").ToString());
            string tasks = TaskLabel(p);
            if (tasks != null) sb.Append(" / ").Append(tasks);
        }
        // 観戦の人は役職を持たないので「観戦」とだけ出し、最後にまとめる
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || !Spectate.IsWatching(p.PlayerId)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("　 ").Append(p.PlayerName).Append(": <color=#9A9A9A>").Append(new Text("MatchLog.Spectating").ToString()).Append("</color>");
        }
        LastResult = sb.ToString();
        ResultGen++;
        _trailOn = false;
    }
}

[HarmonyPatch(typeof(AmongUsClient), "OnGameEnd")]
internal static class MatchLogEndPatch
{
    public static void Postfix()
    {
        try { MatchLog.CaptureResult(); }
        catch (System.Exception e) { Plugin.Logger.LogError($"match log: {e}"); }
        try { Commands.JoinLists.OnMatchEnd(GameEnd.Last != null && GameEnd.Last.Draw); }
        catch (System.Exception e) { Plugin.Logger.LogError($"recent games: {e}"); }
    }
}

// イントロの後の役職を移り変わりの起点にする
[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class MatchLogTrailStartPatch
{
    public static void Postfix()
    {
        try { MatchLog.StartTrail(); }
        catch (System.Exception e) { Plugin.Logger.LogError($"role trail: {e}"); }
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class MatchLogLeftPatch
{
    public static void Prefix([HarmonyArgument(0)] ClientData data) => MatchLog.OnLeft(data);
}
