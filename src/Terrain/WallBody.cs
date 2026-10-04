using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壊す前の壁の線から「壁の中 (厚みの内側)」を判定し、穴の側面に蓋 (壁の断面の当たり判定) を作る。
// 壁の中 = 歩ける基準点 (叩いた側・爆心) からその点まで線を引き、壁の線を奇数回横切る所。
// そのうち切り取った面から壁を横切らずに見通せる所だけを「穴から露出した壁の中」とする
// (切っていない外壁の向こうの宇宙などに蓋や穴の絵を作らないため)。
// 見た目の穴 (DamageMap.Stamp) と蓋は同じ判定から出す。どちらかだけだと絵と当たり判定が食い違う
internal sealed class WallBody
{
    public const string CapName = "MrpBreachCap";

    private readonly List<Vector2> _seg = new();     // 壊す前の壁の線分 (両端を 2 つずつ)
    private readonly List<Vector2> _opening = new(); // 切り取った区間 (両端を 2 つずつ)
    private readonly Vector2 _ref;

    public WallBody(IEnumerable<EdgeCollider2D> walls, Vector2 walkableRef)
    {
        _ref = walkableRef;
        foreach (var col in walls)
        {
            if (!col.enabled) continue;
            var t = col.transform;
            var pts = col.points;
            if (pts.Length < 2) continue;
            Vector2 off = col.offset;
            Vector2 prev = t.TransformPoint(pts[0] + off);
            for (int i = 1; i < pts.Length; i++)
            {
                Vector2 cur = t.TransformPoint(pts[i] + off);
                _seg.Add(prev); _seg.Add(cur);
                prev = cur;
            }
        }
    }

    // 切り取りの結果 (EdgeCutter の removed) を渡す
    public void SetOpening(List<Vector2> removed)
    {
        _opening.Clear();
        _opening.AddRange(removed);
    }

    // 穴から露出した壁の中か
    public bool Exposed(float x, float y)
    {
        var p = new Vector2(x, y);
        if ((Crossings(_ref, p) & 1) == 0) return false;
        // いちばん近い切り取り区間の点まで、壁を横切らずに届くか (切った面そのものは数えないよう少し手前で止める)
        if (!NearestOpening(p, out Vector2 q)) return false;
        float dx = p.x - q.x, dy = p.y - q.y, l = MathF.Sqrt(dx * dx + dy * dy);
        if (l < 0.02f) return true;
        var toward = new Vector2(q.x + dx / l * 0.01f, q.y + dy / l * 0.01f);
        return Crossings(p, toward) == 0;
    }

    // a から b まで壁の線を横切らずに届くか
    public bool Clear(Vector2 a, Vector2 b) => Crossings(a, b) == 0;

    // 形の縁のうち露出した壁の中にある部分を折れ線で返す (蓋)
    public List<List<Vector2>> Caps(CutShape shape)
    {
        var loop = shape.Outline(0.05f);
        var caps = new List<List<Vector2>>();
        int n = loop.Count;
        if (n < 3) return caps;
        var inside = new bool[n];
        int start = -1;
        for (int i = 0; i < n; i++)
        {
            inside[i] = Exposed(loop[i].x, loop[i].y);
            if (!inside[i] && start < 0) start = i;
        }
        if (start < 0) return caps; // 縁が全部壁の中 = どこにも通じていない

        List<Vector2> cur = null;
        for (int k = 1; k <= n; k++)
        {
            int i = (start + k) % n, prev = (start + k - 1) % n;
            if (inside[i])
            {
                if (cur == null) { cur = new List<Vector2> { Edge(loop[prev], loop[i]) }; caps.Add(cur); }
                cur.Add(loop[i]);
            }
            else if (cur != null)
            {
                cur.Add(Edge(loop[i], loop[prev]));
                cur = null;
            }
        }
        return caps;
    }

