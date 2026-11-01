using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;

namespace MoreRolesPlus.Report;

// バグ報告を MRP のサーバー (/mrp/report) へ送り、Discord のスタッフとのやり取りを読み書きする。
// 送った報告は BepInEx/config/mrp-reports.json に控える (番号と、その報告に書くための合言葉)
internal static class ReportClient
{
    public sealed class Saved
    {
        public string Id { get; set; } = "";
        public string Token { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "bug";
        public long Created { get; set; }
        public string LastRead { get; set; } = "";   // 読んだ最後の書き込み (スタッフの返事の数え方)
        public string LastStaff { get; set; } = "";  // 見つけた最新のスタッフの書き込み
        public bool Closed { get; set; }
    }

    public sealed class Attachment
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";   // "image" / "video" / "file"
        public string Url { get; set; } = "";
        public string Thumb { get; set; } = "";  // 小さな絵 (画像だけ)
        public int Width { get; set; }
        public int Height { get; set; }
        public long Size { get; set; }
    }

    public sealed class Message
    {
        public string Id { get; set; } = "";
        public string From { get; set; } = "";
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
        public long Time { get; set; }
        public List<Attachment> Attachments { get; set; } = new();
    }

    public sealed class Thread
    {
        public bool Closed { get; set; }
        public List<Message> Messages { get; set; } = new();
    }

    // みんなのバグの報告 (題・版・日時・直ったか だけ)
    public sealed class KnownBug
    {
        public bool Closed { get; set; }      // 直った (スタッフが閉じた)
        public long ClosedAt { get; set; }
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string ModVersion { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public long Created { get; set; }
    }

    private sealed class KnownList
    {
        public List<KnownBug> Reports { get; set; } = new();
    }

    // 送る前に画面に出す、一緒に送られる物
    public sealed class Info
    {
        public string FriendCode = "", Puid = "", Name = "", ModVersion = "", GameVersion = "", Platform = "", Region = "", Scene = "", Language = "", RoomCode = "";
    }

    // 付けられる画像・動画 (サーバーの既定と同じ。Discord の無料の上限が 10MB)
    public const int MaxFiles = 4;
    public const long MaxFileBytes = 9 * 1024 * 1024 + 512 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static List<Saved> _saved;

    private static string StorePath => Path.Combine(BepInEx.Paths.ConfigPath, "mrp-reports.json");

    public static string BaseUrl => Net.MrpRegion.WebBase + "/mrp";

    public static List<Saved> All
    {
        get
        {
            if (_saved != null) return _saved;
            try { _saved = File.Exists(StorePath) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(StorePath), Json) : null; }
            catch { _saved = null; }
            return _saved ??= new List<Saved>();
        }
    }

    public static void Save()
    {
        try { File.WriteAllText(StorePath, JsonSerializer.Serialize(All, Json)); }
        catch (Exception e) { Plugin.Logger.LogWarning($"report: save failed: {e.Message}"); }
    }

    public static int Unread
    {
        get
        {
            int n = 0;
            foreach (var s in All) if (s.LastStaff.Length > 0 && s.LastStaff != s.LastRead && !Before(s.LastStaff, s.LastRead)) n++;
            return n;
        }
    }

    // Discord の番号は時刻順の数 (長さを揃えて比べる)
    public static bool Before(string a, string b) => a.Length != b.Length ? a.Length < b.Length : string.CompareOrdinal(a, b) < 0;

