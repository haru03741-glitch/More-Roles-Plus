using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Impostor.Api.Events;
using Impostor.Api.Events.Client;
using Impostor.Api.Games;
using Impostor.Api.Games.Managers;
using Impostor.Api.Innersloth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MrpServer;

// つないだ人のフレンドコードを控える (バグ報告の本人確認に使う)
public sealed class PlayerTracker : IEventListener
{
    private readonly Store _store;

    public PlayerTracker(Store store) { _store = store; }

    [EventListener]
    public void OnConnected(IClientConnectedEvent e)
    {
        var c = e.Client;
        _store.Touch(c.FriendCode ?? "", c.Puid ?? "", c.Name, c.Connection?.EndPoint.Address.MapToIPv4().ToString() ?? "");
    }

    [EventListener]
    public void OnJoined(IGamePlayerJoinedEvent e)
    {
        var c = e.Player.Client;
        _store.Touch(c.FriendCode ?? "", c.Puid ?? "", c.Name, c.Connection?.EndPoint.Address.MapToIPv4().ToString() ?? "");
    }
}

// 公開した部屋を Discord のマッチメイキングのチャンネルへ出す。人数・状態が変われば書き換え、非公開・終わったら消す。
// 「参加する」は https のページ (/mrp/join/<コード>) へのリンクで、そこから mrp:// でインストーラー経由でゲームが部屋に入る
public sealed class Matchmaking : BackgroundService, IEventListener
{
    private readonly DiscordApi _discord;
    private readonly MrpConfig _cfg;
    private readonly IGameManager _games;
    private readonly ILogger<Matchmaking> _log;
    private readonly ConcurrentDictionary<int, byte> _dirty = new();
    private readonly Dictionary<int, (string messageId, string last)> _posted = new();

    public Matchmaking(DiscordApi discord, MrpConfig cfg, IGameManager games, ILogger<Matchmaking> log)
    {
        _discord = discord;
        _cfg = cfg;
        _games = games;
        _log = log;
    }

    private bool Enabled => _cfg.Matchmaking.Enabled && _discord.Ready && !string.IsNullOrWhiteSpace(_cfg.Discord.MatchmakingChannelId);

    private void Mark(IGame g)
    {
        if (Enabled) _dirty[g.Code.Value] = 0;
    }

