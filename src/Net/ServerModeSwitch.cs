using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;

namespace MoreRolesPlus.Net;

// ロビーでモード (クラシック / かくれんぼ) を切り替えてよいサーバーか。
// Impostor は部屋を作った時のモードの設定しか読めず、別のモードの設定が届くとホストを切断する。
// MRP のサーバーのプラグイン (MrpServer) は直してあり、/mrp/status に modeSwitch: true を返す。
// LAN とフリープレイはサーバーを通らないので、いつでも切り替えてよい
internal static class ServerModeSwitch
{
    private static volatile int _state; // 0 = 確かめていない / 1 = 確かめ中 / 2 = 対応 / 3 = 未対応
    private static string _checkedFor;

    // 切り替えてよければ null、だめなら知らせの文の鍵
    public static string Blocked()
    {
        var c = AmongUsClient.Instance;
        if (!c || c.NetworkMode != NetworkModes.OnlineGame) return null;
        if (!MrpRegion.IsCurrent()) return "GameModeSwitch.ServerUnsupported";
        Reset();
        switch (_state)
        {
            case 2: return null;
            case 3: return "GameModeSwitch.ServerOld";
            case 0: Check(); break;
        }
        return "GameModeSwitch.Checking";
    }

    // ロビーに着いた時に先に聞いておく (押した時に待たせない)
    public static void Prefetch()
    {
        var c = AmongUsClient.Instance;
        if (!c || !c.AmHost || c.NetworkMode != NetworkModes.OnlineGame || !MrpRegion.IsCurrent()) return;
        Reset();
        if (_state == 0) Check();
    }

    // 接続先が変わったら聞き直す
    private static void Reset()
    {
        string key = MrpRegion.WebBase;
        if (_checkedFor == key) return;
        _checkedFor = key;
        _state = 0;
    }

    private static void Check()
    {
        _state = 1;
        string url = MrpRegion.WebBase + "/mrp/status";
        Task.Run(async () =>
        {
            try
            {
                using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                h.DefaultRequestHeaders.UserAgent.ParseAdd($"MoreRolesPlus/{Plugin.Version}");
                using var res = await h.GetAsync(url).ConfigureAwait(false);
                // 返事はあるが成功でない (MrpServer が入っていない・古い) = 未対応。つながらない時だけ聞き直す
                if (!res.IsSuccessStatusCode) { _state = 3; return; }
                string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                bool ok = doc.RootElement.TryGetProperty("modeSwitch", out var v) && v.ValueKind == JsonValueKind.True;
                _state = ok ? 2 : 3;
            }
            catch (Exception e)
            {
                // つながらない時は次に押した時にまた聞く
                Plugin.Logger.LogWarning($"mode switch check: {e.Message}");
                _state = 0;
            }
        });
    }

    internal static string Describe() => $"state={_state} for={_checkedFor ?? "-"}";
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class ServerModeSwitchLobbyPatch
{
    public static void Postfix() => ServerModeSwitch.Prefetch();
}
