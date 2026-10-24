using System;
using System.Text;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Commands;

// 誰でも使える、試合や設定を見るだけのコマンド。どれも手元の情報を出すだけで何も送らない
internal static class InfoCommands
{
    private static bool InGame => ShipStatus.Instance && GameData.Instance && PlayerControl.LocalPlayer;

    // 死んだ人だけが使える (生きている人に死因を見せない)。番号を付けるとその人の死因
    public static string Death(string args)
    {
        if (!InGame) return new Text("InfoCommands.OnlyDuringAGameSeeLast");
        var me = PlayerControl.LocalPlayer;
        if (me.Data == null || !me.Data.IsDead) return new Text("InfoCommands.OnlyAfterYouDie");
        byte pid = me.PlayerId;
        if (args.Length > 0 && !byte.TryParse(args, out pid)) return new Text("InfoCommands.NoPlayerSeeId").With("arg", args);
        var info = GameData.Instance.GetPlayerById(pid);
        if (info == null) return new Text("InfoCommands.NoPlayerSeeId").With("arg", args);
        if (!info.IsDead) return new Text("InfoCommands.IsAlive").With("name", info.PlayerName);
        var d = MatchLog.Of(pid);
        return d == null ? new Text("InfoCommands.CauseUnknown").With("name", info.PlayerName)
                         : $"{info.PlayerName}: {MatchLog.Describe(d)}";
    }

    public static string LastResult(string _) => MatchLog.LastResult ?? new Text("InfoCommands.NoGamePlayedYet");

    public static string Winners(string _) => MatchLog.LastWinners ?? new Text("InfoCommands.NoGamePlayedYet");

    public static string MyRole(string _)
    {
        if (!InGame) return new Text("InfoCommands.OnlyDuringAGame");
        var role = RoleState.Local;
        if (role != null)
            return RoleDoc.ChatInked($"{RoleDoc.ChatTitle(role, null)}{RoleDisplay.LabelOf(role)[role.ColoredName.Length..]}\n{RoleDoc.Chat(role)}{RoleDisplay.AddonLines(role.PlayerId)}");
        return Dev.DevGod.Label(PlayerControl.LocalPlayer.PlayerId) ?? new Text("InfoCommands.NoRole");
    }

    // 引数なし = MRP の役職の一覧 / 役職名 = その役職の説明
    public static string Roles(string args)
    {
        if (args.Length > 0)
        {
            var r = HostActions.FindRole(args);
            if (r == null)
            {
                var a = FindAddon(args);
                if (a != null) return RoleDoc.ChatInked($"{RoleDoc.ChatTitle(a, new Text("InfoCommands.Addon"))}\n{RoleDoc.Chat(a)}");
                return new Text("InfoCommands.NoRoleCalled").With("arg", args);
            }
            return RoleDoc.ChatInked($"{RoleDoc.ChatTitle(r, TeamName(r.Team))}\n{RoleDoc.Chat(r)}");
        }
        var sb = new StringBuilder();
        foreach (Team team in Enum.GetValues(typeof(Team)))
        {
            var line = new StringBuilder();
            foreach (var r in Registry.Roles)
            {
                if (r.Team != team) continue;
                if (line.Length > 0) line.Append(", ");
                line.Append(r.ColoredName);
            }
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(TeamName(team)).Append(": ").Append(line);
        }
        if (Registry.Addons.Count > 0)
        {
            var line = new StringBuilder();
            foreach (var a in Registry.Addons)
            {
                if (line.Length > 0) line.Append(", ");
                line.Append(a.ColoredName);
            }
            sb.Append('\n').Append(new Text("InfoCommands.Addons").ToString()).Append(": ").Append(line);
        }
        sb.Append('\n').Append(new Text("InfoCommands.DetailsHint").ToString());
        return sb.ToString();
    }

    internal static AddonBase FindAddon(string s)
    {
        foreach (var a in Registry.Addons)
        {
            if (a.Name.Matches(s, a.Id))
                return a;
        }
        return null;
    }

    internal static string TeamName(Team t) => t switch
    {
        Team.Crew => new Text("InfoCommands.Crew"),
        Team.Impostor => new Text("InfoCommands.Impostor"),
        _ => new Text("InfoCommands.Neutral"),
    };

    // 今の設定で配役を何度も試し、役職ごとの出る割合と 1 回分の例を出す。人数を省くと今の部屋の人数。
    // 本番と同じ配り方を手元で回すだけ (乱数は毎回変わる)。数千回を 1 フレームで回すのでロビーでだけ
    private const int SimBudget = 150000; // 人数 × 回数の上限 (15 人 10000 回で 1 フレーム約 75ms)

