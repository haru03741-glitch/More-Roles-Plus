// 向いている方向へ撃つ役職 (クノイチ・トリガーハッピー・波動砲) の共通部品
using UnityEngine;

namespace MoreRolesPlus.Roles;

internal static class ShotKit
{
    private static Vector2 _face = Vector2.right;

    // 自分の端末で毎フレーム呼ぶ: 最後に歩いた向きを覚える
    internal static void Track()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !lp.MyPhysics || !lp.MyPhysics.body) return;
        var v = lp.MyPhysics.body.velocity;
        if (v.sqrMagnitude > 0.01f) _face = v.normalized;
    }

    internal static Vector2 Facing => _face;

    // 線の絵に使う材質 (1 つを使い回す。Destroy では材質は消えないので、物ごとに作らない)
    private static Material _lineMat;
    internal static Material LineMat => _lineMat ? _lineMat : (_lineMat = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.HideAndDontSave });

    // from から dir へ、壁 (当たりのある物) までの距離。無ければ len
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit2D> _rayHits = new((long)8); // int を渡すと Android で壊れた配列になる
    internal static float WallDistance(Vector2 from, Vector2 dir, float len) => WallDistance(from, dir, len, Constants.ShipAndObjectsMask);

    internal static float WallDistance(Vector2 from, Vector2 dir, float len, int mask)
    {
        int n = Physics2D.RaycastNonAlloc(from, dir, _rayHits, len, mask);
        float best = len;
        for (int i = 0; i < n; i++)
        {
            var c = _rayHits[i].collider;
            if (!c || c.isTrigger) continue;
            float d = _rayHits[i].distance;
            if (d < best) best = d;
        }
        return best;
    }

    // from から dir へ、壁までの距離とその面の向き (跳ね返りに使う)。壁が無ければ false で dist = len
    internal static bool WallHit(Vector2 from, Vector2 dir, float len, out float dist, out Vector2 normal)
    {
        int n = Physics2D.RaycastNonAlloc(from, dir, _rayHits, len, Constants.ShipAndObjectsMask);
        dist = len;
        normal = default;
        int best = -1;
        // 当たりは近い順に並んでいるので、最初の当たりのある物が壁 (弾ごとに呼ぶので残りは読まない)
        for (int i = 0; i < n; i++)
        {
            var c = _rayHits[i].collider;
            if (!c || c.isTrigger) continue;
            dist = _rayHits[i].distance;
            best = i;
            break;
        }
        if (best < 0) return false;
        normal = _rayHits[best].normal;
        return true;
    }

    // from から dir へ len だけ進んだ所 (walls なら壁で止まる)
    internal static Vector2 End(Vector2 from, Vector2 dir, float len, bool walls)
    {
        if (walls)
        {
            var hit = Physics2D.Raycast(from, dir, len, Constants.ShipAndObjectsMask);
            if (hit.collider) return hit.point;
        }
        return from + dir * len;
    }

    // p が線分 a-b から width 以内にいるか。いるなら a からの距離を返す (いなければ -1)
    internal static float OnLine(Vector2 a, Vector2 b, float width, PlayerControl p)
    {
        Vector2 ab = b - a, ap = p.GetTruePosition() - a;
        float len = ab.magnitude;
        if (len < 0.001f) return -1f;
        float t = Vector2.Dot(ap, ab / len);
        if (t < 0f || t > len) return -1f;
        float d = (ap - ab / len * t).magnitude;
        return d <= width ? t : -1f;
    }
}
