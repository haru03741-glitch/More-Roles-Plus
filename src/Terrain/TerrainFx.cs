using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壊れる瞬間の動き (崩れ落ちる塊・飛ぶ破片・土煙・火花・爆発の閃光)。
// 塊と破片は止まったらそのまま瓦礫として残し、土煙・火花・閃光は消える。
// 乱数は損傷イベントの種から作るので、全員が同じ散らばり方になる。
// 割れた塊と飛ぶ破片は残った壁の線 (WallSegments) に当たって跳ね返る (床の上の 2D + 高さの自前の動き)
internal static class TerrainFx
{
    private enum Kind { Fall, Fly, Puff, Spark, Flash, Piece, Junk }

    private sealed class Item
    {
        public Kind Kind;
        public Transform Tr;
        public SpriteRenderer Sr;
        public float T, Life;
        // 毎フレームの計算は managed の float だけで (Unity の Vector の演算子・コンストラクタは interop を通ってゴミを出す)
        public float Px, Py, Vx, Vy; // 床の上の位置と速さ
        public float Height, VH;   // 床からの高さ (3/4 視点なので画面では上にずれて見える)
        public float Rot, VRot;
        public float S0, S1, Z;
        public float SpriteW;     // 絵の幅 (土煙の倍率を毎フレーム出すため)
        public float H0;          // 塊: 落ち始めの高さ
        public float Sx, Sy, Sz;  // 塊: 元の倍率 (床に寝るにつれて縦を縮める)
        public float Lie;         // 塊: 今の寝かせ具合 (変わった時だけ倍率を書く)
        public float[] Walls;     // 跳ね返る壁の線 (WallSegments)。null なら壁を見ない
        public bool Hidden;       // 動き出すまで隠している (表示の切り替えは変わった時だけ書く)
        public bool Steer;        // 大きな瓦礫: ホストが決めた所 (Tx, Ty) へ放物線で飛んでそこで止まる (壁は見ない)
        public float Tx, Ty;
        public float Rest;        // 止まる高さ (瓦礫の山の上に乗る。0 = 床)
        public float PileX, PileY, PileRx, PileRy, PileH; // 落ちた所の山 (PileH = 0 なら山なし)
        public float Flip, VFlip; // 金属板: 空中で裏返る (横幅を cos で縮める)
        public float Roll;        // パイプ: 床を転がる (半径。0 = 転がらない)
        public int Bounces;
    }

    private static readonly List<Item> Items = new();
    private static Sprite _flash;
    private static bool _flashSearched;
    private static long _lastMs;

    private const float Gravity = 9f;
    // 壁の面の高さ (3/4 視点で、壁の上面は床よりこれだけ上に描かれている)。崩れた塊はこの高さまでしか落ちない
    private const float WallHeight = 0.85f;
    // 床に落ちた塊は寝る: 縦をこの割合に、横をこの割合に縮める (重なって 1 枚の面に見えないよう、隙間が空く程度に小さく)
    private const float LieFlatY = 0.45f, LieFlatX = 0.75f;
    // 壁に当たった時: 壁に垂直な速さはこの割合で跳ね返り、沿う速さはこの割合に減る
    private const float WallBounce = 0.35f, WallSlide = 0.7f;

    // ── 発生 ───────────────────────────────────────────────────────────

