using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 部屋の絵に描き込まれた家具 (自分の絵を持たず、層 12 の当たり判定だけを持つ物。Skeld の食堂のテーブルなど) を、
// 初めて押された時に部屋の絵から切り抜いて動かせるようにする。動かすのは PropSim (重い物)。
// - 跡の床 (_UseDamage = 8): 元の場所で家具の画素の上に、同じ部屋の絵のきれいな床を模様の繰り返しの幅ごとに写して描く。
// - 家具 (_UseDamage = 7): 部屋の絵の同じ範囲を切り出し、当たり判定の形の中で、その場所に本来ある床の模様と違う画素だけ描く (落とす影ごと動く)。
// 床の模様は家具ごと (無ければ部屋の絵ごと) に測った値 (FurnitureFloors)。載っていない家具は持ち上げない。
// 本編の物 (緊急ボタンなどの端末) が載った家具は持ち上げない
internal static class FurnitureLift
{
    private const int FurnitureLayer = 12;
    internal const int ShapePx = 64;         // 形のテクスチャの 1 辺
    internal const float Pad = 0.3f;         // 形を当たり判定から広げる幅の上限 (当たり判定は絵より小さい所がある。広げた所の床は模様で外す)
    private const float CutZ = 0.0006f;      // 部屋の絵より手前・水 (0.0004) より手前
    private const float FillZ = 0.0002f;     // 部屋の絵より手前・水より奥
    internal const int ClassPx = 256;        // 画素の見分けを読み戻す格子の 1 辺 (広げた四角の上)

    // 床の測り方: きれいな床の左下 (部屋の絵の画素・左下原点)・繰り返しの幅 (画素・0 = その向きは同じ列/行をそのまま引く)・
    // 形を当たり判定から広げる幅 (世界単位・左右下上。隣に柄の違う床や壁・ほかの家具の影がある側は狭い)
    internal readonly struct Floor
    {
        public readonly float X, Y, Tx, Ty, PadL, PadR, PadB, PadT;
        public Floor(float x, float y, float tx, float ty, float pad) : this(x, y, tx, ty, pad, pad, pad, pad) { }
        public Floor(float x, float y, float tx, float ty, float padL, float padR, float padB, float padT)
        {
            X = x; Y = y; Tx = tx; Ty = ty; PadL = padL; PadR = padR; PadB = padB; PadT = padT;
        }
    }

    // 家具 (部屋の絵の名前/当たり判定の名前)・無ければ部屋の絵の名前で引く。手で測った表 (Manual) を先に見る
    private static Floor? FloorFor(string room, string name)
    {
        string key = room + "/" + name;
        if (!FurnitureFloors.Manual.TryGetValue(key, out var f) && !FurnitureFloors.Table.TryGetValue(key, out f) && !FurnitureFloors.Table.TryGetValue(room, out f)) return null;
        // 広げる幅は世界の左右。左右反転の船では入れ替える (床の測り方は部屋の絵の画素なので絵と一緒に裏返る)
        return MapFlip.Flipped ? new Floor(f.X, f.Y, f.Tx, f.Ty, f.PadR, f.PadL, f.PadB, f.PadT) : f;
    }

    // 切り抜いた家具と跡の床 (影の中の焼き込みが上から描き込む)・作った絵と形 (船が替わる時に片付ける)
    private static readonly List<GameObject> Lifted = new();
    private static readonly List<UnityEngine.Object> Owned = new();
    // 動かさない家具 (部屋の絵に描かれたまま)。床マスクではその絵が壁の面なので、床の水が家具を壁として這い上がり、水面の板と二重に覆う。
    // 水の絵の壁の面の高さから、この型の中を外す
    private static readonly List<Lift> StillShapes = new();
    private static int _stillGen = -1;

    internal static void CollectLifted(List<GameObject> into)
    {
        foreach (var go in Lifted) if (go) into.Add(go);
    }

