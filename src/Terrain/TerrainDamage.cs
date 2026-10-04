using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 損傷イベントを地形に適用する唯一の入口。壊れ方は DamageProfile で決まる。
// ゲームに関わる物 (ドアなど) は当面壊さない。
internal static class TerrainDamage
{
    private const int ShipLayer = 9;
    private const int ShadowLayer = 10;
    private const int WallMask = (1 << ShipLayer) | (1 << ShadowLayer);
    // 崩れた塊が飛ぶ範囲 (壊した形の外へこれだけ先の壁まで跳ね返りに使う)
    private const float FxReach = 3f;

    // テスト用: 直前の破壊で切り取った壁の区間 (線分の両端)。見た目の穴と当たり判定の抜けが揃っているかを holecheck で調べる
    internal static readonly List<Vector2> LastRemoved = new();

    // 依頼を結果に決める (ホストだけが呼ぶ)。地形は変えない。何も起きない時 (届く所に壁が無い・外壁) は false
    public static bool TryResolve(in DamageEvent e, out ResolvedDamage r, out string why)
    {
        r = default;
        why = null;
        var p = DamageProfile.Of(e.Kind);
        if (e.Kind == DamageKind.Explosion)
        {
            r = new ResolvedDamage(e.Kind, TerrainWire.Q(e.Position), Vector2.zero, TerrainWire.QNormal(e.Direction),
                TerrainWire.QForce(e.Force), TerrainWire.QSize(e.Size), 0, e.Seed);
            return true;
        }
        if (e.Kind != DamageKind.Blunt) { why = "unknown kind"; return false; }

        Vector2 dir = e.Direction.sqrMagnitude > 1e-6f ? e.Direction.normalized : Vector2.right;
        if (!FindWall(e.Position, dir, p.Reach, out Vector2 hit, out Vector2 normal)) { why = "blunt no wall in reach"; return false; }
        // 叩いた辺りの多角形・箱の壁を先に折れ線へ (奥の面を辿れるように)
        WallsNear(hit, MaxDepth);
        float far = FarSide(hit, -normal);
        if (far <= 0f) { why = "blunt outer wall (protected)"; return false; }

        // 抜く向きと、その向きに沿った壁の厚み (奥の面まで + 余白)。受け手は送った向きと長さをそのまま使う。
        // 斜めの先に奥の面が無い (斜めに進むと壁の外へ出る) 時は真っ直ぐ抜く
        Vector2 qn = TerrainWire.QNormal(normal), qd = TerrainWire.QNormal(dir);
        Vector2 axis = SlantAxis(-qn, qd, p.MaxSlantDeg, out _);
        float run = far;
        if (axis != -qn)
        {
            float slanted = FarSide(hit, axis);
            if (slanted > 0f) run = slanted;
            else { axis = -qn; qd = -qn; }
        }
        float depth = Math.Max(p.BreachDepth, run + 0.25f);

        // 耐久の格子は送る値 (量子化した点) で数える。受け手も同じ点で同じ格子に書く
        hit = TerrainWire.Q(hit);
        int hp = Math.Max(WallDurability.Remaining(hit) - p.WallDamage, sbyte.MinValue);
        r = new ResolvedDamage(e.Kind, hit, qn, qd, TerrainWire.QForce(e.Force), TerrainWire.QSize(depth), (sbyte)hp, e.Seed);
        return true;
    }

    // 決まった結果を地形に適用する (ホストも含め全員が同じ順で呼ぶ)。
    // 穴の形は量子化済みの結果の値だけから作る (ホストと客が同じ入力に同じ計算を掛けるため)
    public static string Apply(in ResolvedDamage r)
    {
        var profile = DamageProfile.Of(r.Kind);
        return r.Kind switch
        {
            DamageKind.Explosion => Explode(r, profile),
            DamageKind.Blunt => Strike(r, profile),
            _ => "unknown kind",
        };
    }

