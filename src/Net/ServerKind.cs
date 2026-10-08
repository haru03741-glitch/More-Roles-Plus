// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/Utils.cs IsOfficialServer (GPL-3.0)
using System;
using InnerNet;

namespace MoreRolesPlus.Net;

// 今つないでいる (つなごうとしている) サーバーが公式か。公式サーバーには本編の上限 (インポスター 3 人まで等) を守る。
// 判定は地域の表示名でなく接続先のアドレスで行う (表示名は利用者が自由に付けられる)。
internal static class ServerKind
{
    // ignoreNetworkMode = 部屋を立てる前 (まだオンラインでない) でも選んでいる地域で判定する。
    // unknownIsOfficial = 地域が分からない時の答え
    public static bool IsOfficial(bool ignoreNetworkMode = false, bool unknownIsOfficial = true)
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (!ignoreNetworkMode && (!client || client.NetworkMode != NetworkModes.OnlineGame)) return false; // ローカル / LAN / フリープレイ
            var region = ServerManager.Instance ? ServerManager.Instance.CurrentRegion : null;
            if (region == null) return unknownIsOfficial;
            var ping = region.PingServer;
            return ping != null && ping.EndsWith("among.us", StringComparison.Ordinal);
        }
        catch { return unknownIsOfficial; }
    }
}