    // 水の絵のタイル (x, y から size 四方を n×n・画素 k = x + k × step) の壁の面の画素のうち、動かさない家具の型の中を面でなくする
    internal static void CutStillFaces(float x, float y, float size, int n, byte[] face)
    {
        if (_stillGen != GameClock.ShipGen || StillShapes.Count == 0) return;
        float step = size / (n - 1);
        foreach (var l in StillShapes)
        {
            var r = l.ShapeWorld;
            var inside = l.ShapeIn;
            if (inside == null || r.xMax < x || r.xMin > x + size || r.yMax < y || r.yMin > y + size) continue;
            int px0 = Math.Max(0, (int)MathF.Ceiling((r.xMin - x) / step)), px1 = Math.Min(n - 1, (int)MathF.Floor((r.xMax - x) / step));
            int py0 = Math.Max(0, (int)MathF.Ceiling((r.yMin - y) / step)), py1 = Math.Min(n - 1, (int)MathF.Floor((r.yMax - y) / step));
            float sx = ShapePx / r.width, sy = ShapePx / r.height;
            for (int py = py0; py <= py1; py++)
            {
                int ty = Math.Min(ShapePx - 1, (int)((y + py * step - r.yMin) * sy));
                for (int px = px0; px <= px1; px++)
                {
                    int i = py * n + px;
                    if (face[i] > FloorMask.FaceTop) continue;
                    int tx = Math.Min(ShapePx - 1, (int)((x + px * step - r.xMin) * sx));
                    if (inside[ty * ShapePx + tx]) face[i] = 255;
                }
            }
        }
    }

    // 船が替わった時・失敗した時: 切り抜いた絵と、家具ごとに作ったマテリアル・Sprite・形のテクスチャを消す
    internal static void Clear()
    {
        foreach (var go in Lifted) if (go) UnityEngine.Object.Destroy(go);
        foreach (var o in Owned) if (o) UnityEngine.Object.Destroy(o);
        Lifted.Clear();
        Owned.Clear();
        StillShapes.Clear();
    }

    internal sealed class Lift
    {
        public string Name, Room;
        public Floor Floor;
        public bool Measured;       // Floor を測ってある (無い家具は Floor が既定値)
        public Vector2 CutOffset;   // 切り抜きの絵の中心 - 当たり判定の範囲の中心 (広げる幅が左右で違う時)
        public Collider2D Col;
        public Transform ColTr;
        public Vector3 ColT0;
        public float ColRot0;
        public Rect Bounds;
        public Transform Cut;
        // 切り抜きの形 (白黒) と、その真ん中の切り抜きの絵から見た位置・絵の大きさ (家具の水面の板が型に使う)
        public Texture2D Shape;
        public bool[] ShapeIn;  // Shape と同じ形 (ShapePx 四方・左下原点)
        public Rect ShapeWorld;
        public Vector2 ShapeOff, ShapeScale;
        public bool[] ColIn;    // 広げる前の当たり判定の中 (Shape と同じ格子)
        public bool[] FurnPx;   // 家具の画素 (広げた四角 ShapeWorld の上の ClassPx 四方・GPU の見分けを読み戻した物・無ければ null)
        // 家具を描き込んだ部屋の絵 (床を測った家具だけ)。水面の板が切り抜きと同じ見分け方で、型の中の家具の画素だけに描くのに使う
        public BreakPieces.Room Art;
        public bool Tried, Failed;
        public bool Still;      // 端末が載っていて動かさない家具 (切り抜かず、水面の板の型と置き場だけ作る)
        // 今の置き方 (元の足元 Pivot を軸に PoseAng 度回して Pose へ)。床マスクが家具の今いる所を抜くのに使う
        public float PivotX, PivotY, PoseX, PoseY, PoseAng;
    }

    // 船の中の持ち上げられそうな家具 (並びは当たり判定の階層順 = 全員同じ)
    // all = 床を測っていない部屋の家具も (床を測る道具へ書き出す用)
    internal static void Collect(ShipStatus ship, List<Lift> into, bool all = false) => Collect(ship, into, all, false);

