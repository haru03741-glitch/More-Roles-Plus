using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 割れた塊 1 つ。最初は元の場所にぴったり重なっていて、TerrainFx が崩す
internal sealed class BreakPiece
{
    public Transform Tr;
    public SpriteRenderer Sr;
    public Vector2 Origin; // 元の場所 (塊の中心の世界座標)
    public float Size;     // 塊の大きさ (世界単位)
}

// 抜いた所の壁の絵を、細胞模様 (CellLattice) の細胞ごとに切り出した塊にする。
// 塊の絵は部屋の絵のテクスチャをそのまま使い、見える範囲はシェーダ (_UseDamage = 4) が元の場所の損傷マスクで決める
// (部屋の絵が抜いた所のちょうど補集合・その画素を最後に抜いた破壊の番号が自分のものだけ)。
// CPU は塊ごとの範囲 (どの細胞がどこまであるか) を決めるだけで、形の判定は部屋の絵と同じ 1 か所 (シェーダ) にある
internal static class BreakPieces
{
    private const int MaxPieces = 24;   // 破壊 1 回の上限 (部屋が重なる所は部屋ごとに 1 つと数える)
    private const int MinTexels = 3;    // これより小さい細胞 (マスクの画素数) は塊にしない
    private const byte VisibleFloor = 43; // マスクの R がこれ未満だと割れ口のずらし (最大 +0.33) を足しても抜けない
    private const float PieceZ = 0.004f;  // 部屋の絵より手前へ
    private const float PieceLine = 0.022f; // 塊の輪郭線の幅 (世界単位)。隣の塊と合わせて部屋の割れ口の線 (≈0.045) と同じ太さ

    private sealed class Room
    {
        public Material Mat;
        public Texture2D Tex;
        public Rect TexRect;          // テクスチャ上のこの絵の範囲 (はみ出すとアトラスの隣の絵が乗る)
        public float Ppu;
        public float W0x, W0y, Dx, Dy; // テクスチャの画素 (tx, ty) の世界座標 = (W0x + tx * Dx, W0y + ty * Dy)
    }

    private static readonly Dictionary<int, Room> Rooms = new();
    private static readonly HashSet<int> Unusable = new();
    // 残っている塊 (古い順)。上限を超えたら古いものから消す
    private const int MaxAlive = 240;
    private static readonly Queue<(GameObject Go, Sprite Sp, byte Gen)> Alive = new();

    private struct Acc
    {
        public int X0, Y0, X1, Y1, Count;
        public long SumX, SumY;
    }

    // テスト用: false で塊を作らない (見た目の比較用)
    internal static bool Enabled = true;

    // touched = この破壊が抜いた損傷マスクの画素の範囲
    public static void Spawn(RectInt touched, List<BreakPiece> output)
    {
        if (touched.width <= 0 || !MrpBundle.Ready || !Enabled) return;
        byte gen = DamageMap.CurrentGen;
        // 番号が一周した時: 同じ番号の古い塊は消す (残すと新しい穴の同じ細胞に重なって描かれる)
        if (DamageMap.GenWrapped) Evict(gen);
        int w = DamageMap.MapW, h = DamageMap.MapH;

        var cells = new Dictionary<byte, Acc>();
        for (int py = touched.yMin; py < touched.yMax; py++)
        for (int px = touched.xMin; px < touched.xMax; px++)
        {
            if (DamageMap.GenAt(px, py) != gen || !Visible(px, py, w, h)) continue;
            Vector2 c = DamageMap.TexelCenter(px, py);
            byte key = CellLattice.KeyAt(c.x, c.y, CellLattice.PieceScale);
            if (!cells.TryGetValue(key, out var a)) a = new Acc { X0 = px, Y0 = py, X1 = px, Y1 = py };
            if (px < a.X0) a.X0 = px;
            if (px > a.X1) a.X1 = px;
            if (py < a.Y0) a.Y0 = py;
            if (py > a.Y1) a.Y1 = py;
            a.Count++;
            a.SumX += px; a.SumY += py;
            cells[key] = a;
        }

        // 大きい塊から (同じ数なら細胞の番号順 = 全員で同じ順)
        var keys = new List<byte>();
        foreach (var kv in cells) if (kv.Value.Count >= MinTexels) keys.Add(kv.Key);
        keys.Sort((a, b) => cells[a].Count != cells[b].Count ? cells[b].Count.CompareTo(cells[a].Count) : a.CompareTo(b));

        if (keys.Count == 0) return;
        Vector2 tlo = DamageMap.TexelCenter(touched.xMin, touched.yMin), thi = DamageMap.TexelCenter(touched.xMax, touched.yMax);
        var arts = Candidates(Rect.MinMaxRect(tlo.x - 1f, tlo.y - 1f, thi.x + 1f, thi.y + 1f));
        // 1 画素ぶん (細胞の境の端数) と輪郭線ぶん外へ広げる
        float pad = DamageMap.TexelSize * 1.5f + 0.05f;
        foreach (byte key in keys)
        {
            if (output.Count >= MaxPieces) break;
            var a = cells[key];
            Vector2 lo = DamageMap.TexelCenter(a.X0, a.Y0), hi = DamageMap.TexelCenter(a.X1, a.Y1);
            float half = DamageMap.TexelSize * 0.5f;
            var rect = Rect.MinMaxRect(lo.x - half - pad, lo.y - half - pad, hi.x + half + pad, hi.y + half + pad);
            // 切り出すのは、塊の真ん中でいちばん手前に描かれている部屋の絵だけ。部屋の絵は重なっていて
            // (Skeld の MedBay の下に廊下の絵)、奥の絵の隠れた所には見えない前提の絵が入っている
            Vector2 mid = DamageMap.TexelCenter((int)(a.SumX / a.Count), (int)(a.SumY / a.Count));
            var room = FrontRoom(arts, mid, rect);
            if (room == null) continue;
            var piece = Make(room, rect, key, gen);
            if (piece != null) output.Add(piece);
        }
    }

