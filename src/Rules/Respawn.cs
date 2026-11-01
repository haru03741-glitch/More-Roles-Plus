using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Rules;

// 死んでも設定の秒数で生き返る (GameRules.Respawn)。ホストが死んだ時刻を覚え、時間が来たら全員の端末で生き返らせる。
// 会議と追放の画面の間は待ち、終わった後に生き返らせる。0.25 秒に 1 回だけ見る
internal static class Respawn
{
    private const long CheckMs = 250;
    private static long _next;
    private static readonly Dictionary<byte, long> DiedAt = new();

    public static void Tick()
    {
        if (!GameRules.Respawn)
        {
            if (DiedAt.Count > 0) DiedAt.Clear();
            return;
        }
        long now = Environment.TickCount64;
        if (now < _next) return;
        _next = now + CheckMs;
        var client = AmongUsClient.Instance;
        var gd = GameData.Instance;
        if (!client || !client.AmHost || !gd || !ShipStatus.Instance || !Options.GameModeSwitch.IsNormal)
        {
            DiedAt.Clear();
            return;
        }
        if (Roles.GameEnd.Decided || MeetingHud.Instance || ExileController.Instance) return;
        long wait = GameRules.RespawnSeconds * 1000L;
        var all = gd.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null) continue;
            byte id = p.PlayerId;
            if (p.Disconnected || !p.IsDead || !p.Object)
            {
                DiedAt.Remove(id);
                continue;
            }
            if (!DiedAt.TryGetValue(id, out long died))
            {
                DiedAt[id] = now;
                continue;
            }
            if (now - died < wait) continue;
            DiedAt.Remove(id);
            Dev.DevCommands.Revive(id);
        }
    }
}