    // 打撃で壁が崩れる: 壁の区間に沿って塊が上から落ちて山になり、根元に土煙。
    // axis = 抜けた向き (振った向き)・force = 振りの強さ。強いほど山が向こう側へ押し出される。
    // pieces = 壁の絵を割った塊 (元の場所に重なっている)。叩いた所から順に、壁の根元へ落ちて向こうへ寄る
    // from = 叩いた側の床 (塊の落ちる先は、ここから残った壁を越えずに届く所)。
    // given = 大きな瓦礫の止まる所 (客: ホストから届いた物 / null = ここで決める)。返り値 = 置いた瓦礫
    public static RubbleLanding[] Crumble(Vector2 center, Vector2 tangent, Vector2 normal, Vector2 axis, float force, float length, ushort seed,
        List<BreakPiece> pieces, List<Vector2> segs, float[] walls, Vector2 from, RubbleLanding[] given)
    {
        var rnd = new System.Random(seed ^ Hash(center));
        Vector2 hit = center;
        center += axis * (force * 0.3f);
        int made = 0;
        var byRank = new Item[pieces?.Count ?? 0];
        if (pieces != null)
            for (int rank = 0; rank < pieces.Count; rank++)
            {
                var p = pieces[rank];
                Vector2 ground = Ground(p.Origin, segs, walls, from, out float h);
                // 崩れて落ちた先: 根元から振った向きへ少し (強いほど遠く)・壁に沿ってわずかに散る
                Vector2 rest = ground + axis * (0.1f + (float)rnd.NextDouble() * 0.5f + force * 0.3f)
                                      + tangent * (((float)rnd.NextDouble() - 0.5f) * 0.6f);
                var it = AddPiece(p, ground, h, walls);
                if (it == null) continue;
                // 高い所からは落ちるのに掛かる時間で、低い所 (横の壁) は小さく跳ねて、落ちた先へ滑る
                it.VH = h > 0.05f ? 0f : 0.9f;
                float flight = h > 0.05f ? MathF.Sqrt(2f * h / Gravity) : 2f * it.VH / Gravity;
                float k = 1f / (flight + 0.15f);
                it.Vx = (rest.x - ground.x) * k;
                it.Vy = (rest.y - ground.y) * k;
                it.VRot = ((float)rnd.NextDouble() - 0.5f) * 80f;
                it.T = -(0.04f + (p.Origin - hit).magnitude * 0.12f + (float)rnd.NextDouble() * 0.08f);
                byRank[rank] = it;
                made++;
            }
        int chunks = made >= 6 ? 4 : 16; // 絵の塊が出ない壁 (横の壁は枠線だけ) は手続きの塊で崩す
        for (int k = 0; k < chunks; k++)
        {
            float along = ((float)rnd.NextDouble() - 0.5f) * length;
            float across = ((float)rnd.NextDouble() - 0.4f) * 0.45f; // 壁の線に寄せて山にする (叩いた側の反対へ少し多め)
            Vector2 rest = center + tangent * along - normal * across;
            float size = 0.24f + (float)rnd.NextDouble() * 0.22f;
            var it = Spawn(Kind.Fall, DebrisArt.Chunk(rnd.Next()), rest, size, keep: true);
            it.Height = 0.35f + (float)rnd.NextDouble() * 0.45f; // 壁の高さから落ちる
            it.T = -(float)rnd.NextDouble() * 0.25f;            // 崩れ始めをずらす
            it.Rot = (float)rnd.NextDouble() * 360f;
            it.VRot = ((float)rnd.NextDouble() - 0.5f) * 200f;
        }
        for (int k = 0; k < 22; k++)
        {
            Vector2 rest = center + tangent * (((float)rnd.NextDouble() - 0.5f) * length * 1.2f) - normal * (((float)rnd.NextDouble() - 0.4f) * 0.9f);
            var it = Spawn(Kind.Fall, DebrisArt.Pebble, rest, 0.06f + (float)rnd.NextDouble() * 0.07f, keep: true);
            it.Height = 0.2f + (float)rnd.NextDouble() * 0.5f;
            it.T = -(float)rnd.NextDouble() * 0.35f;
        }
        for (int k = 0; k < 7; k++)
            Dust(center + tangent * (((float)rnd.NextDouble() - 0.5f) * length), rnd, 0.55f, 1.3f);
        // 船の中の瓦礫: 壁の根元に山 (真ん中ほど高く積もる) を作り、金属板・パイプ・配線・ナット・壁の中身を落とす
        var pile = new Pile { X = center.x + axis.x * 0.15f, Y = center.y + axis.y * 0.15f, Rx = length * 0.65f, Ry = 0.4f, H = 0.1f + force * 0.05f };
        Stain(new Vector2(pile.X, pile.Y), length * 1.5f, 0.35f);
        foreach (var it in byRank) if (it != null) SetPile(it, pile);
        foreach (var (art, count, size) in CrumbleJunk)
            for (int k = 0; k < count; k++)
            {
                Vector2 src = hit + tangent * (((float)rnd.NextDouble() - 0.5f) * length);
                // 3 つに 2 つは振った向きへ (穴の中と向こう)、残りは叩いた側へこぼれる
                Vector2 to = rnd.NextDouble() < 0.66
                    ? axis * (0.15f + (float)rnd.NextDouble() * 0.6f + force * 0.4f)
                    : -axis * (0.1f + (float)rnd.NextDouble() * 0.35f);
                to += tangent * (((float)rnd.NextDouble() - 0.5f) * 0.5f);
                var it = Junk(art, rnd, src, size, walls, pile);
                it.Height = 0.15f + (float)rnd.NextDouble() * 0.6f;
                float flight = MathF.Sqrt(2f * it.Height / Gravity) + 0.25f;
                it.Vx = to.x / flight; it.Vy = to.y / flight;
                it.VH = 0.4f + (float)rnd.NextDouble() * 1.2f;
                it.T = -(0.03f + (float)rnd.NextDouble() * 0.35f);
            }
        return Settle(byRank, pieces, walls, given);
    }

