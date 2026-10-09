using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// スケルドの外壁が宇宙へ抜けた穴の「喉」: 口 (元の壁の線) から宇宙まで、船体の絵 (Hull* のメッシュ) を切り抜いて後ろの星空を見せる。
// 船体のメッシュは複製して名前が Hull で始まらない子に載せ、元の描画だけ止める (歩ける所の地図は元のメッシュから作るので変えない)。
// 切り口には本編の絵柄と同じ濃い輪郭線を同じメッシュに頂点色で足す。部屋の絵の壁の帯は損傷マスク (R と A) で抜く。
// 穴が開いた時に 1 回だけ動く (毎フレームの処理は無い)。見た目だけで同期はしない (全員が同じ口から同じ形を作る)
internal static class HullThroat
{
    private const float Margin = 0.35f;     // 宇宙側へのはみ出し (切る物が無いので無害)
    private const float FloorStop = 0.2f;   // 喉の先に別の部屋の床がある時に手前で止める余白
    private const float Outline = 0.055f;   // 切り口の輪郭線の太さ
    private const float SampleStep = 0.1f;  // 口に沿って深さを測る間隔
    private static readonly Color32 OutlineColor = new(22, 22, 24, 255);

    private struct P
    {
        public float X, Y;
        public P(float x, float y) { X = x; Y = y; }
    }

    private sealed class Cut
    {
        public Transform Tf;
        public MeshRenderer Src;
        public GameObject Go;
        public Mesh Mesh;
        public readonly List<Vector3> V = new();
        public readonly List<Color32> C = new();
        public readonly List<Vector2> U = new();
        public readonly List<int> T = new();
        public bool HasUv;
    }

    private static readonly List<Cut> Cuts = new();
    private static readonly List<P[]> Opened = new(); // 開けた喉の塊 (世界座標・反時計回り)
    private static int _shipGen = -1;
    private static bool _failed;

