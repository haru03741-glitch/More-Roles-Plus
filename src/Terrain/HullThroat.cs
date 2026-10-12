using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 外壁が宇宙/空へ抜けた穴の「喉」: 口 (元の壁の線) から宇宙まで、船体の絵を切り抜いて後ろの星空 (エアシップは空と雲) を見せる。
// スケルドの船体は Hull* のメッシュ、エアシップの船体は部屋の絵と同じ扱いの HullBlock の板 (損傷マスクで抜けるので切るのはマスクだけ)。
// スケルドの船体のメッシュは複製して名前が Hull で始まらない子に載せ、元の描画だけ止める (歩ける所の地図は元のメッシュから作るので変えない)。
// 切り口には本編の絵柄と同じ濃い輪郭線を同じメッシュに頂点色で足す。部屋の絵の壁の帯は損傷マスク (R と A) で抜く。
// 穴が開いた時に 1 回だけ動く (毎フレームの処理は無い)。見た目だけで同期はしない (全員が同じ口から同じ形を作る)
internal static class HullThroat
{
    private const float Margin = 0.35f;     // 宇宙側へのはみ出し (切る物が無いので無害)
    private const float FloorStop = 0.2f;   // 喉の先に別の部屋の床がある時に手前で止める余白
    private const float Outline = 0.055f;   // 切り口の輪郭線の太さ
    private const float SampleStep = 0.1f;  // 口に沿って深さを測る間隔
    // エアシップ: 船体は空まで 9〜13 単位あるので空までは抜かない。口の外の船体に浅く広い穴を開けて真下の空と雲を見せる (開通も厚みを見ずに決めている)
    private const float CraterDepth = 2.4f; // 口から外向きの深さ (種で ±0.4)
    private const float CraterFlare = 0.45f; // 奥の端を口の両端より左右へ広げる幅
    internal const float CraterReach = CraterDepth + 0.4f + 0.4f; // 穴のいちばん奥 (深さのばらつき + 奥の縁の欠け)。泡はここまで詰める
    internal const float CraterSide = CraterFlare + 0.45f;         // 口の両端より外へ穴が広がる最大 (広げた分 + 脇の欠け)
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
        // 頂点 (x y z)・色 (r g b a)・uv (x y) を VN 個、三角形の頂点の番号を TN 個 (C++ へそのまま渡す並び)
        public float[] V = Array.Empty<float>();
        public byte[] C = Array.Empty<byte>();
        public float[] U = Array.Empty<float>();
        public int VN;
        public int[] T = Array.Empty<int>();
        public int TN;
        public bool HasUv;
        public bool Dirty; // 切ったがまだメッシュへ書いていない

        public void EnsureVerts(int n)
        {
            if (V.Length >= n * 3) return;
            int cap = Math.Max(n, VN * 2);
            Array.Resize(ref V, cap * 3); Array.Resize(ref C, cap * 4); Array.Resize(ref U, cap * 2);
        }

