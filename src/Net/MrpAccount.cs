using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Net;

// MRP のアカウント (MRP コイン・持ち物・ランク)。数は全部 MRP のサーバーが持ち、ここは読んで出すだけ。
// MRP コインは非公式のゲーム内通貨で、本編のお金・豆・星とは関係ない。
// アカウントの合言葉は BepInEx/config/MoreRolesPlus/account.json に控える (無くすとアカウントも無くなる)。
// 部屋に入ったら、サーバーにだけ届く隠しのチャット (/mrpbind <一回きりの番号>) でゲームの接続とアカウントを結ぶ。
// 結んだ人のランクと持ち物は、部屋ごとにサーバーから読む (頭の上のランク・持っていないエモートやスタンプを出させない)
internal static class MrpAccount
{
    private sealed class Saved
    {
        public string Id { get; set; } = "";
        public string Token { get; set; } = "";
    }

    public sealed class Item
    {
        public string Id { get; set; } = "";
        public int Price { get; set; }
        public int MinTier { get; set; }
    }

    public sealed class PlayerRank
    {
        public int Rating, Tier;
        public HashSet<string> Owned = new();
    }

    private static Saved _saved;
    private static readonly ConcurrentQueue<Action> Main = new();

    // 自分 (サーバーから読んだ物)
    public static bool Ready { get; private set; }
    public static int Coins { get; private set; }
    public static int Rating { get; private set; }
    public static int Tier { get; private set; }
    public static int Wins { get; private set; }
    public static int Losses { get; private set; }
    public static int Leaves { get; private set; }
    public static readonly HashSet<string> Owned = new();
    public static string LastError { get; private set; } = "";
    public static int Version { get; private set; } // 読み直すたびに増える (画面が描き直すかを見る)

    public static readonly List<Item> Catalog = new();
    public static int[] TierFloor = { 0, 1100, 1250, 1450, 1700, 2000 };

    // 今の部屋
    public static readonly Dictionary<int, PlayerRank> Room = new(); // ClientId →
    public static bool RoomRanked { get; private set; }
    private static int _roomCode = -1;
    private static float _roomAt, _bindAt;
    private static bool _bound, _binding, _roomBusy;
    private const float RoomEvery = 8f;

    private static string FilePath => Path.Combine(Paths.ConfigPath, "MoreRolesPlus", "account.json");
    private static string BaseUrl => MrpRegion.WebBase + "/mrp";

    // ── 持っているか ───────────────────────────

    public static bool OwnsEmote(int kind) => kind < Fx.EmoteArt.FreeCount || Owned.Contains("emote:" + kind);
    public static bool OwnsStamp(int kind) => kind < Menu.MeetingStamps.FreeCount || Owned.Contains("stamp:" + kind);

    // 他の人が持っているか (サーバーから読めていなければ、ランクの部屋以外は信じる)
    public static bool PlayerOwns(PlayerControl p, string item, int freeCount, int kind)
    {
        if (kind < freeCount) return true;
        if (!p) return false;
        if (p.AmOwner) return Owned.Contains(item);
        if (Room.TryGetValue(p.OwnerId, out var r)) return r.Owned.Contains(item);
        // 結ばれていない人 (サーバーとつながっていない・まだ読めていない) は持っていない扱い
        return false;
    }

    public static PlayerRank RankOf(PlayerControl p)
    {
        if (!p) return null;
        if (p.AmOwner && Ready) return new PlayerRank { Rating = Rating, Tier = Tier };
        return Room.TryGetValue(p.OwnerId, out var r) ? r : null;
    }

    public static int TierOf(int rating)
    {
        int t = 0;
        for (int i = 0; i < TierFloor.Length; i++) if (rating >= TierFloor[i]) t = i;
        return t;
    }

    // 今の段の中での進み (0〜1)。一番上の段は 1
    public static float Progress(int rating)
    {
        int t = TierOf(rating);
        if (t >= TierFloor.Length - 1) return 1f;
        return Mathf.Clamp01((rating - TierFloor[t]) / (float)(TierFloor[t + 1] - TierFloor[t]));
    }

