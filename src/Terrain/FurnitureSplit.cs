using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 1 枚の絵に何個かの家具がまとめて描かれた物 (Mira の倉庫の奥 = 左の箱の山・棚・右の緑の箱) を、家具ごとの絵と当たり判定に分ける。
// 絵: 同じテクスチャから家具ごとの多角形だけを切り出す (Sprite.OverrideGeometry)。重なって描かれた所は手前の家具の多角形に入れる。
// 当たり判定: 元の多角形を家具ごとの横の範囲で切った物。元の絵と当たり判定は止める。
// 分けた家具は兄弟の GameObject (名前 = 元の名前-部品名) で、動かし方は FurnitureKinds に載せる。
// 全員が試合の始め (PropSim の準備) に同じ順で作るので、動く物の並びは端末で同じ
internal static class FurnitureSplit
{
    private readonly struct Piece
    {
        public readonly string Name;
        public readonly float X0, X1;   // 当たり判定を切る横の範囲 (絵の画素)
        public readonly int[] Poly;     // 絵の画素 (左上が原点) の x, y の並び

        public Piece(string name, float x0, float x1, params int[] poly)
        {
            Name = name; X0 = x0; X1 = x1; Poly = poly;
        }
    }

    // キー = 船の名前/親の名前/家具の名前
    private static readonly Dictionary<string, Piece[]> Table = new()
    {
        // storage-backwall (305x177): 左の箱の山が棚の左下に、右の緑の箱が棚の右に重なって描かれている
        ["MiraShip/Storage/storage-top"] = new[]
        {
            new Piece("boxes", float.NegativeInfinity, 113f,
                0, 40, 93, 40, 93, 64, 95, 64, 102, 67, 105, 78, 106, 92, 111, 97, 113, 100, 113, 177, 0, 177),
            new Piece("shelf", 113f, 228f,
                93, 0, 246, 0, 246, 55, 240, 62, 237, 87, 228, 100, 228, 133, 113, 133, 113, 100, 111, 97, 106, 92, 105, 78, 102, 67, 95, 64, 93, 64),
            new Piece("crates", 228f, float.PositiveInfinity,
                246, 0, 305, 0, 305, 177, 228, 177, 228, 100, 237, 87, 240, 62, 246, 55),
        },
    };

    private static readonly List<UnityEngine.Object> Owned = new();

