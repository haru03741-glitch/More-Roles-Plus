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
    private const int ShapePx = 64;          // 形のテクスチャの 1 辺
    internal const float Pad = 0.3f;         // 形を当たり判定から広げる幅の上限 (当たり判定は絵より小さい所がある。広げた所の床は模様で外す)
    private const float CutZ = 0.0006f;      // 部屋の絵より手前・水 (0.0004) より手前
    private const float FillZ = 0.0002f;     // 部屋の絵より手前・水より奥

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
        if (FurnitureFloors.Manual.TryGetValue(key, out var f)) return f;
        if (FurnitureFloors.Table.TryGetValue(key, out f)) return f;
        if (FurnitureFloors.Table.TryGetValue(room, out f)) return f;
        return null;
    }

    // 切り抜いた家具と跡の床 (影の中の焼き込みが上から描き込む)・作った絵と形 (船が替わる時に片付ける)
    private static readonly List<GameObject> Lifted = new();
    private static readonly List<UnityEngine.Object> Owned = new();

    internal static void CollectLifted(List<GameObject> into)
    {
        foreach (var go in Lifted) if (go) into.Add(go);
    }

    // 船が替わった時・失敗した時: 切り抜いた絵と、家具ごとに作ったマテリアル・Sprite・形のテクスチャを消す
    internal static void Clear()
    {
        foreach (var go in Lifted) if (go) UnityEngine.Object.Destroy(go);
        foreach (var o in Owned) if (o) UnityEngine.Object.Destroy(o);
        Lifted.Clear();
        Owned.Clear();
    }

    internal sealed class Lift
    {
        public string Name, Room;
        public Floor Floor;
        public Vector2 CutOffset;   // 切り抜きの絵の中心 - 当たり判定の範囲の中心 (広げる幅が左右で違う時)
        public Collider2D Col;
        public Transform ColTr;
        public Vector3 ColT0;
        public float ColRot0;
        public Rect Bounds;
        public Transform Cut;
        public bool Tried, Failed;
        // 今の置き方 (元の足元 Pivot を軸に PoseAng 度回して Pose へ)。床マスクが家具の今いる所を抜くのに使う
        public float PivotX, PivotY, PoseX, PoseY, PoseAng;
    }

    // 船の中の持ち上げられそうな家具 (並びは当たり判定の階層順 = 全員同じ)
    // all = 床を測っていない部屋の家具も (床を測る道具へ書き出す用)
    internal static void Collect(ShipStatus ship, List<Lift> into, bool all = false)
    {
        var consoles = new List<Vector2>();
        foreach (var c in ship.AllConsoles) if (c) consoles.Add(c.transform.position);
        foreach (var c in ship.GetComponentsInChildren<SystemConsole>(true)) if (c) consoles.Add(c.transform.position);
        foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
        {
            if (!col || !col.enabled || col.isTrigger || col.gameObject.layer != FurnitureLayer) continue;
            if (col.GetComponent<SpriteRenderer>()) continue; // 自分の絵を持つ物は部屋の絵に描き込まれていない
            bool hasConsole = false;
            foreach (var p in consoles) if (col.OverlapPoint(p)) { hasConsole = true; break; }
            if (hasConsole) continue;
            var b = col.bounds;
            var r = Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y);
            string room = BreakPieces.RoomNameAt(r.center, r);
            var floor = room == null ? null : FloorFor(room, col.name);
            if (room == null || (!all && floor == null)) continue; // 床を測っていない家具は動かさない
            var tr = col.transform;
            into.Add(new Lift
            {
                Name = col.name, Room = room, Floor = floor ?? default, Col = col, ColTr = tr, ColT0 = tr.position, ColRot0 = tr.eulerAngles.z,
                Bounds = r,
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
            Func<Material, BreakPieces.Room, bool> configure = (mat, room) =>
            {
                if (room.Name != l.Room) return false;
                var f = l.Floor;
                mat.SetTexture("_ShapeTex", shape);
                mat.SetVector("_ShapeRect", new Vector4(world.xMin, world.yMin, 1f / world.width, 1f / world.height));
                float px = room.W0x + (room.TexRect.xMin + f.X) * room.Dx, py = room.W0y + (room.TexRect.yMin + f.Y) * room.Dy;
                mat.SetVector("_FloorPatch", new Vector4(px, py, f.Tx * Math.Abs(room.Dx), f.Ty * Math.Abs(room.Dy)));
                return true;
            };
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
                l.Failed = true;
                return false;
            }
            SetZ(fill.Tr, FillZ);
            SetZ(cut.Tr, CutZ);
            Lifted.Add(fill.Tr.gameObject);
            Lifted.Add(cut.Tr.gameObject);
            l.Cut = cut.Tr;
            // 跡は床になった (跡の床を描くのは形の中だけ) ので、水もそこまで広がって見せる
            var padded = PaddedRect(l);
            l.PivotX = l.PoseX = l.Bounds.center.x;
            l.PivotY = l.PoseY = l.Bounds.center.y;
            FloorMask.AddFloor(padded, shapeIn, ShapePx, l);
            WaterArt.FloorChanged(padded);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[FurnitureLift] {l.Name}: {e}");
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
        int rx = (int)MathF.Ceiling(Math.Max(f.PadL, f.PadR) / sx), ry = (int)MathF.Ceiling(Math.Max(f.PadB, f.PadT) / sy);
        var px = new byte[ShapePx * ShapePx * 4];
        shapeIn = new bool[ShapePx * ShapePx];
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
                        // 中の画素から見たこの画素の向きの広げる幅 (dx > 0 = 右へ広げた所)
                        float px0 = dx > 0 ? f.PadR : f.PadL, py0 = dy > 0 ? f.PadT : f.PadB;
                        float ex = dx == 0 ? 0f : dx * sx / Math.Max(px0, 1e-4f), ey = dy == 0 ? 0f : dy * sy / Math.Max(py0, 1e-4f);
                        if (ex * ex + ey * ey <= 1f) { hit = true; break; }
                    }
                }
                if (!hit) continue;
                shapeIn[y * ShapePx + x] = true;
                int i = (y * ShapePx + x) * 4;
                px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255;
            }
        var tex = new Texture2D(ShapePx, ShapePx, TextureFormat.RGBA32, false) { name = "MrpLiftShape", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, false);
        return tex;
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