    public static readonly Color[] TierColors =
    {
        new(0.80f, 0.52f, 0.30f), // ブロンズ
        new(0.80f, 0.84f, 0.90f), // シルバー
        new(1.00f, 0.82f, 0.30f), // ゴールド
        new(0.45f, 0.95f, 0.85f), // プラチナ
        new(0.55f, 0.75f, 1.00f), // ダイヤ
        new(0.95f, 0.45f, 1.00f), // マスター
    };

    public static string TierName(int tier) => new Text("Rank.Tier" + Mathf.Clamp(tier, 0, 5)).ToString();
    public static string TierColorHex(int tier) => ColorUtility.ToHtmlStringRGB(TierColors[Mathf.Clamp(tier, 0, 5)]);

    // ── 通信 ─────────────────────────────────

    private static HttpClient Client()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"MoreRolesPlus/{Plugin.Version}");
        if (_saved != null && _saved.Id.Length > 0) h.DefaultRequestHeaders.Add("X-Mrp-Account", _saved.Id + ":" + _saved.Token);
        return h;
    }

    private static async Task<JsonElement?> Call(HttpMethod method, string path, object body = null)
    {
        using var http = Client();
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req).ConfigureAwait(false);
        string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new Exception("bad_reply"); }
        if (!res.IsSuccessStatusCode)
            throw new Exception(root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "http_" + (int)res.StatusCode);
        return root;
    }

    // 背中で回して、結果は本編の側 (毎フレームの Pump) で受ける
    private static void Run(Func<Task> work, Action<string> onError = null)
    {
        Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception e)
            {
                string msg = e is HttpRequestException or TaskCanceledException ? "network" : e.Message;
                Main.Enqueue(() =>
                {
                    LastError = msg;
                    onError?.Invoke(msg);
                });
            }
        });
    }

    private static void Load()
    {
        if (_saved != null) return;
        try
        {
            if (File.Exists(FilePath)) _saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"account load: {e.Message}"); }
        _saved ??= new Saved();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_saved));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"account save: {e.Message}"); }
    }

    private static string MyName()
    {
        try { return AmongUs.Data.DataManager.Player.Customization.Name; } catch { return ""; }
    }

    private static bool _refreshing;

    // アカウントが無ければ作り、自分の数を読み直す
    public static void Refresh(Action done = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        Load();
        string name = MyName();
        Run(async () =>
        {
            if (_saved.Id.Length == 0)
            {
                var made = await Call(HttpMethod.Post, "/account/new", new { name }).ConfigureAwait(false);
                if (made is { } m)
                {
                    _saved = new Saved { Id = m.GetProperty("id").GetString() ?? "", Token = m.GetProperty("token").GetString() ?? "" };
                    Save();
                }
            }
            var me = await Call(HttpMethod.Get, "/account").ConfigureAwait(false);
            var shop = Catalog.Count == 0 ? await Call(HttpMethod.Get, "/shop").ConfigureAwait(false) : null;
            Main.Enqueue(() =>
            {
                _refreshing = false;
                if (me is { } v) Apply(v);
                if (shop is { } sh) ApplyShop(sh);
                LastError = "";
                done?.Invoke();
            });
        }, _ => { _refreshing = false; done?.Invoke(); });
    }

    private static void Apply(JsonElement v)
    {
        Coins = v.GetProperty("coins").GetInt32();
        Rating = v.GetProperty("rating").GetInt32();
        Tier = v.GetProperty("tier").GetInt32();
        Wins = v.GetProperty("wins").GetInt32();
        Losses = v.GetProperty("losses").GetInt32();
        Leaves = v.GetProperty("leaves").GetInt32();
        Owned.Clear();
        foreach (var o in v.GetProperty("owned").EnumerateArray()) Owned.Add(o.GetString() ?? "");
        Ready = true;
        Version++;
    }

    private static void ApplyShop(JsonElement v)
    {
        Catalog.Clear();
        foreach (var i in v.GetProperty("items").EnumerateArray())
            Catalog.Add(new Item { Id = i.GetProperty("id").GetString() ?? "", Price = i.GetProperty("price").GetInt32(), MinTier = i.GetProperty("minTier").GetInt32() });
        if (v.TryGetProperty("tiers", out var t))
        {
            var list = new List<int>();
            foreach (var x in t.EnumerateArray()) list.Add(x.GetInt32());
            if (list.Count > 0) TierFloor = list.ToArray();
        }
        Version++;
    }

    // 買う。done(エラーの鍵 or null)
    public static void Buy(string item, Action<string> done)
    {
        Run(async () =>
        {
            var v = await Call(HttpMethod.Post, "/shop/buy", new { item }).ConfigureAwait(false);
            Main.Enqueue(() =>
            {
                if (v is { } x) Apply(x);
                done?.Invoke(null);
            });
        }, e => done?.Invoke(e));
    }

    // ── 部屋 ─────────────────────────────────

    private static bool InMrpRoom()
    {
        var c = AmongUsClient.Instance;
        return c && c.AmConnected && c.GameState != InnerNet.InnerNetClient.GameStates.NotJoined && MrpRegion.IsCurrent()
               && PlayerControl.LocalPlayer;
    }

    private static string RoomCode() => InnerNet.GameCode.IntToGameName(AmongUsClient.Instance.GameId);

    // 毎フレーム
    internal static void Tick()
    {
        while (Main.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception e) { Plugin.Logger.LogError($"account: {e}"); }
        }
        if (!InMrpRoom())
        {
            if (_roomCode != -1)
            {
                _roomCode = -1;
                Room.Clear();
                RoomRanked = false;
                _bound = false;
            }
            return;
        }
        int code = AmongUsClient.Instance.GameId;
        if (code != _roomCode)
        {
            _roomCode = code;
            Room.Clear();
            _bound = false;
            _bindAt = Time.realtimeSinceStartup + 1.5f;
            _roomAt = 0f;
        }
        float now = Time.realtimeSinceStartup;
        if (!_bound && !_binding && now >= _bindAt) Bind();
        if (now >= _roomAt && !_roomBusy)
        {
            _roomAt = now + RoomEvery;
            ReadRoom();
        }
    }

    // ゲームの接続とアカウントを結ぶ: 一回きりの番号を貰い、サーバーにだけ届くチャットで送る
    private static void Bind()
    {
        Load();
        if (_saved.Id.Length == 0)
        {
            _bindAt = Time.realtimeSinceStartup + 10f;
            if (!_refreshing) Refresh();
            return;
        }
        _binding = true;
        Run(async () =>
        {
            var v = await Call(HttpMethod.Post, "/account/bind").ConfigureAwait(false);
            string nonce = v?.GetProperty("nonce").GetString() ?? "";
            string prefix = v?.GetProperty("prefix").GetString() ?? "/mrpbind ";
            Main.Enqueue(() =>
            {
                _binding = false;
                if (!InMrpRoom() || nonce.Length == 0) return;
                SendHidden(prefix + nonce);
                _bound = true;
                _roomAt = Time.realtimeSinceStartup + 1.5f; // 結んだらすぐ読み直す
            });
        }, _ =>
        {
            _binding = false;
            _bindAt = Time.realtimeSinceStartup + 15f;
        });
    }

    // チャットの RPC をサーバーへ送る (自分の画面には出さない。サーバーが止めて他の人には届けない。届いても MRP は隠す)
    private static void SendHidden(string text)
    {
        var lp = PlayerControl.LocalPlayer;
        var c = AmongUsClient.Instance;
        if (!lp || !c) return;
        var w = c.StartRpcImmediately(lp.NetId, (byte)RpcCalls.SendChat, Hazel.SendOption.Reliable, -1);
        w.Write(text);
        c.FinishRpcImmediately(w);
    }

    internal static bool IsBindChat(string text) => text != null && text.StartsWith("/mrpbind ", StringComparison.Ordinal);

    private static void ReadRoom()
    {
        _roomBusy = true;
        string code = RoomCode();
        Run(async () =>
        {
            var v = await Call(HttpMethod.Get, $"/game/{code}/players").ConfigureAwait(false);
            Main.Enqueue(() =>
            {
                _roomBusy = false;
                if (v is not { } x || !InMrpRoom() || RoomCode() != code) return;
                RoomRanked = x.GetProperty("ranked").GetBoolean();
                Room.Clear();
                foreach (var p in x.GetProperty("players").EnumerateArray())
                {
                    var r = new PlayerRank { Rating = p.GetProperty("rating").GetInt32(), Tier = p.GetProperty("tier").GetInt32() };
                    foreach (var o in p.GetProperty("owned").EnumerateArray()) r.Owned.Add(o.GetString() ?? "");
                    Room[p.GetProperty("clientId").GetInt32()] = r;
                }
                // 自分が載っていなければ結び直す (番号が届かなかった)
                if (!Room.ContainsKey(AmongUsClient.Instance.ClientId) && _bound) { _bound = false; _bindAt = Time.realtimeSinceStartup + 3f; }
                Version++;
            });
        }, _ => _roomBusy = false);
    }

    // ── ランクマッチ ───────────────────────────

    public sealed class RankedSettings
    {
        public string SettingCode = "";
        public int MaxPlayers = 15, FillSeconds = 90, MinHumans = 2;
    }

    public static void RankedConfig(Action<RankedSettings, string> done)
    {
        Run(async () =>
        {
            var v = await Call(HttpMethod.Get, "/ranked/config").ConfigureAwait(false);
            var s = new RankedSettings();
            if (v is { } x)
            {
                s.SettingCode = x.GetProperty("settingCode").GetString() ?? "";
                s.MaxPlayers = x.GetProperty("maxPlayers").GetInt32();
                s.FillSeconds = x.GetProperty("fillSeconds").GetInt32();
                s.MinHumans = x.GetProperty("minHumans").GetInt32();
            }
            Main.Enqueue(() => done(s, null));
        }, e => done(null, e));
    }

    // 入れる部屋のコード (無ければ "" = 自分で作る)
    public static void Queue(Action<string, string> done)
    {
        Refresh(() =>
        {
            if (!Ready) { done(null, LastError.Length > 0 ? LastError : "network"); return; }
            Run(async () =>
            {
                var v = await Call(HttpMethod.Post, "/ranked/queue").ConfigureAwait(false);
                string code = v?.GetProperty("code").GetString() ?? "";
                Main.Enqueue(() => done(code, null));
            }, e => done(null, e));
        });
    }

    public static void Register(string code, Action<string> done)
    {
        Run(async () =>
        {
            await Call(HttpMethod.Post, "/ranked/register", new { code }).ConfigureAwait(false);
            Main.Enqueue(() => { RoomRanked = true; done?.Invoke(null); });
        }, e => done?.Invoke(e));
    }

    // 見た勝った人 (playerId) を知らせ、少し待って自分の数を読み直す
    public static void Report(List<int> winners)
    {
        if (!InMrpRoom()) return;
        string code = RoomCode();
        Run(async () =>
        {
            await Call(HttpMethod.Post, "/ranked/result", new { code, winners }).ConfigureAwait(false);
            await Task.Delay(4000).ConfigureAwait(false);
            Main.Enqueue(() => Refresh());
        });
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.Logger.LogError($"account tick: {e.Message}"); }
        }
    }

    // 結ぶチャットを画面に出さない (サーバーが止められない時の安全網)
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
    private static class HideBindPatch
    {
        public static bool Prefix([HarmonyArgument(1)] string chatText) => !IsBindChat(chatText);
    }

    internal static string Describe() =>
        $"ready={Ready} id={(_saved?.Id ?? "-")} coins={Coins} rating={Rating} tier={Tier} owned=[{string.Join(",", Owned)}] room={Room.Count} ranked={RoomRanked} bound={_bound} err={LastError}";
}
