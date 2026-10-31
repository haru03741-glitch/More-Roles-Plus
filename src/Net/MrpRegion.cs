using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MoreRolesPlus.Net;

// MRP のサーバー (地域) を地域の一覧に足し、起動して最初に一覧を読んだ時にそれを選ぶ。
// 起動の後に利用者が別の地域へ切り替えたら、その起動の間は選び直さない。
// 接続先は BepInEx/config の [Server] で変えられる (AutoSelect = false で選ぶのをやめる)
internal static class MrpRegion
{
    private static ConfigEntry<string> _name, _address;
    private static ConfigEntry<ushort> _port;
    private static ConfigEntry<bool> _enabled, _autoSelect;
    private static bool _selected;

    public static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Server", "AddMrpServer", true, "MRP のサーバーを地域の一覧に足す");
        _autoSelect = config.Bind("Server", "AutoSelect", true, "起動した時に MRP のサーバーを選ぶ");
        _name = config.Bind("Server", "Name", "MRP Server (Kobe)", "地域の一覧に出す名前");
        _address = config.Bind("Server", "Address", "180.12.155.8", "サーバーのアドレス (IP かホスト名)");
        _port = config.Bind("Server", "Port", (ushort)1232, "サーバーのポート");
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
        string url = host.StartsWith("http://") || host.StartsWith("https://") ? host : "http://" + host;
        var servers = new Il2CppReferenceArray<ServerInfo>(new[] { new ServerInfo("Http-1", url, _port.Value, false) });
        var region = new StaticHttpRegionInfo(_name.Value, StringNames.NoTranslation, url, servers).Cast<IRegionInfo>();
        sm.AddOrUpdateRegion(region);
        if (_selected || !_autoSelect.Value) return;
        _selected = true;
        sm.SetRegion(region);
        Plugin.Logger.LogInfo($"mrp region selected: {url}:{_port.Value}");
    }
}