    // 端末が載っていて動かさない家具 (水面の板だけ付ける)
    internal static void CollectStill(ShipStatus ship, List<Lift> into) => Collect(ship, into, true, true);

    private const float BoxSlack = 0.05f; // 外接箱で端末を省く時のゆとり (当たり判定の形は箱の中に収まる)

    private static void Collect(ShipStatus ship, List<Lift> into, bool all, bool still)
    {
        var consoles = new List<Vector2>();
        foreach (var c in ship.AllConsoles) if (c) consoles.Add(c.transform.position);
        foreach (var c in ship.GetComponentsInChildren<SystemConsole>(true)) if (c) consoles.Add(c.transform.position);
        foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
        {
            if (!col || !col.enabled || col.isTrigger || col.gameObject.layer != FurnitureLayer) continue;
            if (col.GetComponent<SpriteRenderer>()) continue; // 自分の絵を持つ物は部屋の絵に描き込まれていない
            // 外接箱は直に読む (Bounds の min/max は 1 回ごとに interop)。箱の外の端末は物理に問い合わせない
            var b = col.bounds;
            float x0 = b.m_Center.x - b.m_Extents.x, y0 = b.m_Center.y - b.m_Extents.y, x1 = b.m_Center.x + b.m_Extents.x, y1 = b.m_Center.y + b.m_Extents.y;
            bool hasConsole = false;
            foreach (var p in consoles)
            {
                if (p.x < x0 - BoxSlack || p.x > x1 + BoxSlack || p.y < y0 - BoxSlack || p.y > y1 + BoxSlack) continue;
                if (col.OverlapPoint(p)) { hasConsole = true; break; }
            }
            if (hasConsole != still) continue;
            var r = Rect.MinMaxRect(x0, y0, x1, y1);
            string room = BreakPieces.RoomNameAt(r.center, r);
            var floor = room == null ? null : FloorFor(room, col.name);
            if (room == null || (!all && floor == null)) continue; // 床を測っていない家具は動かさない
            var tr = col.transform;
            into.Add(new Lift
            {
                Name = col.name, Room = room, Floor = floor ?? default, Measured = floor != null, Col = col, ColTr = tr, ColT0 = tr.position, ColRot0 = tr.eulerAngles.z,
                Bounds = r, Still = still,
            });
        }
    }

    // 部屋の絵から切り抜く (1 回だけ)。できなければ false (家具は動かさない)
    internal static bool Ensure(Lift l)
    {
        if (l.Tried) return !l.Failed;
        l.Tried = true;
        try
        {
            var shape = MakeShape(l, out var shapeIn);
            Owned.Add(shape);
            var world = PaddedRect(l);
            Vector2 at = world.center;
            l.CutOffset = FxMath.V2(world.center.x - l.Bounds.center.x, world.center.y - l.Bounds.center.y);
            var configure = Configure(l, shape, world);
            var fill = BreakPieces.MakeLift(at, world, 8f, configure);
            var cut = fill == null ? null : BreakPieces.MakeLift(at, world, 7f, configure);
            foreach (var piece in new[] { fill, cut })
            {
                if (piece == null) continue;
                Owned.Add(piece.Sr.sprite);
                Owned.Add(piece.Sr.sharedMaterial);
            }
            if (fill == null || cut == null)
            {
                if (fill != null) UnityEngine.Object.Destroy(fill.Tr.gameObject);
                l.Art = null;
                l.Failed = true;
                return false;
            }
            SetZ(fill.Tr, FillZ);
            SetZ(cut.Tr, CutZ);
            Lifted.Add(fill.Tr.gameObject);
            Lifted.Add(cut.Tr.gameObject);
            l.Cut = cut.Tr;
            l.FurnPx = Classify(world, configure);
            var cp = cut.Tr.position;
            var cs = cut.Tr.lossyScale;
            l.Shape = shape;
            l.ShapeIn = shapeIn;
            l.ShapeWorld = world;
            l.ShapeScale = FxMath.V2(cs.x != 0f ? 1f / cs.x : 1f, cs.y != 0f ? 1f / cs.y : 1f);
            l.ShapeOff = FxMath.V2((world.center.x - cp.x) * l.ShapeScale.x, (world.center.y - cp.y) * l.ShapeScale.y);
            // 跡は床になった (跡の床を描くのは形の中だけ) ので、水もそこまで広がって見せる
            var padded = PaddedRect(l);
            l.PivotX = l.PoseX = l.Bounds.center.x;
            l.PivotY = l.PoseY = l.Bounds.center.y;
            FloorMask.AddFloor(padded, shapeIn, ShapePx, l);
            WaterArt.FloorChanged(padded);
            // 持ち上げるのは水が来てからなので、掛かるタイルの壁の面の高さは跡にする前の床 (家具の絵 = 壁の面) で作ってある。
            // そのままだと床の水が家具を壁として這い上がり、水面の板と二重に覆う
            WaterArt.HeightChanged(padded);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[FurnitureLift] {l.Name}: {e}");
            l.Failed = true;
            return false;
        }
    }

