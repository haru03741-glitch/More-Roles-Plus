using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MrpServer;

// Discord の REST。返事は報告のスレッドを読みに行って拾う (管理のボタンが押されたのは DiscordGateway で受ける)
public sealed class DiscordApi
{
    private const string Base = "https://discord.com/api/v10";
    private readonly HttpClient _http;
    private readonly MrpConfig _cfg;
    private readonly ILogger<DiscordApi> _log;
    private readonly SemaphoreSlim _gate = new(1, 1); // 同時に投げない (429 を受けにくく)
    private string? _botId;
    private readonly Dictionary<string, int> _channelTypes = new();

    public DiscordApi(MrpConfig cfg, ILogger<DiscordApi> log) : this(cfg, log, new HttpClientHandler()) { }

    // handler = 試験で Discord の代わりに答える物を差し込む口
    public DiscordApi(MrpConfig cfg, ILogger<DiscordApi> log, HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _cfg = cfg;
        _log = log;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", cfg.Discord.BotToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/haru03741-glitch/More-Roles-Plus, 1.0)");
    }

    public bool Ready => _cfg.DiscordReady;

    // 誰も通知されないようにする (報告の文に @everyone などが入っていても鳴らさない)
    public static JsonObject NoMentions() => new() { ["parse"] = new JsonArray() };

    private async Task<JsonNode?> Send(Func<HttpRequestMessage> make, bool gated = true)
    {
        if (gated) await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                using var req = make();
                using var res = await _http.SendAsync(req).ConfigureAwait(false);
                string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (res.StatusCode == (HttpStatusCode)429)
                {
                    double wait = 1;
                    try { wait = JsonNode.Parse(body)?["retry_after"]?.GetValue<double>() ?? 1; } catch { }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 0.2, 30))).ConfigureAwait(false);
                    continue;
                }
                if (res.StatusCode == HttpStatusCode.NoContent) return null;
                if (!res.IsSuccessStatusCode)
                {
                    _log.LogWarning("discord {Method} {Url}: {Status} {Body}", req.Method, req.RequestUri, (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
                    throw new DiscordException((int)res.StatusCode, body);
                }
                return string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body);
            }
            throw new DiscordException(429, "rate limited");
        }
        finally { if (gated) _gate.Release(); }
    }

    private static HttpRequestMessage JsonReq(HttpMethod m, string path, JsonNode? body)
    {
        var req = new HttpRequestMessage(m, Base + path);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    public Task<JsonNode?> Get(string path) => Send(() => JsonReq(HttpMethod.Get, path, null));
    public Task<JsonNode?> Post(string path, JsonNode body) => Send(() => JsonReq(HttpMethod.Post, path, body));
    public Task<JsonNode?> Patch(string path, JsonNode body) => Send(() => JsonReq(HttpMethod.Patch, path, body));
    public Task<JsonNode?> Delete(string path) => Send(() => JsonReq(HttpMethod.Delete, path, null));

    // ボタンへの答え。3 秒以内に返さないといけないので、大きな添付の送信などを待たずに投げる
    public Task<JsonNode?> Callback(string interactionId, string token, JsonNode body) =>
        Send(() => JsonReq(HttpMethod.Post, $"/interactions/{interactionId}/{token}/callback", body), false);

    // 添付付き。payload は payload_json に入れ、files[i] を後ろに付ける
    public Task<JsonNode?> PostMultipart(string path, JsonObject payload, IReadOnlyList<(string name, byte[] data)> files) =>
        Send(() =>
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), "payload_json");
            for (int i = 0; i < files.Count; i++)
            {
                var c = new ByteArrayContent(files[i].data);
                c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(c, $"files[{i}]", files[i].name);
            }
            return new HttpRequestMessage(HttpMethod.Post, Base + path) { Content = form };
        });

    public async Task<string> BotId()
    {
        if (_botId != null) return _botId;
        var me = await Get("/users/@me").ConfigureAwait(false);
        return _botId = me?["id"]?.GetValue<string>() ?? "";
    }

    // 0 = テキスト / 15 = フォーラム
    public async Task<int> ChannelType(string id)
    {
        lock (_channelTypes) if (_channelTypes.TryGetValue(id, out int t)) return t;
        var ch = await Get($"/channels/{id}").ConfigureAwait(false);
        int type = ch?["type"]?.GetValue<int>() ?? 0;
        lock (_channelTypes) _channelTypes[id] = type;
        return type;
    }

    // 管理のボタンを押してよい人か (押した人の情報 = interaction の member)。
    // スタッフの役職を決めていればその役職の人、決めていなければサーバーの管理者だけ (管理者はどちらでも押せる)
    public bool IsStaff(JsonNode? member)
    {
        if (member == null) return false;
        const ulong Administrator = 1 << 3;
        if (ulong.TryParse(member["permissions"]?.GetValue<string>(), out ulong perms) && (perms & Administrator) != 0) return true;
        string role = _cfg.Discord.StaffRoleId;
        return !string.IsNullOrWhiteSpace(role) && member["roles"] is JsonArray roles && roles.Any(r => r?.GetValue<string>() == role);
    }

    public static string Clean(string? s, int max)
    {
        s ??= "";
        s = new string(s.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    public static string Json(object o) => JsonSerializer.Serialize(o);
}

public sealed class DiscordException : Exception
{
    public int Status { get; }
    public DiscordException(int status, string body) : base($"discord {status}: {body}") { Status = status; }
}
