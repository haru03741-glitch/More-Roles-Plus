using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 1 枚の絵に何個かの家具がまとめて描かれた物 (Mira の倉庫の奥・Skeld の倉庫の箱の山・Fungle のたき火の椅子) を、家具ごとの絵と当たり判定に分ける。
// 絵: 同じテクスチャから家具ごとの多角形だけを切り出す (Sprite.OverrideGeometry)。重なって描かれた所は手前の家具の多角形に入れる。
// 同じテクスチャに別の絵が重ねて定義されている物 (たき火) は、元の絵の網目を家具ごとの凸の範囲で切る (別の絵の画素を拾わない)。
// 手前の家具に隠れていた奥の家具の所は、同じ奥の家具の似た所を切り出した裏地を奥の家具の後ろに置いて埋める (手前の家具が動くと見える)。
// 当たり判定: 家具ごとに絵の画素で決めた多角形か、無ければ元の多角形 (絵と同じ GameObject の PolygonCollider2D) を家具ごとの横の範囲で切った物。
// 視界の影の線 (層 10 の EdgeCollider2D) は線の区間ごとに真ん中を含む家具へ分ける。子の端末などは位置を含む家具へ付け替える。元の絵と当たり判定は止める。
// 一部だけを分ける物 (たき火) は元の GameObject を残し、絵を残りの範囲に差し替える (火・当たり判定・子はそのまま。分けた家具の分の当たり判定だけ止める)。
// 分けた家具は兄弟の GameObject (名前 = 元の名前-部品名) で、動かし方は FurnitureKinds に載せる。
// 全員が試合の始め (PropSim の準備) に同じ順で作るので、動く物の並びは端末で同じ
internal static class FurnitureSplit
{
    private const int ShadowLayer = 10; // 視界の影の線
    private const float Front = -0.002f; // 床に置かれた手前の家具の奥行きのずらし

    private readonly struct Piece
    {
        public readonly string Name;
        public readonly float X0, X1;   // 元の当たり判定を切る横の範囲 (絵の画素・Col が無い時)
        public readonly int[] Poly;     // 絵の画素 (左上が原点) の x, y の並び。網目を切る分け方では凸
        public readonly int[] Col;      // 当たり判定の多角形 (絵の画素)。null = 元の当たり判定を X0〜X1 で切る
        public readonly int[] Patches;  // 裏地: 切り出す左上 x, y・幅・高さ・置く左上 x, y (絵の画素) の 6 個ずつ
        public readonly float Dz;       // 奥行きのずらし (負 = 手前。動いて重なった時に前後が入れ替わらない)

        public Piece(string name, float x0, float x1, params int[] poly)
        {
            Name = name; X0 = x0; X1 = x1; Poly = poly; Col = null; Patches = null; Dz = 0f;
        }

        public Piece(string name, int[] poly, int[] col, float dz = 0f, int[] patches = null)
        {
            Name = name; X0 = float.NegativeInfinity; X1 = float.PositiveInfinity; Poly = poly; Col = col; Patches = patches; Dz = dz;
        }
    }

    private sealed class Entry
    {
        public Piece[] Pieces;
        public bool Mesh;      // 元の絵の網目を凸の範囲で切る
        public int[] Keep;     // 元の GameObject に残す範囲 (凸・絵の画素)。null = 全部を分けて元は止める
        public string[] Drop;  // Keep の時に止める子 (分けた家具へ移した当たり判定)
    }