    // 動かさない家具: 部屋の絵は切り抜かず、水面の板の型 (当たり判定の形) と、板を下げる置き場 (切り抜きと同じ奥行き) だけ作る
    internal static bool EnsureStill(Lift l)
    {
        if (l.Tried) return !l.Failed;
        l.Tried = true;
        try
        {
            var shape = MakeShape(l, out var shapeIn);
            Owned.Add(shape);
            var world = PaddedRect(l);
            var go = new GameObject("MrpStillAnchor");
            go.transform.position = FxMath.V3(world.center.x, world.center.y, 0f);
            SetZ(go.transform, CutZ);
            Lifted.Add(go);
            l.Cut = go.transform;
            l.Shape = shape;
            l.ShapeIn = shapeIn;
            l.ShapeWorld = world;
            l.ShapeScale = FxMath.V2(1f, 1f);
            l.ShapeOff = FxMath.V2(0f, 0f);
            // 床を測っていない家具は形を広げていない (型 = 当たり判定) ので、画素の見分けは要らない
            if (l.Measured)
            {
                var room = BreakPieces.RoomAt(world.center, world);
                if (room != null && room.Name == l.Room)
                {
                    l.Art = room;
                    // 型の中の床と家具が落とす影は床の水が描く (板は家具の画素だけ)。部屋の絵の家具はそのまま (床マスクの壁の面)
                    var cls = Classify(world, Configure(l, shape, world), inShapeNotFurniture: true);
                    if (cls != null) FloorMask.AddFloor(world, cls, ClassPx, null);
                }
            }
            // 板を出すのは水が来てからなので、掛かるタイルの壁の面の高さは作り直す
            if (_stillGen != GameClock.ShipGen) { StillShapes.Clear(); _stillGen = GameClock.ShipGen; }
            StillShapes.Add(l);
            WaterArt.HeightChanged(world);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[FurnitureLift] still {l.Name}: {e}");
            l.Failed = true;
            return false;
        }
    }

    private static void SetZ(Transform tr, float dz)
    {
        var p = tr.position;
        float front = DamageMap.FrontZ(FxMath.V2(p.x, p.y));
        tr.position = FxMath.V3(p.x, p.y, front - dz * DamageMap.ZScale(front));
    }

    // きれいな床の左下 (世界) と繰り返しの幅 (世界単位) = シェーダの _FloorPatch
    internal static Vector4 FloorPatch(Floor f, BreakPieces.Room room)
    {
        // 返すのは測る床の世界での左下。裏返った絵 (左右反転の船) は画素の並びが世界と逆向きなので、反対の端の画素の外側の縁が左下になる
        float ex = room.Dx < 0f ? Math.Max(f.Tx, 1f) : 0f, ey = room.Dy < 0f ? Math.Max(f.Ty, 1f) : 0f;
        float px = room.W0x + (room.TexRect.xMin + f.X + ex) * room.Dx, py = room.W0y + (room.TexRect.yMin + f.Y + ey) * room.Dy;
        return new Vector4(px, py, f.Tx * Math.Abs(room.Dx), f.Ty * Math.Abs(room.Dy));
    }