    // 船ごとに 1 回 (PropSim が家具を集める前)。前の船で作った Sprite はここで消す
    internal static void Apply(ShipStatus ship)
    {
        Clear();
        string shipName = FurnitureKinds.ShipName(ship);
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            var tr = sr.transform;
            if (!sr.enabled || !tr.parent) continue;
            if (!Table.TryGetValue(shipName + "/" + tr.parent.name + "/" + tr.name, out var pieces)) continue;
            try { Split(sr, pieces); }
            catch (Exception e) { Plugin.Logger.LogWarning($"furniture split {tr.name}: {e.Message}"); }
        }
    }

    private static void Split(SpriteRenderer sr, Piece[] pieces)
    {
        var sp = sr.sprite;
        var tex = sp ? sp.texture : null;
        var col = sr.GetComponent<PolygonCollider2D>();
        if (!tex || !col || col.pathCount != 1 || sr.flipX || sr.flipY ||
            (sp.packed && (sp.packingMode == SpritePackingMode.Tight || sp.packingRotation != SpritePackingRotation.None))) return;
        Rect texRect = sp.textureRect;
        Vector2 pivot = sp.pivot;
        float ppu = sp.pixelsPerUnit, h = sp.rect.height;
        var tr = sr.transform;

        // 元の当たり判定 (ワールド)
        var local = col.points;
        var path = new List<Vector2>(local.Length);
        Vector2 off = col.offset;
        foreach (var p in local) path.Add(tr.TransformPoint(p + off));

        foreach (var piece in pieces)
        {
            int n = piece.Poly.Length / 2;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                int x = piece.Poly[i * 2], y = piece.Poly[i * 2 + 1];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            // 切り出す範囲 (テクスチャの画素・左下が原点)
            var rect = new Rect(texRect.x + minX, texRect.y + (h - maxY), maxX - minX, maxY - minY);
            var psp = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
            psp.name = sp.name + "-" + piece.Name;
            Owned.Add(psp);
            var verts = new Vector2[n];
            for (int i = 0; i < n; i++) verts[i] = new Vector2(piece.Poly[i * 2] - minX, maxY - piece.Poly[i * 2 + 1]);
            psp.OverrideGeometry(verts, Triangulate(verts));

            var go = new GameObject(tr.name + "-" + piece.Name) { layer = sr.gameObject.layer };
            var ptr = go.transform;
            ptr.SetParent(tr.parent, false);
            ptr.localRotation = tr.localRotation;
            ptr.localScale = tr.localScale;
            // 切り出した範囲の真ん中 (元の絵の pivot からの画素) の位置
            float cx = (minX + maxX) * 0.5f - pivot.x, cy = h - (minY + maxY) * 0.5f - pivot.y;
            ptr.position = tr.TransformPoint(new Vector3(cx / ppu, cy / ppu, 0f));
            // 当たり判定: 元の多角形を横の範囲で切る。絵より先に付ける (絵があると付けた時に絵の輪郭を読もうとし、読めないテクスチャでエラーを出す)
            float wx0 = float.IsNegativeInfinity(piece.X0) ? float.NegativeInfinity : tr.TransformPoint(new Vector3((piece.X0 - pivot.x) / ppu, 0f, 0f)).x;
            float wx1 = float.IsPositiveInfinity(piece.X1) ? float.PositiveInfinity : tr.TransformPoint(new Vector3((piece.X1 - pivot.x) / ppu, 0f, 0f)).x;
            var clipped = ClipX(ClipX(path, wx0, true), wx1, false);
            if (clipped.Count >= 3)
            {
                var pc = go.AddComponent<PolygonCollider2D>();
                var pts = new Vector2[clipped.Count];
                for (int i = 0; i < pts.Length; i++) pts[i] = ptr.InverseTransformPoint(clipped[i]);
                pc.points = pts;
            }
            var psr = go.AddComponent<SpriteRenderer>();
            psr.sprite = psp;
            psr.sharedMaterial = sr.sharedMaterial;
            psr.color = sr.color;
            psr.sortingLayerID = sr.sortingLayerID;
            psr.sortingOrder = sr.sortingOrder;
        }
        sr.enabled = false;
        col.enabled = false;
    }

    // 縦の線 x = edge で多角形を切る (keepRight = 線の右を残す)
    private static List<Vector2> ClipX(List<Vector2> poly, float edge, bool keepRight)
    {
        if (float.IsInfinity(edge)) return poly;
        var o = new List<Vector2>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            bool ia = keepRight ? a.x >= edge : a.x <= edge, ib = keepRight ? b.x >= edge : b.x <= edge;
            if (ia) o.Add(a);
            if (ia != ib)
            {
                float t = (edge - a.x) / (b.x - a.x);
                o.Add(new Vector2(edge, a.y + (b.y - a.y) * t));
            }
        }
        return o;
    }

    // 単純な多角形を三角形に分ける (耳を切る)
    private static ushort[] Triangulate(Vector2[] v)
    {
        int n = v.Length;
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(i);
        float area = 0f;
        for (int i = 0; i < n; i++) { Vector2 a = v[i], b = v[(i + 1) % n]; area += a.x * b.y - b.x * a.y; }
        float sign = area >= 0f ? 1f : -1f;
        var tris = new List<ushort>((n - 2) * 3);
        int guard = n * n;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool cut = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                Vector2 a = v[ia], b = v[ib], c = v[ic];
                if (Cross(a, b, c) * sign <= 0f) continue; // 凹んだ角
                bool inside = false;
                foreach (int k in idx)
                {
                    if (k == ia || k == ib || k == ic) continue;
                    if (InTri(v[k], a, b, c, sign)) { inside = true; break; }
                }
                if (inside) continue;
                tris.Add((ushort)ia); tris.Add((ushort)ib); tris.Add((ushort)ic);
                idx.RemoveAt(i);
                cut = true;
                break;
            }
            if (!cut) break;
        }
        if (idx.Count == 3) { tris.Add((ushort)idx[0]); tris.Add((ushort)idx[1]); tris.Add((ushort)idx[2]); }
        return tris.ToArray();
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float sign) =>
        Cross(a, b, p) * sign >= 0f && Cross(b, c, p) * sign >= 0f && Cross(c, a, p) * sign >= 0f;

    // 作った Sprite を消す (GameObject は船と一緒に消える)
    private static void Clear()
    {
        foreach (var o in Owned) if (o) UnityEngine.Object.Destroy(o);
        Owned.Clear();
    }
}
