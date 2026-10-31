using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MoreRolesPlus.Net;

// MRP のサーバー (地域) を地域の一覧に足し、起動して最初に一覧を読んだ時にそれを選ぶ。
// 起動の後に利用者が別の地域へ切り替えたら、その起動の間は選び直さない。
// 接続先は BepInEx/config の [Server] で変えられる (AutoSelect = false で選ぶのをやめる)。
// 選んだ後に裏で TCP (部屋探しの HTTP) がつながるか確かめ、つながらなければ前の地域へ戻す
// (落ちたサーバーのまま部屋を作ると、本編は暗転したまま長く待つ)。戻した後も 20 秒ごとに確かめ、つながったら MRP へ戻す。
// 地域の一覧では MRP を先頭に置く (本編は足した地域を末尾に付けるので、作成画面の一覧では隠れて見えなかった)
internal static class MrpRegion
{
    private static ConfigEntry<string> _name, _address;
    private static ConfigEntry<ushort> _port;
    private static ConfigEntry<bool> _enabled, _autoSelect;
    private static bool _selected;
    private static IRegionInfo _previous;
    private static volatile int _probe; // 0 = 確かめていない / 1 = 確かめ中 / 2 = つながった / 3 = つながらない
    private static string _host;
    private static string _fallback; // つながらなくて切り替えた先 (利用者が別の地域へ移したら見張りをやめる)
    private static long _retryAt;
    private const int RetryMs = 20000;

    public static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Server", "AddMrpServer", true, "MRP のサーバーを地域の一覧に足す");
        _autoSelect = config.Bind("Server", "AutoSelect", true, "起動した時に MRP のサーバーを選ぶ");
        _name = config.Bind("Server", "Name", "MRP Server (Kobe)", "地域の一覧に出す名前");
        _address = config.Bind("Server", "Address", "amongus.nkzk.win", "サーバーのアドレス (IP かホスト名。443 番の時は https で話す)");
        _port = config.Bind("Server", "Port", (ushort)443, "サーバーのポート");
        // 前の既定 (神戸の 180.12.155.8:1232) のまま残っている設定は新しいサーバーへ移す (自分で変えた値は触らない)
        if (_address.Value.Trim() == "180.12.155.8" && _port.Value == 1232)
        {
            _address.Value = (string)_address.DefaultValue;
            _port.Value = (ushort)_port.DefaultValue;
        }
    }

    [HarmonyPatch(typeof(ServerManager), nameof(ServerManager.LoadServers))]
    private static class LoadServersPatch
    {
        public static void Postfix(ServerManager __instance)
        {
            if (_enabled == null || !_enabled.Value) return;
            try { Add(__instance); }
            catch (System.Exception e) { Plugin.Logger.LogError($"mrp region: {e}"); }
        }
    }

    private static void Add(ServerManager sm)
    {
        string host = _address.Value.Trim();
        string url = host.StartsWith("http://") || host.StartsWith("https://") ? host : (_port.Value == 443 ? "https://" : "http://") + host;
        var servers = new Il2CppReferenceArray<ServerInfo>((long)1);
        servers[0] = new ServerInfo("Http-1", url, _port.Value, false);
        var region = new StaticHttpRegionInfo(_name.Value, StringNames.NoTranslation, url, servers).Cast<IRegionInfo>();
        sm.AddOrUpdateRegion(region);
        MoveToTop(sm);
        if (_selected || !_autoSelect.Value) return;
        _selected = true;
        _previous = sm.CurrentRegion;
        sm.SetRegion(region);
        Plugin.Logger.LogInfo($"mrp region selected: {url}:{_port.Value}");
        _host = host.Replace("http://", "").Replace("https://", "").TrimEnd('/');
        Probe(_host, _port.Value);
    }

    private static void MoveToTop(ServerManager sm)
    {
        var all = sm.AvailableRegions;
        if (all == null || all.Length < 2 || all[0].Name == _name.Value) return;
        var sorted = new Il2CppReferenceArray<IRegionInfo>((long)all.Length);
        int at = 0;
        foreach (var r in all) if (r.Name == _name.Value) sorted[at++] = r;
        foreach (var r in all) if (r.Name != _name.Value) sorted[at++] = r;
        sm.AvailableRegions = sorted;
    }

    private static void Probe(string host, int port)
    {
        _probe = 1;
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connect = tcp.ConnectAsync(host, port);
                bool ok = await System.Threading.Tasks.Task.WhenAny(connect, System.Threading.Tasks.Task.Delay(4000)) == connect && tcp.Connected;
                _probe = ok ? 2 : 3;
            }
            catch { _probe = 3; }
        });
    }

    // 確かめた結果を本編の側 (メインスレッド) で反映する
    public static void Tick()
    {
        if (_fallback != null && _probe == 0 && System.Environment.TickCount64 >= _retryAt) Probe(_host, _port.Value);
        if (_probe < 2) return;
        int r = _probe;
        _probe = 0;
        if (_fallback != null) { Retried(r == 2); return; }
        if (r == 2) { Plugin.Logger.LogInfo("mrp region: server reachable"); return; }
        var sm = ServerManager.Instance;
        if (!sm || sm.CurrentRegion == null || sm.CurrentRegion.Name != _name.Value) return; // 利用者がもう切り替えた
        // 前回も MRP を選んでいた時は保存された地域も MRP なので、公式のアジア (無ければ最初の公式) へ
        var back = _previous != null && _previous.Name != _name.Value ? _previous : null;
        if (back == null)
            foreach (var r2 in sm.AvailableRegions)
                if (r2.Name != _name.Value && (back == null || r2.Name.Contains("Asia"))) back = r2;
        if (back == null) { Plugin.Logger.LogWarning("mrp region: server unreachable (no other region)"); return; }
        sm.SetRegion(back);
        _fallback = back.Name;
        _retryAt = System.Environment.TickCount64 + RetryMs;
        Plugin.Logger.LogWarning($"mrp region: server unreachable, back to {back.Name} (retry every {RetryMs / 1000}s)");
    }

    // 戻した後の確かめ直し。部屋の中では切り替えない (メニューにいる時だけ)
    private static void Retried(bool ok)
    {
        var sm = ServerManager.Instance;
        if (!sm || sm.CurrentRegion == null || sm.CurrentRegion.Name != _fallback) { _fallback = null; return; } // 利用者が選び直した
        if (!ok) { _retryAt = System.Environment.TickCount64 + RetryMs; return; }
        var client = AmongUsClient.Instance;
        if (client && client.GameState != InnerNet.InnerNetClient.GameStates.NotJoined) { _retryAt = System.Environment.TickCount64 + RetryMs; return; }
        foreach (var region in sm.AvailableRegions)
        {
            if (region.Name != _name.Value) continue;
            sm.SetRegion(region);
            _fallback = null;
            Plugin.Logger.LogInfo("mrp region: server is back, selected again");
            return;
        }
    }
}