    // 口 a-b (外向き away) の奥を宇宙まで抜く。TerrainDamage が口を張った直後に呼ぶ (スケルドだけ)
    internal static void Open(Vector2 a, Vector2 b, Vector2 away)
    {
        try { OpenCore(a, b, away); }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Logger.LogError($"[HullThroat] open: {e}");
            // 切り抜きの途中で落ちたら元の船体の絵に戻す (船体が消えたままにしない)
            foreach (var c in Cuts)
            {
                if (c.Go) c.Go.SetActive(false);
                if (c.Src) c.Src.enabled = true;
            }
        }
    }

    private static void OpenCore(Vector2 a, Vector2 b, Vector2 away)
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            Cuts.Clear();
            Opened.Clear();
            _failed = false;
        }
        if (_failed || !ShipStatus.Instance) return;
        float nx = away.x, ny = away.y, nl = MathF.Sqrt(nx * nx + ny * ny);
        if (nl < 1e-4f) return;
        nx /= nl; ny /= nl;
        float ax = a.x, ay = a.y, bx = b.x, by = b.y;
        float ux = bx - ax, uy = by - ay, len = MathF.Sqrt(ux * ux + uy * uy);
        if (len < 0.05f) return;
        ux /= len; uy /= len;

        // 口に沿って宇宙までの深さを測る。いちばん深い所に揃えて矩形にする (凸の塊で切るため)。
        // 先に別の部屋の床に当たる所は床の手前で止める
        float depth = 0f;
        int n = Math.Max(2, (int)MathF.Ceiling(len / SampleStep) + 1);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            float d = SolidMap.SkyDistance(ax + (bx - ax) * t, ay + (by - ay) * t, nx, ny, TerrainDamage.HullReach + 1f);
            if (d < 0f) d = MathF.Max(0f, -d - FloorStop);
            else if (d > 0f) d += Margin;
            depth = MathF.Max(depth, d);
        }
        if (depth <= 0.05f) return;

        // 喉 = 口から外向きに少しすぼまる台形 + 両脇のちぎれた欠け。種は口の座標から (全員で同じ形)
        var rnd = new System.Random((int)(ax * 977f) ^ (int)(ay * 613f) * 31 ^ (int)(bx * 389f) * 17 ^ (int)(by * 211f));
        float pinch = MathF.Min(len * 0.18f, 0.3f); // 宇宙側の端は左右これだけ内側
        var holes = new List<P[]>
        {
            new[]
            {
                new P(ax, ay), new P(bx, by),
                new P(bx + nx * depth - ux * pinch, by + ny * depth - uy * pinch),
                new P(ax + nx * depth + ux * pinch, ay + ny * depth + uy * pinch),
            },
        };
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? ax : bx, sy = side == 0 ? ay : by;
            float ox = side == 0 ? -ux : ux, oy = side == 0 ? -uy : uy; // 喉の外へ向く横
            float along = 0.05f + (float)rnd.NextDouble() * 0.1f;
            while (along < depth)
            {
                // 縁の線 (台形の脇) の上の点から外へ三角の欠け。大きさと間隔をばらして、ちぎれた金属の縁にする
                float k = along / depth;
                float ex = sx + nx * along - ox * pinch * k, ey = sy + ny * along - oy * pinch * k;
                float w = 0.12f + (float)rnd.NextDouble() * 0.3f;
                float h = (0.05f + (float)rnd.NextDouble() * 0.22f) * (1f - 0.5f * k);
                float tip = ((float)rnd.NextDouble() - 0.5f) * w * 0.8f;
                holes.Add(new[]
                {
                    new P(ex - nx * w * 0.5f - ox * 0.03f, ey - ny * w * 0.5f - oy * 0.03f),
                    new P(ex + nx * tip + ox * h, ey + ny * tip + oy * h),
                    new P(ex + nx * w * 0.5f - ox * 0.03f, ey + ny * w * 0.5f - oy * 0.03f),
                });
                along += w * (0.55f + (float)rnd.NextDouble() * 0.5f);
            }
        }
        for (int i = 0; i < holes.Count; i++) holes[i] = Ccw(holes[i]);

        if (Cuts.Count == 0 && !Prepare()) { _failed = true; return; }
        int before = 0, after = 0;
        foreach (var c in Cuts)
        {
            before += c.T.Count / 3;
            Apply(c, holes);
            after += c.T.Count / 3;
        }
        // 切り抜いた複製ができてから元の描画を止める
        foreach (var c in Cuts) if (c.Src.enabled) c.Src.enabled = false;
        DamageMap.MarkThroat(Throat(holes));
        Opened.AddRange(holes);
        // 影の中の焼いた絵も喉の範囲で焼き直す (喉は穴の絵の範囲より奥まで伸びる)
        foreach (var h in holes)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var q in h) { x0 = MathF.Min(x0, q.X); y0 = MathF.Min(y0, q.Y); x1 = MathF.Max(x1, q.X); y1 = MathF.Max(y1, q.Y); }
            ShadowPatch.MarkDirty(new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f), MathF.Max(x1 - x0, y1 - y0) * 0.5f + 0.2f);
        }
        int hidden = WallDecor.Hide(Vector2.zero, 1e5f, p => Contains(p)); // 壁に付いていた飾りのうち喉の中の物 (範囲は喉の形で決める)
        hidden += WaterLeak.HidePipesInThroat();
        Plugin.Logger.LogInfo($"[HullThroat] mouth {ax:0.00},{ay:0.00}-{bx:0.00},{by:0.00} depth={depth:0.00} pieces={holes.Count} tris {before}->{after} hidden={hidden}");
    }

    // 点が開けた喉の中か (壁に付いていた物を穴の中に残さない用)
    internal static bool Contains(Vector2 p)
    {
        if (GameClock.ShipGen != _shipGen) return false;
        var q = new P(p.x, p.y);
        foreach (var h in Opened) if (Inside(h, q)) return true;
        return false;
    }

    private static bool Inside(P[] poly, P q)
    {
        for (int i = 0; i < poly.Length; i++)
            if (Side(poly[i], poly[(i + 1) % poly.Length], q) < 0f) return false;
        return true;
    }

    // 切り抜いた船体の絵の奥行き (まだ切っていなければ NaN)。これより奥に置いた物は切り口から覗く
    internal static float BackZ()
    {
        if (GameClock.ShipGen != _shipGen) return float.NaN;
        foreach (var c in Cuts) if (c.Go) return c.Go.transform.position.z;
        return float.NaN;
    }

    // 損傷マスクに書く形 (世界座標)
    private static List<Vector2[]> Throat(List<P[]> holes)
    {
        var list = new List<Vector2[]>(holes.Count);
        foreach (var h in holes)
        {
            var v = new Vector2[h.Length];
            for (int i = 0; i < h.Length; i++) v[i] = new Vector2(h[i].X, h[i].Y);
            list.Add(v);
        }
        return list;
    }

    // 船体のメッシュを複製して子に載せ、元の描画を止める
    private static bool Prepare()
    {
        var ship = ShipStatus.Instance;
        var life = GameClock.Ship;
        foreach (var mf in ship.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf || !mf.gameObject.name.StartsWith("Hull", StringComparison.Ordinal)) continue;
            var src = mf.sharedMesh;
            var mr = mf.GetComponent<MeshRenderer>();
            if (!src || !src.isReadable || !mr) continue;
            var c = new Cut { Tf = mf.transform, Src = mr };
            var vs = src.vertices;
            var cs = src.colors32;
            var us = src.uv;
            var ts = src.triangles;
            bool hasColor = cs != null && cs.Length == vs.Length;
            c.HasUv = us != null && us.Length == vs.Length;
            for (int i = 0; i < vs.Length; i++)
            {
                c.V.Add(vs[i]);
                c.C.Add(hasColor ? cs[i] : new Color32(255, 255, 255, 255));
                c.U.Add(c.HasUv ? us[i] : default);
            }
            for (int i = 0; i < ts.Length; i++) c.T.Add(ts[i]);

            var go = new GameObject("MrpThroatHull") { layer = mf.gameObject.layer };
            go.transform.SetParent(mf.transform, false);
            c.Mesh = life.Bind(new Mesh { name = "MrpThroatHull" });
            go.AddComponent<MeshFilter>().sharedMesh = c.Mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mr.sharedMaterial;
            r.sortingLayerID = mr.sortingLayerID;
            r.sortingOrder = mr.sortingOrder;
            c.Go = go;
            life.Bind(go);
            Cuts.Add(c);
            Plugin.Logger.LogInfo($"[HullThroat] hull {mf.gameObject.name} verts={vs.Length} tris={ts.Length / 3} color={hasColor} uv={c.HasUv}");
        }
        return Cuts.Count > 0;
    }

    // 三角形ごとに喉の塊を引き、喉を輪郭の太さだけ広げた塊に掛かる所は輪郭の色で残す
    private static void Apply(Cut c, List<P[]> holes)
    {
        var tf = c.Tf;
        var local = new List<P[]>(holes.Count);
        var grown = new List<P[]>(holes.Count);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var h in holes)
        {
            local.Add(ToLocal(tf, h));
            var g = ToLocal(tf, Grow(h, Outline));
            grown.Add(g);
            foreach (var p in g) { minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y); }
        }

        var oldT = c.T.ToArray();
        c.T.Clear();
        var keep = new List<P[]>();
        var ring = new List<P[]>();
        var tmp = new List<P[]>();
        for (int k = 0; k + 2 < oldT.Length; k += 3)
        {
            int i0 = oldT[k], i1 = oldT[k + 1], i2 = oldT[k + 2];
            Vector3 v0 = c.V[i0], v1 = c.V[i1], v2 = c.V[i2];
            if (MathF.Max(v0.x, MathF.Max(v1.x, v2.x)) < minX || MathF.Min(v0.x, MathF.Min(v1.x, v2.x)) > maxX ||
                MathF.Max(v0.y, MathF.Max(v1.y, v2.y)) < minY || MathF.Min(v0.y, MathF.Min(v1.y, v2.y)) > maxY)
            {
                c.T.Add(i0); c.T.Add(i1); c.T.Add(i2);
                continue;
            }
            var tri = Ccw(new[] { new P(v0.x, v0.y), new P(v1.x, v1.y), new P(v2.x, v2.y) });
            if (MathF.Abs(Area(tri)) < 1e-9f) continue;

            // 広げた喉の外 = そのまま / 中 = 喉そのものを引いた残りが輪郭
            keep.Clear(); keep.Add(tri);
            ring.Clear();
            foreach (var g in grown)
            {
                tmp.Clear();
                foreach (var piece in keep)
                {
                    var inside = Intersect(piece, g);
                    if (inside != null) ring.Add(inside);
                    Subtract(piece, g, tmp);
                }
                (keep, tmp) = (tmp, keep);
            }
            if (ring.Count == 0) { c.T.Add(i0); c.T.Add(i1); c.T.Add(i2); continue; }
            foreach (var h in local)
            {
                tmp.Clear();
                foreach (var piece in ring) Subtract(piece, h, tmp);
                (ring, tmp) = (tmp, ring);
            }
            foreach (var piece in keep) Emit(c, piece, i0, i1, i2, false);
            foreach (var piece in ring) Emit(c, piece, i0, i1, i2, true);
        }
        Upload(c);
    }

    private static void Emit(Cut c, P[] poly, int i0, int i1, int i2, bool outline)
    {
        if (poly.Length < 3 || Area(poly) < 1e-7f) return;
        Vector3 v0 = c.V[i0], v1 = c.V[i1], v2 = c.V[i2];
        float det = (v1.y - v2.y) * (v0.x - v2.x) + (v2.x - v1.x) * (v0.y - v2.y);
        if (MathF.Abs(det) < 1e-12f) return;
        Color32 c0 = c.C[i0], c1 = c.C[i1], c2 = c.C[i2];
        Vector2 u0 = c.U[i0], u1 = c.U[i1], u2 = c.U[i2];
        int baseIdx = c.V.Count;
        foreach (var p in poly)
        {
            float w0 = ((v1.y - v2.y) * (p.X - v2.x) + (v2.x - v1.x) * (p.Y - v2.y)) / det;
            float w1 = ((v2.y - v0.y) * (p.X - v2.x) + (v0.x - v2.x) * (p.Y - v2.y)) / det;
            float w2 = 1f - w0 - w1;
            c.V.Add(new Vector3(p.X, p.Y, v0.z * w0 + v1.z * w1 + v2.z * w2));
            c.C.Add(outline
                ? new Color32(OutlineColor.r, OutlineColor.g, OutlineColor.b, c0.a)
                : new Color32(Mix(c0.r, c1.r, c2.r, w0, w1, w2), Mix(c0.g, c1.g, c2.g, w0, w1, w2),
                              Mix(c0.b, c1.b, c2.b, w0, w1, w2), Mix(c0.a, c1.a, c2.a, w0, w1, w2)));
            c.U.Add(new Vector2(u0.x * w0 + u1.x * w1 + u2.x * w2, u0.y * w0 + u1.y * w1 + u2.y * w2));
        }
        for (int i = 1; i + 1 < poly.Length; i++)
        {
            c.T.Add(baseIdx); c.T.Add(baseIdx + i); c.T.Add(baseIdx + i + 1);
        }
    }

    private static byte Mix(byte a, byte b, byte c, float wa, float wb, float wc)
        => (byte)Math.Clamp((int)MathF.Round(a * wa + b * wb + c * wc), 0, 255);

    private static void Upload(Cut c)
    {
        // 使われなくなった頂点は詰めない (穴は数えるほどしか開かない)
        if (c.V.Count > 65000) c.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        c.Mesh.Clear();
        c.Mesh.vertices = c.V.ToArray();
        c.Mesh.colors32 = c.C.ToArray();
        if (c.HasUv) c.Mesh.uv = c.U.ToArray();
        c.Mesh.triangles = c.T.ToArray();
        c.Mesh.RecalculateBounds();
    }

    private static P[] ToLocal(Transform tf, P[] poly)
    {
        float z = tf.position.z;
        var o = new P[poly.Length];
        for (int i = 0; i < poly.Length; i++)
        {
            var l = tf.InverseTransformPoint(new Vector3(poly[i].X, poly[i].Y, z));
            o[i] = new P(l.x, l.y);
        }
        return Ccw(o);
    }

    // ── 凸多角形の道具 (どれも反時計回りの凸多角形) ──────────────────────

    private static float Area(P[] p)
    {
        float s = 0f;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++) s += p[j].X * p[i].Y - p[i].X * p[j].Y;
        return s * 0.5f;
    }

    private static P[] Ccw(P[] p)
    {
        if (Area(p) >= 0f) return p;
        var o = new P[p.Length];
        for (int i = 0; i < p.Length; i++) o[i] = p[p.Length - 1 - i];
        return o;
    }

    // 凸多角形を各辺の外へ d だけ広げる (角は 2 本の辺をずらした線の交点)
    private static P[] Grow(P[] p, float d)
    {
        p = Ccw(p);
        int n = p.Length;
        var o = new P[n];
        for (int i = 0; i < n; i++)
        {
            P a0 = p[(i + n - 1) % n], a1 = p[i], b1 = p[(i + 1) % n];
            Outward(a0, a1, out float n0x, out float n0y);
            Outward(a1, b1, out float n1x, out float n1y);
            float bx = n0x + n1x, by = n0y + n1y, bl = bx * bx + by * by;
            float k = bl > 1e-8f ? 2f * d / bl : d;
            o[i] = new P(a1.X + bx * k, a1.Y + by * k);
        }
        return o;
    }

    private static void Outward(P a, P b, out float nx, out float ny)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, l = MathF.Sqrt(dx * dx + dy * dy);
        if (l < 1e-9f) { nx = ny = 0f; return; }
        nx = dy / l; ny = -dx / l; // 反時計回りの多角形の外向き
    }

    // 辺 a→b の左側 (反時計回りの内側) なら正
    private static float Side(P a, P b, P q) => (b.X - a.X) * (q.Y - a.Y) - (b.Y - a.Y) * (q.X - a.X);

    // s を半平面 (a→b の左側 = keepLeft・右側 = !keepLeft) で切る
    private static P[] HalfPlane(P[] s, P a, P b, bool keepLeft)
    {
        var o = new List<P>(s.Length + 2);
        for (int i = 0; i < s.Length; i++)
        {
            P cur = s[i], nxt = s[(i + 1) % s.Length];
            float dc = Side(a, b, cur), dn = Side(a, b, nxt);
            if (!keepLeft) { dc = -dc; dn = -dn; }
            if (dc >= 0f) o.Add(cur);
            if ((dc > 0f && dn < 0f) || (dc < 0f && dn > 0f))
            {
                float t = dc / (dc - dn);
                o.Add(new P(cur.X + (nxt.X - cur.X) * t, cur.Y + (nxt.Y - cur.Y) * t));
            }
        }
        return o.Count >= 3 ? o.ToArray() : null;
    }

    private static P[] Intersect(P[] s, P[] h)
    {
        P[] cur = s;
        for (int i = 0; i < h.Length && cur != null; i++) cur = HalfPlane(cur, h[i], h[(i + 1) % h.Length], true);
        return cur != null && Area(cur) > 1e-9f ? cur : null;
    }

    // s − h を凸の塊に分けて into へ足す
    private static void Subtract(P[] s, P[] h, List<P[]> into)
    {
        P[] rest = s;
        for (int i = 0; i < h.Length && rest != null; i++)
        {
            P a = h[i], b = h[(i + 1) % h.Length];
            var outside = HalfPlane(rest, a, b, false);
            if (outside != null && Area(outside) > 1e-9f) into.Add(outside);
            rest = HalfPlane(rest, a, b, true);
        }
    }
}