    // 爆発: 形の内側の壁はまとめて抜け、外側の輪の壁にはひびが入って耐久が減る。
    // 向きに偏った爆発 (力 > 0) は、向きの先へ伸びて後ろが縮んだ涙形に抜ける
    private static string Explode(in ResolvedDamage e, DamageProfile p)
    {
        CutShape core = e.Force > 0.02f
            ? ConvexShape.Cone(e.Position, e.Size, e.Direction, p.ConeStretch * e.Force, p.ConeShrink * e.Force)
            : new CircleShape(e.Position, e.Size);
        float ring = e.Size * (p.OuterRingScale - 1f);
        float outer = core.BoundRadius + ring;
        Vector2 c = core.Center;
        var removed = new List<Vector2>();
        int cut = 0, cracked = 0;

        // 外壁は壊さない: 切り取る区間ごとに、爆心から見て壁の向こう側に奥の面があるか (動きの層の面)
        Vector2 blast = e.Position;
        bool Inner(Vector2 a, Vector2 b)
        {
            Vector2 m = (a + b) * 0.5f, d = b - a;
            if (d.x * d.x + d.y * d.y < 1e-10f) return true;
            var n = new Vector2(-d.y, d.x);
            Vector2 away = (m.x - blast.x) * n.x + (m.y - blast.y) * n.y >= 0f ? n : -n;
            away = away.normalized;
            // 向こうに奥の面がある (内壁の手前の面) か、爆心との間に別の壁がある (厚い壁の奥の面) なら抜く
            if (HasFarSide(m, away)) return true;
            float toBlast = (m - blast).magnitude;
            float first = FirstFace(m, -away, Math.Min(toBlast, MaxDepth));
            return first > 0f;
        }
        var keep = DamageMap.FurnitureFor(core); // 家具の保護範囲は壁も残す (絵を抜かない所を通れないように)
        var walls = WallsNear(c, outer);
        // 壁の中の判定は切る前の壁の線で。爆心は歩ける場所にある前提 (武器は弾が止まった位置で依頼する)
        var body = new WallBody(ShipOnly(walls), e.Position);
        // 区間ごとの可否は、全部を切る前の地形で先に決める (順に切りながら決めると、先に切った壁が奥の面や
        // 間の壁として見えなくなり、処理の順番で結果が変わる)
        // 蓋 (前の穴の側面) は穴の中に作った壁なので常に切る
        var allowed = new HashSet<(float, float, float, float)>();
        foreach (var col in walls)
        {
            if (col.gameObject.layer != ShipLayer) continue;
            bool cap = col.gameObject.name == WallBody.CapName;
            EdgeCutter.Cut(col, core, null, (a, b) =>
            {
                if (cap || Inner(a, b)) allowed.Add((a.x, a.y, b.x, b.y));
                return false;
            }, keep, dryRun: true);
        }
        foreach (var col in walls)
            if (col.gameObject.layer == ShipLayer && EdgeCutter.Cut(col, core, removed, (a, b) => allowed.Contains((a.x, a.y, b.x, b.y)), keep)) cut++;

        // 視界の層は動きの層を切った後で決める: 爆心から、残った動きの壁を横切らずに届く (穴から見通せる) 面だけ抜く。
        // 動きの壁が残った所 (外壁・家具の裏) の視界の面は残す。逆に穴が開いたのに視界の面が残ると、
        // 影の中に壊れていない壁が見え、穴の中から影が伸びる
        var after = new WallBody(ShipOnly(WallsNear(c, outer)), blast);
        foreach (var col in walls)
            if (col.gameObject.layer == ShadowLayer &&
                EdgeCutter.Cut(col, core, removed, (a, b) => after.Clear(blast, (a + b) * 0.5f), keep)) cut++;
        body.SetOpening(removed);

        // 外側の輪: 残った壁の、爆心にいちばん近い点にひび (壁 1 本につき 1 か所)
        foreach (var col in WallsNear(c, outer))
        {
            if (col.gameObject.layer != ShipLayer) continue;
            if (!ClosestPoint(col, e.Position, out Vector2 q)) continue;
            float d = core.SignedDistance(q.x, q.y);
            if (d <= 0f || d > ring) continue;
            WallDurability.Hit(q, 1);
            DamageMap.Cracks(q, 0.35f, AngleOf(q - e.Position));
            cracked++;
        }

        // 穴の側面 (露出した壁の中との境) に蓋。ひびを付け終えてから作る (蓋にひびが付かないように)
        int caps = cut > 0 ? WallBody.Build(body.Caps(core)) : 0;
        LastRemoved.Clear(); LastRemoved.AddRange(removed);
        var pieces = new List<BreakPiece>();
        string visual = cut > 0 ? DamageMap.Breach(core, removed, p.Scorch, keep: keep, body: body, pieces: pieces) : null;
        // 塊が跳ね返る壁は切った後の壁 (蓋を含む) から
        if (visual == null) TerrainFx.Explosion(e.Position, e.Size, e.Direction, e.Force, e.Seed, pieces, removed, WallSegments.Snapshot(c, outer + FxReach));
        return $"explosion cut={cut} cracked={cracked} caps={caps} pieces={pieces.Count} visual={visual ?? "ok"}";
    }

