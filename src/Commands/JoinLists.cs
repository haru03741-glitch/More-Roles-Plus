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

[Settings(Tab.General, "JoinSettings.Title", order: -90)]
public static class JoinSettings
{
    public static readonly BoolOpt UseBanList = new("JoinSettings.UseBanList", true);
    public static readonly BoolOpt Whitelist = new("JoinSettings.Whitelist", false);
    public static readonly BoolOpt Consecutive = new("JoinSettings.Consecutive", false);
    public static readonly IntOpt ConsecutiveGames = new("JoinSettings.ConsecutiveGames", 1, 1, 5);
}

// ホストの手元にある BAN 一覧・ホワイトリスト・VIP 一覧 (BepInEx/config/MoreRolesPlus/ の BanList.txt・WhiteList.txt・VipList.txt)。
// VIP はホワイトリストにいるのと同じ扱いで、そのうえ荒らし対策 (禁止語・開始の催促) で数えない。
// 1 行 = 「フレンドコード,PUID の要約,名前」。照合はフレンドコードと PUID の要約のどちらかが一致すれば当たり。
// Based on Gurge44/EndlessHostRoles (BanManager・WhitelistManager)、EnhancedNetwork/TownofHost-Enhanced (PUID の要約)、
// Lotus-AU/LotusContinued (WhitelistManager)、satokazoku/TownOfHost-Pko (前の試合にいた人を続けて入れない仕組み)
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
    private static readonly ListFile Vips = new("VipList.txt");

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
        DevChat.Notice(Vanilla.Chat, text);
        Plugin.Logger.LogInfo($"join lists: {text}");
    }

    public static void OnPlayerJoined(ClientData c)
    {
        if (!AmHost || !OnlineRoom || c == null || c.Id == AmongUsClient.Instance.ClientId) return;
        var (fc, puid) = Keys(c);
        string name = c.PlayerName;
        if (fc.Length == 0 && puid.Length == 0)
        {
            if (JoinSettings.Whitelist) Notify(new Text("JoinLists.NoIdForWhitelist").With("name", name));
            return;
        }
        if (JoinSettings.UseBanList && Bans.Find(fc, puid) != null)
        {
            AmongUsClient.Instance.KickPlayer(c.Id, true);
            Notify(new Text("JoinLists.IsOnTheBanList").With("name", name));
            return;
        }
        bool trusted = White.Find(fc, puid) != null || Vips.Find(fc, puid) != null;
        if (JoinSettings.Whitelist && !trusted)
        {
            AmongUsClient.Instance.KickPlayer(c.Id, false);
            Notify(new Text("JoinLists.IsNotOnTheWhitelist").With("name", name));
            return;
        }
        // ホワイトリストと VIP の人は続けて入ってもよい
        if (JoinSettings.Consecutive && puid.Length > 0 && !trusted && PlayedRecently(puid))
        {
            AmongUsClient.Instance.KickPlayer(c.Id, false);
            Notify(new Text("JoinLists.KeptOutRecent").With("name", name));
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
        return new Text("JoinLists.RecentCleared");
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
        if (c == null) err = new Text("JoinLists.NoPlayerSeeId").With("arg", arg);
        else if (c.Id == AmongUsClient.Instance.ClientId) { err = new Text("JoinLists.YouCantPickYourself"); c = null; }
        return c;
    }

    public static string Kick(string args) => KickOrBan(args, false);
    public static string Ban(string args) => KickOrBan(args, true);

    private static string KickOrBan(string args, bool ban)
    {
        if (!OnlineRoom) return new Text("JoinLists.OnlyInAnOnlineRoom");
        if (args.Length == 0) return new Text("JoinLists.NeedPlayerNumber");
        var c = ClientOf(args, out string err);
        if (c == null) return err;
        string name = c.PlayerName;
        AmongUsClient.Instance.KickPlayer(c.Id, ban);
        return ban ? new Text("JoinLists.Banned").With("name", name)
                   : new Text("JoinLists.Kicked").With("name", name);
    }

    public static string BanList(string _) => ListText(Bans, new Text("JoinLists.TheBanListIsEmpty"));

    public static string Unban(string args) => Remove(Bans, args, new Text("JoinLists.Unbanned"));

    // その人が VIP 一覧にいるか (ホストの端末で使う)
    public static bool IsVip(int clientId)
    {
        var c = AmongUsClient.Instance ? AmongUsClient.Instance.GetRecentClient(clientId) : null;
        if (c == null) return false;
        var (fc, puid) = Keys(c);
        return (fc.Length > 0 || puid.Length > 0) && Vips.Find(fc, puid) != null;
    }

    // /wl = 一覧 / /wl add <番号> / /wl del <フレンドコードか一覧の番号>
    public static string WhiteCmd(string args) => ListCmd(White, "/wl", new Text("JoinLists.TheWhitelist"), args);

    // /vip = 一覧 / /vip add <番号> / /vip del <フレンドコードか一覧の番号>
    public static string VipCmd(string args) => ListCmd(Vips, "/vip", new Text("JoinLists.TheVIPList"), args);

    private static string ListCmd(ListFile file, string cmd, Text what, string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string rest = parts.Length > 1 ? parts[1].Trim() : "";
        switch (sub)
        {
            case "":
            case "list":
                return ListText(file, new Text("JoinLists.ListIsEmpty").With("list", Cap(what)));
            case "add":
            {
                if (!OnlineRoom) return new Text("JoinLists.OnlyInAnOnlineRoomFc");
                var c = ClientOf(rest, out string err);
                if (c == null) return err;
                var (fc, puid) = Keys(c);
                if (fc.Length == 0 && puid.Length == 0) return new Text("JoinLists.HasNoID").With("name", c.PlayerName);
                if (file.Find(fc, puid) != null) return new Text("JoinLists.IsAlreadyListed").With("name", c.PlayerName);
                file.Get().Add(new Entry { Fc = fc, Puid = puid, Name = Clean(c.PlayerName) });
                file.Save();
                return new Text("JoinLists.AddedTo").With("name", c.PlayerName).With("what", what);
            }
            case "del":
            case "remove":
                return Remove(file, rest, new Text("JoinLists.RemovedFrom").With("what", what));
            default:
                return new Text("JoinLists.ListUsage").With("cmd", cmd);
        }
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

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
        if (arg.Length == 0) return new Text("JoinLists.NeedListNumber");
        Entry hit = null;
        if (int.TryParse(arg, out int n) && n >= 1 && n <= list.Count) hit = list[n - 1];
        else
        {
            string key = NormFc(arg.StartsWith("puid:", StringComparison.OrdinalIgnoreCase) ? arg[5..] : arg);
            foreach (var e in list)
                if (e.Fc == key || e.Puid == key) { hit = e; break; }
        }
        if (hit == null) return new Text("JoinLists.IsNotOnTheList").With("arg", arg);
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