    // キー = 船の名前/親の名前/家具の名前
    private static readonly Dictionary<string, Entry> Table = new()
    {
        // storage-backwall (305x177): 左の箱の山が棚の左下に、右の緑の箱が棚の右に重なって描かれている
        ["MiraShip/Storage/storage-top"] = new Entry
        {
            Pieces = new[]
            {
                new Piece("boxes", float.NegativeInfinity, 113f,
                    0, 40, 93, 40, 93, 64, 95, 64, 102, 67, 105, 78, 106, 92, 111, 97, 113, 100, 113, 177, 0, 177),
                new Piece("shelf", 113f, 228f,
                    93, 0, 246, 0, 246, 55, 240, 62, 237, 87, 228, 100, 228, 133, 113, 133, 113, 100, 111, 97, 106, 92, 105, 78, 102, 67, 95, 64, 93, 64),
                new Piece("crates", 228f, float.PositiveInfinity,
                    246, 0, 305, 0, 305, 177, 228, 177, 228, 100, 237, 87, 240, 62, 246, 55),
            },
        },
        // storage_Boxes (326x335): 床に置かれた左の箱・手前の箱・右の燃料缶と、台 3 枚 (青・赤・緑) とその上の箱 2 つ (奥の塊)。
        // 当たり判定は元の外周 1 本 (と缶の下の膨らみ) を家具ごとに分けた物。手前の箱は箱の輪郭まで (上の飾りが手前の箱に載る・奥の台と重なる)。
        // 手前の箱が隠していた緑の台の前面と、缶が隠していた赤い台の角は裏地で埋める
        ["SkeldShip/Ground/storage_Boxes"] = new Entry
        {
            Pieces = new[]
            {
                new Piece("left",
                    new[] { 32, 99, 72, 121, 72, 173, 47, 199, -1, 185, -1, 128 },
                    new[] { 70, 122, 32, 104, -1, 133, 0, 178, 47, 195, 71, 171 }, Front),
                new Piece("front",
                    new[] { 149, 253, 189, 218, 246, 242, 247, 335, 149, 335 },
                    new[] { 150, 253, 151, 312, 209, 334, 245, 298, 245, 243, 189, 219 }, Front),
                new Piece("can",
                    new[] { 289, 240, 289, 190, 292, 181, 298, 176, 306, 174, 314, 175, 320, 178, 324, 184, 326, 190, 326, 240 },
                    new[] { 289, 228, 298, 235, 311, 236, 326, 229, 326, 189, 317, 177, 300, 180, 289, 195 }, Front),
                new Piece("stack",
                    new[] { 0, 0, 326, 0, 326, 190, 324, 184, 320, 178, 314, 175, 306, 174, 298, 176, 292, 181, 289, 190, 289, 240, 326, 240,
                            326, 335, 247, 335, 246, 242, 189, 218, 149, 253, 149, 335, 0, 335, -1, 185, 47, 199, 72, 173, 72, 121, 32, 99, -1, 128 },
                    new[] { 71, 232, 128, 232, 128, 278, 150, 278, 246, 278, 290, 279, 289, 228, 289, 195, 300, 180, 317, 177,
                            317, 26, 180, 24, 181, 44, 71, 45, 71, 122, 71, 171 }, 0f,
                    new[] { 250, 216, 26, 63, 147, 216,   250, 216, 26, 63, 173, 216,   250, 216, 26, 63, 199, 216,   250, 216, 26, 63, 225, 216,
                            289, 152, 32, 18, 289, 168 }),
            },
        },
        // bonfireArea (516x417): 右上の椅子 3 脚だけを分ける (石の輪・丸太・火は元のまま)。
        // 同じテクスチャにたき火の端末の絵が重ねて定義されているので網目を切る。椅子は斜めに並ぶので、椅子の下を通る斜めの線より上を 3 つに区切る
        ["FungleShip/OutsideBeach/Bonfire"] = new Entry
        {
            Mesh = true,
            Keep = new[] { 0, 0, 238, 0, 516, 197, 516, 417, 0, 417 },
            Drop = new[] { "Chairs" },
            Pieces = new[]
            {
                new Piece("chair1",
                    new[] { 340, 0, 438, 0, 399, 114, 340, 72 },
                    new[] { 357, 30, 410, 20, 410, 70, 395, 80, 358, 68 }),
                new Piece("chair2",
                    new[] { 438, 0, 511, 0, 433, 138, 399, 114 },
                    new[] { 425, 45, 462, 45, 462, 95, 440, 104, 409, 92 }),
                new Piece("chair3",
                    new[] { 511, 0, 516, 0, 516, 197, 433, 138 },
                    new[] { 450, 100, 505, 90, 506, 130, 475, 139, 447, 125 }),
            },
        },
    };

