using System;
using System.Text;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace MoreRolesPlus.Net;

// 切断の理由を 1 回につき 1 行だけログに残す (本編のログは理由の値を出さない)。
// - 自分が抜けた/切れた: 理由・往復時間・返事待ちの Reliable の数・MRP の送信の数・声の送受の数
// - 誰かが抜けた/切れた: その人と理由 (ホストのログから客ごとの切れ方が分かる)
// 同じ人が何度も ClientTimeout なら その人の回線、同じ時刻に何人も切れるならホスト/サーバー/mod の側
internal static class NetLog
{
    internal static string Link()
    {
        var sb = new StringBuilder();
        var c = AmongUsClient.Instance;
        if (!c) return "client=none";
        sb.Append("mode=").Append(c.NetworkMode).Append(" state=").Append(c.GameState)
          .Append(" host=").Append(c.AmHost);
        var udp = c.connection?.TryCast<Hazel.Udp.UdpConnection>();
        if (udp != null) sb.Append(" ping=").Append(udp.AveragePingMs).Append("ms unack=").Append(udp.reliableDataPacketsSent.Count);
        sb.Append(" mrpPackets=").Append(Remote.Packets).Append(" mrpBytes=").Append(Remote.Bytes)
          .Append(" last1s=").Append(Remote.SentLastSecond())
          .Append(" voiceSent=").Append(Voice.VoiceChat.Sent).Append(" voiceRecv=").Append(Voice.VoiceChat.Received)
          .Append(" t=").Append((int)Time.realtimeSinceStartup);
        return sb.ToString();
    }
}

[HarmonyPatch]
internal static class NetLogPatches
{
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame)), HarmonyPrefix]
    public static void Exit([HarmonyArgument(0)] DisconnectReasons reason)
    {
        try { Plugin.Logger.LogInfo($"net exit: reason={reason} {NetLog.Link()}"); }
        catch (Exception e) { Plugin.Logger.LogError($"net exit: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft)), HarmonyPrefix]
    public static void Left([HarmonyArgument(0)] ClientData data, [HarmonyArgument(1)] DisconnectReasons reason)
    {
        try
        {
            string who = data == null ? "?" : $"{data.PlayerName} id={data.Id}";
            Plugin.Logger.LogInfo($"net left: {who} reason={reason} {NetLog.Link()}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"net left: {e.Message}"); }
    }
}
