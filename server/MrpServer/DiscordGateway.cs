using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MrpServer;

// Discord の Gateway に常時つなぎ、管理のボタンが押されたの (INTERACTION_CREATE) だけを受け取る。
// ボタンの押下は intents が 0 でも届く。切れたら少し待って最初からつなぎ直す
public sealed class DiscordGateway : BackgroundService
{
    private const string Url = "wss://gateway.discord.gg/?v=10&encoding=json";
    private readonly DiscordApi _discord;
    private readonly Reports _reports;
    private readonly MrpConfig _cfg;
    private readonly ILogger<DiscordGateway> _log;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public DiscordGateway(DiscordApi discord, Reports reports, MrpConfig cfg, ILogger<DiscordGateway> log)
    {
        _discord = discord;
        _reports = reports;
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_discord.Ready) return;
        var wait = TimeSpan.FromSeconds(5);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var close = await RunOnce(stop).ConfigureAwait(false);
                // 4004 = トークンが違う / 4013・4014 = intents の間違い (つなぎ直しても同じ)
                if (close is WebSocketCloseStatus s && (int)s is 4004 or 4013 or 4014)
                {
                    _log.LogError("MRP: Discord の Gateway に断られました ({Code})。BotToken を確かめてください", (int)s);
                    wait = TimeSpan.FromMinutes(10);
                }
                else wait = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogWarning("MRP: Discord の Gateway が切れました: {Msg}", e.Message);
                wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 120));
            }
            try { await Task.Delay(wait, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<WebSocketCloseStatus?> RunOnce(CancellationToken stop)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Url), stop).ConfigureAwait(false);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        long? seq = null;

        var hello = await Receive(ws, stop).ConfigureAwait(false);
        int interval = hello?["d"]?["heartbeat_interval"]?.GetValue<int>() ?? 41250;
        var heartbeat = Task.Run(async () =>
        {
            await Task.Delay((int)(interval * Random.Shared.NextDouble()), run.Token).ConfigureAwait(false);
            while (!run.IsCancellationRequested)
            {
                await SendJson(ws, new JsonObject { ["op"] = 1, ["d"] = seq }, run.Token).ConfigureAwait(false);
                await Task.Delay(interval, run.Token).ConfigureAwait(false);
            }
        }, run.Token);

        await SendJson(ws, new JsonObject
        {
            ["op"] = 2,
            ["d"] = new JsonObject
            {
                ["token"] = _cfg.Discord.BotToken,
                ["intents"] = 0,
                ["properties"] = new JsonObject { ["os"] = "linux", ["browser"] = "mrp", ["device"] = "mrp" },
            },
        }, stop).ConfigureAwait(false);

        try
        {
            while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var msg = await Receive(ws, stop).ConfigureAwait(false);
                if (msg == null) break;
                if (msg["s"] is JsonValue sv && sv.TryGetValue(out long s)) seq = s;
                int op = msg["op"]?.GetValue<int>() ?? -1;
                switch (op)
                {
                    case 0:
                        string t = msg["t"]?.GetValue<string>() ?? "";
                        if (t == "READY") _log.LogInformation("MRP: Discord の Gateway につなぎました (管理のボタンを受け付けます)");
                        else if (t == "INTERACTION_CREATE" && msg["d"] is JsonNode d)
                            _ = Task.Run(async () =>
                            {
                                try { await _reports.HandleInteraction(d).ConfigureAwait(false); }
                                catch (Exception e) { _log.LogWarning("mrp interaction: {Msg}", e.Message); }
                            });
                        break;
                    case 1:
                        await SendJson(ws, new JsonObject { ["op"] = 1, ["d"] = seq }, stop).ConfigureAwait(false);
                        break;
                    case 7: // つなぎ直して
                    case 9: // 続きはできない
                        return null;
                }
            }
            return ws.CloseStatus;
        }
        finally
        {
            run.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { }
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private async Task SendJson(ClientWebSocket ws, JsonNode body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    // 1 つの知らせ (分かれて届くのをつなぐ)。閉じられたら null
    private static async Task<JsonNode?> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
            if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("gateway message too large");
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }
}
