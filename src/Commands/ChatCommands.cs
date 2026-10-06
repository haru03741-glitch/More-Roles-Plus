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
        public string Usage;
        public Text Help;
        public Func<string, string> Run;
    }

    private static readonly List<Cmd> List = new();
    private static readonly Dictionary<string, Cmd> ByName = new(StringComparer.OrdinalIgnoreCase);

    static ChatCommands()
    {
        Add(new[] { "help", "h" }, Who.Everyone, "", new Text("使えるコマンドの一覧", "List the commands you can use"), _ => HelpText());
        Add(new[] { "id" }, Who.Everyone, "", new Text("全員の番号と名前", "Everyone's number and name"), _ => HostActions.PlayerList());
        Add(new[] { "m", "myrole" }, Who.Everyone, "", new Text("自分の役職と説明 (試合中)", "Your role and what it does (in game)"), InfoCommands.MyRole);
        Add(new[] { "r", "roles" }, Who.Everyone, "[役職]", new Text("役職の一覧 / その役職の説明", "List roles / describe one"), InfoCommands.Roles);
        Add(new[] { "n", "now" }, Who.Everyone, "", new Text("今の MRP の設定", "Current More Roles Plus settings"), InfoCommands.Now);
        Add(new[] { "rename" }, Who.Everyone, "<名前>", new Text("自分の名前を変える (ロビー)", "Change your name (lobby)"), LobbyTools.Rename);
        Add(new[] { "color", "colour" }, Who.Everyone, "<番号|色の名前>", new Text("自分の色を変える (ロビー)", "Change your color (lobby)"), LobbyTools.Color);
        Add(new[] { "death" }, Who.Everyone, "[番号]", new Text("自分 (番号でその人) の死因 (死んでから)", "How you (or someone) died (after you die)"), InfoCommands.Death);
        Add(new[] { "l", "last" }, Who.Everyone, "", new Text("前の試合の結果 (全員の役職と死因)", "Last game's result (roles and causes of death)"), InfoCommands.LastResult);
        Add(new[] { "win", "winner" }, Who.Everyone, "", new Text("前の試合の勝者", "Last game's winners"), InfoCommands.Winners);

        Add(new[] { "start" }, Who.Host, "", new Text("試合を始める (数え中なら数えを飛ばす)", "Start the game (skips the countdown if counting)"), _ => HostActions.Start());
        Add(new[] { "end", "廃村" }, Who.Host, "", new Text("廃村: 勝者なしで試合を打ち切る (Shift+L+Enter)", "Abort the game with no winner (Shift+L+Enter)"), _ => HostActions.Abort());
        Add(new[] { "mt", "meet" }, Who.Host, "", new Text("会議を開く / 開いている会議を閉じる (Shift+M+Enter)", "Call a meeting / close the current one (Shift+M+Enter)"), _ => HostActions.ToggleMeeting());
        Add(new[] { "setrole" }, Who.Host, "<役職> [番号]", new Text("その人 (省略で自分) の役職を変える (試合中)", "Change someone's role (yourself if omitted, in game)"), HostActions.SetRole);
        Add(new[] { "kill" }, Who.Host, "[番号]", new Text("その人 (省略で自分) を倒す (自分は Shift+E+Enter)", "Kill someone (yourself if omitted, Shift+E+Enter)"), HostActions.Kill);
        Add(new[] { "revive" }, Who.Host, "[番号]", new Text("その人 (省略で自分) を生き返らせる", "Revive someone (yourself if omitted)"), HostActions.Revive);
        Add(new[] { "kick" }, Who.Host, "<番号>", new Text("その人を部屋から出す", "Kick someone out of the room"), JoinLists.Kick);
        Add(new[] { "ban" }, Who.Host, "<番号>", new Text("その人を BAN して一覧に書く (次から入れない)", "Ban someone and add them to the ban list"), JoinLists.Ban);
        Add(new[] { "banlist" }, Who.Host, "", new Text("BAN 一覧", "Show the ban list"), JoinLists.BanList);
        Add(new[] { "unban" }, Who.Host, "<一覧の番号|フレンドコード>", new Text("BAN を解く", "Remove someone from the ban list"), JoinLists.Unban);
        Add(new[] { "cjclear" }, Who.Host, "", new Text("前の試合にいた人の記録を消す (続けて入れない設定用)", "Forget who played the previous games"), JoinLists.ClearRecent);
        Add(new[] { "say" }, Who.Host, "<文>", new Text("全員の画面にお知らせを出す", "Show a message to everyone"), LobbyTools.Say);
        Add(new[] { "preset" }, Who.Host, "[save|load|del <名前>]", new Text("設定を名前を付けて保存 / 読み込み", "Save / load settings by name"), LobbyTools.Preset);
        Add(new[] { "wl", "whitelist" }, Who.Host, "[add <番号> | del <一覧の番号>]", new Text("ホワイトリストの一覧 / 追加 / 削除 (使うかは設定で)", "Whitelist: list / add / remove (turn it on in settings)"), JoinLists.WhiteCmd);

        Add(new[] { "devhelp" }, Who.Dev, "[語]", new Text("開発者コマンドの一覧", "List the developer commands"), args =>
        {
            var sb = new StringBuilder();
            Bridge.TestBridge.Run("help " + args, s => { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); });
            return sb.ToString();
        });
    }

    private static void Add(string[] names, Who who, string usage, Text help, Func<string, string> run)
    {
        var c = new Cmd { Names = names, Who = who, Usage = usage, Help = help, Run = run };
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
            reply = new Text("ホストだけが使えます", "Only the host can use this");
            return true;
        }
        try { reply = c.Run(args.Trim()); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"chat command /{name}: {e}");
            reply = new Text($"失敗しました ({e.GetType().Name})", $"Failed ({e.GetType().Name})");
        }
        Plugin.Logger.LogInfo($"chat command /{name} {args} -> {reply?.Replace('\n', ' ')}");
        return true;
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
                    Who.Host => new Text("― ホスト ―", "- Host -"),
                    Who.Dev => new Text("― 開発者 ―", "- Developer -"),
                    _ => new Text("― 全員 ―", "- Everyone -"),
                });
            }
            sb.Append("\n/").Append(string.Join(" /", c.Names));
            if (c.Usage.Length > 0) sb.Append(' ').Append(c.Usage);
            sb.Append("  ").Append(c.Help.ToString());
        }
        if (!Allowed(Who.Host)) sb.Append('\n').Append(new Text("(ホスト用のコマンドもあります)", "(The host has more commands)").ToString());
        return sb.ToString();
    }
}
