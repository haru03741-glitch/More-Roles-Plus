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

    public static Death Of(byte pid) => Deaths.TryGetValue(pid, out var d) ? d : null;

    public static void Reset() => Deaths.Clear();

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

    public static void OnLeft(ClientData c)
    {
        var p = c?.Character;
        if (!p || !ShipStatus.Instance || p.Data == null || p.Data.IsDead) return;
        Deaths[p.PlayerId] = new Death { Kind = Kind.Left };
    }

    public static string Describe(Death d) => d.Kind switch
    {
        Kind.Killed => new Text($"{d.Killer} ({d.KillerRole}) に倒された", $"Killed by {d.Killer} ({d.KillerRole})"),
        Kind.Suicide => new Text("倒した人なしで死んだ", "Died with no killer"),
        Kind.Exiled => new Text("追放された", "Ejected"),
        _ => new Text("切断した", "Disconnected"),
    };

    // 役職の名前 (色付き)。死んだ人の本編の役職は幽霊の役職に替わっているので、生きていた時の役職を出す
    private static string RoleLabel(NetworkedPlayerInfo p)
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

    // 試合が終わった瞬間 (役職と勝者がまだ残っている間) に結果の文を作る
    public static void CaptureResult()
    {
        var data = GameData.Instance;
        if (!data) return;
        // MRP が勝者を決めた試合 (第三陣営・廃村) はその番号で、本編の判定のまま終わった試合は本編の勝者の名前で見る
        var last = GameEnd.Last;
        var winnerNames = new HashSet<string>();
        var wsb = new StringBuilder();
        if (last != null && last.Draw) wsb.Append(new Text("廃村 (勝者なし)", "Aborted (no winners)").ToString());
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
        LastWinners = wsb.Length > 0 ? wsb.ToString() : new Text("勝者なし", "No winners");

        var sb = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Won(p) ? "★ " : "　 ");
            sb.Append(p.PlayerName).Append(": ").Append(RoleLabel(p));
            // 蘇生された人の死因は残っているので、今の生死を先に見る
            var d = Of(p.PlayerId);
            if (!p.IsDead && !p.Disconnected) sb.Append(" / ").Append(new Text("生存", "Alive").ToString());
            else if (d != null) sb.Append(" / ").Append(Describe(d));
            else if (p.Disconnected) sb.Append(" / ").Append(new Text("切断した", "Disconnected").ToString());
        }
        LastResult = sb.ToString();
    }
}

[HarmonyPatch(typeof(AmongUsClient), "OnGameEnd")]
internal static class MatchLogEndPatch
{
    public static void Postfix()
    {
        try { MatchLog.CaptureResult(); }
        catch (System.Exception e) { Plugin.Logger.LogError($"match log: {e}"); }
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class MatchLogLeftPatch
{
    public static void Prefix([HarmonyArgument(0)] ClientData data) => MatchLog.OnLeft(data);
}
