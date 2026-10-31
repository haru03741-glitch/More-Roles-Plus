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

    // 全員の端末で: 線をしばらく描く
    internal static void Beam(Vector2 a, Vector2 b, Color c, float width, float secs)
    {
        var go = new GameObject("MrpBeam");
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, new Vector3(a.x, a.y, -5f));
        lr.SetPosition(1, new Vector3(b.x, b.y, -5f));
        lr.startWidth = lr.endWidth = width;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = c;
        PortKit.After(secs, () => { if (go) Object.Destroy(go); });
    }
}
