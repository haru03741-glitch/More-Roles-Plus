using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 大きな瓦礫 1 つの止まる所 (ホストが決めて配る)。Rank = その破壊で割れた塊の並び (BreakPieces の大きい順) の何番目か
internal readonly struct RubbleLanding
{
    public readonly byte Rank;
    public readonly Vector2 Position;
    public readonly float Radius;

    public RubbleLanding(byte rank, Vector2 position, float radius)
    {
        Rank = rank;
        Position = position;
        Radius = radius;
    }
}

// 大きな瓦礫の当たり判定。移動だけを塞ぎ (動きの層 9 だけ・視界の層 10 には置かない)、閉じ込めないよう
// 「どの瓦礫も周りの壁・他の瓦礫からクルー 1 人ぶん以上離れた島になる」所にだけ置く
// (島の周りを一周できるので、瓦礫が増えても通れない所はできない)
internal static class RubbleBlocks
{
    public const int MaxPerEvent = 3;
    private const int ShipLayer = 9;
    private const float MinPieceSize = 0.3f;   // これより小さい塊は当たり判定を持たない
    private const float Clearance = 0.5f;      // 瓦礫と壁・瓦礫同士の隙間の下限 (クルーの当たり判定の直径 ≈ 0.45 + 余白)
    private const float PlayerClearance = 0.35f; // 置く時にクルーが立っている所には置かない (押し出し・閉じ込めを避ける)
    private const float MaxNudge = 0.35f;      // 隙間が足りない時に壁から離す距離の上限 (それでも足りなければ置かない)
    public const string Name = "MrpRubbleBlock"; // TerrainDamage.ProtectedName が名前ちょうどで拾う (壁として切られない・奥の面と見なされない)

    // 拾える瓦礫 (構築士のドローンが持ち帰る)。拾える試合では塊の絵を床の板へ焼かずに残し、当たり判定と一緒に消せるようにする
    internal sealed class Block
    {
        public GameObject Go;
        public float X, Y;
        public Transform Piece;  // 止まった塊の絵 (止まるまで / 焼いた試合では null)
        public bool OwnSprite;
        public bool Taken;       // 拾うと決まった (絵と当たり判定はドローンが着いた時に消す)
    }

    private static readonly List<Block> Blocks = new();
    internal static int Count => Blocks.Count;
    internal static IEnumerable<GameObject> All
    {
        get { foreach (var b in Blocks) yield return b.Go; }
    }

    // 拾える試合か (構築士がいる)。false の試合では塊の絵は従来どおり床の板へ焼く
    internal static Func<bool> Pickable = () => false;
    private const float TakeMatch = 0.1f; // 拾う知らせの位置と当たり判定の位置の食い違いの許し (電文の丸め)

    // ホスト: 予測した止まる所 (rest[i] = 並び i の塊。null は塊が無い) から、置ける物を最大 MaxPerEvent 個選ぶ。
    // 値は電文と同じ丸めを掛けて返す (ホストも丸めた値で置く)
    public static RubbleLanding[] Decide(Vector2?[] rest, float[] sizes, float[] walls)
    {
        var chosen = new List<RubbleLanding>();
        for (int i = 0; i < rest.Length && chosen.Count < MaxPerEvent; i++)
        {
            if (rest[i] is not Vector2 p || sizes[i] < MinPieceSize) continue;
            float r = TerrainWire.QRadius(Math.Clamp(sizes[i] * 0.3f, 0.1f, 0.2f));
            if (!Fit(ref p, r, walls, chosen)) continue;
            p = TerrainWire.Q(p);
            if (!Clear(p, r, walls, chosen)) continue; // 丸めで隙間を割った
            chosen.Add(new RubbleLanding((byte)i, p, r));
        }
        return chosen.ToArray();
    }