    [EventListener] public void OnAlter(IGameAlterEvent e) => Mark(e.Game);
    [EventListener] public void OnJoined(IGamePlayerJoinedEvent e) => Mark(e.Game);
    [EventListener] public void OnLeft(IGamePlayerLeftEvent e) => Mark(e.Game);
    [EventListener] public void OnStarted(IGameStartedEvent e) => Mark(e.Game);
    [EventListener] public void OnEnded(IGameEndedEvent e) => Mark(e.Game);
    [EventListener] public void OnDestroyed(IGameDestroyedEvent e) => Mark(e.Game);
    [EventListener] public void OnHost(IGameHostChangedEvent e) => Mark(e.Game);
    [EventListener] public void OnOptions(IGameOptionsChangedEvent e) => Mark(e.Game);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(2500, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            foreach (int code in _dirty.Keys.ToList())
            {
                _dirty.TryRemove(code, out _);
                try { await Sync(code).ConfigureAwait(false); }
                catch (Exception e) { _log.LogWarning("mrp matchmaking {Code}: {Msg}", GameCode.From(code).Code, e.Message); }
            }
        }
        // 止める時は出している物を全部消す
        foreach (var (code, p) in _posted.ToList())
        {
            try { await _discord.Delete($"/channels/{_cfg.Discord.MatchmakingChannelId}/messages/{p.messageId}").ConfigureAwait(false); }
            catch { }
        }
    }

    private bool Listed(IGame? g)
    {
        if (g == null || !g.IsPublic || g.GameState is GameStates.Destroyed) return false;
        if (_cfg.Matchmaking.OnlyMrpGames && (g.ModGuid?.ToString() ?? "") != _cfg.ModGuid) return false;
        return g.Host != null;
    }

    private async Task Sync(int code)
    {
        string channel = _cfg.Discord.MatchmakingChannelId;
        var game = _games.Games.FirstOrDefault(g => g.Code.Value == code);
        _posted.TryGetValue(code, out var posted);
        if (!Listed(game))
        {
            if (posted.messageId == null) return;
            _posted.Remove(code);
            try { await _discord.Delete($"/channels/{channel}/messages/{posted.messageId}").ConfigureAwait(false); }
            catch (DiscordException e) when (e.Status == 404) { }
            return;
        }

        var message = Render(game!);
        string snapshot = message.ToJsonString();
        if (snapshot == posted.last) return;
        if (posted.messageId != null)
        {
            try
            {
                await _discord.Patch($"/channels/{channel}/messages/{posted.messageId}", message).ConfigureAwait(false);
                _posted[code] = (posted.messageId, snapshot);
                return;
            }
            catch (DiscordException e) when (e.Status == 404) { } // 手で消された → 出し直す
        }
        var sent = await _discord.Post($"/channels/{channel}/messages", message).ConfigureAwait(false);
        _posted[code] = (sent?["id"]?.GetValue<string>() ?? "", snapshot);
    }

    private static readonly string[] MapNames = { "The Skeld", "MIRA HQ", "Polus", "The Skeld (逆)", "The Airship", "The Fungle" };

    private JsonObject Render(IGame g)
    {
        string code = g.Code.Code;
        bool lobby = g.GameState is GameStates.NotStarted or GameStates.Ended;
        int max = g.Options.MaxPlayers;
        int count = g.PlayerCount;
        string host = DiscordApi.Clean(g.Host?.Client.Name, 32);
        int mapIndex = (int)g.Options.Map;
        string map = mapIndex >= 0 && mapIndex < MapNames.Length ? MapNames[mapIndex] : g.Options.Map.ToString();
        string mode = g.Options.GameMode is GameModes.HideNSeek or GameModes.SeekFools ? "かくれんぼ" : "通常";
        bool full = count >= max;

        var embed = new JsonObject
        {
            ["title"] = lobby ? (full ? $"🟡 {host} の部屋 (満員)" : $"🟢 {host} の部屋 — 募集中") : $"🎮 {host} の部屋 — 試合中",
            ["color"] = lobby && !full ? 0x00E676 : 0x5A6270,
            ["fields"] = new JsonArray
            {
                new JsonObject { ["name"] = "人数", ["value"] = $"{count} / {max}", ["inline"] = true },
                new JsonObject { ["name"] = "マップ", ["value"] = map, ["inline"] = true },
                new JsonObject { ["name"] = "モード", ["value"] = mode, ["inline"] = true },
                new JsonObject { ["name"] = "インポスター", ["value"] = g.Options.NumImpostors.ToString(), ["inline"] = true },
                new JsonObject { ["name"] = "部屋コード", ["value"] = $"`{code}`", ["inline"] = true },
            },
            ["footer"] = new JsonObject { ["text"] = "More Roles Plus ・ ボタンを押すとインストーラーが開いて部屋に入ります" },
        };
        var msg = new JsonObject
        {
            ["embeds"] = new JsonArray { embed },
            ["allowed_mentions"] = DiscordApi.NoMentions(),
            ["components"] = new JsonArray(),
        };
        if (lobby && !full)
        {
            msg["components"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = 1,
                    ["components"] = new JsonArray
                    {
                        new JsonObject { ["type"] = 2, ["style"] = 5, ["label"] = "参加する", ["emoji"] = new JsonObject { ["name"] = "▶️" }, ["url"] = $"{_cfg.PublicUrl.TrimEnd('/')}/mrp/join/{code}" },
                    },
                },
            };
        }
        return msg;
    }
}
