using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Impostor.Api.Games.Managers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace MrpServer;

// /mrp/… の受け口
//   POST /mrp/report                    バグ報告 (multipart: meta = JSON, logs = zip, media = 画像・動画 (いくつでも))。→ { id, token }
//   GET  /mrp/report/{id}/messages      その報告のやり取り (X-Mrp-Token)
//   POST /mrp/report/{id}/messages      ゲームから書く ({ text } か multipart: text, media)
//   GET  /mrp/join/{code}               Discord の「参加する」から開くページ (mrp://join/<コード> へ飛ぶ)
public sealed partial class Http
{
    private readonly Reports _reports;
    private readonly Store _store;
    private readonly RateLimiter _limits;
    private readonly MrpConfig _cfg;
    private readonly DiscordApi _discord;
    private readonly IGameManager _games;
    private readonly ILogger<Http> _log;

    public Http(Reports reports, Store store, RateLimiter limits, MrpConfig cfg, DiscordApi discord, IGameManager games, ILogger<Http> log)
    {
        _reports = reports;
        _store = store;
        _limits = limits;
        _cfg = cfg;
        _discord = discord;
        _games = games;
        _log = log;
    }

    [GeneratedRegex(@"^/report/([a-f0-9]{10})/messages$")]
    private static partial Regex MessagesPath();

    [GeneratedRegex(@"^/join/([A-Za-z]{4}|[A-Za-z]{6})/?$")]
    private static partial Regex JoinPath();

