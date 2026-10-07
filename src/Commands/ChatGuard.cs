// Based on https://github.com/Gurge44/EndlessHostRoles Modules/SpamManager.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Commands;

[Settings(Tab.General, "荒らし対策", "Chat moderation", order: -85)]
public static class ChatGuardSettings
{
    public static readonly BoolOpt BanWords = new("禁止語を言った人を部屋から出す (BanWords.txt)", "Kick players who say a banned word (BanWords.txt)", false);
    public static readonly BoolOpt StartSpam = new("ロビーで開始を急かす人を部屋から出す", "Kick players who keep asking to start in the lobby", false);
    public static readonly IntOpt Times = new("何回目で出すか", "Kick on the Nth time", 3, 1, 10);
}

// ホストの端末で、届いたチャットを見て荒らしを部屋から出す。禁止語は手元の BanWords.txt (1 行 1 語・# で始まる行は飛ばす)。
// 決まった回数までは注意だけ出し、超えたら出す。回数は部屋に入り直すまで覚える
internal static class ChatGuard
{
    private static string WordsPath => Path.Combine(Paths.ConfigPath, "MoreRolesPlus", "BanWords.txt");
    private static List<string> _words;
    private static DateTime _wordsStamp;
    private static readonly Dictionary<int, int> Strikes = new(); // clientId → 回数

    // 「開始」の催促とみなす言葉 (文字と数字だけを残し、小文字にしてから全体で比べる)
    private static readonly HashSet<string> StartWords = new(StringComparer.Ordinal)
    {
        "start", "plsstart", "pleasestart", "startplease", "startpls",
        "開始", "開始して", "かいし", "はじめて", "始めて", "はよ", "はよはよ", "はやく", "早く", "すたーと", "スタート",
    };

    public static void Reset() => Strikes.Clear();

    private static List<string> Words()
    {
        // 書き換えたら次のチャットから効くように、ファイルが変わった時だけ読み直す
        try
        {
            if (!File.Exists(WordsPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(WordsPath));
                File.WriteAllText(WordsPath, "# 1 行に 1 語。チャットにこの語が含まれていたら禁止語として数える (大文字と小文字は区別しない)\n");
            }
            var stamp = File.GetLastWriteTimeUtc(WordsPath);
            if (_words != null && stamp == _wordsStamp) return _words;
            _wordsStamp = stamp;
            _words = new List<string>();
            foreach (string raw in File.ReadAllLines(WordsPath))
            {
                string w = raw.Trim();
                if (w.Length > 0 && w[0] != '#') _words.Add(w);
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"ban words: {e}"); }
        return _words ??= new List<string>();
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c) || c == 'ー') sb.Append(c);
        return sb.ToString();
    }

    public static void OnChat(PlayerControl sender, string text)
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || !sender || sender.OwnerId == client.HostId || sender.AmOwner || string.IsNullOrEmpty(text)) return;
        string why = null;
        if (ChatGuardSettings.StartSpam && !ShipStatus.Instance && Vanilla.StartManager && StartWords.Contains(Normalize(text)))
            why = new Text("開始の催促", "asking to start");
        else if (ChatGuardSettings.BanWords)
        {
            foreach (var w in Words())
                if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) { why = new Text("禁止語", "banned word"); break; }
        }
        if (why == null) return;

        int n = Strikes.TryGetValue(sender.OwnerId, out int c) ? c + 1 : 1;
        Strikes[sender.OwnerId] = n;
        string name = sender.Data != null ? sender.Data.PlayerName : $"#{sender.PlayerId}";
        int limit = ChatGuardSettings.Times.Value;
        string msg;
        if (n >= limit)
        {
            client.KickPlayer(sender.OwnerId, false);
            msg = new Text($"{name} を部屋から出しました ({why} {n} 回目)", $"Kicked {name} ({why}, {n} times)");
        }
        else msg = new Text($"{name}: {why} {n} 回目 ({limit} 回目で部屋から出します)", $"{name}: {why} ({n}/{limit}, kicked at {limit})");
        Plugin.Logger.LogInfo($"chat guard: {msg}");
        DevChat.Notice(Vanilla.Chat, msg);
    }
}

[HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
internal static class ChatGuardPatch
{
    public static void Postfix([HarmonyArgument(0)] PlayerControl sourcePlayer, [HarmonyArgument(1)] string chatText)
    {
        try { ChatGuard.OnChat(sourcePlayer, chatText); }
        catch (Exception e) { Plugin.Logger.LogError($"chat guard: {e}"); }
    }
}
