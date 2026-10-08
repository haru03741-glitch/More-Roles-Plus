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
        if (!InGame) return new Text("試合中だけ使えます (前の試合の死因は /l)", "Only during a game (see /l for the last game)");
        var me = PlayerControl.LocalPlayer;
        if (me.Data == null || !me.Data.IsDead) return new Text("死んでから使えます", "Only after you die");
        byte pid = me.PlayerId;
        if (args.Length > 0 && !byte.TryParse(args, out pid)) return new Text($"{args} 番の人はいません (/id で番号を確認)", $"No player {args} (see /id)");
        var info = GameData.Instance.GetPlayerById(pid);
        if (info == null) return new Text($"{args} 番の人はいません (/id で番号を確認)", $"No player {args} (see /id)");
        if (!info.IsDead) return new Text($"{info.PlayerName} は生きています", $"{info.PlayerName} is alive");
        var d = MatchLog.Of(pid);
        return d == null ? new Text($"{info.PlayerName}: 死因が分かりません", $"{info.PlayerName}: cause unknown")
                         : $"{info.PlayerName}: {MatchLog.Describe(d)}";
    }

    public static string LastResult(string _) => MatchLog.LastResult ?? new Text("まだ試合の記録がありません", "No game played yet");

    public static string Winners(string _) => MatchLog.LastWinners ?? new Text("まだ試合の記録がありません", "No game played yet");

    public static string MyRole(string _)
    {
        if (!InGame) return new Text("試合中だけ使えます", "Only during a game");
        var role = RoleState.Local;
        if (role != null) return $"{RoleDisplay.LabelOf(role)}\n{role.Description}{RoleDisplay.AddonLines(role.PlayerId)}";
        return Dev.DevGod.Label(PlayerControl.LocalPlayer.PlayerId) ?? new Text("役職がありません", "No role");
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
                if (a != null) return $"{a.ColoredName} ({new Text("アドオン", "Addon")})\n{a.Description}";
                return new Text($"{args} という役職はありません", $"No role called {args}");
            }
            return $"{r.ColoredName} ({TeamName(r.Team)})\n{r.Description}";
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
            sb.Append('\n').Append(new Text("アドオン", "Addons").ToString()).Append(": ").Append(line);
        }
        sb.Append('\n').Append(new Text("(/r <役職名・アドオン名> で説明)", "(/r <role or addon> for details)").ToString());
        return sb.ToString();
    }

    private static AddonBase FindAddon(string s)
    {
        foreach (var a in Registry.Addons)
        {
            if (string.Equals(a.Id, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name.Ja, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name.En, s, StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return null;
    }

    private static string TeamName(Team t) => t switch
    {
        Team.Crew => new Text("クルー", "Crew"),
        Team.Impostor => new Text("インポスター", "Impostor"),
        _ => new Text("第三陣営", "Neutral"),
    };

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
                    foreach (var o in sec.Opts) sb.Append(" / ").Append(o.Label.ToString()).Append(' ').Append(o.Display());
                    continue;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("― ").Append(sec.Title.ToString()).Append(" ―");
                foreach (var o in sec.Opts) sb.Append('\n').Append(o.Label.ToString()).Append(": ").Append(o.Display());
            }
        }
        return sb.ToString();
    }
}