    public async Task Handle(HttpContext ctx)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        string path = ctx.Request.Path.Value ?? "";
        string method = ctx.Request.Method;
        try
        {
            if (path == "/report" && method == "POST") { await NewReport(ctx); return; }
            var m = MessagesPath().Match(path);
            if (m.Success && method == "GET") { await GetMessages(ctx, m.Groups[1].Value); return; }
            if (m.Success && method == "POST") { await PostMessage(ctx, m.Groups[1].Value); return; }
            var j = JoinPath().Match(path);
            if (j.Success && method == "GET") { await JoinPage(ctx, j.Groups[1].Value.ToUpperInvariant()); return; }
            if (path is "/status" or "/status/") { await Json(ctx, 200, new { ok = true, reports = _discord.Ready, modeSwitch = GameModeFix.Installed }); return; }
            await Error(ctx, 404, "not_found");
        }
        catch (DiscordException e)
        {
            _log.LogWarning("mrp http {Path}: {Msg}", path, e.Message);
            await Error(ctx, 502, "discord");
        }
        catch (Exception e)
        {
            _log.LogError(e, "mrp http {Path}", path);
            await Error(ctx, 500, "server");
        }
    }

    private string Ip(HttpContext ctx)
    {
        if (_cfg.TrustProxyHeaders)
        {
            string cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
            if (!string.IsNullOrWhiteSpace(cf) && IPAddress.TryParse(cf.Trim(), out var a)) return a.MapToIPv4().ToString();
            string xff = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(xff, out var b)) return b.MapToIPv4().ToString();
        }
        return ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";
    }

    private static Task Json(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    // error = ゲームが言葉を選ぶための鍵
    private static Task Error(HttpContext ctx, int status, string error, object? extra = null) =>
        Json(ctx, status, new { error, extra });

    // 大きな添付を受けられるように、この受け口だけ受ける大きさを上げる
    private void AllowBody(HttpContext ctx, long max)
    {
        var f = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (f is { IsReadOnly: false }) f.MaxRequestBodySize = max;
    }

    private long MediaLimit => (long)_cfg.Reports.MaxMediaFiles * _cfg.Reports.MaxMediaBytes;

    // form の media を読む。数・大きさ・種類が違えば error を返す
    private async Task<(List<MediaFile> files, string? error)> ReadMedia(IFormCollection form)
    {
        var rc = _cfg.Reports;
        var list = new List<MediaFile>();
        var files = form.Files.GetFiles("media");
        if (files.Count > rc.MaxMediaFiles) return (list, "too_many_files");
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (f.Length > rc.MaxMediaBytes) return (list, "too_large");
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var data = ms.ToArray();
            if (Media.Detect(data) is not { } t) return (list, "bad_media");
            list.Add(new MediaFile(Media.SafeName(f.FileName, t.ext, i), data, t.kind));
        }
        return (list, null);
    }

    private async Task NewReport(HttpContext ctx)
    {
        if (!_discord.Ready || string.IsNullOrWhiteSpace(_cfg.Discord.ReportChannelId)) { await Error(ctx, 503, "disabled"); return; }
        string ip = Ip(ctx);
        var rc = _cfg.Reports;
        if (!ctx.Request.HasFormContentType) { await Error(ctx, 400, "bad_request"); return; }
        long max = rc.MaxLogBytes + MediaLimit + 256 * 1024;
        if (ctx.Request.ContentLength > max) { await Error(ctx, 413, "too_large"); return; }
        AllowBody(ctx, max);
        var form = await ctx.Request.ReadFormAsync();
        Reports.NewReport? n;
        try { n = JsonSerializer.Deserialize<Reports.NewReport>(form["meta"].ToString(), MrpConfig.Json); }
        catch (JsonException) { n = null; }
        if (n == null) { await Error(ctx, 400, "bad_request"); return; }
        n.FriendCode = n.FriendCode.Trim();
        if (!Reports.FriendCodePattern().IsMatch(n.FriendCode)) { await Error(ctx, 400, "no_friend_code"); return; }
        if (string.IsNullOrWhiteSpace(n.Title) || n.Title.Length > 200 || n.Body.Length > 6000) { await Error(ctx, 400, "bad_text"); return; }
        if (_store.BanFor(n.FriendCode, ip) != null) { await Error(ctx, 403, "banned"); return; }
        if (rc.RequireRecentConnection)
        {
            var seen = _store.Seen(n.FriendCode);
            if (seen == null || DateTime.UtcNow - seen.LastSeen > TimeSpan.FromHours(rc.RecentConnectionHours)) { await Error(ctx, 403, "not_seen"); return; }
        }

        byte[]? logs = null;
        var file = form.Files["logs"];
        if (file != null)
        {
            if (file.Length > rc.MaxLogBytes) { await Error(ctx, 413, "too_large"); return; }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            logs = ms.ToArray();
            // zip だけ (PK の頭)
            if (logs.Length < 4 || logs[0] != 'P' || logs[1] != 'K') logs = null;
        }
        var (media, mediaError) = await ReadMedia(form);
        if (mediaError != null) { await Error(ctx, mediaError == "too_large" ? 413 : 400, mediaError); return; }

        // 1 時間の枠 (スタッフが既読にした報告は数えない)。空いていれば記録を先に作る
        if (_store.TryReserve(n.FriendCode, DiscordApi.Clean(n.Name, 32), DiscordApi.Clean(n.Title, 90), ip, rc.MaxPerHourPerPlayer, rc.MaxPerHourPerIp) is not { } made)
        { await Error(ctx, 429, "too_many"); return; }
        try
        {
            string trust = await _reports.Create(made.report, n, ip, logs, media);
            await Json(ctx, 200, new { id = made.report.Id, token = made.token, trust });
        }
        catch
        {
            _store.Remove(made.report.Id);
            throw;
        }
    }

    private Report? Auth(HttpContext ctx, string id)
    {
        var r = _store.Get(id);
        return r != null && Store.TokenOk(r, ctx.Request.Headers["X-Mrp-Token"].ToString()) ? r : null;
    }

    private async Task GetMessages(HttpContext ctx, string id)
    {
        var r = Auth(ctx, id);
        if (r == null) { await Error(ctx, 404, "not_found"); return; }
        if (!_limits.Allow("g:" + id, 40, TimeSpan.FromMinutes(1))) { await Error(ctx, 429, "too_many"); return; }
        var list = await _reports.Messages(r);
        await Json(ctx, 200, new { closed = r.Closed, messages = list });
    }

    private sealed class ChatIn
    {
        public string Text { get; set; } = "";
    }

    private async Task PostMessage(HttpContext ctx, string id)
    {
        var r = Auth(ctx, id);
        if (r == null) { await Error(ctx, 404, "not_found"); return; }
        if (r.Closed) { await Error(ctx, 409, "closed"); return; }
        string ip = Ip(ctx);
        if (_store.BanFor(r.FriendCode, ip) != null) { await Error(ctx, 403, "banned"); return; }
        var rc = _cfg.Reports;
        if (!_limits.Allow("c:" + id, 1, TimeSpan.FromSeconds(rc.ChatCooldownSeconds), false)
            || !_limits.Allow("ch:" + r.FriendCode, rc.MaxChatPerHour, TimeSpan.FromHours(1), false))
        { await Error(ctx, 429, "too_many"); return; }
        string raw;
        var media = new List<MediaFile>();
        if (ctx.Request.HasFormContentType)
        {
            long max = MediaLimit + 64 * 1024;
            if (ctx.Request.ContentLength > max) { await Error(ctx, 413, "too_large"); return; }
            AllowBody(ctx, max);
            var form = await ctx.Request.ReadFormAsync();
            raw = form["text"].ToString();
            string? mediaError;
            (media, mediaError) = await ReadMedia(form);
            if (mediaError != null) { await Error(ctx, mediaError == "too_large" ? 413 : 400, mediaError); return; }
        }
        else
        {
            ChatIn? body;
            try { body = await JsonSerializer.DeserializeAsync<ChatIn>(ctx.Request.Body, MrpConfig.Json); }
            catch (JsonException) { body = null; }
            raw = body?.Text ?? "";
        }
        string text = DiscordApi.Clean(raw, 600);
        if (text.Length == 0 && media.Count == 0) { await Error(ctx, 400, "bad_text"); return; }
        _limits.Allow("c:" + id, 1, TimeSpan.FromSeconds(rc.ChatCooldownSeconds));
        _limits.Allow("ch:" + r.FriendCode, rc.MaxChatPerHour, TimeSpan.FromHours(1));
        await _reports.PostFromPlayer(r, text, media);
        await Json(ctx, 200, new { ok = true });
    }

    private async Task JoinPage(HttpContext ctx, string code)
    {
        var game = _games.Games.FirstOrDefault(g => g.Code.Code == code);
        bool open = game != null && game.IsPublic;
        string players = game != null ? $"{game.PlayerCount} / {game.Options.MaxPlayers}" : "-";
        string host = WebUtility.HtmlEncode(game?.Host?.Client.Name ?? "");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(JoinHtml
            .Replace("{{CODE}}", code)
            .Replace("{{HOST}}", host)
            .Replace("{{PLAYERS}}", players)
            .Replace("{{OPEN}}", open ? "1" : "0"));
    }

    private const string JoinHtml = """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>More Roles Plus — 部屋 {{CODE}}</title>
<meta property="og:title" content="More Roles Plus の部屋 {{CODE}}">
<meta property="og:description" content="押すと More Roles Plus のゲームが開いて部屋に入ります">
<meta name="theme-color" content="#00e676">
<style>
:root{--a:#00e676;--bg:#0e1013;--card:#171a1f;--sub:#9aa3ad}
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:#fff;font-family:system-ui,"Segoe UI","Yu Gothic UI",sans-serif;padding:16px}
.card{width:100%;max-width:440px;background:var(--card);border-radius:18px;padding:28px;box-shadow:0 10px 40px #0008;text-align:center;animation:pop .35s ease-out}
@keyframes pop{from{transform:scale(.96);opacity:0}to{transform:none;opacity:1}}
h1{font-size:20px;margin:0 0 6px}.sub{color:var(--sub);font-size:14px;margin:0 0 22px}
.code{font-size:44px;letter-spacing:.18em;font-weight:800;color:var(--a);margin:6px 0 4px;user-select:all}
.meta{color:var(--sub);font-size:13px;margin-bottom:22px}
a.btn,button{display:block;width:100%;border:0;border-radius:12px;padding:14px;font-size:16px;font-weight:700;cursor:pointer;text-decoration:none;margin-top:10px;transition:transform .1s,filter .1s}
a.btn{background:var(--a);color:#062b16}button{background:#262b33;color:#fff}a.btn:hover,button:hover{filter:brightness(1.1)}a.btn:active,button:active{transform:scale(.98)}
.note{color:var(--sub);font-size:12px;margin-top:18px;line-height:1.6}.closed{color:#ff8a80;font-size:14px;margin-bottom:14px}
</style>
</head>
<body>
<div class="card">
  <h1>More Roles Plus の部屋</h1>
  <p class="sub">{{HOST}}</p>
  <div class="code" id="code">{{CODE}}</div>
  <div class="meta">人数 {{PLAYERS}}</div>
  <div class="closed" id="closed" hidden>この部屋は今は公開されていません (終わったか、非公開になりました)</div>
  <a class="btn" id="go" href="mrp://join/{{CODE}}">ゲームを開いて参加</a>
  <button id="copy">部屋コードをコピー</button>
  <p class="note">押すと「MoreRolesPlusInstaller を開きますか？」と聞かれるので「開く」を選んでください。<br>
  開かない時は、MRP のインストーラーを一度起動してから押し直すか、コードをコピーしてゲームで入力してください。</p>
</div>
<script>
const open = "{{OPEN}}" === "1";
if (!open) document.getElementById("closed").hidden = false;
else setTimeout(() => { location.href = "mrp://join/{{CODE}}"; }, 400);
document.getElementById("copy").onclick = async (e) => {
  try { await navigator.clipboard.writeText("{{CODE}}"); e.target.textContent = "コピーしました ✓"; } catch { }
};
</script>
</body>
</html>
""";
}