    // 当たり判定の範囲を左右下上の広げる幅だけ広げた四角
    private static Rect PaddedRect(Lift l)
    {
        var b = l.Bounds;
        var f = l.Floor;
        return Rect.MinMaxRect(b.xMin - f.PadL, b.yMin - f.PadB, b.xMax + f.PadR, b.yMax + f.PadT);
    }

    // 当たり判定の形を左右下上の広げる幅だけ太らせて ShapePx 四方の白黒にする (切り抜きと跡の床の両方が使う)。
    // 当たり判定は画素ごとに 1 回だけ引き、太らせは画素の上で (向きごとの半径の楕円の中に中の画素があれば中)
    private static unsafe Texture2D MakeShape(Lift l, out bool[] shapeIn)
    {
        var w = PaddedRect(l);
        var f = l.Floor;
        float x0 = w.xMin, y0 = w.yMin;
        float sx = w.width / ShapePx, sy = w.height / ShapePx;
        var inside = new bool[ShapePx * ShapePx];
        for (int y = 0; y < ShapePx; y++)
            for (int x = 0; x < ShapePx; x++)
                inside[y * ShapePx + x] = l.Col.OverlapPoint(FxMath.V2(x0 + (x + 0.5f) * sx, y0 + (y + 0.5f) * sy));
        l.ColIn = inside;
        int rx = (int)MathF.Ceiling(Math.Max(f.PadL, f.PadR) / sx), ry = (int)MathF.Ceiling(Math.Max(f.PadB, f.PadT) / sy);
        var px = new byte[ShapePx * ShapePx * 4];
        shapeIn = new bool[ShapePx * ShapePx];
        fixed (bool* ins = inside) fixed (byte* o = px) fixed (bool* si = shapeIn)
        {
            var a = new ShapeArgs
            {
                Inside = (byte*)ins, N = ShapePx, Rx = rx, Ry = ry, PadL = f.PadL, PadR = f.PadR, PadB = f.PadB, PadT = f.PadT,
                Sx = sx, Sy = sy, Px = o, ShapeIn = (byte*)si,
            };
            Native.MrpNative.LiftShape(&a);
        }
        if (Native.MrpNative.Verify)
        {
            var csPx = new byte[px.Length];
            var csIn = new bool[shapeIn.Length];
            GrowCs(inside, rx, ry, f, sx, sy, csPx, csIn);
            long diff = 0;
            for (int i = 0; i < px.Length; i++) if (px[i] != csPx[i]) diff++;
            for (int i = 0; i < csIn.Length; i++) if (shapeIn[i] != csIn[i]) diff++;
            VerifyShapes++;
            VerifyShapeDiff += diff;
        }
        // G = 広げる前の当たり判定の中 (水面の板が、当たり判定の中の床の画素を床の水として描くのに使う。切り抜きは R だけを見る)
        for (int i = 0; i < inside.Length; i++) px[i * 4 + 1] = inside[i] ? (byte)255 : (byte)0;
        var tex = new Texture2D(ShapePx, ShapePx, TextureFormat.RGBA32, false) { name = "MrpLiftShape", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, false);
        return tex;
    }

    // 答え合わせ (native verify): 太らせを C# でもやって比べる
    internal static long VerifyShapes, VerifyShapeDiff;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct ShapeArgs
    {
        public byte* Inside; public int N;
        public int Rx, Ry;
        public float PadL, PadR, PadB, PadT;
        public float Sx, Sy;
        public byte* Px;
        public byte* ShapeIn;
    }