    // 隙間が足りなければ一番近い壁から離してみる
    private static bool Fit(ref Vector2 p, float r, float[] walls, List<RubbleLanding> others)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Clear(p, r, walls, others)) return true;
            if (!Nearest(p, walls, out Vector2 q)) return false;
            Vector2 away = p - q;
            float d = away.magnitude;
            if (d < 1e-4f) return false;
            float need = r + Clearance + 0.02f - d;
            if (need <= 0f || need > MaxNudge) return false;
            p += away / d * need;
        }
        return Clear(p, r, walls, others);
    }

    private static bool Clear(Vector2 p, float r, float[] walls, List<RubbleLanding> others)
    {
        float min = r + Clearance;
        for (int k = 0; k + 3 < walls.Length; k += 4)
            if (SegDist2(p.x, p.y, walls[k], walls[k + 1], walls[k + 2], walls[k + 3]) < min * min) return false;
        foreach (var o in others)
        {
            float m = r + o.Radius + Clearance;
            if ((o.Position - p).sqrMagnitude < m * m) return false;
        }
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc || pc.Data == null || pc.Data.IsDead) continue;
            Vector2 at = pc.GetTruePosition();
            float m = r + PlayerClearance;
            if ((at - p).sqrMagnitude < m * m) return false;
        }
        return true;
    }

    private static bool Nearest(Vector2 p, float[] walls, out Vector2 q)
    {
        q = default;
        float best = float.MaxValue;
        for (int k = 0; k + 3 < walls.Length; k += 4)
        {
            float ax = walls[k], ay = walls[k + 1], ex = walls[k + 2] - ax, ey = walls[k + 3] - ay;
            float l2 = ex * ex + ey * ey;
            float s = l2 > 0f ? Math.Clamp(((p.x - ax) * ex + (p.y - ay) * ey) / l2, 0f, 1f) : 0f;
            float cx = ax + ex * s, cy = ay + ey * s;
            float d = (p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy);
            if (d < best) { best = d; q = new Vector2(cx, cy); }
        }
        return best < float.MaxValue;
    }

    private static float SegDist2(float px, float py, float ax, float ay, float bx, float by)
    {
        float ex = bx - ax, ey = by - ay;
        float l2 = ex * ex + ey * ey;
        float s = l2 > 0f ? Math.Clamp(((px - ax) * ex + (py - ay) * ey) / l2, 0f, 1f) : 0f;
        float cx = ax + ex * s - px, cy = ay + ey * s - py;
        return cx * cx + cy * cy;
    }

    // 全員: 決まった所に当たり判定を置く。並びは landings と同じ
    public static Block[] Place(RubbleLanding[] landings)
    {
        var placed = new Block[landings.Length];
        for (int i = 0; i < landings.Length; i++)
        {
            var l = landings[i];
            var go = new GameObject(Name) { layer = ShipLayer };
            DamageMap.Track(go);
            go.transform.position = new Vector3(l.Position.x, l.Position.y, 0f);
            var col = go.AddComponent<CircleCollider2D>();
            // 親 (船) の倍率を打ち消して世界での半径にする
            float s = MathF.Abs(go.transform.lossyScale.x);
            col.radius = l.Radius / (s > 1e-4f ? s : 1f);
            Blocks.Add(placed[i] = new Block { Go = go, X = l.Position.x, Y = l.Position.y });
        }
        return placed;
    }

    // 全員: 塊の絵が止まった。拾える試合なら焼かずに持っておく (true)。もう拾われていれば消す
    internal static bool KeepPiece(Block b, Transform piece, bool ownSprite)
    {
        if (b == null || (!b.Taken && !Pickable())) return false;
        b.Piece = piece;
        b.OwnSprite = ownSprite;
        if (b.Taken) Remove(b);
        return true;
    }

    // 拾える瓦礫で、(x, y) からいちばん近い物 (止まった絵があり、まだ拾われていない・r 以内)。無ければ null
    internal static Block NearestFree(float x, float y, float r)
    {
        Block best = null;
        float bestD = r * r;
        foreach (var b in Blocks)
        {
            if (b.Taken || !b.Piece || !b.Go) continue;
            float dx = b.X - x, dy = b.Y - y, d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = b; }
        }
        return best;
    }

    // (x, y) の拾われていない瓦礫 (ホストの確かめと、拾う知らせの受け手)
    internal static Block FreeAt(float x, float y)
    {
        foreach (var b in Blocks)
        {
            if (b.Taken || !b.Go) continue;
            if (MathF.Abs(b.X - x) <= TakeMatch && MathF.Abs(b.Y - y) <= TakeMatch) return b;
        }
        return null;
    }

    // 全員: 絵と当たり判定を消す (ドローンが着いた時)。止まる前なら止まった時に消す
    internal static void Remove(Block b)
    {
        b.Taken = true;
        if (b.Go) { UnityEngine.Object.Destroy(b.Go); b.Go = null; }
        if (!b.Piece) return;
        if (b.OwnSprite)
        {
            var sr = b.Piece.GetComponent<SpriteRenderer>();
            if (sr && sr.sprite) UnityEngine.Object.Destroy(sr.sprite);
        }
        UnityEngine.Object.Destroy(b.Piece.gameObject);
        b.Piece = null;
        ShadowPatch.MarkSettled(b.X, b.Y); // 影の中の焼いた絵からも消す
    }

    // マップが変わった時 (GameObject は DamageMap が片付ける)
    public static void Clear() => Blocks.Clear();
}