    // 打撃: ホストが決めた壁の点の耐久を書く。0 になったらその壁の区間が抜ける。
    // 斜めに振ると振った向きに傾いて抜け (上限 MaxSlantDeg)、強く振るほど広く抜ける
    private static string Strike(in ResolvedDamage e, DamageProfile p)
    {
        Vector2 hit = e.Position, normal = e.Normal, dir = -normal;
        float depth = e.Size; // 抜く向きに沿った長さ

        int hp = e.Hp;
        WallDurability.Set(hit, hp);
        if (hp > 0)
        {
            // 耐久が減るほどひびが育つ
            float reach = hp >= WallDurability.MaxHp - 1 ? 0.3f : 0.6f;
            if (DamageMap.Cracks(hit, reach, AngleOf(dir)) == null) TerrainFx.Chip(hit, dir, e.Seed);
            return $"blunt hit hp={hp} at=({hit.x:0.00},{hit.y:0.00})";
        }

        // 叩いた面から壁の奥 (部屋と部屋の隙間・向こうの部屋の枠) までの区間を、振った向きに沿って抜く
        Vector2 tangent = new(-normal.y, normal.x);
        Vector2 axis = SlantAxis(dir, e.Direction, p.MaxSlantDeg, out float cos);
        float length = p.BreachLength * ForceScale(e.Force, p.ForceLength);
        float run = depth;
        var shape = ConvexShape.Slanted(hit + normal * 0.1f, tangent, axis, length, run);
        Vector2 center = shape.Center;
        var removed = new List<Vector2>();
        int cut = 0;
        var keep = DamageMap.FurnitureFor(shape); // 家具の保護範囲は壁も残す (絵を抜かない所を通れないように)
        var walls = WallsNear(center, shape.BoundRadius);
        var body = new WallBody(ShipOnly(walls), hit + normal * 0.1f); // 叩いた側 (歩ける床) を基準に壁の中を判定
        foreach (var col in walls)
            if (EdgeCutter.Cut(col, shape, removed, keep: keep)) cut++;
        body.SetOpening(removed);
        int caps = cut > 0 ? WallBody.Build(body.Caps(shape)) : 0;

        // 下から叩いた (壁の手前の床に立っている) 時は、叩いた点の少し上から下を見た目では抜かない
        // Polus だけ (壁の当たり判定が見た目の根元より下まで伸びている実測)。他のマップで掛けると壁の根元の線が残る
        bool skirt = ShipStatus.Instance && ShipStatus.Instance.TryCast<PolusShipStatus>() != null;
        float floorY = skirt && normal.y < -0.5f ? hit.y + 0.12f : float.NegativeInfinity;
        LastRemoved.Clear(); LastRemoved.AddRange(removed);
        // floorY は見た目だけ (その下の当たり判定は床の絵の上の見えない壁なので切ってよい)
        var pieces = new List<BreakPiece>();
        string visual = cut > 0 ? DamageMap.Breach(shape, removed, p.Scorch, floorY, keep, body, pieces) : null;
        // 壁が崩れ落ちて瓦礫の山になる (壁の線の少し奥を中心に、振った向きへ寄せて)
        // 全部が家具の裏で何も切れなかった時は崩さない (崩れた見た目なのに壁が残るのを避ける)
        if (cut == 0) TerrainFx.Chip(hit, dir, e.Seed);
        else if (visual == null) TerrainFx.Crumble(hit + axis * (run * 0.3f), tangent, normal, axis, e.Force, length, e.Seed, pieces, removed,
            WallSegments.Snapshot(center, shape.BoundRadius + FxReach), hit + normal * 0.1f);
        return $"blunt breach cut={cut} caps={caps} pieces={pieces.Count} depth={depth:0.00} slant={MathF.Acos(cos) * 57.29578f:0} len={length:0.00} visual={visual ?? "ok"}";
    }

