using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using HarmonyLib;
using InnerNet;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Commands;

[Settings(Tab.General, "入室", "Joining", order: -90)]
public static class JoinSettings
{
    public static readonly BoolOpt UseBanList = new("BAN した人を入れない", "Keep banned players out", true);
    public static readonly BoolOpt Whitelist = new("ホワイトリストの人だけ入れる", "Only let whitelisted players in", false);
    public static readonly BoolOpt Consecutive = new("前の試合にいた人を続けて入れない", "Keep out players from the previous game", false);
    public static readonly IntOpt ConsecutiveGames = new("何試合前までを見るか", "How many past games to check", 1, 1, 5);
}

// ホストの手元にある BAN 一覧とホワイトリスト (BepInEx/config/MoreRolesPlus/ の BanList.txt・WhiteList.txt)。
// 1 行 = 「フレンドコード,PUID の要約,名前」。照合はフレンドコードと PUID の要約のどちらかが一致すれば当たり。
// Based on Gurge44/EndlessHostRoles (BanManager・WhitelistManager)、EnhancedNetwork/TownofHost-Enhanced (PUID の要約)、
// Lotus-AU/LotusContinued (WhitelistManager)
internal static class JoinLists
{
    private sealed class Entry
    {
        public string Fc, Puid, Name;
    }

    private sealed class ListFile
    {
        public readonly string Path;
        public readonly List<Entry> Entries = new();
        private bool _loaded;

        public ListFile(string name) => Path = System.IO.Path.Combine(Dir, name);

        public List<Entry> Get()
        {
            if (_loaded) return Entries;
            _loaded = true;
            try
            {
                if (!File.Exists(Path)) return Entries;
                foreach (string raw in File.ReadAllLines(Path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split(',');
                    var e = new Entry { Fc = NormFc(f[0]), Puid = f.Length > 1 ? f[1].Trim().ToLowerInvariant() : "", Name = f.Length > 2 ? f[2].Trim() : "" };
                    if (e.Fc.Length > 0 || e.Puid.Length > 0) Entries.Add(e);
                }
            }
            catch (Exception e) { Plugin.Logger.LogError($"join list read {Path}: {e}"); }
            return Entries;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                foreach (var e in Entries) sb.Append(e.Fc).Append(',').Append(e.Puid).Append(',').Append(e.Name).Append('\n');
                File.WriteAllText(Path, sb.ToString());
            }
            catch (Exception e) { Plugin.Logger.LogError($"join list write {Path}: {e}"); }
        }

        public Entry Find(string fc, string puid)
        {
            foreach (var e in Get())
            {
                // 空の欄同士は一致にしない (身元の取れない人が全員当たってしまう)
                if (fc.Length > 0 && e.Fc == fc) return e;
                if (puid.Length > 0 && e.Puid == puid) return e;
            }
            return null;
        }
    }

    private static string Dir => Path.Combine(Paths.ConfigPath, "MoreRolesPlus");
    private static readonly ListFile Bans = new("BanList.txt");
    private static readonly ListFile White = new("WhiteList.txt");

    private static string NormFc(string s) => s.Trim().Replace(':', '#').ToLowerInvariant();

