using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MrpServer;

// バグ報告 = Discord の報告チャンネルのスレッド 1 本。ゲームからの書き込みは Bot が埋め込みで代わりに書き、
// スタッフがスレッドに書いた事をゲームが読みに来る。// で始まる行はスタッフ同士の書き込み (ゲームには出さない)。
// 報告ごとに管理のボタン (既読・閉じる/開け直す・停止/停止を外す) の書き込みを、管理用のチャンネル
// (無ければ報告のスレッドの中) に置く。押せるのはスタッフの役職の人とサーバーの管理者だけ
public sealed partial class Reports
{
    public const string PlayerFooter = "mrp:player", SystemFooter = "mrp:system", PanelFooter = "mrp:panel";
    private const string StaffNoteMark = "//";
    private const int Green = 0x00E676, Gray = 0x5A6270, Red = 0xFF5252, Yellow = 0xFFD54F;

    private readonly DiscordApi _discord;
    private readonly Store _store;
    private readonly MrpConfig _cfg;
    private readonly ILogger<Reports> _log;
    private readonly Dictionary<string, (DateTime at, List<ChatMessage> list)> _cache = new();

    public Reports(DiscordApi discord, Store store, MrpConfig cfg, ILogger<Reports> log)
    {
        _discord = discord;
        _store = store;
        _cfg = cfg;
        _log = log;
    }

    public sealed class NewReport
    {
        public string FriendCode { get; set; } = "";
        public string Puid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "bug";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string ModVersion { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Region { get; set; } = "";
        public string Scene { get; set; } = "";
        public string Language { get; set; } = "";
        public string RoomCode { get; set; } = "";
    }

    public sealed class Attachment
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";    // "image" / "video" / "file"
        public string Url { get; set; } = "";     // 元の大きさ (押した時にブラウザーで開く)
        public string Thumb { get; set; } = "";   // ゲームに出す小さな絵 (画像だけ)
        public int Width { get; set; }
        public int Height { get; set; }
        public long Size { get; set; }
    }

    public sealed class ChatMessage
    {
        public string Id { get; set; } = "";
        public string From { get; set; } = "";   // "me" / "staff" / "system"
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
        public long Time { get; set; }            // Unix 秒
        public List<Attachment> Attachments { get; set; } = new();
    }

    [GeneratedRegex(@"^[a-z0-9]{2,20}#\d{4}$", RegexOptions.IgnoreCase)]
    public static partial Regex FriendCodePattern();

    private static string CategoryLabel(string c) => c switch
    {
        "idea" => "💡 要望",
        "other" => "💬 その他",
        _ => "🐞 バグ",
    };

    public string Trust(NewReport n, string ip)
    {
        var seen = _store.Seen(n.FriendCode);
        // 名乗ったフレンドコードで、同じ IP からつないでいたか (違えば「未確認」として出す)
        string trust = seen == null ? "⚠ サーバーへの接続記録なし"
            : seen.Ip == ip ? "✅ 接続記録と IP が一致"
            : "⚠ 接続記録はあるが IP が違う";
        if (seen != null && !string.IsNullOrEmpty(seen.Puid) && !string.IsNullOrEmpty(n.Puid) && seen.Puid != n.Puid)
            trust = "⚠ PUID が接続記録と違う";
        return trust;
    }