    // 抜く向き: 壁の奥 (inward) から振った向きへ、上限の角度まで傾ける。横から掠める振り (奥へ進まない) は真っ直ぐ抜く
    private static Vector2 SlantAxis(Vector2 inward, Vector2 swing, float maxDeg, out float cos)
    {
        float c = inward.x * swing.x + inward.y * swing.y;
        if (c <= 0.05f) { cos = 1f; return inward; }
        float maxCos = MathF.Cos(maxDeg * (MathF.PI / 180f));
        if (c >= maxCos) { cos = c; return swing; }
        float side = inward.x * swing.y - inward.y * swing.x >= 0f ? 1f : -1f;
        float sin = MathF.Sqrt(1f - maxCos * maxCos) * side;
        cos = maxCos;
        return new Vector2(inward.x * maxCos - inward.y * sin, inward.x * sin + inward.y * maxCos);
    }

    // 力 0 → 0.7 倍・0.5 → 1 倍・1 → full 倍
    private static float ForceScale(float f, float full) => f < 0.5f ? 0.7f + 0.6f * f : 1f + (full - 1f) * (2f * f - 1f);

    // 範囲に掛かる壁を折れ線で返す。箱・多角形・円の壁はここで初めて折れ線に置き換える
    private static List<EdgeCollider2D> WallsNear(Vector2 c, float r)
    {
        var list = new List<EdgeCollider2D>();
        foreach (var c2 in Physics2D.OverlapCircleAll(c, r, WallMask))
        {
            if (!c2 || c2.isTrigger || !c2.enabled || IsProtected(c2)) continue;
            var e = c2.TryCast<EdgeCollider2D>();
            if (e) { list.Add(e); continue; }
            list.AddRange(WallOutline.Convert(c2));
        }
        return list;
    }

    private static IEnumerable<EdgeCollider2D> ShipOnly(List<EdgeCollider2D> walls)
    {
        foreach (var w in walls) if (w.gameObject.layer == ShipLayer) yield return w;
    }