    // 打撃が当たったが崩れない: 小さな土煙と、欠けた小石が少しこぼれる
    public static void Chip(Vector2 at, Vector2 dir, ushort seed)
    {
        var rnd = new System.Random(seed ^ Hash(at));
        for (int k = 0; k < 4; k++)
        {
            Vector2 rest = at - dir * (0.05f + (float)rnd.NextDouble() * 0.3f) + new Vector2(0f, ((float)rnd.NextDouble() - 0.5f) * 0.3f);
            var it = Spawn(Kind.Fall, DebrisArt.Pebble, rest, 0.06f + (float)rnd.NextDouble() * 0.05f, keep: true);
            it.Height = 0.15f + (float)rnd.NextDouble() * 0.3f;
        }
        Dust(at - dir * 0.1f, rnd, 0.35f, 0.8f);
    }

    // 剥げかけの表面が欠け落ちる: 細胞の絵 (元の場所に重なっている) が根元 (baseY) へ落ちて手前へ転がる
    public static void DropPeel(BreakPiece p, float baseY, Vector2 dir, int seed)
    {
        var rnd = new System.Random(seed);
        float h = Math.Max(0f, p.Origin.y - baseY);
        var it = AddPiece(p, new Vector2(p.Origin.x, p.Origin.y - h), h, null);
        if (it == null) return;
        float flight = MathF.Sqrt(2f * h / Gravity) + 0.15f;
        it.Vx = ((float)rnd.NextDouble() - 0.5f) * 0.3f / flight;
        it.Vy = -dir.y * (0.15f + (float)rnd.NextDouble() * 0.2f) / flight; // 叩いた側へ
        it.VRot = ((float)rnd.NextDouble() - 0.5f) * 120f;
        it.T = -(0.02f + (float)rnd.NextDouble() * 0.06f);
    }

    // 爆発: 本編の爆発の絵が一瞬 → 塊が外へ飛んで散らばる → 火花と煙。
    // 向きに偏った爆発 (force > 0) は塊と火花も向きの先へ多く飛ぶ
    public static RubbleLanding[] Explosion(Vector2 c, float radius, Vector2 dir, float force, ushort seed,
        List<BreakPiece> pieces, List<Vector2> segs, float[] walls, RubbleLanding[] given)
    {
        var rnd = new System.Random(seed ^ Hash(c));
        int made = 0;
        var byRank = new Item[pieces?.Count ?? 0];
        if (pieces != null)
            for (int rank = 0; rank < pieces.Count; rank++)
            {
                var p = pieces[rank];
                Vector2 ground = Ground(p.Origin, segs, walls, c, out float h);
                var it = AddPiece(p, ground, h, walls);
                if (it == null) continue;
                Vector2 away = p.Origin - c;
                float m = away.magnitude;
                away = m > 1e-3f ? away / m : new Vector2(MathF.Cos(seed), MathF.Sin(seed));
                // 爆心に近い塊ほど速く飛ぶ
                float sp = radius * (1.5f + (float)rnd.NextDouble() * 2f) * Math.Clamp(1.4f - m / (radius * 2f), 0.4f, 1.4f);
                it.Vx = (away.x + dir.x * force) * sp;
                it.Vy = (away.y + dir.y * force) * sp;
                it.VH = 1f + (float)rnd.NextDouble() * 1.5f;
                it.VRot = ((float)rnd.NextDouble() - 0.5f) * 300f;
                byRank[rank] = it;
                made++;
            }
        var flash = FlashSprite();
        if (flash)
        {
            var f = Spawn(Kind.Flash, flash, c, radius * 1.6f, keep: false);
            f.Life = 0.45f;
            f.Z -= 0.01f;
        }
        int chunks = made >= 6 ? 4 : 12;
        for (int k = 0; k < chunks; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            float sp = radius * (2.5f + (float)rnd.NextDouble() * 3f);
            var it = Spawn(Kind.Fly, DebrisArt.Chunk(rnd.Next()), c, 0.12f + (float)rnd.NextDouble() * 0.14f, keep: true);
            it.Vx = (MathF.Cos(ang) + dir.x * force) * sp;
            it.Vy = (MathF.Sin(ang) + dir.y * force) * sp;
            it.Walls = walls;
            it.VH = 1.5f + (float)rnd.NextDouble() * 2f;
            it.VRot = ((float)rnd.NextDouble() - 0.5f) * 720f;
        }
        // 船の中の瓦礫が外へ飛び散る (爆発は山にしない)
        foreach (var (art, count, size) in BlastJunk)
            for (int k = 0; k < count; k++)
            {
                float ang = (float)(rnd.NextDouble() * Math.PI * 2);
                float sp = radius * (1.8f + (float)rnd.NextDouble() * 3.2f);
                var it = Junk(art, rnd, c, size, walls, default);
                it.Vx = (MathF.Cos(ang) + dir.x * force) * sp;
                it.Vy = (MathF.Sin(ang) + dir.y * force) * sp;
                it.Height = 0.1f;
                it.VH = 1.5f + (float)rnd.NextDouble() * 2.5f;
                it.T = -(float)rnd.NextDouble() * 0.06f;
            }
        Stain(c, radius * 1.6f, 0.2f);
        for (int k = 0; k < 18; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            float sp = 3f + (float)rnd.NextDouble() * 4f;
            var it = Spawn(Kind.Spark, DebrisArt.Spark, c, 0.08f + (float)rnd.NextDouble() * 0.06f, keep: false);
            it.Vx = (MathF.Cos(ang) + dir.x * force) * sp;
            it.Vy = (MathF.Sin(ang) + dir.y * force) * sp;
            it.VH = 1f + (float)rnd.NextDouble() * 2f;
            it.Life = 0.35f + (float)rnd.NextDouble() * 0.5f;
        }
        for (int k = 0; k < 6; k++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            Dust(c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radius * 0.5f * (float)rnd.NextDouble(), rnd, radius * 0.6f, 1.3f);
        }
        return Settle(byRank, pieces, walls, given);
    }

