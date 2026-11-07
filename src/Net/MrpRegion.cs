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

    // MRP のサーバーの Web の口 (バグ報告など)。443 なら https
    public static string WebBase
    {
        get
        {
            string host = (_address?.Value ?? "amongus.nkzk.win").Trim().TrimEnd('/');
            if (host.StartsWith("http://") || host.StartsWith("https://")) return host;
            ushort port = _port?.Value ?? 443;
            return port == 443 ? "https://" + host : $"http://{host}:{port}";
        }
    }

    // 今選んでいる地域が MRP のサーバーか (表示名でなく接続先で見る)
    public static bool IsCurrent()
    {
        try
        {
            var region = ServerManager.Instance ? ServerManager.Instance.CurrentRegion : null;
            if (region == null || _address == null) return false;
            // 毎フレーム呼ばれるので、地域と接続先の設定が同じ間は前の答えを使う (PingServer の文字列を毎回写さない)
            System.IntPtr ptr = region.Pointer;
            string addr = _address.Value;
            long now = System.Environment.TickCount64;
            if (ptr == _cachedRegion && ReferenceEquals(addr, _cachedAddress) && now < _cachedUntil) return _cachedIsCurrent;
            string host = addr.Trim().Replace("http://", "").Replace("https://", "").TrimEnd('/');
            _cachedIsCurrent = host.Length > 0 && (region.PingServer ?? "").Contains(host, System.StringComparison.OrdinalIgnoreCase);
            _cachedRegion = ptr;
            _cachedAddress = addr;
            _cachedUntil = now + 1000; // 地域の物が作り直されて同じ番地に別の地域が来た時のため、1 秒で見直す
            return _cachedIsCurrent;
        }
        catch { return false; }
    }

    private static System.IntPtr _cachedRegion;
    private static string _cachedAddress;
    private static bool _cachedIsCurrent;
    private static long _cachedUntil;

    // MRP のサーバーへ切り替える (参加リンクから入る時)。地域の一覧に無ければ false
    public static bool Select()
    {
        var sm = ServerManager.Instance;
        if (!sm || _name == null) return false;
        if (IsCurrent()) return true;
        foreach (var r in sm.AvailableRegions)
        {
            if (r.Name != _name.Value) continue;
            sm.SetRegion(r);
            _fallback = null;
            return true;
        }
        return false;
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
        var back = OtherRegion(sm);
        if (back == null) { Plugin.Logger.LogWarning("mrp region: server unreachable (no other region)"); return; }
        sm.SetRegion(back);
        _fallback = back.Name;
        _retryAt = System.Environment.TickCount64 + RetryMs;
        Plugin.Logger.LogWarning($"mrp region: server unreachable, back to {back.Name} (retry every {RetryMs / 1000}s)");
    }

    // MRP 以外の戻り先 = 起動した時に選ばれていた地域。前回も MRP のまま閉じていた時は公式のアジア
    // (無ければ最初の公式・それも無ければ最初の別の地域)。公式かは表示名でなく接続先で見る
    private static IRegionInfo OtherRegion(ServerManager sm)
    {
        if (_previous != null && _previous.Name != _name.Value) return _previous;
        IRegionInfo asia = null, official = null, any = null;
        foreach (var r in sm.AvailableRegions)
        {
            if (r.Name == _name.Value) continue;
            any ??= r;
            if (!(r.PingServer ?? "").EndsWith("among.us", System.StringComparison.OrdinalIgnoreCase)) continue;
            official ??= r;
            if (asia == null && r.Name.Contains("Asia")) asia = r;
        }
        return asia ?? official ?? any;
    }

    // 閉じる時に MRP のサーバーを選んだままなら戻り先へ切り替えて保存する。
    // 地域の保存 (regionInfo.json) は本編と他の Mod で共用なので、そのままだと次に起動した別の物も MRP のサーバーへつながる。
    // MRP は次の起動で AutoSelect がまた選ぶ
    public static void RestoreOnQuit()
    {
        try
        {
            if (_name == null || !ServerManager.InstanceExists || !IsCurrent()) return;
            var sm = ServerManager.Instance;
            var back = OtherRegion(sm);
            if (back == null) return;
            sm.SetRegion(back);
            Plugin.Logger.LogInfo($"mrp region: restored {back.Name} on quit");
        }
        catch (System.Exception e) { Plugin.Logger.LogError($"mrp region quit: {e}"); }
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