        public Cut Copy() => new()
        {
            V = (float[])V.Clone(), C = (byte[])C.Clone(), U = (float[])U.Clone(), VN = VN, T = (int[])T.Clone(), TN = TN, HasUv = HasUv,
        };
    }

    // 答え合わせ (native verify): 喉の切り抜きを C# でもやって頂点・色・uv・三角形を比べる
    internal static long VerifyClips, VerifyClipDiff;

    private static readonly List<Cut> Cuts = new();
    private static readonly List<P[]> Opened = new(); // 開けた喉の塊 (世界座標・反時計回り)
    private static int _shipGen = -1;
    private static bool _failed;
    private static float _craterZ = float.NaN; // エアシップ: 穴を開けた船体の板の奥行き

    // 口 a-b (外向き away) の奥を宇宙まで抜く。TerrainDamage が口を張った直後に呼ぶ (スケルドとエアシップ)
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

    // 開けた喉をメッシュへ書く。1 回の爆発で口がいくつできても、書くのは最後に 1 回だけ
    internal static void Flush()
    {
        if (GameClock.ShipGen != _shipGen || _failed) return;
        try
        {
            foreach (var c in Cuts)
            {
                if (!c.Dirty) continue;
                Upload(c);
                c.Dirty = false;
            }
            // 切り抜いた複製ができてから元の描画を止める
            foreach (var c in Cuts) if (c.Src.enabled) c.Src.enabled = false;
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Logger.LogError($"[HullThroat] flush: {e}");
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
            _craterZ = float.NaN;
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
            float d = SolidMap.SkyDistance(ax + (bx - ax) * t, ay + (by - ay) * t, nx, ny, TerrainDamage.BreachReach + 1f);
            if (d < 0f) d = MathF.Max(0f, -d - FloorStop);
            else if (d > 0f) d += Margin;
            depth = MathF.Max(depth, d);
        }
        if (depth <= 0.05f) return;

        // 喉 = 口から外向きに少しすぼまる台形 + 両脇のちぎれた欠け。種は口の座標から (全員で同じ形)
        var rnd = new System.Random((int)(ax * 977f) ^ (int)(ay * 613f) * 31 ^ (int)(bx * 389f) * 17 ^ (int)(by * 211f));
        float pinch = MathF.Min(len * 0.18f, 0.3f); // 宇宙側の端は左右これだけ内側
        bool crater = !SolidMap.BreachableHull; // エアシップ: 奥の端も船体の中なので、広がる形にして奥の縁もちぎる
        if (crater)
        {
            depth = MathF.Min(depth, CraterDepth + ((float)rnd.NextDouble() - 0.5f) * 0.8f);
            pinch = -MathF.Min(CraterFlare, 0.25f + len * 0.2f);
        }
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
        if (crater)
        {
            // 奥の縁 (船体の中) にも外向きの三角の欠け
            float fx0 = ax + nx * depth + ux * pinch, fy0 = ay + ny * depth + uy * pinch;
            float span = len - 2f * pinch;
            float along = 0.05f + (float)rnd.NextDouble() * 0.1f;
            while (along < span)
            {
                float ex = fx0 + ux * along, ey = fy0 + uy * along;
                float w = 0.15f + (float)rnd.NextDouble() * 0.35f;
                float h = 0.08f + (float)rnd.NextDouble() * 0.3f;
                float tip = ((float)rnd.NextDouble() - 0.5f) * w * 0.8f;
                holes.Add(new[]
                {
                    new P(ex - ux * w * 0.5f - nx * 0.03f, ey - uy * w * 0.5f - ny * 0.03f),
                    new P(ex + ux * tip + nx * h, ey + uy * tip + ny * h),
                    new P(ex + ux * w * 0.5f - nx * 0.03f, ey + uy * w * 0.5f - ny * 0.03f),
                });
                along += w * (0.55f + (float)rnd.NextDouble() * 0.5f);
            }
        }
        for (int i = 0; i < holes.Count; i++) holes[i] = Ccw(holes[i]);

        int before = 0, after = 0;
        if (SolidMap.BreachableHull)
        {
            if (Cuts.Count == 0 && !Prepare()) { _failed = true; return; }
            foreach (var c in Cuts)
            {
                before += c.TN / 3;
                Apply(c, holes);
                after += c.TN / 3;
            }
        }
        if (crater && float.IsNaN(_craterZ)) _craterZ = HullBlockZ();
        DamageMap.MarkThroat(Throat(holes));
        Opened.AddRange(holes);
        // 影の中の焼いた絵も喉の範囲で焼き直す (喉は穴の絵の範囲より奥まで伸びる)
        float bx0 = float.MaxValue, by0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue;
        foreach (var h in holes)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var q in h) { x0 = MathF.Min(x0, q.X); y0 = MathF.Min(y0, q.Y); x1 = MathF.Max(x1, q.X); y1 = MathF.Max(y1, q.Y); }
            ShadowPatch.MarkDirty(new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f), MathF.Max(x1 - x0, y1 - y0) * 0.5f + 0.2f);
            bx0 = MathF.Min(bx0, x0); by0 = MathF.Min(by0, y0); bx1 = MathF.Max(bx1, x1); by1 = MathF.Max(by1, y1);
        }
        // 壁に付いていた飾りのうち喉の中の物 (中かどうかは開けた喉全部で見る)。今回の喉に掛からない飾りは前の喉の時と変わらないので見ない
        float hw = (bx1 - bx0) * 0.5f, hh = (by1 - by0) * 0.5f;
        int hidden = WallDecor.Hide(new Vector2(bx0 + hw, by0 + hh), MathF.Sqrt(hw * hw + hh * hh) + 0.01f, p => Contains(p));
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
        return _craterZ;
    }

    // 穴を開けたのが船体の板 (エアシップ) か
    internal static bool IsCrater => GameClock.ShipGen == _shipGen && !float.IsNaN(_craterZ);

    // エアシップの船体の板 (HullBlock) のいちばん奥の z。泡はこれより奥に置き、穴の縁が泡にかぶさるようにする
    private static float HullBlockZ()
    {
        float z = float.NaN;
        foreach (var sr in DamageMap.RoomArts)
            if (sr && sr.gameObject.name.StartsWith("HullBlock", StringComparison.Ordinal))
            {
                float rz = sr.transform.position.z;
                if (float.IsNaN(z) || rz > z) z = rz;
            }
        return z;
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
            c.EnsureVerts(vs.Length);
            for (int i = 0; i < vs.Length; i++)
            {
                var v = vs[i];
                c.V[i * 3] = v.x; c.V[i * 3 + 1] = v.y; c.V[i * 3 + 2] = v.z;
                if (hasColor) { var col = cs[i]; c.C[i * 4] = col.r; c.C[i * 4 + 1] = col.g; c.C[i * 4 + 2] = col.b; c.C[i * 4 + 3] = col.a; }
                else c.C[i * 4] = c.C[i * 4 + 1] = c.C[i * 4 + 2] = c.C[i * 4 + 3] = 255;
                if (c.HasUv) { var uv = us[i]; c.U[i * 2] = uv.x; c.U[i * 2 + 1] = uv.y; }
            }
            c.VN = vs.Length;
            c.T = new int[ts.Length];
            for (int i = 0; i < ts.Length; i++) c.T[i] = ts[i];
            c.TN = ts.Length;

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

    // 三角形ごとに喉の塊を引き、喉を輪郭の太さだけ広げた塊に掛かる所は輪郭の色で残す (C++。照合の時は C# でもやって比べる)
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
        Cut cs = Native.MrpNative.Verify ? c.Copy() : null;
        ClipNative(c, local, grown, minX, minY, maxX, maxY);
        if (cs != null)
        {
            ClipCs(cs, local, grown, minX, minY, maxX, maxY);
            long diff = cs.VN != c.VN || cs.TN != c.TN ? 1000000 : 0;
            int vn = Math.Min(cs.VN, c.VN);
            for (int i = 0; i < vn * 3; i++) if (BitConverter.SingleToInt32Bits(cs.V[i]) != BitConverter.SingleToInt32Bits(c.V[i])) diff++;
            for (int i = 0; i < vn * 2; i++) if (BitConverter.SingleToInt32Bits(cs.U[i]) != BitConverter.SingleToInt32Bits(c.U[i])) diff++;
            for (int i = 0; i < vn * 4; i++) if (cs.C[i] != c.C[i]) diff++;
            for (int i = 0; i < Math.Min(cs.TN, c.TN); i++) if (cs.T[i] != c.T[i]) diff++;
            VerifyClips++;
            VerifyClipDiff += diff;
        }
        c.Dirty = true;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct ClipArgs
    {
        public float* V; public byte* C; public float* U;
        public int VN, VCap;
        public int* TriIn; public int TriInN;
        public int* TriOut; public int TriOutN, TriOutCap;
        public float* Holes; public int* HoleN;
        public float* Grown; public int* GrownN;
        public int HoleCount;
        public float MinX, MinY, MaxX, MaxY;
        public int OutR, OutG, OutB;
        public int Over;
    }

    private static unsafe void ClipNative(Cut c, List<P[]> local, List<P[]> grown, float minX, float minY, float maxX, float maxY)
    {
        int hp = 0, gp = 0;
        foreach (var h in local) hp += h.Length;
        foreach (var g in grown) gp += g.Length;
        var hs = new float[hp * 2]; var gs = new float[gp * 2];
        var hn = new int[local.Count]; var gn = new int[grown.Count];
        for (int i = 0, o = 0; i < local.Count; i++) { hn[i] = local[i].Length; foreach (var q in local[i]) { hs[o++] = q.X; hs[o++] = q.Y; } }
        for (int i = 0, o = 0; i < grown.Count; i++) { gn[i] = grown[i].Length; foreach (var q in grown[i]) { gs[o++] = q.X; gs[o++] = q.Y; } }
        var triIn = new int[c.TN];
        Array.Copy(c.T, triIn, c.TN);
        int vcap = c.VN + 16384, tcap = c.TN + 32768;
        for (int tries = 0; tries < 6; tries++)
        {
            c.EnsureVerts(vcap);
            if (c.T.Length < tcap) c.T = new int[tcap];
            int over;
            fixed (float* v = c.V) fixed (byte* col = c.C) fixed (float* u = c.U) fixed (int* ti = triIn) fixed (int* to = c.T)
            fixed (float* h = hs) fixed (int* hc = hn) fixed (float* g = gs) fixed (int* gc = gn)
            {
                var a = new ClipArgs
                {
                    V = v, C = col, U = u, VN = c.VN, VCap = c.V.Length / 3,
                    TriIn = ti, TriInN = triIn.Length, TriOut = to, TriOutCap = c.T.Length,
                    Holes = h, HoleN = hc, Grown = g, GrownN = gc, HoleCount = local.Count,
                    MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
                    OutR = OutlineColor.r, OutG = OutlineColor.g, OutB = OutlineColor.b,
                };
                Native.MrpNative.HullClip(&a);
                over = a.Over;
                if (over == 0) { c.VN = a.VN; c.TN = a.TriOutN; return; }
            }
            // 頂点・三角形の置き場が足りなければ広げて最初からやり直す (足りない時は何も書き換えていない)
            if ((over & 1) == 0) break;
            vcap = c.VN + (vcap - c.VN) * 4;
            tcap *= 4;
        }
        // 1 枚の三角形が細かく割れすぎた (起こらないはず)。この喉は切らずに残す
        Array.Copy(triIn, c.T, triIn.Length);
        c.TN = triIn.Length;
        Plugin.Logger.LogError($"[HullThroat] clip overflow tris={c.TN / 3} holes={local.Count}");
    }

    // C# 版 (照合用)
    private static void ClipCs(Cut c, List<P[]> local, List<P[]> grown, float minX, float minY, float maxX, float maxY)
    {
        var oldT = new int[c.TN];
        Array.Copy(c.T, oldT, c.TN);
        var outT = new List<int>(c.TN + 64);
        var keep = new List<P[]>();
        var ring = new List<P[]>();
        var tmp = new List<P[]>();
        var hBox = new float[local.Count * 4];
        var gBox = new float[grown.Count * 4];
        for (int i = 0; i < local.Count; i++) { Bounds(local[i], hBox, i * 4); Bounds(grown[i], gBox, i * 4); }
        for (int k = 0; k + 2 < oldT.Length; k += 3)
        {
            int i0 = oldT[k], i1 = oldT[k + 1], i2 = oldT[k + 2];
            float x0 = c.V[i0 * 3], y0 = c.V[i0 * 3 + 1], x1 = c.V[i1 * 3], y1 = c.V[i1 * 3 + 1], x2 = c.V[i2 * 3], y2 = c.V[i2 * 3 + 1];
            float tx0 = MathF.Min(x0, MathF.Min(x1, x2)), tx1 = MathF.Max(x0, MathF.Max(x1, x2));
            float ty0 = MathF.Min(y0, MathF.Min(y1, y2)), ty1 = MathF.Max(y0, MathF.Max(y1, y2));
            if (tx1 < minX || tx0 > maxX || ty1 < minY || ty0 > maxY)
            {
                outT.Add(i0); outT.Add(i1); outT.Add(i2);
                continue;
            }
            var tri = Ccw(new[] { new P(x0, y0), new P(x1, y1), new P(x2, y2) });
            if (MathF.Abs(Area(tri)) < 1e-9f) continue;

            // 広げた喉の外 = そのまま / 中 = 喉そのものを引いた残りが輪郭。三角形と範囲が重ならない塊は見ない (余計に割らない)
            keep.Clear(); keep.Add(tri);
            ring.Clear();
            for (int gi = 0; gi < grown.Count; gi++)
            {
                if (Apart(gBox, gi * 4, tx0, ty0, tx1, ty1)) continue;
                var g = grown[gi];
                tmp.Clear();
                foreach (var piece in keep)
                {
                    if (Separated(piece, g)) { tmp.Add(piece); continue; }
                    var inside = Intersect(piece, g);
                    if (inside != null) ring.Add(inside);
                    Subtract(piece, g, tmp);
                }
                (keep, tmp) = (tmp, keep);
            }
            if (ring.Count == 0) { outT.Add(i0); outT.Add(i1); outT.Add(i2); continue; }
            for (int hi = 0; hi < local.Count; hi++)
            {
                if (Apart(hBox, hi * 4, tx0, ty0, tx1, ty1)) continue;
                var h = local[hi];
                tmp.Clear();
                foreach (var piece in ring)
                {
                    if (Separated(piece, h)) tmp.Add(piece);
                    else Subtract(piece, h, tmp);
                }
                (ring, tmp) = (tmp, ring);
            }
            foreach (var piece in keep) Emit(c, outT, piece, i0, i1, i2, false);
            foreach (var piece in ring) Emit(c, outT, piece, i0, i1, i2, true);
        }
        if (c.T.Length < outT.Count) c.T = new int[outT.Count];
        outT.CopyTo(c.T);
        c.TN = outT.Count;
    }

    private static void Bounds(P[] p, float[] b, int o)
    {
        b[o] = b[o + 2] = p[0].X; b[o + 1] = b[o + 3] = p[0].Y;
        for (int i = 1; i < p.Length; i++)
        {
            b[o] = MathF.Min(b[o], p[i].X); b[o + 1] = MathF.Min(b[o + 1], p[i].Y);
            b[o + 2] = MathF.Max(b[o + 2], p[i].X); b[o + 3] = MathF.Max(b[o + 3], p[i].Y);
        }
    }

    // h か s のどれかの辺の外側 (右) に相手が全部ある = 重ならない (s は割らずにそのまま残す)。両方が反時計回りの凸の時だけ正しい
    private static bool Separated(P[] s, P[] h)
    {
        for (int i = 0; i < h.Length; i++)
        {
            P a = h[i], b = h[(i + 1) % h.Length];
            int k = 0;
            while (k < s.Length && Side(a, b, s[k]) <= 0f) k++;
            if (k == s.Length) return true;
        }
        for (int i = 0; i < s.Length; i++)
        {
            P a = s[i], b = s[(i + 1) % s.Length];
            int k = 0;
            while (k < h.Length && Side(a, b, h[k]) <= 0f) k++;
            if (k == h.Length) return true;
        }
        return false;
    }

    private static bool Apart(float[] b, int o, float x0, float y0, float x1, float y1)
        => x1 < b[o] || x0 > b[o + 2] || y1 < b[o + 1] || y0 > b[o + 3];

    private static void Emit(Cut c, List<int> outT, P[] poly, int i0, int i1, int i2, bool outline)
    {
        if (poly.Length < 3 || Area(poly) < 1e-7f) return;
        float[] V = c.V;
        float v0x = V[i0 * 3], v0y = V[i0 * 3 + 1], v0z = V[i0 * 3 + 2];
        float v1x = V[i1 * 3], v1y = V[i1 * 3 + 1], v1z = V[i1 * 3 + 2];
        float v2x = V[i2 * 3], v2y = V[i2 * 3 + 1], v2z = V[i2 * 3 + 2];
        float det = (v1y - v2y) * (v0x - v2x) + (v2x - v1x) * (v0y - v2y);
        if (MathF.Abs(det) < 1e-12f) return;
        c.EnsureVerts(c.VN + poly.Length);
        V = c.V;
        float[] U = c.U;
        byte[] C = c.C;
        int baseIdx = c.VN;
        foreach (var p in poly)
        {
            float w0 = ((v1y - v2y) * (p.X - v2x) + (v2x - v1x) * (p.Y - v2y)) / det;
            float w1 = ((v2y - v0y) * (p.X - v2x) + (v0x - v2x) * (p.Y - v2y)) / det;
            float w2 = 1f - w0 - w1;
            int n = c.VN;
            V[n * 3] = p.X; V[n * 3 + 1] = p.Y; V[n * 3 + 2] = v0z * w0 + v1z * w1 + v2z * w2;
            if (outline) { C[n * 4] = OutlineColor.r; C[n * 4 + 1] = OutlineColor.g; C[n * 4 + 2] = OutlineColor.b; C[n * 4 + 3] = C[i0 * 4 + 3]; }
            else
                for (int ch = 0; ch < 4; ch++) C[n * 4 + ch] = Mix(C[i0 * 4 + ch], C[i1 * 4 + ch], C[i2 * 4 + ch], w0, w1, w2);
            U[n * 2] = U[i0 * 2] * w0 + U[i1 * 2] * w1 + U[i2 * 2] * w2;
            U[n * 2 + 1] = U[i0 * 2 + 1] * w0 + U[i1 * 2 + 1] * w1 + U[i2 * 2 + 1] * w2;
            c.VN++;
        }
        for (int i = 1; i + 1 < poly.Length; i++)
        {
            outT.Add(baseIdx); outT.Add(baseIdx + i); outT.Add(baseIdx + i + 1);
        }
    }

    private static byte Mix(byte a, byte b, byte c, float wa, float wb, float wc)
        => (byte)Math.Clamp((int)MathF.Round(a * wa + b * wb + c * wc), 0, 255);

    private static unsafe void Upload(Cut c)
    {
        // 使われなくなった頂点は詰めない (穴は数えるほどしか開かない)。
        // 頂点・色・uv・三角形の並びは本編の配列と同じなので、本編の配列を作ってバイトのまま写す (要素ごとの変換をしない)
        int n = c.VN;
        if (n > 65000) c.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        var vs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>((long)n);
        var cs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>((long)n);
        var ts = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>((long)c.TN);
        fixed (float* v = c.V) Buffer.MemoryCopy(v, (void*)Data(vs), n * 12L, n * 12L);
        fixed (byte* col = c.C) Buffer.MemoryCopy(col, (void*)Data(cs), n * 4L, n * 4L);
        fixed (int* t = c.T) Buffer.MemoryCopy(t, (void*)Data(ts), c.TN * 4L, c.TN * 4L);
        c.Mesh.Clear();
        c.Mesh.vertices = vs;
        c.Mesh.colors32 = cs;
        if (c.HasUv)
        {
            var us = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2>((long)n);
            fixed (float* u = c.U) Buffer.MemoryCopy(u, (void*)Data(us), n * 8L, n * 8L);
            c.Mesh.uv = us;
        }
        c.Mesh.triangles = ts;
        c.Mesh.RecalculateBounds();
    }

    // il2cpp の配列の中身の先頭 (頭 = 型・監視・範囲・長さの 4 語)
    private static IntPtr Data(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase a) => a.Pointer + 4 * IntPtr.Size;

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