    // 壊さない物: ゲームに関わる物 (当面の裁定)・家具や小物 (Ship 層に入っているマップがある)・マップの外周と地形
    private static readonly System.Text.RegularExpressions.Regex ProtectedName = new(
        @"table|chair|desk|box|rock|ball|stand|panel|candle|parasite_|railing|mushroom|boundary|cliff|lava|^hole$|bridge|background|computer|office-|storage-",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool IsProtected(Component c)
    {
        if (c.GetComponentInParent<OpenableDoor>()) return true;
        for (var t = c.transform; t && !t.GetComponent<ShipStatus>(); t = t.parent)
            if (ProtectedName.IsMatch(t.name)) return true;
        return false;
    }

    // 外壁 (向こう側が宇宙・マップの外) は壊さない (当面の裁定)。
    // 内壁には「奥の面」がある (部屋と部屋の隙間の向こうの枠・厚みのある壁の裏側)。壁の面から奥へ MaxDepth 以内に
    // 次の壁の面が無ければ、向こうは何も無い外側とみなす。部屋の範囲は屋外 (Polus など) を含まないので使わない
    private static bool HasFarSide(Vector2 surface, Vector2 inward) => FarSide(surface, inward) > 0f;

    // 叩いた面から奥へ、壁の向こう側の面までの厚さ (見つからなければ既定値)。
    // 奥の面 = 叩いた面から MaxDepth 以内で次に当たる Ship 層の壁 (部屋と部屋の間の隙間と向こうの部屋の枠を含む)
    private const float MaxDepth = 2.6f;

    // 奥の面までの距離 (無ければ 0)。CircleCastAll は 1 つの部品につき最初の 1 か所しか返さないので、
    // 当たった少し先から探し直して同じ折れ線の奥の面まで辿る。RaycastAll は Android の libunity に無いので細い円で代用。
    // 面を越えた先が叩いた側と別の部屋の範囲なら、そこが奥の面 (向こうの部屋に入った) として止まる。
    // 止めないと狭い通路を横切って向こう側の壁 (外壁のこともある) まで抜いてしまう。
    // 多角形の壁は内側から投げると出口の面を返さないので、呼ぶ前に折れ線へ置き換えておくこと
    private static float FarSide(Vector2 hit, Vector2 inward)
    {
        var start = RoomAt(hit - inward * 0.1f);
        float far = 0f, travelled = 0.05f;
        for (int step = 0; step < 8 && travelled < MaxDepth; step++)
        {
            float best = FirstFace(hit + inward * travelled, inward, MaxDepth - travelled);
            if (best <= 0f) break;
            travelled += best;
            far = travelled;
            var beyond = RoomAt(hit + inward * (travelled + 0.05f));
            if (beyond != null && beyond != start) break;
            travelled += 0.05f;
        }
        return far;
    }

    // from から dir へ、最初に当たる Ship 層の壁の面までの距離 (無ければ 0)
    private static float FirstFace(Vector2 from, Vector2 dir, float max)
    {
        float best = float.MaxValue;
        foreach (var h in Physics2D.CircleCastAll(from, 0.01f, dir, max, 1 << ShipLayer))
        {
            if (!h.collider || h.collider.isTrigger || IsProtected(h.collider)) continue;
            if (h.distance > 0.001f && h.distance < best) best = h.distance;
        }
        return best == float.MaxValue ? 0f : best;
    }

    // 点を含む部屋 (本編の部屋の範囲)。無ければ null (壁の中・屋外・宇宙)
    private static PlainShipRoom RoomAt(Vector2 p)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return null;
        foreach (var r in ship.AllRooms)
            if (r && r.roomArea && r.roomArea.OverlapPoint(p)) return r;
        return null;
    }

    private static bool FindWall(Vector2 from, Vector2 dir, float reach, out Vector2 point, out Vector2 normal)
    {
        point = default; normal = default;
        float best = float.MaxValue;
        foreach (var h in Physics2D.CircleCastAll(from, 0.1f, dir, reach, 1 << ShipLayer))
        {
            if (!h.collider || h.collider.isTrigger || IsProtected(h.collider) || h.distance >= best) continue;
            best = h.distance;
            point = h.point;
            normal = h.normal;
        }
        return best < float.MaxValue;
    }

    // Collider2D.ClosestPoint は Android の libunity に無いので、折れ線の頂点から自前で求める
    private static bool ClosestPoint(EdgeCollider2D col, Vector2 p, out Vector2 q)
    {
        q = default;
        var t = col.transform;
        var pts = col.points;
        if (pts.Length < 2) return false;
        float best = float.MaxValue;
        Vector2 prev = t.TransformPoint(pts[0] + col.offset);
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 cur = t.TransformPoint(pts[i] + col.offset);
            Vector2 d = cur - prev;
            float l2 = d.sqrMagnitude;
            float s = l2 > 0 ? Math.Clamp(Vector2.Dot(p - prev, d) / l2, 0f, 1f) : 0f;
            Vector2 c = prev + d * s;
            float dist = (c - p).sqrMagnitude;
            if (dist < best) { best = dist; q = c; }
            prev = cur;
        }
        return true;
    }

    private static float AngleOf(Vector2 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
}
