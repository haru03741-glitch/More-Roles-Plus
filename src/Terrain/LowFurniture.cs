using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int nCol = 0;
        foreach (var c in ship.GetComponentsInChildren<Collider2D>(false))
        {
            if (!c || !c.enabled || c.isTrigger || c.gameObject.layer != ShipLayer) continue;
            if (c.gameObject.name.StartsWith("Mrp", StringComparison.Ordinal) || c.GetComponentInParent<OpenableDoor>()) continue;
            if (FurnitureKinds.TryGet(c.transform, shipName, out _) || BreakableProps.Owns(c)) continue; // 動く家具・物ごと壊れる家具は別に守る
            nCol++;
            if (Natural(c.transform)) continue;
            // 頂点は物ごとに行列を 1 回読んで小数で直す (TransformPoint と Vector2 の演算子は 1 回ごとに interop)
            var m = c.transform.localToWorldMatrix;
            var edge = c.TryCast<EdgeCollider2D>();
            if (edge)
            {
                var src = edge.points;
                if (src.Length < 2) continue;
                var o = edge.offset;
                pts.Clear();
                foreach (var p in src) Add(pts, m, p.x + o.x, p.y + o.y);
                float gx = pts[0].x - pts[pts.Count - 1].x, gy = pts[0].y - pts[pts.Count - 1].y;
                if (pts.Count < 4 || MathF.Sqrt(gx * gx + gy * gy) >= 0.05f) continue; // 開いた線
                pts.RemoveAt(pts.Count - 1);
                Check(c, pts);
                continue;
            }
            var box = c.TryCast<BoxCollider2D>();
            if (box)
            {
                var sz = box.size;
                var o = box.offset;
                float hx = sz.x * 0.5f, hy = sz.y * 0.5f;
                pts.Clear();
                Add(pts, m, o.x - hx, o.y - hy); Add(pts, m, o.x + hx, o.y - hy);
                Add(pts, m, o.x + hx, o.y + hy); Add(pts, m, o.x - hx, o.y + hy);
                Check(c, pts);
                continue;
            }
            var poly = c.TryCast<PolygonCollider2D>();
            if (poly)
            {
                var o = poly.offset;
                for (int k = 0; k < poly.pathCount; k++)
                {
                    var path = poly.GetPath(k);
                    pts.Clear();
                    foreach (var p in path) Add(pts, m, p.x + o.x, p.y + o.y);
                    Check(c, pts);
                }
                continue;
            }
            var circle = c.TryCast<CircleCollider2D>();
            if (circle)
            {
                var o = circle.offset;
                float rad = circle.radius;
                pts.Clear();
                for (int i = 0; i < 16; i++)
                {
                    float ang = i * MathF.PI / 8f;
                    Add(pts, m, o.x + MathF.Cos(ang) * rad, o.y + MathF.Sin(ang) * rad);
                }
                Check(c, pts);
            }
        }
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"[LowFurniture] rects={Rects.Count} surveyed={Survey.Count} colliders={nCol} shadowProbes={_nShadow} ms={sw.Elapsed.TotalMilliseconds:F1}");
        _nShadow = 0;
    }

    // 輪の外側が床か・影の線が沿っているか
    private static void Add(List<Vector2> pts, Matrix4x4 m, float x, float y) =>
        pts.Add(FxMath.V2(m.m00 * x + m.m01 * y + m.m03, m.m10 * x + m.m11 * y + m.m13));

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
        // 先に床だけ数え、床が足りた輪だけ影の線を問い合わせる (物理の問い合わせが重い)。
        // 影が上限を超えた所で打ち切る (守らないと決まる)。打ち切った輪の影の割合はテスト用の記録に NaN で残す
        int pieces = 0, floor = 0, shadow = 0;
        bool measured = false;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                if (pieces == 0 || floor / (float)pieces < MinFloor) break;
                measured = true;
            }
            for (int i = 0; i < n; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % n];
                float ex = b.x - a.x, ey = b.y - a.y;
                float len = MathF.Sqrt(ex * ex + ey * ey);
                if (len < 1e-4f) continue;
                float dx = ex / len, dy = ey / len, nx = -dy, ny = dx;
                int parts = Math.Max(1, (int)MathF.Ceiling(len / Piece));
                for (int k = 0; k < parts; k++)
                {
                    float s = len * (k + 0.5f) / parts;
                    float mx = a.x + dx * s, my = a.y + dy * s;
                    if (pass == 0)
                    {
                        pieces++;
                        float q = outSign * FloorProbe;
                        if (SolidMap.IslandAt(mx + nx * q, my + ny * q) != 0) floor++;
                        continue;
                    }
                    if (Physics2D.OverlapCircleAll(FxMath.V2(mx, my), ShadowNear, Constants.ShadowMask).Length > 0) shadow++; // OverlapCircle (1 個版) は Android の libunity に無い
                    _nShadow++;
                    if (shadow / (float)pieces > MaxShadow) { measured = false; break; }
                }
                if (pass == 1 && !measured) break;
            }
        }
        if (pieces == 0) return;
        float fFloor = floor / (float)pieces, fShadow = measured ? shadow / (float)pieces : float.NaN;
        bool kept = measured && fShadow <= MaxShadow;
        var rc = Rect.MinMaxRect(x0 - Margin, y0 - Margin, x1 + Margin, y1 + DamageMap.FurnitureUp);
        if (kept) Rects.Add(rc);
        if (fFloor >= 0.2f) Survey.Add(new Found(rc, Path(c), kept, fFloor, fShadow));
    }

    private static int _nShadow; // 影の線の問い合わせの数 (ログ用)

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