    private static readonly List<UnityEngine.Object> Owned = new();
    private static readonly List<GameObject> Made = new(); // 今分けている物で作った GameObject

    // 船ごとに 1 回 (PropSim が家具を集める前)。前の船で作った Sprite はここで消す
    internal static void Apply(ShipStatus ship)
    {
        Clear();
        string shipName = FurnitureKinds.ShipName(ship);
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            var tr = sr.transform;
            if (!sr.enabled || !tr.parent) continue;
            if (!Table.TryGetValue(shipName + "/" + tr.parent.name + "/" + tr.name, out var entry)) continue;
            Made.Clear();
            try { Split(sr, entry); }
            catch (Exception e)
            {
                // 途中で落ちたら作りかけの家具を消し、元の絵と当たり判定はそのまま使う
                foreach (var go in Made) if (go) UnityEngine.Object.Destroy(go);
                Plugin.Logger.LogWarning($"furniture split {tr.name}: {e.Message}");
            }
        }
    }

    private static void Split(SpriteRenderer sr, Entry entry)
    {
        var pieces = entry.Pieces;
        var sp = sr.sprite;
        var tex = sp ? sp.texture : null;
        var tr = sr.transform;
        var srcCol = sr.GetComponent<PolygonCollider2D>();
        bool needClip = false;
        foreach (var piece in pieces) if (piece.Col == null) needClip = true;
        if (!tex || (needClip && (!srcCol || srcCol.pathCount != 1)) || sr.flipX || sr.flipY ||
            (sp.packed && (sp.packingMode == SpritePackingMode.Tight || sp.packingRotation != SpritePackingRotation.None))) return;
        Vector2 pivot = sp.pivot;
        float ppu = sp.pixelsPerUnit, w = sp.rect.width, h = sp.rect.height;
        // 絵の範囲の左下のテクスチャ上の位置 (余白を詰めた絵は textureRect が余白の分ずれている)
        Vector2 origin = sp.textureRect.position - sp.textureRectOffset;

        // 元の網目 (絵の画素・左下が原点)
        Vector2[] meshV = null;
        ushort[] meshT = null;
        if (entry.Mesh)
        {
            var vs = sp.vertices;
            meshV = new Vector2[vs.Length];
            for (int i = 0; i < vs.Length; i++) meshV[i] = vs[i] * ppu + pivot;
            meshT = sp.triangles;
        }

        // 元の当たり判定 (ワールド・横の範囲で切る家具だけが使う)
        List<Vector2> path = null;
        if (needClip)
        {
            var local = srcCol.points;
            path = new List<Vector2>(local.Length);
            Vector2 off = srcCol.offset;
            foreach (var p in local) path.Add(tr.TransformPoint(p + off));
        }

        var made = new Transform[pieces.Length];
        var areas = new List<Vector2>[pieces.Length]; // 家具ごとの絵の多角形 (ワールド・子と影の線の振り分け用)
        for (int k = 0; k < pieces.Length; k++)
        {
            var piece = pieces[k];
            var psp = Shape(tex, origin, w, h, ppu, piece.Poly, meshV, meshT, true, pivot, out Vector2 center, sp.name + "-" + piece.Name);
            if (!psp) continue;

            var go = new GameObject(tr.name + "-" + piece.Name) { layer = sr.gameObject.layer };
            Made.Add(go);
            var ptr = go.transform;
            ptr.SetParent(tr.parent, false);
            ptr.localRotation = tr.localRotation;
            ptr.localScale = tr.localScale;
            // 切り出した範囲の真ん中 (元の絵の pivot からの画素) の位置
            ptr.position = tr.TransformPoint(new Vector3((center.x - pivot.x) / ppu, (center.y - pivot.y) / ppu, 0f)) + new Vector3(0f, 0f, piece.Dz);
            // 当たり判定は絵より先に付ける (絵があると付けた時に絵の輪郭を読もうとし、読めないテクスチャでエラーを出す)
            List<Vector2> colPath;
            if (piece.Col != null)
            {
                colPath = new List<Vector2>(piece.Col.Length / 2);
                for (int i = 0; i + 1 < piece.Col.Length; i += 2) colPath.Add(PixelToWorld(tr, pivot, ppu, h, piece.Col[i], piece.Col[i + 1]));
            }
            else
            {
                float wx0 = float.IsNegativeInfinity(piece.X0) ? float.NegativeInfinity : PixelToWorld(tr, pivot, ppu, h, piece.X0, 0f).x;
                float wx1 = float.IsPositiveInfinity(piece.X1) ? float.PositiveInfinity : PixelToWorld(tr, pivot, ppu, h, piece.X1, 0f).x;
                colPath = ClipX(ClipX(path, wx0, true), wx1, false);
            }
            if (colPath.Count >= 3)
            {
                var pc = go.AddComponent<PolygonCollider2D>();
                var pts = new Vector2[colPath.Count];
                for (int i = 0; i < pts.Length; i++) pts[i] = ptr.InverseTransformPoint(colPath[i]);
                pc.points = pts;
            }
            var psr = go.AddComponent<SpriteRenderer>();
            psr.sprite = psp;
            CopyLook(sr, psr);

            // 裏地: この家具の少し奥に置く。家具の絵が透けている所 (手前の家具が切り取られた跡) だけに見える
            if (piece.Patches != null)
                for (int i = 0; i + 5 < piece.Patches.Length; i += 6)
                {
                    int sx = piece.Patches[i], sy = piece.Patches[i + 1], pw = piece.Patches[i + 2], ph = piece.Patches[i + 3];
                    var bsp = Sprite.Create(tex, new Rect(origin.x + sx, origin.y + (h - sy - ph), pw, ph), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
                    bsp.name = psp.name + "-patch";
                    Owned.Add(bsp);
                    var bgo = new GameObject(go.name + "-patch") { layer = go.layer };
                    var btr = bgo.transform;
                    btr.SetParent(ptr, false);
                    btr.position = PixelToWorld(tr, pivot, ppu, h, piece.Patches[i + 4] + pw * 0.5f, piece.Patches[i + 5] + ph * 0.5f) + new Vector3(0f, 0f, piece.Dz + 0.001f);
                    var bsr = bgo.AddComponent<SpriteRenderer>();
                    bsr.sprite = bsp;
                    CopyLook(sr, bsr);
                }

            var area = new List<Vector2>(piece.Poly.Length / 2);
            for (int i = 0; i + 1 < piece.Poly.Length; i += 2) area.Add(PixelToWorld(tr, pivot, ppu, h, piece.Poly[i], piece.Poly[i + 1]));
            made[k] = ptr;
            areas[k] = area;
        }

        Made.Clear(); // ここから先は端末などを付け替えるので、落ちても作った家具は消さない

        if (entry.Keep != null)
        {
            // 元の GameObject を残す: 絵を残りの範囲に差し替え、分けた家具へ移した当たり判定の子だけ止める
            var rest = Shape(tex, origin, w, h, ppu, entry.Keep, meshV, meshT, false, pivot, out _, sp.name + "-rest");
            if (rest) sr.sprite = rest;
            for (int i = 0; i < tr.childCount; i++)
            {
                var child = tr.GetChild(i);
                if (entry.Drop == null || Array.IndexOf(entry.Drop, child.name) < 0) continue;
                foreach (var c in child.GetComponents<Collider2D>()) c.enabled = false;
            }
            Plugin.Logger.LogInfo($"furniture split {tr.name}: {pieces.Length} pieces (rest kept)");
            return;
        }

        // 子: 当たり判定だけの子 (元の当たり判定と視界の影の線) は止める (影の線は家具ごとに分けて付け直す)・
        // それ以外 (端末・転がる箱など) は位置を含む家具へ付け替える
        var children = new List<Transform>();
        for (int i = 0; i < tr.childCount; i++) children.Add(tr.GetChild(i));
        foreach (var child in children)
        {
            if (OnlyColliders(child))
            {
                foreach (var c in child.GetComponents<Collider2D>())
                {
                    if (!c.enabled || c.isTrigger) continue;
                    var edge = c.TryCast<EdgeCollider2D>();
                    if (edge != null && child.gameObject.layer == ShadowLayer) SplitShadow(edge, made, areas);
                    c.enabled = false;
                }
                continue;
            }
            int owner = Owner(child.position, areas);
            if (made[owner]) child.SetParent(made[owner], true);
        }
        sr.enabled = false;
        foreach (var c in sr.GetComponents<Collider2D>()) if (!c.isTrigger) c.enabled = false;
        Plugin.Logger.LogInfo($"furniture split {tr.name}: {pieces.Length} pieces");
    }

    // 範囲 (絵の画素・左上が原点) の Sprite。網目を切る時は元の網目と凸の範囲の重なり、そうでなければ範囲そのもの。
    // tight = 範囲を囲む最小の四角で切り出す (真ん中が pivot)・false = 元の絵と同じ四角と pivot (元の GameObject の絵を差し替える)
    private static Sprite Shape(Texture2D tex, Vector2 origin, float w, float h, float ppu, int[] poly, Vector2[] meshV, ushort[] meshT,
                                bool tight, Vector2 pivot, out Vector2 center, string name)
    {
        // 範囲 (左下が原点・絵の縁へ寄せる)
        int n = poly.Length / 2;
        var region = new Vector2[n];
        for (int i = 0; i < n; i++)
            region[i] = new Vector2(Math.Clamp(poly[i * 2], 0f, w), h - Math.Clamp(poly[i * 2 + 1], 0f, h));
        var verts = new List<Vector2>();
        var tris = new List<ushort>();
        if (meshV != null)
        {
            var tri = new List<Vector2>(3);
            for (int t = 0; t + 2 < meshT.Length; t += 3)
            {
                tri.Clear();
                tri.Add(meshV[meshT[t]]); tri.Add(meshV[meshT[t + 1]]); tri.Add(meshV[meshT[t + 2]]);
                var cut = ClipConvex(tri, region);
                if (cut.Count < 3) continue;
                int b = verts.Count;
                verts.AddRange(cut);
                for (int i = 1; i + 1 < cut.Count; i++) { tris.Add((ushort)b); tris.Add((ushort)(b + i)); tris.Add((ushort)(b + i + 1)); }
            }
        }
        else
        {
            verts.AddRange(region);
            tris.AddRange(Triangulate(region));
        }
        center = default;
        if (verts.Count < 3 || tris.Count < 3) return null;

        float minX = 0f, minY = 0f, maxX = w, maxY = h;
        if (tight)
        {
            minX = float.MaxValue; minY = float.MaxValue; maxX = float.MinValue; maxY = float.MinValue;
            foreach (var v in verts)
            {
                minX = Math.Min(minX, v.x); maxX = Math.Max(maxX, v.x);
                minY = Math.Min(minY, v.y); maxY = Math.Max(maxY, v.y);
            }
            minX = MathF.Floor(minX); minY = MathF.Floor(minY); maxX = MathF.Ceiling(maxX); maxY = MathF.Ceiling(maxY);
        }
        float rw = maxX - minX, rh = maxY - minY;
        center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
        var piv = tight ? new Vector2(0.5f, 0.5f) : new Vector2(pivot.x / w, pivot.y / h);
        var s = Sprite.Create(tex, new Rect(origin.x + minX, origin.y + minY, rw, rh), piv, ppu, 0, SpriteMeshType.FullRect);
        s.name = name;
        Owned.Add(s);
        var local = new Vector2[verts.Count];
        for (int i = 0; i < local.Length; i++)
            local[i] = new Vector2(Math.Clamp(verts[i].x - minX, 0f, rw), Math.Clamp(verts[i].y - minY, 0f, rh));
        s.OverrideGeometry(local, tris.ToArray());
        return s;
    }

    // 凸の多角形 clip で多角形 poly を切る (どちら回りでもよい)
    private static List<Vector2> ClipConvex(List<Vector2> poly, Vector2[] clip)
    {
        float area = 0f;
        for (int i = 0; i < clip.Length; i++) { Vector2 a = clip[i], b = clip[(i + 1) % clip.Length]; area += a.x * b.y - b.x * a.y; }
        float sign = area >= 0f ? 1f : -1f;
        var cur = poly;
        for (int e = 0; e < clip.Length && cur.Count > 0; e++)
        {
            Vector2 e0 = clip[e], e1 = clip[(e + 1) % clip.Length];
            var next = new List<Vector2>(cur.Count + 2);
            for (int i = 0; i < cur.Count; i++)
            {
                Vector2 a = cur[i], b = cur[(i + 1) % cur.Count];
                float da = Cross(e0, e1, a) * sign, db = Cross(e0, e1, b) * sign;
                if (da >= 0f) next.Add(a);
                if ((da >= 0f) != (db >= 0f)) next.Add(a + (b - a) * (da / (da - db)));
            }
            cur = next;
        }
        return cur;
    }

    // 絵の画素 (左上が原点) → ワールド
    private static Vector3 PixelToWorld(Transform tr, Vector2 pivot, float ppu, float h, float px, float py) =>
        tr.TransformPoint(new Vector3((px - pivot.x) / ppu, (h - py - pivot.y) / ppu, 0f));

    private static void CopyLook(SpriteRenderer from, SpriteRenderer to)
    {
        to.sharedMaterial = from.sharedMaterial;
        to.color = from.color;
        to.sortingLayerID = from.sortingLayerID;
        to.sortingOrder = from.sortingOrder;
    }

    // 子が当たり判定 (と Transform) だけか
    private static bool OnlyColliders(Transform t)
    {
        if (t.childCount > 0) return false;
        bool any = false;
        foreach (var c in t.GetComponents<Component>())
        {
            string tn = c.GetIl2CppType().Name;
            if (tn == "Transform") continue;
            if (!tn.EndsWith("Collider2D")) return false;
            any = true;
        }
        return any;
    }

    // 点を含む家具 (どれにも入らなければ最後の家具 = 残りの塊)
    private static int Owner(Vector2 p, List<Vector2>[] areas)
    {
        for (int k = 0; k < areas.Length; k++) if (areas[k] != null && Inside(p, areas[k])) return k;
        return areas.Length - 1;
    }

    // 影の線を区間ごとに、区間の真ん中を含む家具の子 (層 10 の EdgeCollider2D) へ分ける。続いた区間は 1 本にまとめる
    private static void SplitShadow(EdgeCollider2D edge, Transform[] made, List<Vector2>[] areas)
    {
        var etr = edge.transform;
        var local = edge.points;
        Vector2 off = edge.offset;
        var world = new Vector2[local.Length];
        for (int i = 0; i < local.Length; i++) world[i] = etr.TransformPoint(local[i] + off);
        var run = new List<Vector2>();
        int last = -1;
        for (int i = 0; i + 1 < world.Length; i++)
        {
            Vector2 a = world[i], b = world[i + 1];
            int k = Owner((a + b) * 0.5f, areas);
            if (k != last)
            {
                AddShadow(run, last, made, edge.name);
                run = new List<Vector2> { a };
                last = k;
            }
            run.Add(b);
        }
        AddShadow(run, last, made, edge.name);
    }

    private static void AddShadow(List<Vector2> run, int k, Transform[] made, string name)
    {
        if (k < 0 || run.Count < 2 || !made[k]) return;
        var go = new GameObject(name) { layer = ShadowLayer };
        var t = go.transform;
        t.SetParent(made[k], false);
        var ec = go.AddComponent<EdgeCollider2D>();
        var pts = new Vector2[run.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = t.InverseTransformPoint(run[i]);
        ec.points = pts;
    }

    private static bool Inside(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector2 a = poly[i], b = poly[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
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