    // C# 版 (照合用)
    private static void GrowCs(bool[] inside, int rx, int ry, Floor f, float sx, float sy, byte[] px, bool[] shapeIn)
    {
        for (int y = 0; y < ShapePx; y++)
            for (int x = 0; x < ShapePx; x++)
            {
                bool hit = false;
                for (int dy = -ry; dy <= ry && !hit; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= ShapePx) continue;
                    for (int dx = -rx; dx <= rx; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= ShapePx || !inside[yy * ShapePx + xx]) continue;
                        // この画素が家具のどちら側か (中の画素が右 = dx > 0 なら、この画素は左へ広げた所)
                        float px0 = dx > 0 ? f.PadL : f.PadR, py0 = dy > 0 ? f.PadB : f.PadT;
                        float ex = dx == 0 ? 0f : dx * sx / Math.Max(px0, 1e-4f), ey = dy == 0 ? 0f : dy * sy / Math.Max(py0, 1e-4f);
                        if (ex * ex + ey * ey <= 1f) { hit = true; break; }
                    }
                }
                if (!hit) continue;
                shapeIn[y * ShapePx + x] = true;
                int i = (y * ShapePx + x) * 4;
                px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255;
            }
    }

    // 切り抜きの絵・跡の床・見分けの書き出しのマテリアルに、型と床の模様を渡す (家具の部屋の絵でなければ false)
    private static Func<Material, BreakPieces.Room, bool> Configure(Lift l, Texture2D shape, Rect world) => (mat, room) =>
    {
        if (room.Name != l.Room) return false;
        mat.SetTexture("_ShapeTex", shape);
        mat.SetVector("_ShapeRect", new Vector4(world.xMin, world.yMin, 1f / world.width, 1f / world.height));
        mat.SetVector("_FloorPatch", FloorPatch(l.Floor, room));
        l.Art = room;
        return true;
    };

    // 画素の見分けを GPU で 1 回描いて読み戻す (切り抜きと水面の板が描き分けるのと同じ式・TerrainSprite の _UseDamage = 10)。
    // 返すのは広げた四角 world の上の ClassPx 四方: 家具の画素 (inShapeNotFurniture = 型の中の家具でない画素)。
    // 床マスクは事前に作った絵の見分けで、家具の縁と落とす影の扱いが画素の見分けと食い違うので、水の抜きはこちらで決める。
    // 描くのは見分けの塊 1 枚だけを写す使い捨てのカメラ (Android の libunity には Graphics.Blit が無い)
    private const int ClassifyLayer = 31; // 本編が使っていない層 (瓦礫の焼き込みと同じ・描く間だけ)
    private static bool[] Classify(Rect world, Func<Material, BreakPieces.Room, bool> configure, bool inShapeNotFurniture = false)
    {
        var piece = BreakPieces.MakeLift(world.center, world, 10f, configure);
        if (piece == null) return null;
        var rt = RenderTexture.GetTemporary(ClassPx, ClassPx, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        GameObject camGo = null;
        Texture2D t = null;
        try
        {
            piece.Tr.gameObject.layer = ClassifyLayer;
            camGo = new GameObject("MrpClassifyCam");
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << ClassifyLayer;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 10f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            camGo.transform.position = FxMath.V3(world.center.x, world.center.y, piece.Tr.position.z - 5f);
            cam.orthographicSize = world.height * 0.5f;
            cam.aspect = world.width / world.height;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            RenderTexture.active = rt;
            t = new Texture2D(ClassPx, ClassPx, TextureFormat.RGBA32, false, true);
            t.ReadPixels(new Rect(0, 0, ClassPx, ClassPx), 0, 0, false);
            var raw = t.GetRawTextureData();
            var furn = new bool[ClassPx * ClassPx];
            for (int i = 0; i < furn.Length; i++) furn[i] = raw[i * 4] > 127;
            FillHoles(furn, ClassPx);
            if (!inShapeNotFurniture) return furn;
            var res = new bool[furn.Length];
            for (int i = 0; i < res.Length; i++) res[i] = raw[i * 4 + 1] > 127 && !furn[i];
            return res;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[FurnitureLift] classify: {e.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (t) UnityEngine.Object.Destroy(t);
            if (camGo) UnityEngine.Object.Destroy(camGo);
            var sp = piece.Sr ? piece.Sr.sprite : null;
            var mat = piece.Sr ? piece.Sr.sharedMaterial : null;
            UnityEngine.Object.Destroy(piece.Tr.gameObject);
            if (sp) UnityEngine.Object.Destroy(sp);
            if (mat) UnityEngine.Object.Destroy(mat);
        }
    }

    // 家具の画素に囲まれた小さな「家具でない」所 (枕の輪郭線など、たまたま床の模様と同じ色の画素) を家具にする。
    // 残すと跡の床として床マスクに入り、その上の家具の画素の壁の面の高さが低く測られて、水が家具の途中まで四角く這い上がる。
    // 四角の外の縁から家具でない画素をたどり、届かなかったまとまりのうち小さい物だけ埋める (家具の間の本当の床は外へつながっている)
    private const int HoleMaxDiv = 50; // 埋めるまとまりの上限 = 四角の画素数の 1/50
    private static void FillHoles(bool[] furn, int n)
    {
        var mark = new byte[n * n]; // 1 = 外へつながる・2 = 調べ済みの穴
        var q = new int[n * n];
        int head = 0, tail = 0;
        for (int i = 0; i < n; i++)
        {
            Seed(i); Seed((n - 1) * n + i); Seed(i * n); Seed(i * n + n - 1);
        }
        Spread(1);
        int max = n * n / HoleMaxDiv;
        for (int s = 0; s < furn.Length; s++)
        {
            if (furn[s] || mark[s] != 0) continue;
            head = tail = 0;
            mark[s] = 2;
            q[tail++] = s;
            Spread(2);
            if (tail > max) continue;
            for (int k = 0; k < tail; k++) furn[q[k]] = true;
        }

        void Seed(int i)
        {
            if (furn[i] || mark[i] != 0) return;
            mark[i] = 1;
            q[tail++] = i;
        }

        void Spread(byte m)
        {
            while (head < tail)
            {
                int i = q[head++], x = i % n;
                if (x > 0) Visit(i - 1, m);
                if (x < n - 1) Visit(i + 1, m);
                if (i >= n) Visit(i - n, m);
                if (i < n * n - n) Visit(i + n, m);
            }
        }

        void Visit(int i, byte m)
        {
            if (furn[i] || mark[i] != 0) return;
            mark[i] = m;
            q[tail++] = i;
        }
    }

    // 確かめ用: 持ち上げた家具の画素の見分けをもう一度読み戻して PPM へ (R = 家具・G = 形の中・B = 当たり判定の中)
    internal static string DumpClass(Lift l, string path)
    {
        if (l?.Cut == null || l.Art == null || !l.Shape) return "no lift";
        var configure = Configure(l, l.Shape, l.ShapeWorld);
        var furn = Classify(l.ShapeWorld, configure);
        var inShape = Classify(l.ShapeWorld, configure, inShapeNotFurniture: true);
        if (furn == null || inShape == null) return "classify failed";
        int n = ClassPx, f = 0, s = 0;
        var px = new byte[n * n * 3];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x, o = ((n - 1 - y) * n + x) * 3;
                bool fu = furn[i], sh = inShape[i] || fu;
                int cx = x * ShapePx / n, cy = y * ShapePx / n;
                bool col = l.ColIn != null && l.ColIn[cy * ShapePx + cx];
                px[o] = fu ? (byte)255 : (byte)0; px[o + 1] = sh ? (byte)160 : (byte)0; px[o + 2] = col ? (byte)200 : (byte)0;
                if (fu) f++;
                if (sh) s++;
            }
        using (var fs = System.IO.File.Create(path))
        {
            var head = Encoding.ASCII.GetBytes($"P6\n{n} {n}\n255\n");
            fs.Write(head, 0, head.Length);
            fs.Write(px, 0, px.Length);
        }
        int stored = 0;
        if (l.FurnPx != null) foreach (bool b in l.FurnPx) if (b) stored++;
        return $"{l.Name} furn={f} shape={s} stored={(l.FurnPx == null ? "null" : stored.ToString())} -> {path}";
    }

    // 家具の絵と当たり判定を、元の足元 (pivot) を軸に ang 度回して中心を (cx, cy) へ
    internal static void Place(Lift l, Vector2 pivot0, float cx, float cy, float ang)
    {
        l.PivotX = pivot0.x; l.PivotY = pivot0.y; l.PoseX = cx; l.PoseY = cy; l.PoseAng = ang;
        if (l.Cut)
        {
            var c0 = l.Bounds.center;
            var o = FxMath.RotateZ(ang, c0.x + l.CutOffset.x - pivot0.x, c0.y + l.CutOffset.y - pivot0.y);
            float z = l.Cut.position.z;
            // 切り抜きの絵は作った時に範囲の中心へ置いてある
            l.Cut.position = FxMath.V3(cx + o.x, cy + o.y, z);
            l.Cut.rotation = FxMath.RotZ(ang);
        }
        if (l.ColTr)
        {
            var oc = FxMath.RotateZ(ang, l.ColT0.x - pivot0.x, l.ColT0.y - pivot0.y);
            l.ColTr.position = FxMath.V3(cx + oc.x, cy + oc.y, l.ColT0.z);
            l.ColTr.rotation = FxMath.RotZ(l.ColRot0 + ang);
        }
    }

    // 床を測る道具 (tools/measure-room-floors.py) へ: 持ち上げられそうな家具ごとに、部屋の絵の名前・絵の画素と世界座標の対応・
    // 当たり判定の輪郭 (世界座標の線分) を Screens/lifts_<船>.json へ
    internal static void Register()
    {
        TestBridge.Register("lifts", "部屋の絵に描き込まれた家具 (持ち上げられそうな物) を Screens/lifts_<船>.json へ書き出す (床を測る道具用)", (_, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship) { reply("ERR lifts no ship"); return; }
            var list = new List<Lift>();
            Collect(ship, list, all: true);
            var sb = new StringBuilder("[\n");
            var segs = new List<Vector2>();
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                var room = BreakPieces.RoomAt(l.Bounds.center, l.Bounds);
                if (room == null) continue;
                segs.Clear();
                SolidMap.Segments(l.Col, segs);
                sb.Append("  {\"name\": \"").Append(l.Name).Append("\", \"room\": \"").Append(room.Name)
                  .Append("\", \"measured\": ").Append(FloorFor(room.Name, l.Name) != null ? "true" : "false")
                  .Append(", \"texRect\": [").Append(F(room.TexRect.xMin)).Append(", ").Append(F(room.TexRect.yMin)).Append(", ")
                  .Append(F(room.TexRect.width)).Append(", ").Append(F(room.TexRect.height))
                  .Append("], \"w0\": [").Append(F(room.W0x)).Append(", ").Append(F(room.W0y))
                  .Append("], \"d\": [").Append(F(room.Dx)).Append(", ").Append(F(room.Dy)).Append("], \"segs\": [");
                for (int k = 0; k < segs.Count; k++)
                    sb.Append(k == 0 ? "" : ", ").Append('[').Append(F(segs[k].x)).Append(", ").Append(F(segs[k].y)).Append(']');
                sb.Append("]}").Append(i < list.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]\n");
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"lifts_{ship.name}.json");
            System.IO.File.WriteAllText(path, sb.ToString());
            int l12 = 0, rooms = 0;
            foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
            {
                if (!col || col.gameObject.layer != FurnitureLayer) continue;
                l12++;
                var bb = col.bounds;
                var rr = Rect.MinMaxRect(bb.min.x, bb.min.y, bb.max.x, bb.max.y);
                if (BreakPieces.RoomNameAt(rr.center, rr) != null) rooms++;
            }
            reply($"OK lifts n={list.Count} layer12={l12} withRoom={rooms} -> {path}");
        });
    }

    private static string F(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    // 失敗した時: 当たり判定を元に戻す
    internal static void Restore(Lift l)
    {
        if (!l.ColTr) return;
        l.ColTr.position = l.ColT0;
        l.ColTr.rotation = FxMath.RotZ(l.ColRot0);
    }
}
