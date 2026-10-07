using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 叩くと物ごと壊れる家具 (自分の絵と当たり判定を持ち、表に載った物だけ)。
// 壁のように当たり判定を削らず、耐久が尽きると当たり判定を全部止め、絵を割った欠片にして飛ばし、歩ける所の地図に足す。
// 壁の切り取り・ひびからは守る物として外す (TerrainDamage.IsProtected)。
// 打撃はホストが「当たった点・残りの耐久」を決めて配る。結果の厚み (Size) が 0 の打撃が家具への打撃
// (壁への打撃の厚みは必ず BreachDepth 以上)。受け手は当たった点に掛かる家具を引く。
// 爆発は全員が同じ形と家具の範囲から決める。電文は増やさない。家具に付いた端末 (Console) はその場に残して使える
internal static class BreakableProps
{
    private const int ShipLayer = 9;
    private const float FindRadius = 0.12f; // 受け手が当たった点から家具を引く半径 (点は家具の当たり判定の縁にある)
    private const float CarveMargin = 0.15f; // 壊れた家具の範囲の外へこれだけ広く歩ける所にする (地図は壁を太らせて作るため)

    private readonly struct Spec
    {
        public readonly int Hits;   // 強い振り (力 0.8 以上) で何発で壊れるか (弱い振りはその倍)
        public readonly bool Wood;
        public readonly int Pieces; // 絵を割る欠片の数
        public readonly int Sparks;
        public readonly float Pitch; // 壊れた音・叩いた音の高さの倍率
        public readonly int Cols;    // 0 より大きい = 板の継ぎ目に沿って縦 Cols × 横 (Pieces / Cols) の升に割る (0 = 散らした点の細胞)

        public Spec(int hits, bool wood, int pieces, int sparks, float pitch, int cols = 0)
        {
            Hits = hits; Wood = wood; Pieces = pieces; Sparks = sparks; Pitch = pitch; Cols = cols;
        }
    }

    // キー = 船の名前/親の名前/家具の名前 (FurnitureKinds と同じ)
    private static readonly Dictionary<string, Spec> Table = new()
    {
        ["FungleShip/JungleObstacles/MetalPlate"] = new(2, false, 6, 12, 0.8f), // 密林の金属の板
        ["FungleShip/OutsideBeach/Bonfire-tube1"] = new(1, true, 5, 0, 1f),    // たき火の上の丸太 (FurnitureSplit で分けた物)
        ["FungleShip/OutsideBeach/Bonfire-tube2"] = new(1, true, 5, 0, 1f),    // たき火の下の丸太
        ["PolusShip/LifeSupport/storage_middlewall"] = new(3, false, 8, 6, 0.7f, 4), // 生命維持室の真ん中の仕切り (ごみの小屋)
    };

    private static readonly Color WoodDust = new(0.9f, 0.78f, 0.88f, 1f); // 丸太の中の紫の粉

    private sealed class Prop
    {
        public string Name;
        public Spec Spec;
        public SpriteRenderer Sr;
        public readonly List<Collider2D> Cols = new();
        public readonly List<SpriteRenderer> Renderers = new();
        public float X0, Y0, X1, Y1; // 動きの層の当たり判定の範囲
        public int Hp;
        public bool Broken;
    }

    private static readonly List<Prop> Props = new();
    private static readonly Dictionary<int, int> ByCollider = new(); // 当たり判定の InstanceID → Props の番号
    private static readonly List<UnityEngine.Object> Owned = new(); // 欠片の Sprite (次の船で消す。止まった欠片は焼いた床の板に残る)
    private static int _gen = -1;
    private static IntPtr _ship;

