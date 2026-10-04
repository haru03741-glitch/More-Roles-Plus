using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 損傷イベントを地形に適用する唯一の入口。壊れ方は DamageProfile で決まる。
// ゲームに関わる物 (ドアなど) は当面壊さない。
internal static class TerrainDamage
{
    private const int ShipLayer = 9;
    private const int ShadowLayer = 10;
    private const int WallMask = (1 << ShipLayer) | (1 << ShadowLayer);

    public static string Apply(in DamageEvent e)
    {
        var profile = DamageProfile.Of(e.Kind);
        return e.Kind switch
        {
            DamageKind.Explosion => Explode(e, profile),
            DamageKind.Blunt => Strike(e, profile),
            _ => "unknown kind",
        };
    }

    // 爆発: 半径の内側の壁はまとめて抜け、外側の輪の壁にはひびが入って耐久が減る
    private static string Explode(in DamageEvent e, DamageProfile p)
    {
        var core = new CircleShape(e.Position, e.Size);
        float outer = e.Size * p.OuterRingScale;
        var removed = new List<Vector2>();
        int cut = 0, cracked = 0;

        foreach (var col in WallsNear(e.Position, outer))
        {
            if (EdgeCutter.Cut(col, core, removed)) cut++;
        }

        // 外側の輪: 残った壁の、爆心にいちばん近い点にひび (壁 1 本につき 1 か所)
        foreach (var col in WallsNear(e.Position, outer))
        {
            if (col.gameObject.layer != ShipLayer) continue;
            if (!ClosestPoint(col, e.Position, out Vector2 q)) continue;
            float d = (q - e.Position).magnitude;
            if (d <= e.Size || d > outer) continue;
            WallDurability.Hit(q, 1);
            DamageMap.Cracks(q, 0.35f, AngleOf(q - e.Position));
            cracked++;
        }

        string visual = cut > 0 ? DamageMap.Breach(core, removed, p.Scorch) : null;
        return $"explosion cut={cut} cracked={cracked} visual={visual ?? "ok"}";
    }

    // 打撃: 向きの先で最初に当たる壁の耐久を削る。0 になったらその壁の区間だけ四角く抜ける
    private static string Strike(in DamageEvent e, DamageProfile p)
    {
        Vector2 dir = e.Direction.sqrMagnitude > 1e-6f ? e.Direction.normalized : Vector2.right;
        if (!FindWall(e.Position, dir, p.Reach, out Vector2 hit, out Vector2 normal)) return "blunt no wall in reach";

        int hp = WallDurability.Hit(hit, p.WallDamage);
        if (hp > 0)
        {
            // 耐久が減るほどひびが育つ
            float reach = hp >= WallDurability.MaxHp - 1 ? 0.3f : 0.6f;
            DamageMap.Cracks(hit, reach, AngleOf(dir));
            return $"blunt hit hp={hp} at=({hit.x:0.00},{hit.y:0.00})";
        }

        // 叩いた面から壁の奥 (部屋と部屋の隙間・向こうの部屋の枠) までの区間を抜く
        Vector2 tangent = new(-normal.y, normal.x);
        Vector2 center = hit - normal * (p.BreachDepth * 0.5f - 0.1f);
        var rect = new RectShape(center, tangent, p.BreachLength, p.BreachDepth);
        var removed = new List<Vector2>();
        int cut = 0;
        foreach (var col in WallsNear(center, rect.BoundRadius))
            if (EdgeCutter.Cut(col, rect, removed)) cut++;

        string visual = cut > 0 ? DamageMap.Breach(rect, removed, p.Scorch) : null;
        return $"blunt breach cut={cut} visual={visual ?? "ok"}";
    }

    private static IEnumerable<EdgeCollider2D> WallsNear(Vector2 c, float r)
    {
        foreach (var c2 in Physics2D.OverlapCircleAll(c, r, WallMask))
        {
            var e = c2 ? c2.TryCast<EdgeCollider2D>() : null;
            if (!e || e.isTrigger || !e.enabled || IsProtected(e)) continue;
            yield return e;
        }
    }

    // ゲームに関わる物は壊さない (当面の裁定)
    private static bool IsProtected(Component c) => c.GetComponentInParent<OpenableDoor>();

    private static bool FindWall(Vector2 from, Vector2 dir, float reach, out Vector2 point, out Vector2 normal)
    {
        point = default; normal = default;
        float best = float.MaxValue;
        foreach (var h in Physics2D.CircleCastAll(from, 0.1f, dir, reach, 1 << ShipLayer))
        {
            if (!h.collider || h.collider.isTrigger || IsProtected(h.collider) || h.distance >= best) continue;
            best = h.distance;
            point = h.point;
            normal = h.normal;
        }
        return best < float.MaxValue;
    }

    // Collider2D.ClosestPoint は Android の libunity に無いので、折れ線の頂点から自前で求める
    private static bool ClosestPoint(EdgeCollider2D col, Vector2 p, out Vector2 q)
    {
        q = default;
        var t = col.transform;
        var pts = col.points;
        if (pts.Length < 2) return false;
        float best = float.MaxValue;
        Vector2 prev = t.TransformPoint(pts[0] + col.offset);
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 cur = t.TransformPoint(pts[i] + col.offset);
            Vector2 d = cur - prev;
            float l2 = d.sqrMagnitude;
            float s = l2 > 0 ? Math.Clamp(Vector2.Dot(p - prev, d) / l2, 0f, 1f) : 0f;
            Vector2 c = prev + d * s;
            float dist = (c - p).sqrMagnitude;
            if (dist < best) { best = dist; q = c; }
            prev = cur;
        }
        return true;
    }

    private static float AngleOf(Vector2 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
}
