using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.Dev;

namespace MoreRolesPlus.Commands;

// コマンドを使える人
internal enum Who
{
    Everyone,
    Host,
    Dev,
}

// 遊ぶ人がチャット欄から打つコマンド (/end のように / を付ける)。打った文は送らず手元で実行し、
// 結果は自分にだけ見える注意書きで出す。他の人に効く操作はホストの端末で行い、本編の RPC か MRP の電文で全員に届く。
// テスト用ブリッジのコマンド (開発者だけ) とは別の表。同じ名前があればこちらが先
internal static class ChatCommands
{
    private sealed class Cmd
    {
        public string[] Names;
        public Who Who;
        public Text UsageText;
        public string Usage => UsageText;
        public Text Help;
        public Func<string, string> Run;
    }

    private static readonly List<Cmd> List = new();
    private static readonly Dictionary<string, Cmd> ByName = new(StringComparer.OrdinalIgnoreCase);

    static ChatCommands()
    {
        Add(new[] { "help", "h" }, Who.Everyone, default, new Text("Command.help"), _ => HelpText());
        Add(new[] { "id" }, Who.Everyone, default, new Text("Command.id"), _ => HostActions.PlayerList());
        Add(new[] { "m", "myrole" }, Who.Everyone, default, new Text("Command.myrole"), InfoCommands.MyRole);
        Add(new[] { "r", "roles" }, Who.Everyone, new Text("Command.roles.Usage"), new Text("Command.roles"), InfoCommands.Roles);
        Add(new[] { "n", "now" }, Who.Everyone, default, new Text("Command.now"), InfoCommands.Now);
        Add(new[] { "sim" }, Who.Everyone, new Text("Command.sim.Usage"), new Text("Command.sim"), InfoCommands.Sim);
        Add(new[] { "rename" }, Who.Everyone, new Text("Command.rename.Usage"), new Text("Command.rename"), LobbyTools.Rename);
        Add(new[] { "color", "colour" }, Who.Everyone, new Text("Command.color.Usage"), new Text("Command.color"), LobbyTools.Color);
        Add(new[] { "death" }, Who.Everyone, new Text("Command.death.Usage"), new Text("Command.death"), InfoCommands.Death);
        Add(new[] { "l", "last" }, Who.Everyone, default, new Text("Command.last"), InfoCommands.LastResult);
        Add(new[] { "spectate", "spec" }, Who.Everyone, default, new Text("Command.spectate"), Roles.Spectate.Command);
        Add(new[] { "dump" }, Who.Everyone, default, new Text("Command.dump"), LogDump.Run);
        Add(new[] { "win", "winner" }, Who.Everyone, default, new Text("Command.winner"), InfoCommands.Winners);
        Add(new[] { "lang" }, Who.Everyone, new Text("Command.lang.Usage"), new Text("Command.lang"), LangFiles.Command);

        Add(new[] { "start" }, Who.Host, default, new Text("Command.start"), _ => HostActions.Start());
        Add(new[] { "cancel" }, Who.Host, default, new Text("Command.cancel"), _ => HostActions.Cancel());
        Add(new[] { "end", "廃村" }, Who.Host, default, new Text("Command.end"), _ => HostActions.Abort());
        Add(new[] { "mt", "meet" }, Who.Host, default, new Text("Command.meet"), _ => HostActions.ToggleMeeting());
        Add(new[] { "setrole" }, Who.Host, new Text("Command.setrole.Usage"), new Text("Command.setrole"), HostActions.SetRole);
        Add(new[] { "kill" }, Who.Host, new Text("Command.kill.Usage"), new Text("Command.kill"), HostActions.Kill);
        Add(new[] { "revive" }, Who.Host, new Text("Command.revive.Usage"), new Text("Command.revive"), HostActions.Revive);
        Add(new[] { "kick" }, Who.Host, new Text("Command.kick.Usage"), new Text("Command.kick"), JoinLists.Kick);
        Add(new[] { "ban" }, Who.Host, new Text("Command.ban.Usage"), new Text("Command.ban"), JoinLists.Ban);
        Add(new[] { "banlist" }, Who.Host, default, new Text("Command.banlist"), JoinLists.BanList);
        Add(new[] { "mods" }, Who.Host, default, new Text("Command.mods"), Net.VersionCheck.ModsText);
        Add(new[] { "unban" }, Who.Host, new Text("Command.unban.Usage"), new Text("Command.unban"), JoinLists.Unban);
        Add(new[] { "cjclear" }, Who.Host, default, new Text("Command.cjclear"), JoinLists.ClearRecent);
        Add(new[] { "say" }, Who.Host, new Text("Command.say.Usage"), new Text("Command.say"), LobbyTools.Say);
        Add(new[] { "preset" }, Who.Host, new Text("Command.preset.Usage"), new Text("Command.preset"), LobbyTools.Preset);
        Add(new[] { "vip" }, Who.Host, new Text("Command.vip.Usage"), new Text("Command.vip"), JoinLists.VipCmd);
        Add(new[] { "wl", "whitelist" }, Who.Host, new Text("Command.whitelist.Usage"), new Text("Command.whitelist"), JoinLists.WhiteCmd);

        Add(new[] { "devhelp" }, Who.Dev, new Text("Command.devhelp.Usage"), new Text("Command.devhelp"), args =>
        {
            var sb = new StringBuilder();
            Bridge.TestBridge.Run("help " + args, s => { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); });
            return sb.ToString();
        });
    }

    private static void Add(string[] names, Who who, Text usage, Text help, Func<string, string> run)
    {
        var c = new Cmd { Names = names, Who = who, UsageText = usage, Help = help, Run = run };
        List.Add(c);
        foreach (var n in names) ByName[n] = c;
    }

    public static bool Allowed(Who who) => who switch
    {
        Who.Everyone => true,
        Who.Host => AmongUsClient.Instance && AmongUsClient.Instance.AmHost,
        _ => DevUsers.AmDev,
    };

    // チャットの 1 行を受けたら true (結果は reply)。知らない名前なら false で他へ回す
    public static bool TryRun(string name, string args, out string reply)
    {
        reply = null;
        if (!ByName.TryGetValue(name, out var c)) return false;
        if (!Allowed(c.Who))
        {
            // 開発者のコマンドは名簿に無い人には無いのと同じにする (本編へそのまま流す)
            if (c.Who == Who.Dev) return false;
            reply = new Text("ChatCommands.OnlyTheHostCanUse");
            return true;
        }
        try { reply = c.Run(args.Trim()); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"chat command /{name}: {e}");
            reply = new Text("ChatCommands.Failed").With("error", e.GetType().Name);
        }
        Plugin.Logger.LogInfo($"chat command /{name} {args} -> {reply?.Replace('\n', ' ')}");
        return true;
    }

    // 打ちかけの名前 (/ の後ろ) に合う、自分が使えるコマンド。同じ名前があればそれ、無ければ表の順で前方が一致する最初の名前
    public static bool Suggest(string typed, out string name, out string usage)
    {
        name = null; usage = "";
        if (typed.Length == 0) return false;
        if (ByName.TryGetValue(typed, out var exact) && Allowed(exact.Who))
        {
            name = typed; usage = exact.Usage;
            return true;
        }
        foreach (var c in List)
        {
            if (!Allowed(c.Who)) continue;
            foreach (var n in c.Names)
                if (n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) { name = n; usage = c.Usage; return true; }
        }
        return false;
    }

    private static string HelpText()
    {
        var sb = new StringBuilder();
        Who? section = null;
        foreach (var c in List)
        {
            if (!Allowed(c.Who)) continue;
            if (section != c.Who)
            {
                section = c.Who;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(c.Who switch
                {
                    Who.Host => new Text("ChatCommands.Host"),
                    Who.Dev => new Text("ChatCommands.Developer"),
                    _ => new Text("ChatCommands.Everyone"),
                });
            }
            sb.Append("\n/").Append(string.Join(" /", c.Names));
            if (c.Usage.Length > 0) sb.Append(' ').Append(c.Usage);
            sb.Append("  ").Append(c.Help.ToString());
        }
        if (!Allowed(Who.Host)) sb.Append('\n').Append(new Text("ChatCommands.TheHostHasMoreCommands").ToString());
        return sb.ToString();
    }
}