    private static bool Ensure()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return false;
        if (_gen == GameClock.ShipGen && ship.Pointer == _ship) return true;
        Reset();
        try { Collect(ship); }
        catch (Exception e)
        {
            // 半端に集めた家具は使わない (壊れる家具なしで壁として扱う)
            Plugin.Logger.LogWarning($"breakable props: {e.Message}");
            Props.Clear();
            ByCollider.Clear();
        }
        _gen = GameClock.ShipGen;
        _ship = ship.Pointer;
        return true;
    }

    private static void Collect(ShipStatus ship)
    {
        FurnitureSplit.Apply(ship); // たき火の丸太はたき火の絵から分けた物
        string shipName = FurnitureKinds.ShipName(ship);
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (!sr.enabled || !sr.sprite || !sr.transform.parent) continue;
            if (!Table.TryGetValue(FurnitureKinds.Key(sr.transform, shipName), out var spec)) continue;
            var p = new Prop { Name = sr.name, Spec = spec, Sr = sr, Hp = spec.Hits * DamageProfile.Blunt.StrongDamage };
            p.X0 = p.Y0 = float.MaxValue; p.X1 = p.Y1 = float.MinValue;
            foreach (var c in sr.GetComponentsInChildren<Collider2D>(false))
            {
                if (!c.enabled || c.isTrigger || c.GetComponentInParent<global::Console>()) continue;
                p.Cols.Add(c);
                if (c.gameObject.layer != ShipLayer) continue; // 視界の影の線は範囲に入れない
                var b = c.bounds;
                Vector3 mn = b.min, mx = b.max;
                p.X0 = Math.Min(p.X0, mn.x); p.Y0 = Math.Min(p.Y0, mn.y);
                p.X1 = Math.Max(p.X1, mx.x); p.Y1 = Math.Max(p.Y1, mx.y);
            }
            if (p.X1 < p.X0) continue;
            // 裏地 (家具が消えた跡を埋める絵) は残す
            foreach (var r in sr.GetComponentsInChildren<SpriteRenderer>(false))
                if (!r.name.EndsWith("-patch", StringComparison.Ordinal) && !r.GetComponentInParent<global::Console>()) p.Renderers.Add(r);
            int k = Props.Count;
            Props.Add(p);
            foreach (var c in p.Cols) ByCollider[c.GetInstanceID()] = k;
        }
        if (Props.Count > 0) CollectDecor(ship);
    }

    // 家具の絵の上に貼られた別の飾り (季節の飾りのぬめりなど): 家具の絵の範囲に収まり、奥行きがほぼ同じ小さな絵。家具と一緒に消す
    private static void CollectDecor(ShipStatus ship)
    {
        const float DepthNear = 0.05f, Slack = 0.05f;
        var boxes = new (Vector3 Min, Vector3 Max)[Props.Count];
        for (int i = 0; i < Props.Count; i++) { var pb = Props[i].Sr.bounds; boxes[i] = (pb.min, pb.max); }
        foreach (var r in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (!r.enabled || !r.sprite || r.name.EndsWith("-patch", StringComparison.Ordinal) || r.GetComponentInParent<global::Console>()) continue;
            var b = r.bounds;
            Vector3 mn = b.min, mx = b.max;
            for (int i = 0; i < Props.Count; i++)
            {
                var p = Props[i];
                var (pmn, pmx) = boxes[i];
                if (mn.x < pmn.x - Slack || mn.y < pmn.y - Slack || mx.x > pmx.x + Slack || mx.y > pmx.y + Slack) continue;
                if (Math.Abs((mn.z + mx.z) - (pmn.z + pmx.z)) * 0.5f > DepthNear) continue;
                if ((mx.x - mn.x) * (mx.y - mn.y) > (pmx.x - pmn.x) * (pmx.y - pmn.y) * 0.5f) continue;
                if (r == p.Sr || p.Renderers.Contains(r)) continue;
                p.Renderers.Add(r);
                break;
            }
        }
    }

    // 試合の始めの準備: 家具を集めておく (最初の一撃で家具を探して止まらないように)
    internal static void Warm()
    {
        Ensure();
        DebrisArt.Splinter(0);
    }

    internal static void Reset()
    {
        Props.Clear();
        ByCollider.Clear();
        foreach (var o in Owned) if (o) UnityEngine.Object.Destroy(o);
        Owned.Clear();
        _gen = -1;
        _ship = IntPtr.Zero;
    }

    // 壁の道 (切り取り・ひび・奥の面) から外す当たり判定か
    internal static bool Owns(Component c) => Ensure() && ByCollider.ContainsKey(c.GetInstanceID());

    // ホスト: 打撃の届く所で、壁 (wallHit) より手前の壊れていない家具に当たるか。当たれば結果を決める
    internal static bool TryResolve(in DamageEvent e, Vector2 dir, float reach, bool wall, Vector2 wallHit, out ResolvedDamage r)
    {
        r = default;
        if (!Ensure() || Props.Count == 0) return false;
        float best = float.MaxValue;
        int idx = -1;
        Vector2 point = default, normal = default;
        foreach (var h in Physics2D.CircleCastAll(e.Position, 0.1f, dir, reach, 1 << ShipLayer))
        {
            if (!h.collider || h.collider.isTrigger || h.distance >= best) continue;
            if (!ByCollider.TryGetValue(h.collider.GetInstanceID(), out int k) || Props[k].Broken) continue;
            best = h.distance; idx = k; point = h.point; normal = h.normal;
        }
        if (idx < 0) return false;
        if (wall && (wallHit - e.Position).sqrMagnitude < (point - e.Position).sqrMagnitude) return false;
        var p = DamageProfile.Blunt;
        int damage = TerrainWire.QForce(e.Force) >= p.StrongForce ? p.StrongDamage : p.WallDamage;
        int hp = Math.Max(Props[idx].Hp - damage, sbyte.MinValue);
        r = new ResolvedDamage(DamageKind.Blunt, TerrainWire.Q(point), TerrainWire.QNormal(normal), TerrainWire.QNormal(dir),
            TerrainWire.QForce(e.Force), 0f, (sbyte)hp, e.Seed);
        return true;
    }

    // 全員: 家具への打撃を適用する
    internal static string Strike(in ResolvedDamage e)
    {
        if (!Ensure()) return "prop no ship";
        int k = At(e.Position);
        if (k < 0) return "prop not found";
        var p = Props[k];
        p.Hp = e.Hp;
        TerrainDigest.Hp(-1 - k, p.Hp);
        TerrainDamage.LastMaterial = p.Spec.Wood ? "wood" : "metal";
        TerrainDamage.LastPitch = p.Spec.Pitch;
        Vector2 into = -e.Normal;
        if (e.Hp > 0)
        {
            TerrainFx.PropChip(e.Position, into, p.Spec.Wood, e.Seed);
            return $"prop hit {p.Name} hp={e.Hp}";
        }
        // 振った向きと面の奥の間へ飛ぶ (横から掠めても家具の向こうへ)
        Vector2 d = e.Direction + into;
        float m = d.magnitude;
        int n = Break(p, m > 1e-3f ? d / m : into, e.Position + e.Normal * 0.1f, e.Seed);
        return $"prop break {p.Name} pieces={n}";
    }

    // 全員: 爆発の形に掛かる家具を壊す。壊した数
    internal static int Blast(CutShape core, Vector2 center, ushort seed)
    {
        if (!Ensure()) return 0;
        int broke = 0;
        for (int k = 0; k < Props.Count; k++)
        {
            var p = Props[k];
            if (p.Broken || !p.Sr) continue;
            // 範囲の中で爆心にいちばん近い点が形の中なら壊れる
            float qx = Math.Clamp(center.x, p.X0, p.X1), qy = Math.Clamp(center.y, p.Y0, p.Y1);
            if (core.SignedDistance(qx, qy) >= 0f) continue;
            p.Hp = 0;
            TerrainDigest.Hp(-1 - k, 0);
            float dx = (p.X0 + p.X1) * 0.5f - center.x, dy = (p.Y0 + p.Y1) * 0.5f - center.y, m = MathF.Sqrt(dx * dx + dy * dy);
            var dir = m > 1e-3f ? new Vector2(dx / m, dy / m) : Vector2.right;
            TerrainDamage.LastPieces += Break(p, dir, center, (ushort)(seed + k * 7919));
            broke++;
        }
        return broke;
    }

    // 当たった点に掛かる壊れていない家具。いくつも掛かれば範囲が点にいちばん近い物 (同じなら番号の小さい方 = 全員で同じ)
    private static int At(Vector2 at)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var c in Physics2D.OverlapCircleAll(at, FindRadius, 1 << ShipLayer))
        {
            if (!c || !ByCollider.TryGetValue(c.GetInstanceID(), out int k) || Props[k].Broken) continue;
            var p = Props[k];
            float dx = at.x - Math.Clamp(at.x, p.X0, p.X1), dy = at.y - Math.Clamp(at.y, p.Y0, p.Y1), d = dx * dx + dy * dy;
            if (d < bestD || (d == bestD && k < best)) { best = k; bestD = d; }
        }
        return best;
    }

    // 当たり判定と絵を止めて欠片を飛ばす。home = 壊した側の歩ける点。欠片の数を返す
    private static int Break(Prop p, Vector2 dir, Vector2 home, ushort seed)
    {
        p.Broken = true;
        if (!p.Sr) return 0;
        var tr = p.Sr.transform;
        Vector3 ls = tr.lossyScale;
        var parts = Parts(p, seed, out float sx, out float sy);
        if (parts.Count == 0) { sx = ls.x; sy = ls.y; }
        foreach (var c in p.Cols) if (c) c.enabled = false;
        foreach (var r in p.Renderers) if (r) r.enabled = false;

        float cx = (p.X0 + p.X1) * 0.5f, cy = (p.Y0 + p.Y1) * 0.5f, w = p.X1 - p.X0, h = p.Y1 - p.Y0;
        var center = new Vector2(cx, cy);
        SolidMap.Carve(new RectShape(center, Vector2.right, w + CarveMargin * 2f, h + CarveMargin * 2f), null, home);
        if (parts.Count == 0) Fallback(p, parts, seed, ref sx, ref sy);
        float reach = MathF.Sqrt(w * w + h * h) * 0.5f + 3f;
        TerrainFx.Shatter(parts, sx, sy, center, dir, p.Spec.Wood, p.Spec.Sparks, WoodDust, seed, WallSegments.Snapshot(center, reach), home);
        Plugin.Logger.LogInfo($"prop break {p.Name}: pieces={parts.Count} at=({cx:0.00},{cy:0.00})");
        return parts.Count;
    }

    // 絵を割った欠片: 絵の四角に散らした点ごとの細胞 (近い点の側だけを残した凸の多角形) で元の絵を切り出す
    private static List<(Sprite, Vector2, float)> Parts(Prop p, ushort seed, out float sx, out float sy)
    {
        var parts = new List<(Sprite, Vector2, float)>();
        var sp = p.Sr.sprite;
        var tr = p.Sr.transform;
        Vector3 ls = tr.lossyScale;
        sx = ls.x; sy = ls.y;
        float w = sp.rect.width, h = sp.rect.height, ppu = sp.pixelsPerUnit;
        Vector2 pivot = sp.pivot;
        int n = p.Spec.Pieces;
        var rnd = new System.Random(seed);
        var pts = new Vector2[n];
        if (p.Spec.Cols > 0)
        {
            // 升の真ん中を少しだけずらす (継ぎ目がほぼ真っ直ぐに通る)
            int cols = p.Spec.Cols, rows = Math.Max(1, n / cols);
            n = cols * rows;
            pts = new Vector2[n];
            for (int i = 0; i < n; i++)
                pts[i] = new Vector2((i % cols + 0.4f + 0.2f * (float)rnd.NextDouble()) / cols * w, (i / cols + 0.4f + 0.2f * (float)rnd.NextDouble()) / rows * h);
        }
        else
        {
            // 長い辺に沿って並べ、少しずつずらす (同じ所に点が固まって細い欠片ばかりになるのを避ける)
            bool wide = w >= h;
            for (int i = 0; i < n; i++)
            {
                float along = (i + 0.2f + 0.6f * (float)rnd.NextDouble()) / n, across = 0.15f + 0.7f * (float)rnd.NextDouble();
                pts[i] = wide ? new Vector2(along * w, across * h) : new Vector2(across * w, along * h);
            }
        }
        var cell = new List<Vector2>(16);
        var next = new List<Vector2>(16);
        for (int i = 0; i < n; i++)
        {
            cell.Clear();
            cell.Add(new Vector2(0f, 0f)); cell.Add(new Vector2(w, 0f)); cell.Add(new Vector2(w, h)); cell.Add(new Vector2(0f, h));
            for (int j = 0; j < n && cell.Count >= 3; j++)
            {
                if (j == i) continue;
                Vector2 a = pts[i], b = pts[j];
                float nx = b.x - a.x, ny = b.y - a.y, mx = (a.x + b.x) * 0.5f, my = (a.y + b.y) * 0.5f;
                ClipHalf(cell, next, nx, ny, mx, my);
                (cell, next) = (next, cell);
            }
            if (cell.Count < 3) continue;
            var poly = new int[cell.Count * 2];
            for (int v = 0; v < cell.Count; v++) { poly[v * 2] = (int)MathF.Round(cell[v].x); poly[v * 2 + 1] = (int)MathF.Round(cell[v].y); }
            var piece = FurnitureSplit.Cut(sp, poly, sp.name + "-shard" + i, out Vector2 c);
            if (!piece) continue;
            Owned.Add(piece);
            Vector3 at = tr.TransformPoint(new Vector3((c.x - pivot.x) / ppu, (c.y - pivot.y) / ppu, 0f));
            // 欠片の下の床: 家具の当たり判定の下の縁から、欠片までの 3 割の高さ (3/4 視点で絵は当たり判定より上に立っている)
            float floorY = Math.Min(at.y, p.Y0 + (at.y - p.Y0) * 0.3f);
            parts.Add((piece, new Vector2(at.x, at.y), floorY));
        }
        return parts;
    }

    // 絵を切り出せなかった時 (詰め方が回転・ぴったりの絵): 素材の瓦礫の絵を家具の範囲に散らす
    private static void Fallback(Prop p, List<(Sprite, Vector2, float)> parts, ushort seed, ref float sx, ref float sy)
    {
        var rnd = new System.Random(seed);
        for (int i = 0; i < p.Spec.Pieces; i++)
        {
            var sp = p.Spec.Wood ? DebrisArt.Splinter(rnd.Next()) : DebrisArt.Plate(rnd.Next());
            float x = p.X0 + (p.X1 - p.X0) * (float)rnd.NextDouble(), y = p.Y0 + (p.Y1 - p.Y0) * (float)rnd.NextDouble();
            parts.Add((sp, new Vector2(x, y + 0.3f), y));
        }
        var any = p.Spec.Wood ? DebrisArt.Splinter(0) : DebrisArt.Plate(0);
        sx = sy = 0.35f / Math.Max(0.0001f, any.bounds.size.x);
    }

    // 凸の多角形 src を、点 (mx, my) を通り (nx, ny) を向いた線の手前側 (向きと逆の側) で切る
    private static void ClipHalf(List<Vector2> src, List<Vector2> dst, float nx, float ny, float mx, float my)
    {
        dst.Clear();
        int cnt = src.Count;
        for (int i = 0; i < cnt; i++)
        {
            Vector2 a = src[i], b = src[(i + 1) % cnt];
            float da = (a.x - mx) * nx + (a.y - my) * ny, db = (b.x - mx) * nx + (b.y - my) * ny;
            if (da <= 0f) dst.Add(a);
            if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
            {
                float t = da / (da - db);
                dst.Add(new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t));
            }
        }
    }

    internal static void Register()
    {
        TestBridge.Register("breakables", "物ごと壊れる家具: 名前・残りの耐久・壊れたか・範囲", (args, reply) =>
        {
            if (!Ensure()) { reply("ERR breakables no ship"); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var p in Props)
            {
                sb.Append($"\n{p.Name} hp={p.Hp} broken={(p.Broken ? 1 : 0)} box=({p.X0:0.00},{p.Y0:0.00})-({p.X1:0.00},{p.Y1:0.00}) cols={p.Cols.Count} renderers=");
                foreach (var r in p.Renderers) if (r) sb.Append(r.name).Append(',');
            }
            reply($"OK breakables n={Props.Count}{sb}");
        });
    }
}
