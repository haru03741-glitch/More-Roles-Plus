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
            if (!ClosestPointWithNormal(col, e.Position, out Vector2 q, out Vector2 n)) continue;
            // 外壁は壊さない: 爆心から見て壁の向こう側に奥の面があるか
            Vector2 away = Vector2.Dot(q - e.Position, n) >= 0 ? n : -n;
            if (!HasFarSide(q, away)) continue;
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
        if (visual == null) TerrainFx.Explosion(e.Position, e.Size, e.Seed);
        return $"explosion cut={cut} cracked={cracked} visual={visual ?? "ok"}";
    }

    // 打撃: 向きの先で最初に当たる壁の耐久を削る。0 になったらその壁の区間だけ四角く抜ける
    private static string Strike(in DamageEvent e, DamageProfile p)
    {
        Vector2 dir = e.Direction.sqrMagnitude > 1e-6f ? e.Direction.normalized : Vector2.right;
        if (!FindWall(e.Position, dir, p.Reach, out Vector2 hit, out Vector2 normal)) return "blunt no wall in reach";
        // 叩いた辺りの多角形・箱の壁を先に折れ線へ (奥の面を辿れるように)
        WallsNear(hit, MaxDepth);
        float far = FarSide(hit, -normal);
        if (far <= 0f) return "blunt outer wall (protected)";
        float depth = Math.Max(p.BreachDepth, far + 0.25f);

        int hp = WallDurability.Hit(hit, p.WallDamage);
        if (hp > 0)
        {
            // 耐久が減るほどひびが育つ
            float reach = hp >= WallDurability.MaxHp - 1 ? 0.3f : 0.6f;
            if (DamageMap.Cracks(hit, reach, AngleOf(dir)) == null) TerrainFx.Chip(hit, dir, e.Seed);
            return $"blunt hit hp={hp} at=({hit.x:0.00},{hit.y:0.00})";
        }

        // 叩いた面から壁の奥 (部屋と部屋の隙間・向こうの部屋の枠) までの区間を抜く
        Vector2 tangent = new(-normal.y, normal.x);
        Vector2 center = hit - normal * (depth * 0.5f - 0.1f);
        var rect = new RectShape(center, tangent, p.BreachLength, depth);
        var removed = new List<Vector2>();
        int cut = 0;
        foreach (var col in WallsNear(center, rect.BoundRadius))
            if (EdgeCutter.Cut(col, rect, removed)) cut++;

        // 下から叩いた (壁の手前の床に立っている) 時は、叩いた点の少し上から下を見た目では抜かない
        float floorY = normal.y < -0.5f ? hit.y + 0.12f : float.NegativeInfinity;
        string visual = cut > 0 ? DamageMap.Breach(rect, removed, p.Scorch, floorY) : null;
        // 壁が崩れ落ちて瓦礫の山になる (壁の線の少し奥を中心に)
        if (visual == null) TerrainFx.Crumble(hit - normal * (depth * 0.3f), tangent, normal, p.BreachLength, e.Seed);
        return $"blunt breach cut={cut} depth={depth:0.00} visual={visual ?? "ok"}";
    }

    // 範囲に掛かる壁を折れ線で返す。箱・多角形・円の壁はここで初めて折れ線に置き換える
    private static List<EdgeCollider2D> WallsNear(Vector2 c, float r)
    {
        var list = new List<EdgeCollider2D>();
        foreach (var c2 in Physics2D.OverlapCircleAll(c, r, WallMask))
        {
            if (!c2 || c2.isTrigger || !c2.enabled || IsProtected(c2)) continue;
            var e = c2.TryCast<EdgeCollider2D>();
            if (e) { list.Add(e); continue; }
            list.AddRange(WallOutline.Convert(c2));
        }
        return list;
    }

    // 壊さない物: ゲームに関わる物 (当面の裁定)・家具や小物 (Ship 層に入っているマップがある)・マップの外周と地形
    private static readonly System.Text.RegularExpressions.Regex ProtectedName = new(
        @"table|chair|desk|box|rock|ball|stand|panel|candle|parasite_|railing|mushroom|boundary|cliff|lava|^hole$|bridge|background|computer|office-|storage-",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool IsProtected(Component c)
    {
        if (c.GetComponentInParent<OpenableDoor>()) return true;
        for (var t = c.transform; t && !t.GetComponent<ShipStatus>(); t = t.parent)
            if (ProtectedName.IsMatch(t.name)) return true;
        return false;
    }

    // 外壁 (向こう側が宇宙・マップの外) は壊さない (当面の裁定)。
    // 内壁には「奥の面」がある (部屋と部屋の隙間の向こうの枠・厚みのある壁の裏側)。壁の面から奥へ MaxDepth 以内に
    // 次の壁の面が無ければ、向こうは何も無い外側とみなす。部屋の範囲は屋外 (Polus など) を含まないので使わない
    private static bool HasFarSide(Vector2 surface, Vector2 inward) => FarSide(surface, inward) > 0f;

    // 叩いた面から奥へ、壁の向こう側の面までの厚さ (見つからなければ既定値)。
    // 奥の面 = 叩いた面から MaxDepth 以内で次に当たる Ship 層の壁 (部屋と部屋の間の隙間と向こうの部屋の枠を含む)
    private const float MaxDepth = 2.6f;

    // 奥の面までの距離 (無ければ 0)。CircleCastAll は 1 つの部品につき最初の 1 か所しか返さないので、
    // 当たった少し先から探し直して同じ折れ線の奥の面まで辿る。RaycastAll は Android の libunity に無いので細い円で代用。
    // 多角形の壁は内側から投げると出口の面を返さないので、呼ぶ前に折れ線へ置き換えておくこと
    private static float FarSide(Vector2 hit, Vector2 inward)
    {
        float far = 0f, travelled = 0.05f;
        for (int step = 0; step < 8 && travelled < MaxDepth; step++)
        {
            float best = float.MaxValue;
            foreach (var h in Physics2D.CircleCastAll(hit + inward * travelled, 0.01f, inward, MaxDepth - travelled, 1 << ShipLayer))
            {
                if (!h.collider || h.collider.isTrigger || IsProtected(h.collider)) continue;
                if (h.distance > 0.001f && h.distance < best) best = h.distance;
            }
            if (best == float.MaxValue) break;
            travelled += best;
            far = travelled;
            travelled += 0.05f;
        }
        return far;
    }

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

    private static bool ClosestPointWithNormal(EdgeCollider2D col, Vector2 p, out Vector2 q, out Vector2 n)
    {
        n = default;
        if (!ClosestPoint(col, p, out q)) return false;
        // いちばん近い区間の向きから法線を作る
        var t = col.transform;
        var pts = col.points;
        float best = float.MaxValue;
        Vector2 prev = t.TransformPoint(pts[0] + col.offset);
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 cur = t.TransformPoint(pts[i] + col.offset);
            Vector2 d = cur - prev;
            float l2 = d.sqrMagnitude;
            float s = l2 > 0 ? Math.Clamp(Vector2.Dot(p - prev, d) / l2, 0f, 1f) : 0f;
            float dist = (prev + d * s - p).sqrMagnitude;
            if (dist < best && l2 > 0) { best = dist; n = new Vector2(-d.y, d.x).normalized; }
            prev = cur;
        }
        return n != default;
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