    // 本編の側 (メインスレッド) で読む
    public static Info Collect()
    {
        var i = new Info { ModVersion = Plugin.Version, GameVersion = Application.version };
        try
        {
            var eos = EOSManager.Instance;
            if (eos)
            {
                i.FriendCode = eos.FriendCode ?? "";
                i.Puid = eos.ProductUserId ?? "";
            }
        }
        catch { }
        try { i.Name = AmongUs.Data.DataManager.Player.Customization.Name ?? ""; } catch { }
        i.Platform = $"{SystemInfo.operatingSystem} / {Application.platform}";
        try { i.Region = ServerManager.Instance ? ServerManager.Instance.CurrentRegion?.Name ?? "" : ""; } catch { }
        i.Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        var c = AmongUsClient.Instance;
        if (c && c.GameState != InnerNet.InnerNetClient.GameStates.NotJoined) i.RoomCode = InnerNet.GameCode.IntToGameName(c.GameId);
        i.Language = Lang.Current.ToString();
        return i;
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"MoreRolesPlus/{Plugin.Version}");
        return h;
    }

    // 失敗はサーバーが返した鍵 (not_seen など) か "network"
    public sealed class ReportException : Exception
    {
        public readonly string Code;
        public ReportException(string code) : base(code) { Code = code; }
    }

    private static async Task<string> Read(HttpResponseMessage res)
    {
        string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (res.IsSuccessStatusCode) return body;
        string code = "http" + (int)res.StatusCode;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } s) code = s;
        }
        catch { }
        throw new ReportException(code);
    }

    // 画像・動画を form に足す (裏のスレッドで読む)
    private static void AddMedia(MultipartFormDataContent form, IReadOnlyList<string> media)
    {
        if (media == null) return;
        foreach (var path in media)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception) { throw new ReportException("file_unreadable"); }
            if (data.Length > MaxFileBytes) throw new ReportException("too_large");
            var c = new ByteArrayContent(data);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(c, "media", Path.GetFileName(path));
        }
    }

    // 裏のスレッド。logs = zip (null = 付けない)・media = 付ける画像・動画の道筋
    public static async Task<Saved> Send(Info info, string category, string title, string body, byte[] logs, IReadOnlyList<string> media)
    {
        var meta = new Dictionary<string, string>
        {
            ["friendCode"] = info.FriendCode, ["puid"] = info.Puid, ["name"] = info.Name, ["category"] = category,
            ["title"] = title, ["body"] = body, ["modVersion"] = info.ModVersion, ["gameVersion"] = info.GameVersion,
            ["platform"] = info.Platform, ["region"] = info.Region, ["scene"] = info.Scene, ["language"] = info.Language, ["roomCode"] = info.RoomCode,
        };
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(meta), Encoding.UTF8), "meta");
        if (logs != null)
        {
            var file = new ByteArrayContent(logs);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "logs", "logs.zip");
        }
        AddMedia(form, media);
        using var http = Client(TimeSpan.FromSeconds(media is { Count: > 0 } ? 240 : 60));
        HttpResponseMessage res;
        try { res = await http.PostAsync(BaseUrl + "/report", form).ConfigureAwait(false); }
        catch (Exception) { throw new ReportException("network"); }
        using (res)
        {
            string json = await Read(res).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return new Saved
            {
                Id = doc.RootElement.GetProperty("id").GetString(),
                Token = doc.RootElement.GetProperty("token").GetString(),
                Title = title,
                Category = category,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
    }

    public static async Task<List<KnownBug>> OpenBugs()
    {
        using var http = Client(TimeSpan.FromSeconds(20));
        HttpResponseMessage res;
        try { res = await http.GetAsync(BaseUrl + "/reports").ConfigureAwait(false); }
        catch (Exception) { throw new ReportException("network"); }
        using (res) return JsonSerializer.Deserialize<KnownList>(await Read(res).ConfigureAwait(false), Json)?.Reports ?? new List<KnownBug>();
    }

    // ---- 部屋の MRP の版 (マッチメイキングに出す) ----

    private static string _announced;
    private static Task _announcing;

    // 毎フレーム: MRP のサーバーで部屋のホストになったら、その部屋の版を 1 回知らせる
    public static void TickRoom()
    {
        var c = AmongUsClient.Instance;
        if (!c || !c.AmHost || c.NetworkMode != NetworkModes.OnlineGame || c.GameState == InnerNet.InnerNetClient.GameStates.NotJoined)
        {
            _announced = null;
            return;
        }
        if (_announcing != null && !_announcing.IsCompleted) return;
        string code = InnerNet.GameCode.IntToGameName(c.GameId);
        if (string.IsNullOrEmpty(code) || code == _announced || !Net.MrpRegion.IsCurrent()) return;
        _announced = code;
        string version = Plugin.Version;
        _announcing = Task.Run(async () =>
        {
            try
            {
                using var http = Client(TimeSpan.FromSeconds(15));
                var body = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["code"] = code, ["modVersion"] = version }), Encoding.UTF8, "application/json");
                using var res = await http.PostAsync(BaseUrl + "/room", body).ConfigureAwait(false);
            }
            catch (Exception) { }
        });
    }

    public static async Task<Thread> Fetch(Saved s)
    {
        using var http = Client(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/report/{s.Id}/messages");
        req.Headers.Add("X-Mrp-Token", s.Token);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req).ConfigureAwait(false); }
        catch (Exception) { throw new ReportException("network"); }
        using (res) return JsonSerializer.Deserialize<Thread>(await Read(res).ConfigureAwait(false), Json) ?? new Thread();
    }

    public static async Task Post(Saved s, string text, IReadOnlyList<string> media = null)
    {
        bool withMedia = media is { Count: > 0 };
        using var http = Client(TimeSpan.FromSeconds(withMedia ? 240 : 20));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/report/{s.Id}/messages");
        if (withMedia)
        {
            var form = new MultipartFormDataContent { { new StringContent(text ?? "", Encoding.UTF8), "text" } };
            AddMedia(form, media);
            req.Content = form;
        }
        else req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text }), Encoding.UTF8, "application/json");
        req.Headers.Add("X-Mrp-Token", s.Token);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req).ConfigureAwait(false); }
        catch (Exception) { throw new ReportException("network"); }
        using (res) await Read(res).ConfigureAwait(false);
    }

    // 添付の小さな絵 (Discord の CDN)
    public static async Task<byte[]> Download(string url)
    {
        using var http = Client(TimeSpan.FromSeconds(20));
        return await http.GetByteArrayAsync(url).ConfigureAwait(false);
    }

    // 付ける前に確かめる (種類と大きさ)。だめなら言葉の鍵
    public static string CheckFile(string path)
    {
        try
        {
            if (Array.IndexOf(FilePicker.Extensions, Path.GetExtension(path).ToLowerInvariant()) < 0) return "Report.ErrBadMedia";
            if (new FileInfo(path).Length > MaxFileBytes) return "Report.ErrFileTooLarge";
            return null;
        }
        catch { return "Report.ErrFileUnreadable"; }
    }

    public static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024f * 1024f):0.0}MB" : $"{Math.Max(1, bytes / 1024)}KB";

    // ---- ログ ----

    private const int MaxPerFile = 2 * 1024 * 1024;

    // 裏のスレッドで呼んでよい (パスは先にメインスレッドで集める)
    public static byte[] ZipLogs(List<(string name, string path)> files, string infoText)
    {
        string user = Environment.UserName;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "info.txt", infoText);
            foreach (var (name, path) in files)
            {
                string text = Tail(path);
                if (text == null) continue;
                // PC の利用者名は伏せる (ログのパスに出る)
                if (user.Length >= 2) text = text.Replace(user, "<user>");
                Add(zip, name, text);
            }
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    // 書き込み中のログも読めるように共有で開き、長い物は最後の 2MB だけ
    private static string Tail(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - MaxPerFile);
            fs.Seek(start, SeekOrigin.Begin);
            using var r = new StreamReader(fs, Encoding.UTF8);
            string s = r.ReadToEnd();
            return start > 0 ? "…(前を省略)…\n" + s : s;
        }
        catch (Exception e) { return $"(読めませんでした: {e.Message})"; }
    }

    public static List<(string name, string path)> LogFiles()
    {
        var list = new List<(string, string)>
        {
            ("LogOutput.log", Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log")),
            ("ErrorLog.log", Path.Combine(BepInEx.Paths.BepInExRootPath, "ErrorLog.log")),
        };
#if !ANDROID
        // Application.consoleLogPath は Android 版のゲームに無い (Android のログは logcat)
        try { list.Add(("Player.log", Application.consoleLogPath)); } catch { }
#endif
        return list;
    }

    public static string InfoText(Info i)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MRP {i.ModVersion} / Among Us {i.GameVersion}");
        sb.AppendLine($"{i.Platform}");
        sb.AppendLine($"Region {i.Region} / Scene {i.Scene} / Room {i.RoomCode} / Lang {i.Language}");
        sb.AppendLine($"Screen {Screen.width}x{Screen.height} / GPU {SystemInfo.graphicsDeviceName} / RAM {SystemInfo.systemMemorySize}MB");
        try
        {
            sb.AppendLine("Plugins:");
            foreach (var f in Directory.GetFiles(BepInEx.Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
                sb.AppendLine("  " + Path.GetFileName(f) + "  " + new FileInfo(f).Length);
        }
        catch { }
        return sb.ToString();
    }
}