    // report は Store.TryReserve で先に作って置いた物
    public async Task<string> Create(Report report, NewReport n, string ip, byte[]? logs, IReadOnlyList<MediaFile> media)
    {
        string trust = Trust(n, ip);
        string title = report.Title;
        var embed = new JsonObject
        {
            ["title"] = $"{CategoryLabel(n.Category)} {title}",
            ["description"] = DiscordApi.Clean(n.Body, 3500) is { Length: > 0 } body ? body : "(本文なし)",
            ["color"] = Green,
            ["fields"] = new JsonArray
            {
                Field("プレイヤー", report.Name, true),
                Field("フレンドコード", $"`{n.FriendCode}`", true),
                Field("確認", trust, true),
                Field("MRP", DiscordApi.Clean(n.ModVersion, 40), true),
                Field("Among Us", DiscordApi.Clean(n.GameVersion, 40), true),
                Field("環境", DiscordApi.Clean(n.Platform, 60), true),
                Field("地域", DiscordApi.Clean(n.Region, 60), true),
                Field("場面", DiscordApi.Clean(n.Scene, 60) + (string.IsNullOrEmpty(n.RoomCode) ? "" : $" / {DiscordApi.Clean(n.RoomCode, 8)}"), true),
                Field("言語", DiscordApi.Clean(n.Language, 20), true),
            },
            ["footer"] = new JsonObject { ["text"] = $"報告 #{report.Id} ・ PUID {DiscordApi.Clean(n.Puid, 64)}" },
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
        };
        var message = new JsonObject
        {
            ["embeds"] = new JsonArray { embed },
            ["allowed_mentions"] = DiscordApi.NoMentions(),
        };
        var files = new List<(string, byte[])>();
        if (logs is { Length: > 0 })
        {
            files.Add(($"mrp-logs-{report.Id}.zip", logs));
            message["attachments"] = new JsonArray { new JsonObject { ["id"] = 0, ["filename"] = $"mrp-logs-{report.Id}.zip" } };
        }
        string threadName = DiscordApi.Clean($"[{CategoryLabel(n.Category)[3..]}] {title}", 95);
        string channel = _cfg.Discord.ReportChannelId;

        if (await _discord.ChannelType(channel).ConfigureAwait(false) == 15)
        {
            // フォーラム: 投稿 = スレッド
            var payload = new JsonObject { ["name"] = threadName, ["message"] = message };
            var thread = await _discord.PostMultipart($"/channels/{channel}/threads", payload, files).ConfigureAwait(false);
            report.ThreadId = thread?["id"]?.GetValue<string>() ?? "";
            report.FirstMessageId = thread?["message"]?["id"]?.GetValue<string>() ?? report.ThreadId;
        }
        else
        {
            // 普通のチャンネル: 書いてからその書き込みにスレッドを付ける
            var msg = await _discord.PostMultipart($"/channels/{channel}/messages", message, files).ConfigureAwait(false);
            string msgId = msg?["id"]?.GetValue<string>() ?? "";
            var thread = await _discord.Post($"/channels/{channel}/messages/{msgId}/threads", new JsonObject { ["name"] = threadName, ["auto_archive_duration"] = 10080 }).ConfigureAwait(false);
            report.ThreadId = thread?["id"]?.GetValue<string>() ?? "";
            report.FirstMessageId = msgId;
        }
        if (string.IsNullOrEmpty(report.ThreadId)) throw new InvalidOperationException("thread not created");
        _store.Save(report);
        _log.LogInformation("mrp report {Id} by {Fc} ({Trust}, media {Media})", report.Id, n.FriendCode, trust, media.Count);

        // 画像・動画はプレイヤーの書き込みとしてスレッドへ (ゲームの吹き出しにも出る)。管理のボタンはその後
        try { if (media.Count > 0) await PostPlayerMessage(report, "", media).ConfigureAwait(false); }
        catch (Exception e) { _log.LogWarning("mrp report {Id} media: {Msg}", report.Id, e.Message); }
        try { await PostPanel(report).ConfigureAwait(false); }
        catch (Exception e) { _log.LogWarning("mrp report {Id} panel: {Msg}", report.Id, e.Message); }
        return trust;
    }

    private static JsonObject Field(string name, string value, bool inline) =>
        new() { ["name"] = name, ["value"] = string.IsNullOrWhiteSpace(value) ? "-" : value, ["inline"] = inline };

    public async Task PostFromPlayer(Report r, string text, IReadOnlyList<MediaFile> media)
    {
        await PostPlayerMessage(r, text, media).ConfigureAwait(false);
        r.LastActivity = DateTime.UtcNow;
        _store.Save(r);
    }

