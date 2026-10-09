using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 船の層 (9) の当たり判定のうち、部屋の中に独立して置かれた低い物 (机・台・棚など) を家具として守る範囲。
// 部屋の外周の線と違い、輪の外側がほぼ全部歩ける床で (外周の輪は外側が壁の中か船の外)、視界の影の線が沿っていない
// (壁や柱は視界を遮るので影の線が沿う)。開いた線 (溶岩の縁・台の縁) と天然の結晶は家具でない。
// 試合の始めの形で 1 回だけ決める (壊した後の形で決め直すと、端末ごとに守る範囲が食い違う)
internal static class LowFurniture
{
    private const int ShipLayer = 9;
    private const float Piece = 0.25f;        // 輪郭を調べる刻み
    private const float FloorProbe = 0.15f;   // 輪郭から外側へこの距離の点が床か
    private const float ShadowNear = 0.3f;    // 輪郭のこの距離に影の線があれば視界を遮る物
    private const float MinFloor = 0.5f;      // 外側が床の刻みの割合の下限
    private const float MaxShadow = 0.2f;     // 影の線が沿う刻みの割合の上限
    private const float MaxSide = 6f;         // これより大きい塊は家具でない (部屋の中の壁の島)
    private const float Margin = 0.08f;

    internal readonly struct Found
    {
        public readonly Rect Area;
        public readonly string Name;
        public readonly bool Kept;
        public readonly float Floor, Shadow;
        public Found(Rect area, string name, bool kept, float floor, float shadow)
        {
            Area = area; Name = name; Kept = kept; Floor = floor; Shadow = shadow;
        }
    }

    private static ShipStatus _ship;
    private static readonly List<Rect> Rects = new();
    internal static readonly List<Found> Survey = new(); // 調べた輪と線の全部 (テスト用)

    // 船が替わった後の最初の呼び出しで作る (歩ける所の地図が要る。試合の始めの先回りの準備でも呼ぶ)
    internal static void Ensure()
    {
        var ship = ShipStatus.Instance;
        if (_ship == ship || !SolidMap.Ensure()) return;
        FurnitureSplit.Apply(ship); // 1 枚の絵から分けた家具は、元の当たり判定を止めてから数える (呼ぶ順で結果が変わらないように)
        Build(ship);
    }

    private static void Build(ShipStatus ship)
    {
        _ship = ship;
        Rects.Clear();
        Survey.Clear();
        if (!ship || !SolidMap.Valid) return;
        string shipName = FurnitureKinds.ShipName(ship);
        var pts = new List<Vector2>();
        foreach (var c in ship.GetComponentsInChildren<Collider2D>(false))
        {
            if (!c || !c.enabled || c.isTrigger || c.gameObject.layer != ShipLayer) continue;
            if (c.gameObject.name.StartsWith("Mrp", StringComparison.Ordinal) || c.GetComponentInParent<OpenableDoor>()) continue;
            if (FurnitureKinds.TryGet(c.transform, shipName, out _) || BreakableProps.Owns(c)) continue; // 動く家具・物ごと壊れる家具は別に守る
            if (Natural(c.transform)) continue;
            var t = c.transform;
            var edge = c.TryCast<EdgeCollider2D>();
            if (edge)
            {
                var src = edge.points;
                if (src.Length < 2) continue;
                pts.Clear();
                foreach (var p in src) pts.Add(t.TransformPoint(p + edge.offset));
                if (pts.Count < 4 || (pts[0] - pts[pts.Count - 1]).magnitude >= 0.05f) continue; // 開いた線
                pts.RemoveAt(pts.Count - 1);
                Check(c, pts);
                continue;
            }
            var box = c.TryCast<BoxCollider2D>();
            if (box)
            {
                Vector2 hs = box.size * 0.5f, o = box.offset;
                pts.Clear();
                pts.Add(t.TransformPoint(o + new Vector2(-hs.x, -hs.y))); pts.Add(t.TransformPoint(o + new Vector2(hs.x, -hs.y)));
                pts.Add(t.TransformPoint(o + new Vector2(hs.x, hs.y))); pts.Add(t.TransformPoint(o + new Vector2(-hs.x, hs.y)));
                Check(c, pts);
                continue;
            }
            var poly = c.TryCast<PolygonCollider2D>();
            if (poly)
            {
                for (int k = 0; k < poly.pathCount; k++)
                {
                    var path = poly.GetPath(k);
                    pts.Clear();
                    foreach (var p in path) pts.Add(t.TransformPoint(p + poly.offset));
                    Check(c, pts);
                }
                continue;
            }
            var circle = c.TryCast<CircleCollider2D>();
            if (circle)
            {
                pts.Clear();
                for (int i = 0; i < 16; i++)
                {
                    float ang = i * MathF.PI / 8f;
                    pts.Add(t.TransformPoint(circle.offset + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * circle.radius));
                }
                Check(c, pts);
            }
        }
    }

    // 輪の外側が床か・影の線が沿っているか
    private static void Check(Collider2D c, List<Vector2> pts)
    {
        int n = pts.Count;
        if (n < 2) return;
        float x0 = pts[0].x, x1 = x0, y0 = pts[0].y, y1 = y0, area2 = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % n];
            x0 = Math.Min(x0, a.x); x1 = Math.Max(x1, a.x); y0 = Math.Min(y0, a.y); y1 = Math.Max(y1, a.y);
            area2 += a.x * b.y - b.x * a.y;
        }
        if (x1 - x0 > MaxSide || y1 - y0 > MaxSide) return;
        float outSign = area2 >= 0f ? -1f : 1f; // 左回りの輪なら、進む向きの右が外
        int pieces = 0, floor = 0, shadow = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[i], d = pts[(i + 1) % n] - a;
            float len = d.magnitude;
            if (len < 1e-4f) continue;
            Vector2 dir = d / len, nrm = new(-dir.y, dir.x);
            int parts = Math.Max(1, (int)MathF.Ceiling(len / Piece));
            for (int k = 0; k < parts; k++)
            {
                Vector2 m = a + dir * (len * (k + 0.5f) / parts);
                pieces++;
                if (SolidMap.IslandAt(m + nrm * (outSign * FloorProbe)) != 0) floor++;
                if (Physics2D.OverlapCircleAll(m, ShadowNear, Constants.ShadowMask).Length > 0) shadow++; // OverlapCircle (1 個版) は Android の libunity に無い
            }
        }
        if (pieces == 0) return;
        float fFloor = floor / (float)pieces, fShadow = shadow / (float)pieces;
        bool kept = fFloor >= MinFloor && fShadow <= MaxShadow;
        var rc = Rect.MinMaxRect(x0 - Margin, y0 - Margin, x1 + Margin, y1 + DamageMap.FurnitureUp);
        if (kept) Rects.Add(rc);
        if (fFloor >= 0.2f) Survey.Add(new Found(rc, Path(c), kept, fFloor, fShadow));
    }

    private static bool Natural(Transform t)
    {
        for (; t && !t.GetComponent<ShipStatus>(); t = t.parent)
            if (t.name.IndexOf("Crystal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string Path(Component c)
    {
        string path = c.name;
        for (var t = c.transform.parent; t && !t.GetComponent<ShipStatus>(); t = t.parent) path = t.name + "/" + path;
        return path;
    }

    // 円 (中心 c・半径 r) に掛かる範囲を足す
    internal static void AddNear(Vector2 c, float r, List<Rect> into)
    {
        Ensure();
        foreach (var rc in Rects)
            if (c.x + r > rc.xMin && c.x - r < rc.xMax && c.y + r > rc.yMin && c.y - r < rc.yMax) into.Add(rc);
    }
}