    // 今の設定で players 人の配役を runs 回試す (人数と回数は上限で切る)。ロビーで通常のモードの時だけ。できない時は null と理由
    internal static AssignPlan.SimResult RunSim(ref int players, ref int runs, out int shownMin, out int shownMax, out Text error)
    {
        shownMin = shownMax = 0;
        error = default;
        if (ShipStatus.Instance) { error = new Text("InfoCommands.SimLobbyOnly"); return null; }
        if (!GameModeSwitch.IsNormal) { error = new Text("InfoCommands.SimNormalOnly"); return null; }
        var opts = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opts == null) { error = new Text("InfoCommands.SimLobbyOnly"); return null; }
        players = Math.Clamp(players, 4, Crowded.MaxPlayers);
        runs = Math.Clamp(runs, 100, Math.Max(100, Math.Min(10000, SimBudget / players)));
        // インポスターの数は本番の数え始め (TeamSizes.OnBegin) と同じ: 人数の帯があればその幅、無ければ「最少」〜本編の設定の数を
        // 部屋の上限で切って引き、引いた数を本編がその人数の上限で切る
        int max, min;
        if (TeamSizes.For(players) is { } band) { max = band.MaxImpostors; min = band.MinImpostors; }
        else { max = opts.NumImpostors; min = ImpostorRange.Min.Value; }
        max = Math.Clamp(max, 1, Math.Max(1, ImpostorCap.MaxFor(ImpostorCap.RoomSize(opts))));
        min = Math.Clamp(min, 1, max);
        int cap = ImpostorCap.CapAt(players);
        shownMin = Math.Min(min, cap);
        shownMax = Math.Min(max, cap);
        return AssignPlan.Run(players, min, max, runs, Environment.TickCount, impostorCap: cap);
    }

    public static string Sim(string args)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int players = a.Length > 0 && int.TryParse(a[0], out int p) ? p : TeamSizes.PlayerCount();
        int runs = a.Length > 1 && int.TryParse(a[1], out int r) ? r : 1000;
        var res = RunSim(ref players, ref runs, out int shownMin, out int shownMax, out var error);
        if (res == null) return error;

        var sb = new StringBuilder();
        string imps = shownMin == shownMax ? shownMin.ToString() : new Text("InfoCommands.SimRange").With("min", shownMin).With("max", shownMax).ToString();
        sb.Append(new Text("InfoCommands.SimHead").With("players", players).With("runs", runs).With("impostors", imps).ToString());
        var order = new int[res.RoleRuns.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) => res.RoleRuns[y].CompareTo(res.RoleRuns[x]));
        foreach (int i in order)
        {
            if (res.RoleRuns[i] == 0) break;
            sb.Append('\n').Append(Registry.Roles[i].ColoredName).Append(' ').Append(Percent(res.RoleRuns[i], runs));
            // 1 試合に 2 人以上出ることがある役職は、出た試合での平均の人数も
            if (res.RoleHits[i] > res.RoleRuns[i])
                sb.Append(' ').Append(new Text("InfoCommands.SimAvg").With("n", ((double)res.RoleHits[i] / res.RoleRuns[i]).ToString("0.0")).ToString());
        }
        var addons = new StringBuilder();
        for (int i = 0; i < res.AddonHits.Length; i++)
        {
            if (res.AddonHits[i] == 0) continue;
            if (addons.Length > 0) addons.Append(", ");
            addons.Append(Registry.Addons[i].ColoredName).Append(' ').Append(((double)res.AddonHits[i] / runs).ToString("0.0"));
        }
        if (addons.Length > 0) sb.Append('\n').Append(new Text("InfoCommands.SimAddons").ToString()).Append(": ").Append(addons);
        sb.Append('\n').Append(new Text("InfoCommands.SimTotals")
            .With("min", res.NeutralMin).With("max", res.NeutralMax).With("roles", ((double)res.WithRole / runs).ToString("0.0")).ToString());

        // 例 = 最後の 1 回 (番号は試走の中だけの席の番号で、部屋の人とは関係ない)
        sb.Append('\n').Append(new Text("InfoCommands.SimExample").ToString());
        for (int i = 0; i < players; i++)
        {
            var pick = res.Example?.Find(x => x.PlayerId == i);
            string role = pick != null && pick.RoleIndex >= 0 ? Registry.Roles[pick.RoleIndex].ColoredName
                : TeamName(res.ExampleImpostor[i] ? Team.Impostor : Team.Crew);
            sb.Append(i == 0 ? " " : " / ").Append(i + 1).Append(": ").Append(role);
            if (pick != null)
                foreach (int ad in pick.Addons) sb.Append(" +").Append(Registry.Addons[ad].ColoredName);
        }
        return sb.ToString();
    }

    internal static string Percent(int n, int of)
    {
        double v = 100.0 * n / of;
        return (v >= 10 || v == 0 ? v.ToString("0") : v.ToString("0.#")) + "%";
    }

    // 今の MRP の設定: 役職以外の設定と、出る役職 (出現率 > 0) だけ
    public static string Now(string _)
    {
        var sb = new StringBuilder();
        foreach (Tab tab in Enum.GetValues(typeof(Tab)))
        {
            foreach (var sec in Registry.SectionsOf(tab))
            {
                if (sec.Role != null)
                {
                    var r = sec.Role;
                    if (r.Chance == null || r.Chance.Value <= 0) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(r.ColoredName);
                    foreach (var o in sec.Opts)
                        if (!o.Blank) sb.Append(" / ").Append(o.Label.ToString()).Append(' ').Append(o.Display());
                    continue;
                }
                if (!sec.Opts.Exists(o => o.Shown)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("― ").Append(sec.Title.ToString()).Append(" ―");
                foreach (var o in sec.Opts)
                    if (o.Shown) sb.Append('\n').Append(o.Label.ToString()).Append(": ").Append(o.Display());
            }
        }
        return sb.ToString();
    }
}