    // 添付は Discord の 1 つの書き込みの上限に収まるように分けて書く (字は最初の書き込みに)
    private async Task PostPlayerMessage(Report r, string text, IReadOnlyList<MediaFile> media)
    {
        JsonObject Message(string t)
        {
            var embed = new JsonObject
            {
                ["author"] = new JsonObject { ["name"] = $"{r.Name} ({r.FriendCode})" },
                ["color"] = Green,
                ["footer"] = new JsonObject { ["text"] = PlayerFooter },
            };
            if (t.Length > 0) embed["description"] = t;
            return new JsonObject { ["embeds"] = new JsonArray { embed }, ["allowed_mentions"] = DiscordApi.NoMentions() };
        }

        if (media.Count == 0)
            await _discord.Post($"/channels/{r.ThreadId}/messages", Message(text)).ConfigureAwait(false);
        else
        {
            bool first = true;
            foreach (var chunk in Media.Chunks(media, _cfg.Reports.MaxMediaBytes))
            {
                var msg = Message(first ? text : "");
                msg["attachments"] = new JsonArray(chunk.Select((f, i) => (JsonNode)new JsonObject { ["id"] = i, ["filename"] = f.Name }).ToArray());
                await _discord.PostMultipart($"/channels/{r.ThreadId}/messages", msg, chunk.Select(f => (f.Name, f.Data)).ToList()).ConfigureAwait(false);
                first = false;
            }
        }
        lock (_cache) _cache.Remove(r.Id);
    }