    // 大きな瓦礫: ホストは割れた塊の止まる所を先に計算して置ける物を選び (RubbleBlocks.Decide)、
    // 全員がその塊をその所へ飛ばして当たり判定を置く。客の塊の並びがホストと違っても当たり判定はホストの所に置く
    private const int PredictRanks = 8; // 止まる所を予測する塊の数 (大きい順)

    private static RubbleLanding[] Settle(Item[] byRank, List<BreakPiece> pieces, float[] walls, RubbleLanding[] given)
    {
        var landings = given;
        if (landings == null)
        {
            if (walls == null || byRank.Length == 0) return Array.Empty<RubbleLanding>();
            int n = Math.Min(byRank.Length, PredictRanks);
            var rest = new Vector2?[n];
            var sizes = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (byRank[i] == null) continue;
                rest[i] = PredictRest(byRank[i]);
                sizes[i] = pieces[i].Size;
            }
            landings = RubbleBlocks.Decide(rest, sizes, walls);
        }
        foreach (var l in landings)
            if (l.Rank < byRank.Length && byRank[l.Rank] != null) Steer(byRank[l.Rank], l.Position);
        RubbleBlocks.Place(landings);
        return landings;
    }

    // 塊の止まる所を、同じ動き (固定の 1/60 秒刻み) で先に計算する。絵には触らない
    private static Vector2 PredictRest(Item src)
    {
        var it = new Item
        {
            Kind = Kind.Piece, Px = src.Px, Py = src.Py, Vx = src.Vx, Vy = src.Vy, Height = src.Height, VH = src.VH,
            H0 = src.H0, Walls = src.Walls, Life = 1f,
        };
        const float dt = 1f / 60f;
        for (int step = 0; step < 600; step++)
        {
            it.T += dt;
            float ox = it.Px, oy = it.Py;
            bool done = StepPiece(it, dt);
            if (it.Walls != null && Collide(it, ox, oy)) done = false;
            if (done) break;
        }
        return new Vector2(it.Px, it.Py);
    }

    // 止まる所が決まった塊: 今の高さと上向きの速さから床に着くまでの時間を出し、その時間でちょうど着く横の速さにする
    private static void Steer(Item it, Vector2 target)
    {
        it.Steer = true;
        it.Walls = null;
        it.Tx = target.x; it.Ty = target.y;
        float h = Math.Max(0f, it.Height), vh = it.VH;
        if (h < 0.05f && vh < 1.2f) vh = 1.2f; // 低い所の塊も少し跳ねて飛ぶ
        float tf = (vh + MathF.Sqrt(vh * vh + 2f * Gravity * h)) / Gravity;
        if (tf < 0.3f)
        {
            tf = 0.3f;
            vh = (0.5f * Gravity * tf * tf - h) / tf;
        }
        it.VH = vh;
        it.Vx = (target.x - it.Px) / tf;
        it.Vy = (target.y - it.Py) / tf;
    }

    // 塊の元の場所の真下の床 (3/4 視点)。壁の面 (手前の根元の線から上) は根元まで、壁の上面とその奥は
    // 壁の高さ (WallHeight) だけ下へ落ちる。根元の線 = 切った区間のうち、真下を横切る一番下の線。
    // 真下に線が無い (縦の壁) 時は壁の高さの半分。
    // そうして決めた床が、from (爆心・叩いた側) から残った壁を越えた先 (壊れていない壁の上面や向こう) になる時は、
    // その壁の手前に落とす (壊れていない壁の面の絵から切った塊が壁の中に落ち、そこから向こうへ飛んでいくのを防ぐ)
    private static Vector2 Ground(Vector2 o, List<Vector2> segs, float[] walls, Vector2 from, out float height)
    {
        float baseY = float.MaxValue;
        if (segs != null)
            for (int k = 0; k + 1 < segs.Count; k += 2)
            {
                Vector2 a = segs[k], b = segs[k + 1];
                float lo = Math.Min(a.x, b.x), hi = Math.Max(a.x, b.x);
                if (o.x < lo - 0.05f || o.x > hi + 0.05f || hi - lo < 1e-4f) continue;
                float y = a.y + (b.y - a.y) * Math.Clamp((o.x - a.x) / (b.x - a.x), 0f, 1f);
                if (y <= o.y + 0.05f && y < baseY) baseY = y;
            }
        height = baseY == float.MaxValue ? WallHeight * 0.5f : Math.Clamp(o.y - baseY, 0f, WallHeight);
        float gx = o.x, gy = o.y - height;
        if (walls != null)
        {
            float dx = gx - from.x, dy = gy - from.y;
            float t = FirstCross(from.x, from.y, dx, dy, walls, out _, out _);
            if (t <= 1f)
            {
                float len = MathF.Sqrt(dx * dx + dy * dy);
                t = Math.Max(0f, t - 0.03f / Math.Max(len, 1e-4f));
                gx = from.x + dx * t; gy = from.y + dy * t;
                height = Math.Max(0f, o.y - gy);
            }
        }
        return new Vector2(gx, gy);
    }

    // 元の場所に重なっている塊を動かし始める (遅れて動き出す間も消さない: 消すとその間だけ穴が見える)
    private static Item AddPiece(BreakPiece p, Vector2 ground, float height, float[] walls)
    {
        if (!p.Tr) return null;
        Vector3 sc = p.Tr.localScale;
        var it = new Item
        {
            Kind = Kind.Piece, Tr = p.Tr, Sr = p.Sr, Px = ground.x, Py = ground.y, Height = height, H0 = height,
            Z = p.Tr.position.z, Life = 1f, Sx = sc.x, Sy = sc.y, Sz = sc.z, Walls = walls,
        };
        Items.Add(it);
        return it;
    }

    // 瓦礫の山: (X, Y) を中心に、横 Rx・縦 Ry の楕円の中ほど高く (最高 H) 積もる
    private struct Pile { public float X, Y, Rx, Ry, H; }

    private enum JunkArt { Plate, Pipe, Wire, Nut, Core, Grit }

    // (絵, 数, 大きさ) 打撃で崩れた時 / 爆発
    private static readonly (JunkArt Art, int Count, float Size)[] CrumbleJunk =
        { (JunkArt.Plate, 3, 0.26f), (JunkArt.Pipe, 2, 0.26f), (JunkArt.Wire, 3, 0.2f), (JunkArt.Nut, 4, 0.06f), (JunkArt.Core, 4, 0.16f), (JunkArt.Grit, 14, 0.07f) };
    private static readonly (JunkArt Art, int Count, float Size)[] BlastJunk =
        { (JunkArt.Plate, 4, 0.26f), (JunkArt.Pipe, 2, 0.26f), (JunkArt.Wire, 5, 0.2f), (JunkArt.Nut, 6, 0.06f), (JunkArt.Core, 5, 0.16f), (JunkArt.Grit, 18, 0.07f) };

    private static Item Junk(JunkArt art, System.Random rnd, Vector2 at, float size, float[] walls, Pile pile)
    {
        float s = size * (0.7f + (float)rnd.NextDouble() * 0.6f);
        Sprite sp = art switch
        {
            JunkArt.Plate => DebrisArt.Plate(rnd.Next()),
            JunkArt.Pipe => DebrisArt.Pipe(rnd.Next()),
            JunkArt.Wire => DebrisArt.Wire(rnd.Next()),
            JunkArt.Nut => DebrisArt.Nut,
            JunkArt.Core => DebrisArt.Core(rnd.Next()),
            _ => rnd.NextDouble() < 0.5 ? DebrisArt.Pebble : DebrisArt.Core(rnd.Next()),
        };
        var it = Spawn(Kind.Junk, sp, at, s, keep: true);
        it.Walls = walls;
        it.Rot = (float)rnd.NextDouble() * 360f;
        it.Tr.rotation = RotZ(it.Rot);
        it.VRot = ((float)rnd.NextDouble() - 0.5f) * (art == JunkArt.Plate ? 500f : 900f);
        if (art == JunkArt.Plate) it.VFlip = (rnd.NextDouble() < 0.5 ? -1f : 1f) * (8f + (float)rnd.NextDouble() * 8f);
        if (art == JunkArt.Pipe) it.Roll = s * 0.15f;
        SetPile(it, pile);
        return it;
    }

    private static void SetPile(Item it, Pile p)
    {
        it.PileX = p.X; it.PileY = p.Y; it.PileRx = p.Rx; it.PileRy = p.Ry; it.PileH = p.H;
    }

    // 落ちた所の山の高さ (山の上に乗って止まる)
    private static float PileHeight(Item it)
    {
        if (it.PileH <= 0f) return 0f;
        float dx = (it.Px - it.PileX) / it.PileRx, dy = (it.Py - it.PileY) / it.PileRy;
        float k = 1f - dx * dx - dy * dy;
        return k > 0f ? it.PileH * k : 0f;
    }

    // 床の土埃の染み (瓦礫の下・床のすぐ上)
    private static void Stain(Vector2 at, float width, float delay)
    {
        var it = Spawn(Kind.Fall, DebrisArt.Stain, at, width, keep: true);
        it.Z += 0.003f;
        it.T = -delay;
    }

    private static void Dust(Vector2 at, System.Random rnd, float size, float life)
    {
        var it = Spawn(Kind.Puff, DebrisArt.Puff, at, size, keep: false);
        it.S0 = size * 0.4f;
        it.S1 = size * (1f + (float)rnd.NextDouble() * 0.4f);
        it.Life = life * (0.8f + (float)rnd.NextDouble() * 0.4f);
        it.VH = 0.4f + (float)rnd.NextDouble() * 0.4f;
        it.Vx = ((float)rnd.NextDouble() - 0.5f) * 0.6f;
        it.Z -= 0.005f; // 塊より手前
    }

    private static Item Spawn(Kind kind, Sprite sprite, Vector2 pos, float worldSize, bool keep)
    {
        var go = new GameObject("MrpDebris");
        var tr = go.transform;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        if (DamageMap.PropMaterial) sr.sharedMaterial = DamageMap.PropMaterial;
        float z = DamageMap.FrontZ(pos) - 0.004f;
        tr.position = new Vector3(pos.x, pos.y, z);
        float sw = Math.Max(0.0001f, sprite.bounds.size.x);
        float s = worldSize / sw;
        tr.localScale = new Vector3(s, s, 1f);
        if (keep) DamageMap.Track(go);
        var it = new Item { Kind = kind, Tr = tr, Sr = sr, Px = pos.x, Py = pos.y, S0 = s, S1 = s, Z = z, Life = 1f, SpriteW = sw };
        Items.Add(it);
        return it;
    }

    private static Sprite FlashSprite()
    {
        if (_flashSearched) return _flash;
        _flashSearched = true;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var sp = o.TryCast<Sprite>();
            if (sp && sp.name == "weapons_explosion") { _flash = sp; break; }
        }
        return _flash;
    }

    // ── 更新 ───────────────────────────────────────────────────────────

    // テスト用: 動きを止める (止めている間の時間は飛ばす)
    public static bool Paused;

    public static void Tick()
    {
        if (Items.Count == 0 || Paused) { _lastMs = 0; return; }
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.05f, (now - _lastMs) / 1000f);
        _lastMs = now;

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var it = Items[i];
            if (!it.Tr) { Items.RemoveAt(i); continue; }
            it.T += dt;
            if (it.T < 0f)
            {
                if (it.Kind != Kind.Piece && !it.Hidden) { it.Sr.enabled = false; it.Hidden = true; }
                continue;
            }
            if (it.Hidden) { it.Sr.enabled = true; it.Hidden = false; }

            float ox = it.Px, oy = it.Py;
            bool done = it.Kind switch
            {
                Kind.Fall => StepFall(it, dt),
                Kind.Fly => StepFly(it, dt),
                Kind.Puff => StepPuff(it, dt),
                Kind.Spark => StepSpark(it, dt),
                Kind.Piece => StepPiece(it, dt),
                Kind.Junk => StepJunk(it, dt),
                _ => StepFlash(it),
            };
            if (it.Walls != null && Collide(it, ox, oy)) done = false;

            it.Tr.position = V3(it.Px, it.Py + it.Height, it.Z);
            if (it.VRot != 0f) it.Tr.rotation = RotZ(it.Rot);

            if (done)
            {
                Items.RemoveAt(i);
                if (it.Kind is Kind.Puff or Kind.Spark or Kind.Flash) UnityEngine.Object.Destroy(it.Tr.gameObject);
                else
                {
                    // 山の上に乗った物ほど手前 (上に積もって見える)
                    if (it.Rest > 0f) { it.Z -= it.Rest * 0.02f; it.Tr.position = V3(it.Px, it.Py + it.Height, it.Z); }
                    RubbleBake.Add(it.Tr, it.Sr, it.Z, it.Kind == Kind.Piece); // 止まった瓦礫は床の板へ焼く
                }
            }
        }
    }

    private static bool StepFall(Item it, float dt)
    {
        it.VH -= Gravity * dt;
        it.Height += it.VH * dt;
        it.Rot += it.VRot * dt;
        if (it.Height > 0f) return false;
        // 床に着いたら 1 回だけ小さく跳ねて止まる
        if (it.VH < -1.2f) { it.Height = 0f; it.VH = -it.VH * 0.25f; it.VRot *= 0.4f; return false; }
        it.Height = 0f; it.VRot = 0f;
        return true;
    }

    // 割れた塊: 横へ動きながら落ち、床で小さく跳ねてから滑って止まる。落ちるにつれて床に寝る (縦が縮む)
    private static bool StepPiece(Item it, float dt)
    {
        it.Px += it.Vx * dt; it.Py += it.Vy * dt;
        it.Rot += it.VRot * dt;
        it.VH -= Gravity * dt;
        it.Height += it.VH * dt;
        float lie = it.H0 > 0.05f ? Math.Clamp(1f - it.Height / it.H0, 0f, 1f) : Math.Clamp(it.T / 0.25f, 0f, 1f);
        if (it.Height <= it.Rest) lie = 1f;
        if (lie != it.Lie && it.Tr is not null) // 予測 (PredictRest) の時は絵が無い
        {
            it.Lie = lie;
            it.Tr.localScale = V3(it.Sx * (1f - (1f - LieFlatX) * lie), it.Sy * (1f - (1f - LieFlatY) * lie), it.Sz);
        }
        if (!it.Steer) it.Rest = PileHeight(it);
        if (it.Height > it.Rest) return false;
        it.Height = it.Rest;
        if (it.Steer)
        {
            // 決まった所にぴたりと止める (当たり判定の位置と見た目を揃える)
            it.Px = it.Tx; it.Py = it.Ty;
            it.Vx = 0f; it.Vy = 0f; it.VH = 0f; it.VRot = 0f;
            return true;
        }
        if (it.VH < -1.2f) { it.VH = -it.VH * 0.2f; it.Vx *= 0.5f; it.Vy *= 0.5f; it.VRot *= 0.4f; return false; }
        it.VH = 0f;
        float f = MathF.Max(0f, 1f - 6f * dt);
        it.Vx *= f; it.Vy *= f;
        it.VRot *= f;
        if (it.Vx * it.Vx + it.Vy * it.Vy > 0.0004f) return false;
        it.VRot = 0f;
        return true;
    }

    private static bool StepFly(Item it, float dt)
    {
        it.Px += it.Vx * dt; it.Py += it.Vy * dt;
        float f = MathF.Max(0f, 1f - 3.5f * dt); // 床をこすって減速
        it.Vx *= f; it.Vy *= f;
        it.VH -= Gravity * dt;
        it.Height = MathF.Max(0f, it.Height + it.VH * dt);
        it.Rot += it.VRot * dt;
        it.VRot *= MathF.Max(0f, 1f - 3f * dt);
        if (it.Vx * it.Vx + it.Vy * it.Vy > 0.01f || it.Height > 0f) return false;
        it.VRot = 0f;
        return true;
    }

    // 船の中の瓦礫: 何度か跳ねて (跳ね返り 0.38)、床をこすって止まる。金属板は空中で裏返り、パイプは床を転がる。
    // 落ちた所に山があれば、その高さで止まる
    private static bool StepJunk(Item it, float dt)
    {
        it.Px += it.Vx * dt; it.Py += it.Vy * dt;
        it.VH -= Gravity * dt;
        it.Height += it.VH * dt;
        it.Rot += it.VRot * dt;
        it.Rest = PileHeight(it);
        if (it.VFlip != 0f)
        {
            it.Flip += it.VFlip * dt;
            float cx = MathF.Cos(it.Flip);
            if (cx > -0.15f && cx < 0.15f) cx = cx < 0f ? -0.15f : 0.15f; // 真横でも線 1 本は残す
            it.Tr.localScale = V3(it.S0 * cx, it.S0, 1f);
        }
        if (it.Height > it.Rest) return false;
        it.Height = it.Rest;
        if (it.VH < -0.8f && it.Bounces < 4)
        {
            it.Bounces++;
            it.VH = -it.VH * 0.38f;
            it.Vx *= 0.6f; it.Vy *= 0.6f;
            it.VRot *= -0.5f;
            it.VFlip *= 0.5f;
            return false;
        }
        it.VH = 0f;
        if (it.VFlip != 0f)
        {
            // 表か裏で寝る
            it.VFlip = 0f;
            it.Flip = MathF.Round(it.Flip / MathF.PI) * MathF.PI;
            it.Tr.localScale = V3(it.S0 * MathF.Cos(it.Flip), it.S0, 1f);
        }
        float f = MathF.Max(0f, 1f - (it.Roll > 0f ? 1.6f : 5f) * dt);
        it.Vx *= f; it.Vy *= f;
        // 転がる物は進んだ分だけ回る。他は床でこすれて回りが止まる
        it.VRot = it.Roll > 0f ? -it.Vx / it.Roll * 57.29578f : it.VRot * f;
        if (it.Vx * it.Vx + it.Vy * it.Vy > 0.0009f) return false;
        it.VRot = 0f;
        return true;
    }

    private static bool StepPuff(Item it, float dt)
    {
        float k = it.T / it.Life;
        float s = it.S0 + (it.S1 - it.S0) * (1f - (1f - k) * (1f - k));
        it.Tr.localScale = V3(s / it.SpriteW, s / it.SpriteW, 1f);
        it.Height += it.VH * dt;
        it.Px += it.Vx * dt; it.Py += it.Vy * dt;
        var c = it.Sr.color; c.a = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f; it.Sr.color = c;
        return k >= 1f;
    }

    private static bool StepSpark(Item it, float dt)
    {
        it.Px += it.Vx * dt; it.Py += it.Vy * dt;
        float f = MathF.Max(0f, 1f - 2f * dt);
        it.Vx *= f; it.Vy *= f;
        it.VH -= Gravity * dt;
        it.Height = MathF.Max(0f, it.Height + it.VH * dt);
        var c = it.Sr.color; c.a = 1f - it.T / it.Life; it.Sr.color = c;
        return it.T >= it.Life;
    }

    private static bool StepFlash(Item it)
    {
        float k = it.T / it.Life;
        float grow = k < 0.25f ? 0.6f + k / 0.25f * 0.5f : 1.1f + (k - 0.25f) * 0.2f;
        it.Tr.localScale = V3(it.S0 * grow, it.S0 * grow, 1f);
        var c = it.Sr.color; c.a = k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f; it.Sr.color = c;
        return k >= 1f;
    }

    // 床の上を (ox, oy) から今の位置まで動いた間に壁の線を横切ったら、当たった所で止めて跳ね返す。
    // 壁は高さに関係なく塞ぐ (3/4 視点で壁の上を飛び越えて見えるのは不自然)。
    // 動き出した時に線の上にいる (壁の根元から落ちた塊) は、その線を横切ったとみなさない
    private static bool Collide(Item it, float ox, float oy)
    {
        float dx = it.Px - ox, dy = it.Py - oy;
        if (dx * dx + dy * dy < 1e-10f) return false;
        float bestT = FirstCross(ox, oy, dx, dy, it.Walls, out float bex, out float bey);
        if (bestT > 1f) return false;
        float len = MathF.Sqrt(bex * bex + bey * bey);
        float nx = -bey / len, ny = bex / len;
        if (nx * dx + ny * dy > 0f) { nx = -nx; ny = -ny; } // 来た側へ向ける
        it.Px = ox + dx * bestT + nx * 0.02f;
        it.Py = oy + dy * bestT + ny * 0.02f;
        float vn = it.Vx * nx + it.Vy * ny;
        if (vn < 0f)
        {
            float tx = it.Vx - vn * nx, ty = it.Vy - vn * ny;
            it.Vx = tx * WallSlide - vn * WallBounce * nx;
            it.Vy = ty * WallSlide - vn * WallBounce * ny;
            it.VRot *= -0.6f;
        }
        return true;
    }

    // (ox, oy) から (ox + dx, oy + dy) までの間で最初に横切る壁の線の割合 (無ければ 2) と、その線の向き。
    // 始めの点が線の上にある線は横切ったとみなさない
    private static float FirstCross(float ox, float oy, float dx, float dy, float[] w, out float bex, out float bey)
    {
        float bestT = 2f;
        bex = 0f; bey = 0f;
        for (int k = 0; k + 3 < w.Length; k += 4)
        {
            float ax = w[k], ay = w[k + 1], ex = w[k + 2] - ax, ey = w[k + 3] - ay;
            float den = dx * ey - dy * ex;
            if (den > -1e-9f && den < 1e-9f) continue;
            float wx = ax - ox, wy = ay - oy;
            float t = (wx * ey - wy * ex) / den, s = (wx * dy - wy * dx) / den;
            if (t < 0f || t > 1f || s < 0f || s > 1f || t >= bestT) continue;
            float cr = wx * ey - wy * ex; // 始めの点から線までの距離 × 線の長さ
            if (cr * cr < 0.005f * 0.005f * (ex * ex + ey * ey)) continue;
            bestT = t; bex = ex; bey = ey;
        }
        return bestT;
    }

    private static Vector3 V3(float x, float y, float z)
    {
        Vector3 v = default;
        v.x = x; v.y = y; v.z = z;
        return v;
    }

    private static Quaternion RotZ(float deg)
    {
        float h = deg * (MathF.PI / 360f);
        Quaternion q = default;
        q.z = MathF.Sin(h); q.w = MathF.Cos(h);
        return q;
    }

    private static int Hash(Vector2 p) => (int)(p.x * 73856.093f) ^ (int)(p.y * 19349.663f);

    public static void Clear() => Items.Clear();
}