    // 点を含む部屋の絵のうち一番手前 (z が小さい) のもの。含むものが無ければ範囲に掛かるうちで一番手前
    private static Room FrontRoom(List<Candidate> arts, Vector2 p, Rect rect)
    {
        SpriteRenderer best = null;
        bool bestHas = false;
        float bestZ = float.MaxValue;
        foreach (var c in arts)
        {
            var b = c.Bounds;
            if (rect.xMax < b.xMin || rect.xMin > b.xMax || rect.yMax < b.yMin || rect.yMin > b.yMax) continue;
            bool has = p.x >= b.xMin && p.x <= b.xMax && p.y >= b.yMin && p.y <= b.yMax;
            if (has && !bestHas || has == bestHas && c.Z < bestZ) { best = c.Sr; bestHas = has; bestZ = c.Z; }
        }
        return best ? RoomOf(best) : null;
    }

    private struct Candidate
    {
        public SpriteRenderer Sr;
        public Rect Bounds;
        public float Z;
    }

    // 範囲に掛かる、損傷マスクを見ている部屋の絵 (範囲と z は破壊 1 回につき 1 度だけ引く)
    private static List<Candidate> Candidates(Rect area)
    {
        var list = new List<Candidate>();
        var arts = DamageMap.RoomArts;
        for (int r = 0; r < arts.Count; r++)
        {
            var sr = arts[r];
            if (!sr || !DamageMap.IsSwapped(sr)) continue;
            var b = sr.bounds;
            if (area.xMax < b.min.x || area.xMin > b.max.x || area.yMax < b.min.y || area.yMin > b.max.y) continue;
            list.Add(new Candidate { Sr = sr, Bounds = Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y), Z = sr.transform.position.z });
        }
        return list;
    }

    // その画素か周りの画素が、割れ口のずらしを足せば抜ける値を持つか (シェーダは R を周りと線形補間して引く)
    private static bool Visible(int px, int py, int w, int h)
    {
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int x = px + dx, y = py + dy;
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            if (DamageMap.HoleByte(x, y) >= VisibleFloor) return true;
        }
        return false;
    }

    private static BreakPiece Make(Room room, Rect world, byte key, byte gen)
    {
        // 世界の範囲 → テクスチャの画素の範囲 (この絵の範囲に収める)
        float tx0 = (world.xMin - room.W0x) / room.Dx, tx1 = (world.xMax - room.W0x) / room.Dx;
        float ty0 = (world.yMin - room.W0y) / room.Dy, ty1 = (world.yMax - room.W0y) / room.Dy;
        if (tx0 > tx1) (tx0, tx1) = (tx1, tx0);
        if (ty0 > ty1) (ty0, ty1) = (ty1, ty0);
        int x0 = Math.Max((int)MathF.Floor(tx0), (int)room.TexRect.xMin), x1 = Math.Min((int)MathF.Ceiling(tx1), (int)room.TexRect.xMax);
        int y0 = Math.Max((int)MathF.Floor(ty0), (int)room.TexRect.yMin), y1 = Math.Min((int)MathF.Ceiling(ty1), (int)room.TexRect.yMax);
        if (x1 - x0 < 2 || y1 - y0 < 2) return null;

        var sprite = Sprite.Create(room.Tex, new Rect(x0, y0, x1 - x0, y1 - y0), new Vector2(0.5f, 0.5f), room.Ppu, 0, SpriteMeshType.FullRect);
        sprite.name = "MrpPiece";

        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
        var center = new Vector2(room.W0x + cx * room.Dx, room.W0y + cy * room.Dy);
        // 層は既定 (0)。部屋の絵の層 (9) に置くと、影のカメラ (層 9〜12 を影のテクスチャへ描き直す) が
        // 頂点色をそのまま掛けた切り抜きなしの四角で描き、Polus の影の中に赤い四角が出る
        var go = new GameObject("MrpPiece");
        DamageMap.Track(go);
        var tr = go.transform;
        tr.position = new Vector3(center.x, center.y, DamageMap.FrontZ(center) - PieceZ);
        // 塊の 1 画素 = 部屋の絵の 1 画素 (反転は負の倍率で持つ)
        float ps = DamageMap.ShipTransform ? DamageMap.ShipTransform.lossyScale.x : 1f;
        tr.localScale = new Vector3(room.Dx * room.Ppu / ps, room.Dy * room.Ppu / ps, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sharedMaterial = room.Mat;
        Alive.Enqueue((go, sprite, gen));
        while (Alive.Count > MaxAlive) Destroy(Alive.Dequeue());
        // 頂点色は色でなく番号 (シェーダの _UseDamage = 4 がそう読む)
        sr.color = new Color(key / 255f, gen / 255f, 0f, 1f);
        return new BreakPiece
        {
            Tr = tr,
            Sr = sr,
            Origin = center,
            Size = Math.Max(Math.Abs((x1 - x0) * room.Dx), Math.Abs((y1 - y0) * room.Dy)),
        };
    }

    // 部屋の絵のテクスチャの画素と世界座標の対応。回転した絵・詰めて回したアトラスの絵は切り出さない
    private static Room RoomOf(SpriteRenderer sr)
    {
        int id = sr.GetInstanceID();
        if (Rooms.TryGetValue(id, out var room)) return room;
        if (Unusable.Contains(id)) return null;
        try
        {
            var sp = sr.sprite;
            var tex = sp ? sp.texture : null;
            if (!tex || (sp.packed && (sp.packingMode == SpritePackingMode.Tight || sp.packingRotation != SpritePackingRotation.None)))
            {
                Unusable.Add(id);
                return null;
            }
            Rect tr = sp.textureRect;
            Vector2 off = sp.textureRectOffset, pv = sp.pivot;
            float ppu = sp.pixelsPerUnit;
            bool fx = sr.flipX, fy = sr.flipY;
            var t = sr.transform;
            Vector3 World(float tx, float ty)
            {
                float lx = (tx - tr.x + off.x - pv.x) / ppu, ly = (ty - tr.y + off.y - pv.y) / ppu;
                if (fx) lx = -lx;
                if (fy) ly = -ly;
                return t.TransformPoint(new Vector3(lx, ly, 0f));
            }
            const float span = 1000f;
            Vector3 w0 = World(0f, 0f), wx = World(span, 0f), wy = World(0f, span);
            // 回転していないこと (横に進むと y が、縦に進むと x が変わらない)
            if (MathF.Abs(wx.y - w0.y) > 0.01f || MathF.Abs(wy.x - w0.x) > 0.01f)
            {
                Unusable.Add(id);
                return null;
            }
            room = new Room
            {
                Tex = tex,
                TexRect = tr,
                Ppu = ppu,
                W0x = w0.x, W0y = w0.y,
                Dx = (wx.x - w0.x) / span, Dy = (wy.y - w0.y) / span,
            };
            room.Mat = new Material(DamageMap.PropMaterial) { name = "MrpPiece" };
            room.Mat.SetFloat("_UseDamage", 4f);
            room.Mat.SetFloat("_PieceScale", CellLattice.PieceScale);
            room.Mat.SetFloat("_PieceLine", PieceLine);
            // uv (0..1) → 世界座標
            room.Mat.SetVector("_PieceMap", new Vector4(room.Dx * tex.width, room.Dy * tex.height, room.W0x, room.W0y));
            Rooms[id] = room;
            return room;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"break pieces: room art {sr.name} unusable: {e.Message}");
            Unusable.Add(id);
            return null;
        }
    }

    // マップが変わった時 (塊の GameObject は DamageMap が片付ける)
    public static void Clear()
    {
        foreach (var a in Alive) if (a.Sp) UnityEngine.Object.Destroy(a.Sp);
        Alive.Clear();
        foreach (var r in Rooms.Values) if (r.Mat) UnityEngine.Object.Destroy(r.Mat);
        Rooms.Clear();
        Unusable.Clear();
    }

    // テスト用: 今ある塊の数
    internal static int Count => Alive.Count;

    private static void Destroy((GameObject Go, Sprite Sp, byte Gen) a)
    {
        if (a.Go) UnityEngine.Object.Destroy(a.Go);
        if (a.Sp) UnityEngine.Object.Destroy(a.Sp);
    }

    private static void Evict(byte gen)
    {
        int n = Alive.Count;
        for (int i = 0; i < n; i++)
        {
            var a = Alive.Dequeue();
            if (a.Gen == gen) Destroy(a);
            else Alive.Enqueue(a);
        }
    }
}