    // スレッドの書き込みを古い順に (最初の報告の書き込み・管理のボタン・スタッフ同士の書き込みは除く)
    public async Task<List<ChatMessage>> Messages(Report r)
    {
        lock (_cache)
            if (_cache.TryGetValue(r.Id, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(3)) return c.list;
        var list = new List<ChatMessage>();
        string bot = await _discord.BotId().ConfigureAwait(false);
        var raw = new List<JsonNode>();
        string? before = null;
        // 新しい方から 100 件ずつ、最大 300 件
        for (int page = 0; page < 3; page++)
        {
            var arr = await _discord.Get($"/channels/{r.ThreadId}/messages?limit=100" + (before != null ? $"&before={before}" : "")).ConfigureAwait(false) as JsonArray;
            if (arr == null || arr.Count == 0) break;
            foreach (var m in arr) if (m != null) raw.Add(m);
            if (arr.Count < 100) break;
            before = arr[^1]?["id"]?.GetValue<string>();
        }
        raw.Reverse();
        foreach (var m in raw)
        {
            string id = m["id"]?.GetValue<string>() ?? "";
            if (id == r.FirstMessageId) continue;
            var author = m["author"];
            string authorId = author?["id"]?.GetValue<string>() ?? "";
            long time = DateTimeOffset.TryParse(m["timestamp"]?.GetValue<string>(), out var ts) ? ts.ToUnixTimeSeconds() : 0;
            var atts = ReadAttachments(m);
            if (authorId == bot)
            {
                var embed = (m["embeds"] as JsonArray)?.FirstOrDefault();
                string footer = embed?["footer"]?["text"]?.GetValue<string>() ?? "";
                string desc = embed?["description"]?.GetValue<string>() ?? "";
                if (footer == PlayerFooter)
                    list.Add(new ChatMessage { Id = id, From = "me", Name = r.Name, Text = desc, Time = time, Attachments = atts });
                else if (footer == SystemFooter)
                    list.Add(new ChatMessage { Id = id, From = "system", Text = desc, Time = time });
                continue;
            }
            if (author?["bot"]?.GetValue<bool>() == true) continue;
            if ((m["type"]?.GetValue<int>() ?? 0) is not (0 or 19)) continue; // 普通の書き込みと返信だけ
            string content = m["content"]?.GetValue<string>() ?? "";
            if (content.StartsWith(StaffNoteMark)) continue;
            if (string.IsNullOrWhiteSpace(content) && atts.Count == 0) continue;
            string name = author?["global_name"]?.GetValue<string>() ?? author?["username"]?.GetValue<string>() ?? "Staff";
            list.Add(new ChatMessage { Id = id, From = "staff", Name = name, Text = content, Time = time, Attachments = atts });
        }
        lock (_cache) _cache[r.Id] = (DateTime.UtcNow, list);
        return list;
    }

    private static List<Attachment> ReadAttachments(JsonNode m)
    {
        var list = new List<Attachment>();
        if (m["attachments"] is not JsonArray arr) return list;
        foreach (var a in arr)
        {
            if (a == null) continue;
            string type = a["content_type"]?.GetValue<string>() ?? "";
            string kind = type.StartsWith("image/") ? "image" : type.StartsWith("video/") ? "video" : "file";
            int w = a["width"]?.GetValue<int>() ?? 0, h = a["height"]?.GetValue<int>() ?? 0;
            string proxy = a["proxy_url"]?.GetValue<string>() ?? "";
            string thumb = "";
            if (kind == "image" && proxy.Length > 0 && w > 0 && h > 0)
            {
                // Discord の縮小 (長い辺 360 まで・ゲームが読める png で)
                float k = Math.Min(1f, 360f / Math.Max(w, h));
                thumb = proxy + (proxy.Contains('?') ? "&" : "?") + $"format=png&width={Math.Max(1, (int)(w * k))}&height={Math.Max(1, (int)(h * k))}";
            }
            list.Add(new Attachment
            {
                Id = a["id"]?.GetValue<string>() ?? "",
                Name = a["filename"]?.GetValue<string>() ?? "",
                Kind = kind,
                Url = a["url"]?.GetValue<string>() ?? "",
                Thumb = thumb,
                Width = w,
                Height = h,
                Size = a["size"]?.GetValue<long>() ?? 0,
            });
        }
        return list;
    }

    // スレッドに「— 〜 —」の知らせを書く (ゲームにも出る)
    private async Task System(Report r, string text, int color)
    {
        await _discord.Post($"/channels/{r.ThreadId}/messages", new JsonObject
        {
            ["embeds"] = new JsonArray { new JsonObject { ["description"] = text, ["color"] = color, ["footer"] = new JsonObject { ["text"] = SystemFooter } } },
            ["allowed_mentions"] = DiscordApi.NoMentions(),
        }).ConfigureAwait(false);
        lock (_cache) _cache.Remove(r.Id);
    }

    // ---- 管理のボタン ----

    private async Task PostPanel(Report r)
    {
        string channel = string.IsNullOrWhiteSpace(_cfg.Discord.StaffChannelId) ? r.ThreadId : _cfg.Discord.StaffChannelId;
        var msg = await _discord.Post($"/channels/{channel}/messages", Panel(r, "")).ConfigureAwait(false);
        r.PanelChannelId = channel;
        r.PanelMessageId = msg?["id"]?.GetValue<string>() ?? "";
        _store.Save(r);
    }

    public JsonObject Panel(Report r, string lastAction)
    {
        bool banned = _store.IsBanned(r.FriendCode);
        string state = banned ? "🚫 停止中" : r.Closed ? "🔒 閉じた" : r.Acked ? "👀 既読" : "🆕 未読";
        int color = banned ? Red : r.Closed ? Gray : r.Acked ? Green : Yellow;
        var fields = new JsonArray
        {
            Field("状態", state, true),
            Field("プレイヤー", $"{r.Name}\n`{r.FriendCode}`", true),
            Field("スレッド", string.IsNullOrEmpty(r.ThreadId) ? "-" : $"<#{r.ThreadId}>", true),
        };
        if (lastAction.Length > 0) fields.Add(Field("最後の操作", lastAction, false));
        var embed = new JsonObject
        {
            ["title"] = $"報告 #{r.Id}  {r.Title}",
            ["description"] = "「既読にする」を押すと、この報告は 1 時間に送れる数に数えなくなります (その人はまた送れます)。",
            ["color"] = color,
            ["fields"] = fields,
            ["footer"] = new JsonObject { ["text"] = PanelFooter },
        };
        JsonObject Btn(string label, int style, string action, bool disabled = false) => new()
        {
            ["type"] = 2, ["style"] = style, ["label"] = label, ["custom_id"] = $"mrp:{action}:{r.Id}", ["disabled"] = disabled,
        };
        var row = new JsonArray
        {
            Btn(r.Acked ? "既読" : "既読にする", 3, "ack", r.Acked),
            r.Closed ? Btn("開け直す", 1, "open") : Btn("閉じる", 2, "close"),
            banned ? Btn("停止を外す", 2, "unban") : Btn("停止", 4, "ban"),
        };
        return new JsonObject
        {
            ["embeds"] = new JsonArray { embed },
            ["components"] = new JsonArray { new JsonObject { ["type"] = 1, ["components"] = row } },
            ["allowed_mentions"] = DiscordApi.NoMentions(),
        };
    }

    // Gateway から (INTERACTION_CREATE)。type 3 = ボタン / 5 = 停止の理由の入力
    public async Task HandleInteraction(JsonNode d)
    {
        int type = d["type"]?.GetValue<int>() ?? 0;
        string iid = d["id"]?.GetValue<string>() ?? "", token = d["token"]?.GetValue<string>() ?? "";
        string customId = d["data"]?["custom_id"]?.GetValue<string>() ?? "";
        if (type is not (3 or 5) || !customId.StartsWith("mrp:")) return;
        var parts = customId.Split(':');
        if (parts.Length != 3) return;
        string action = parts[1];
        var r = _store.Get(parts[2]);

        Task Reply(string text) => _discord.Callback(iid, token, new JsonObject
        {
            ["type"] = 4,
            ["data"] = new JsonObject { ["content"] = text, ["flags"] = 64, ["allowed_mentions"] = DiscordApi.NoMentions() },
        });

        var member = d["member"];
        if (!_discord.IsStaff(member)) { await Reply("このボタンはスタッフだけが押せます。").ConfigureAwait(false); return; }
        if (r == null) { await Reply("この報告はもう残っていません。").ConfigureAwait(false); return; }
        var user = member?["user"];
        string who = user?["global_name"]?.GetValue<string>() ?? user?["username"]?.GetValue<string>() ?? "?";

        if (action == "ban")
        {
            // 理由を聞いてから止める
            await _discord.Callback(iid, token, new JsonObject
            {
                ["type"] = 9,
                ["data"] = new JsonObject
                {
                    ["custom_id"] = $"mrp:banr:{r.Id}",
                    ["title"] = "停止 (報告とチャットを止める)",
                    ["components"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = 1,
                            ["components"] = new JsonArray
                            {
                                new JsonObject { ["type"] = 4, ["custom_id"] = "reason", ["label"] = "理由 (なくてもよい・本人にも出ます)", ["style"] = 2, ["required"] = false, ["max_length"] = 300 },
                            },
                        },
                    },
                },
            }).ConfigureAwait(false);
            return;
        }

        string note, sys = "";
        int sysColor = Green;
        switch (action)
        {
            case "ack":
                if (r.Acked) { await Reply("もう既読です。").ConfigureAwait(false); return; }
                r.Acked = true;
                note = "既読";
                sys = "👀 スタッフが報告を読みました。";
                break;
            case "close":
                r.Closed = true;
                note = "閉じた";
                sys = "🔒 この報告は閉じられました。ありがとうございました！";
                sysColor = Gray;
                break;
            case "open":
                r.Closed = false;
                note = "開け直した";
                sys = "🔓 報告を開け直しました。";
                break;
            case "banr":
                string reason = DiscordApi.Clean(d["data"]?["components"]?[0]?["components"]?[0]?["value"]?.GetValue<string>(), 300);
                _store.AddBan(new Ban { FriendCode = r.FriendCode, Ip = r.Ip, Reason = reason, At = DateTime.UtcNow });
                r.Closed = true;
                r.Acked = true;
                note = "停止" + (reason.Length > 0 ? $" (理由: {reason})" : "");
                sys = "🚫 この報告とチャットは止められました。" + (reason.Length > 0 ? $"\n理由: {reason}" : "");
                sysColor = Red;
                break;
            case "unban":
                _store.RemoveBan(r.FriendCode);
                note = "停止を外した";
                break;
            default:
                return;
        }
        _store.Save(r);
        string last = $"{who}: {note} ・ <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>";
        // 先にボタンの書き込みを書き換えて答え (3 秒以内)、知らせは後から
        await _discord.Callback(iid, token, new JsonObject { ["type"] = 7, ["data"] = Panel(r, last) }).ConfigureAwait(false);
        _log.LogInformation("mrp report {Id}: {Action} by {Who}", r.Id, action, who);
        if (sys.Length > 0)
        {
            try { await System(r, sys, sysColor).ConfigureAwait(false); }
            catch (Exception e) { _log.LogWarning("mrp report {Id} notice: {Msg}", r.Id, e.Message); }
        }
    }
}
