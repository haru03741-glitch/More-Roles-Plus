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
        if (role != null) return $"{RoleDisplay.LabelOf(role)}\n{role.Help}{RoleDisplay.AddonLines(role.PlayerId)}";
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
                if (a != null) return $"{a.ColoredName} ({new Text("InfoCommands.Addon")})\n{a.Help}";
                return new Text("InfoCommands.NoRoleCalled").With("arg", args);
            }
            return $"{r.ColoredName} ({TeamName(r.Team)})\n{r.Help}";
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

    private static string TeamName(Team t) => t switch
    {
        Team.Crew => new Text("InfoCommands.Crew"),
        Team.Impostor => new Text("InfoCommands.Impostor"),
        _ => new Text("InfoCommands.Neutral"),
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