    // 外の点と中の点の間で、切り替わる点を二分で詰める (そこが切り取った面と縁の交点)
    private Vector2 Edge(Vector2 outP, Vector2 inP)
    {
        for (int it = 0; it < 10; it++)
        {
            Vector2 m = (outP + inP) * 0.5f;
            if (Exposed(m.x, m.y)) inP = m; else outP = m;
        }
        return (outP + inP) * 0.5f;
    }

    private bool NearestOpening(Vector2 p, out Vector2 q)
    {
        q = default;
        float best = float.MaxValue;
        for (int i = 0; i + 1 < _opening.Count; i += 2)
        {
            Vector2 a = _opening[i], b = _opening[i + 1];
            float ax = b.x - a.x, ay = b.y - a.y, l2 = ax * ax + ay * ay;
            float s = l2 > 0 ? Math.Clamp(((p.x - a.x) * ax + (p.y - a.y) * ay) / l2, 0f, 1f) : 0f;
            float cx = a.x + ax * s, cy = a.y + ay * s;
            float dist = (cx - p.x) * (cx - p.x) + (cy - p.y) * (cy - p.y);
            if (dist < best) { best = dist; q = new Vector2(cx, cy); }
        }
        return best < float.MaxValue;
    }

    // 線分 a→b が壁の線を横切る回数
    private int Crossings(Vector2 a, Vector2 b)
    {
        int n = 0;
        float dx = b.x - a.x, dy = b.y - a.y;
        for (int i = 0; i + 1 < _seg.Count; i += 2)
        {
            Vector2 c = _seg[i], e = _seg[i + 1];
            float ex = e.x - c.x, ey = e.y - c.y;
            float den = dx * ey - dy * ex;
            if (MathF.Abs(den) < 1e-12f) continue;
            float cx = c.x - a.x, cy = c.y - a.y;
            float t = (cx * ey - cy * ex) / den; // a→b 上の位置
            float u = (cx * dy - cy * dx) / den; // 壁の線分上の位置
            if (t > 0f && t <= 1f && u >= 0f && u < 1f) n++; // 頂点の二重数えを避けて片側だけ閉じる
        }
        return n;
    }

    // 蓋を作る (動きの層と視界の層)。マップの子に置くので試合が終われば一緒に消え、次の破壊では普通の壁として切られる
    public static int Build(List<List<Vector2>> caps)
    {
        var ship = ShipStatus.Instance;
        if (!ship || caps.Count == 0) return 0;
        int made = 0;
        foreach (int layer in new[] { 9, 10 })
        {
            var go = new GameObject(CapName) { layer = layer };
            go.transform.SetParent(ship.transform, true);
            var t = go.transform;
            foreach (var raw in caps)
            {
                // 頂点を間引く (縁は 0.05 刻みで調べたので直線の上にも点が並ぶ。本編の視界は頂点ごとに光線を飛ばすので、多すぎると重く欠けも出る)
                var cap = Simplify(raw, 0.015f);
                if (cap.Count < 2) continue;
                var col = go.AddComponent<EdgeCollider2D>();
                var pts = new Vector2[cap.Count];
                for (int i = 0; i < pts.Length; i++) pts[i] = t.InverseTransformPoint(cap[i]);
                col.points = pts;
                made++;
            }
        }
        return made;
    }

    // 折れ線の簡略化 (Douglas-Peucker)。tol より近い点は落とす
    private static List<Vector2> Simplify(List<Vector2> pts, float tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (i0, i1) = stack.Pop();
            Vector2 a = pts[i0], b = pts[i1];
            float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            int best = -1;
            float worst = tol;
            for (int i = i0 + 1; i < i1; i++)
            {
                float d = len > 1e-6f
                    ? MathF.Abs((pts[i].x - a.x) * dy - (pts[i].y - a.y) * dx) / len
                    : MathF.Sqrt((pts[i].x - a.x) * (pts[i].x - a.x) + (pts[i].y - a.y) * (pts[i].y - a.y));
                if (d > worst) { worst = d; best = i; }
            }
            if (best < 0) continue;
            keep[best] = true;
            stack.Push((i0, best));
            stack.Push((best, i1));
        }
        var outp = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
        return outp;
    }
}
