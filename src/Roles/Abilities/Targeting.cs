using System;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 近くの人を狙う時の共通の判定 (キルボタンと能力ボタン)
internal static class Targeting
{
    // reach 以内で一番近い、生きていてベントや昇降機の中にいない、間に壁の無い人 (filter = (自分, 相手) で絞る)
    internal static PlayerControl ClosestPlayer(PlayerControl lp, float reach, Func<PlayerControl, PlayerControl, bool> filter = null)
    {
        float best = reach * reach;
        Vector2 me = lp.GetTruePosition();
        float mx = me.x, my = me.y;
        PlayerControl pick = null;
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected || p.IsDead || p.PlayerId == lp.PlayerId) continue;
            var pc = p.Object;
            if (!pc || pc.inVent || pc.inMovingPlat || !pc.Visible) continue;
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - mx, dy = at.y - my;
            float sq = dx * dx + dy * dy;
            if (sq >= best) continue;
            if (filter != null && !filter(lp, pc)) continue;
            if (PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false)) continue;
            best = sq;
            pick = pc;
        }
        return pick;
    }

    // ホストが依頼を確かめる時: 届かない理由 (届くなら null)
    internal static string Unreachable(PlayerControl from, PlayerControl to, float reach)
    {
        Vector2 a = from.GetTruePosition(), b = to.GetTruePosition();
        float dx = b.x - a.x, dy = b.y - a.y;
        if (dx * dx + dy * dy > reach * reach) return "far";
        if (PhysicsHelpers.AnythingBetween(a, b, Constants.ShipAndObjectsMask, false)) return "wall";
        return null;
    }
}
