using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 部屋の絵に描き込まれた家具 (自分の絵を持たず、層 12 の当たり判定だけを持つ物。Skeld の食堂のテーブルなど) を、
// 初めて押された時に部屋の絵から切り抜いて動かせるようにする。動かすのは PropSim (重い物)。
// - 跡の床 (_UseDamage = 8): 元の場所で家具の画素の上に、同じ部屋の絵のきれいな床を模様の繰り返しの幅ごとに写して描く。
// - 家具 (_UseDamage = 7): 部屋の絵の同じ範囲を切り出し、当たり判定の形の中で、その場所に本来ある床の模様と違う画素だけ描く (落とす影ごと動く)。
// 床の模様は部屋の絵ごとに測った値 (Floors)。載っていない部屋の家具は持ち上げない。
// 本編の物 (緊急ボタンなどの端末) が載った家具は持ち上げない
internal static class FurnitureLift
{
    private const int FurnitureLayer = 12;
    private const int ShapePx = 64;          // 形のテクスチャの 1 辺
    internal const float Pad = 0.3f;
    private const float ShapePad = Pad;     // 形を当たり判定から広げる幅 (当たり判定は絵より小さい所がある。広げた所の床は色で外す)
    private const float CutZ = 0.0006f;      // 部屋の絵より手前・水 (0.0004) より手前
    private const float FillZ = 0.0002f;     // 部屋の絵より手前・水より奥

    // 部屋の絵ごとの床 (絵の画素・左下原点): きれいな床の左下・模様の繰り返しの幅
    private readonly struct Floor
    {
        public readonly float X, Y, Period;
        public Floor(float x, float y, float period) { X = x; Y = y; Period = period; }
    }

    private static readonly Dictionary<string, Floor> Floors = new()
    {
        ["room_cafeteria"] = new Floor(410f, 665f, 124.3f),
    };


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
        public string Name;
        public Collider2D Col;
        public Transform ColTr;
        public Vector3 ColT0;
        public float ColRot0;
        public Rect Bounds;
        public Transform Cut;
        public bool Tried, Failed;
    }

    // 船の中の持ち上げられそうな家具 (並びは当たり判定の階層順 = 全員同じ)
    internal static void Collect(ShipStatus ship, List<Lift> into)
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
            if (room == null || !Floors.ContainsKey(room)) continue; // 床を測っていない部屋の家具は動かさない
            var tr = col.transform;
            into.Add(new Lift
            {
                Name = col.name, Col = col, ColTr = tr, ColT0 = tr.position, ColRot0 = tr.eulerAngles.z,
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
            var shape = MakeShape(l);
            Owned.Add(shape);
            var b = l.Bounds;
            var world = Rect.MinMaxRect(b.xMin - ShapePad, b.yMin - ShapePad, b.xMax + ShapePad, b.yMax + ShapePad);
            Vector2 at = world.center;
            Func<Material, BreakPieces.Room, bool> configure = (mat, room) =>
            {
                if (!Floors.TryGetValue(room.Name, out var f)) return false;
                mat.SetTexture("_ShapeTex", shape);
                mat.SetVector("_ShapeRect", new Vector4(world.xMin, world.yMin, 1f / world.width, 1f / world.height));
                float px = room.W0x + (room.TexRect.xMin + f.X) * room.Dx, py = room.W0y + (room.TexRect.yMin + f.Y) * room.Dy;
                mat.SetVector("_FloorPatch", new Vector4(px, py, f.Period * Math.Abs(room.Dx), 0f));
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

    // 当たり判定の形を ShapePad だけ太らせて ShapePx 四方の白黒にする (切り抜きと跡の床の両方が使う)。
    // 当たり判定は画素ごとに 1 回だけ引き、太らせは画素の上で (半径 ShapePad の楕円の中に中の画素があれば中)
    private static unsafe Texture2D MakeShape(Lift l)
    {
        var w = l.Bounds;
        float x0 = w.xMin - ShapePad, y0 = w.yMin - ShapePad;
        float sx = (w.width + ShapePad * 2f) / ShapePx, sy = (w.height + ShapePad * 2f) / ShapePx;
        var inside = new bool[ShapePx * ShapePx];
        for (int y = 0; y < ShapePx; y++)
            for (int x = 0; x < ShapePx; x++)
                inside[y * ShapePx + x] = l.Col.OverlapPoint(FxMath.V2(x0 + (x + 0.5f) * sx, y0 + (y + 0.5f) * sy));
        int rx = (int)MathF.Ceiling(ShapePad / sx), ry = (int)MathF.Ceiling(ShapePad / sy);
        var px = new byte[ShapePx * ShapePx * 4];
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
                        float ex = dx * sx, ey = dy * sy;
                        if (ex * ex + ey * ey <= ShapePad * ShapePad) { hit = true; break; }
                    }
                }
                if (!hit) continue;
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
        if (l.Cut)
        {
            var c0 = l.Bounds.center;
            var o = FxMath.RotateZ(ang, c0.x - pivot0.x, c0.y - pivot0.y);
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

    // 失敗した時: 当たり判定を元に戻す
    internal static void Restore(Lift l)
    {
        if (!l.ColTr) return;
        l.ColTr.position = l.ColT0;
        l.ColTr.rotation = FxMath.RotZ(l.ColRot0);
    }
}