    // 本物の PUID は 32 文字。空や短い値は誰でも同じ要約になるので身元に使わない
    private static string PuidKey(ClientData c)
    {
        string puid = c.ProductUserId;
        if (string.IsNullOrEmpty(puid) || puid.Length != 32) return "";
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(puid));
        string hex = Convert.ToHexString(h).ToLowerInvariant();
        return hex[..5] + hex[^4..];
    }

    private static (string fc, string puid) Keys(ClientData c) => (NormFc(c.FriendCode ?? ""), PuidKey(c));

    private static string Label(Entry e) => e.Fc.Length > 0 ? e.Fc : "puid:" + e.Puid;

    private static bool AmHost => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    // ローカルとフリープレイはフレンドコードが無いので照合しない
    private static bool OnlineRoom => AmongUsClient.Instance && AmongUsClient.Instance.NetworkMode == NetworkModes.OnlineGame;

    private static void Notify(string text)
    {
        if (HudManager.InstanceExists) DevChat.Notice(HudManager.Instance.Chat, text);
        Plugin.Logger.LogInfo($"join lists: {text}");
    }

    public static void OnPlayerJoined(ClientData c)
    {
        if (!AmHost || !OnlineRoom || c == null || c.Id == AmongUsClient.Instance.ClientId) return;
        var (fc, puid) = Keys(c);
        string name = c.PlayerName;
        if (fc.Length == 0 && puid.Length == 0)
        {
            if (JoinSettings.Whitelist) Notify(new Text($"{name} は身元が取れないので、ホワイトリストで判断できません", $"{name} has no ID, so the whitelist can't check them"));
            return;
        }
        if (JoinSettings.UseBanList && Bans.Find(fc, puid) != null)
        {
            AmongUsClient.Instance.KickPlayer(c.Id, true);
            Notify(new Text($"{name} は BAN 一覧にいるので入れませんでした", $"{name} is on the ban list and was kept out"));
            return;
        }
        if (JoinSettings.Whitelist && White.Find(fc, puid) == null)
        {
            AmongUsClient.Instance.KickPlayer(c.Id, false);
            Notify(new Text($"{name} はホワイトリストにいないので入れませんでした", $"{name} is not on the whitelist and was kept out"));
            return;
        }
        // ホワイトリストの人は続けて入ってもよい
        if (JoinSettings.Consecutive && puid.Length > 0 && White.Find(fc, puid) == null && PlayedRecently(puid))
        {
            AmongUsClient.Instance.KickPlayer(c.Id, false);
            Notify(new Text($"{name} は前の試合にいたので入れませんでした", $"{name} played the previous game and was kept out"));
        }
    }

    // ---- 前の試合にいた人 (試合ごとの PUID の要約。ホストを再起動しても覚えておくようにファイルへ書く) ----

    private const int MaxHistory = 5;
    private static string HistoryPath => Path.Combine(Dir, "RecentGames.txt");
    private static List<HashSet<string>> _history; // 古い順
    private static HashSet<string> _current;

    private static List<HashSet<string>> History()
    {
        if (_history != null) return _history;
        _history = new List<HashSet<string>>();
        try
        {
            if (File.Exists(HistoryPath))
                foreach (string line in File.ReadAllLines(HistoryPath))
                    if (line.Trim().Length > 0) _history.Add(new HashSet<string>(line.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries)));
        }
        catch (Exception e) { Plugin.Logger.LogError($"recent games read: {e}"); }
        return _history;
    }

    private static bool PlayedRecently(string puid)
    {
        var h = History();
        for (int i = Math.Max(0, h.Count - JoinSettings.ConsecutiveGames.Value); i < h.Count; i++)
            if (h[i].Contains(puid)) return true;
        return false;
    }

    // 試合の始まり: その時いる人を控える (ホストのオンラインの部屋だけ)
    public static void OnMatchStart()
    {
        _current = null;
        if (!AmHost || !OnlineRoom) return;
        _current = new HashSet<string>();
        var clients = AmongUsClient.Instance.allClients;
        for (int i = 0; i < clients.Count; i++)
        {
            var c = clients[i];
            if (c == null || c.Id == AmongUsClient.Instance.ClientId) continue;
            string k = PuidKey(c);
            if (k.Length > 0) _current.Add(k);
        }
    }

    // 試合の終わり: 廃村でなければ控えを履歴に積む
    public static void OnMatchEnd(bool aborted)
    {
        var cur = _current;
        _current = null;
        if (cur == null || aborted) return;
        var h = History();
        h.Add(cur);
        while (h.Count > MaxHistory) h.RemoveAt(0);
        SaveHistory();
    }

    private static void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            foreach (var g in History()) sb.Append(string.Join(",", g)).Append('\n');
            File.WriteAllText(HistoryPath, sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogError($"recent games write: {e}"); }
    }

    public static string ClearRecent(string _)
    {
        History().Clear();
        SaveHistory();
        return new Text("前の試合にいた人の記録を消しました", "Forgot who played the previous games");
    }

    // 本編の BAN (人の一覧の BAN ボタン) も含め、BAN した人は一覧に書く
    public static void OnBan(int clientId)
    {
        var c = AmongUsClient.Instance.GetRecentClient(clientId);
        if (c == null) return;
        var (fc, puid) = Keys(c);
        if (fc.Length == 0 && puid.Length == 0 || Bans.Find(fc, puid) != null) return;
        Bans.Get().Add(new Entry { Fc = fc, Puid = puid, Name = Clean(c.PlayerName) });
        Bans.Save();
    }

    // 名前のカンマと改行は一覧の区切りと混ざるので消す
    private static string Clean(string s) => (s ?? "").Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');

    // ---- コマンド ----

    private static ClientData ClientOf(string arg, out string err)
    {
        err = null;
        var data = GameData.Instance;
        var info = data && byte.TryParse(arg, out byte pid) ? data.GetPlayerById(pid) : null;
        var pc = info != null ? info.Object : null;
        var c = pc ? AmongUsClient.Instance.GetClientFromCharacter(pc) : null;
        if (c == null) err = new Text($"{arg} 番の人はいません (/id で番号を確認)", $"No player {arg} (see /id)");
        else if (c.Id == AmongUsClient.Instance.ClientId) { err = new Text("自分は選べません", "You can't pick yourself"); c = null; }
        return c;
    }

    public static string Kick(string args) => KickOrBan(args, false);
    public static string Ban(string args) => KickOrBan(args, true);

    private static string KickOrBan(string args, bool ban)
    {
        if (!OnlineRoom) return new Text("オンラインの部屋でだけ使えます", "Only in an online room");
        if (args.Length == 0) return new Text("番号を付けてください (/id で確認)", "Add a player number (see /id)");
        var c = ClientOf(args, out string err);
        if (c == null) return err;
        string name = c.PlayerName;
        AmongUsClient.Instance.KickPlayer(c.Id, ban);
        return ban ? new Text($"{name} を BAN しました (一覧に書きました)", $"Banned {name} (added to the list)")
                   : new Text($"{name} を部屋から出しました", $"Kicked {name}");
    }

    public static string BanList(string _) => ListText(Bans, new Text("BAN 一覧は空です", "The ban list is empty"));

    public static string Unban(string args) => Remove(Bans, args, new Text("BAN を解きました", "Unbanned"));

    // /wl = 一覧 / /wl add <番号> / /wl del <フレンドコードか一覧の番号>
    public static string WhiteCmd(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string rest = parts.Length > 1 ? parts[1].Trim() : "";
        switch (sub)
        {
            case "":
            case "list":
                return ListText(White, new Text("ホワイトリストは空です", "The whitelist is empty"));
            case "add":
            {
                if (!OnlineRoom) return new Text("オンラインの部屋でだけ使えます (フレンドコードが要る)", "Only in an online room (needs friend codes)");
                var c = ClientOf(rest, out string err);
                if (c == null) return err;
                var (fc, puid) = Keys(c);
                if (fc.Length == 0 && puid.Length == 0) return new Text($"{c.PlayerName} は身元が取れません", $"{c.PlayerName} has no ID");
                if (White.Find(fc, puid) != null) return new Text($"{c.PlayerName} はもう入っています", $"{c.PlayerName} is already listed");
                White.Get().Add(new Entry { Fc = fc, Puid = puid, Name = Clean(c.PlayerName) });
                White.Save();
                return new Text($"{c.PlayerName} をホワイトリストに入れました", $"Added {c.PlayerName} to the whitelist");
            }
            case "del":
            case "remove":
                return Remove(White, rest, new Text("ホワイトリストから外しました", "Removed from the whitelist"));
            default:
                return new Text("/wl (一覧)・/wl add <番号>・/wl del <一覧の番号かフレンドコード>", "/wl (list), /wl add <number>, /wl del <list number or friend code>");
        }
    }

    private static string ListText(ListFile f, string empty)
    {
        var list = f.Get();
        if (list.Count == 0) return empty;
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(i + 1).Append(": ").Append(list[i].Name.Length > 0 ? list[i].Name : "?").Append("  ").Append(Label(list[i]));
        }
        return sb.ToString();
    }

    // 一覧の番号 (1 から)・フレンドコード・PUID の要約のどれかで消す
    private static string Remove(ListFile f, string arg, string done)
    {
        var list = f.Get();
        if (arg.Length == 0) return new Text("一覧の番号かフレンドコードを付けてください", "Add a list number or friend code");
        Entry hit = null;
        if (int.TryParse(arg, out int n) && n >= 1 && n <= list.Count) hit = list[n - 1];
        else
        {
            string key = NormFc(arg.StartsWith("puid:", StringComparison.OrdinalIgnoreCase) ? arg[5..] : arg);
            foreach (var e in list)
                if (e.Fc == key || e.Puid == key) { hit = e; break; }
        }
        if (hit == null) return new Text($"{arg} は一覧にありません", $"{arg} is not on the list");
        list.Remove(hit);
        f.Save();
        return $"{done}: {(hit.Name.Length > 0 ? hit.Name : Label(hit))}";
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class JoinListsJoinPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData data)
    {
        try { JoinLists.OnPlayerJoined(data); }
        catch (Exception e) { Plugin.Logger.LogError($"join lists: {e}"); }
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.KickPlayer))]
internal static class JoinListsKickPatch
{
    public static bool Prefix([HarmonyArgument(0)] int clientId, [HarmonyArgument(1)] bool ban)
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost) return true;
        // 自分 (ホスト) を蹴ると部屋ごと壊れる
        if (clientId == client.ClientId) return false;
        if (ban)
        {
            try { JoinLists.OnBan(clientId); }
            catch (Exception e) { Plugin.Logger.LogError($"join lists ban: {e}"); }
        }
        return true;
    }
}
