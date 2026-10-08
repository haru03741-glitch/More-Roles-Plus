using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
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
    private const float CrackLift = 0.4f; // 打撃の割れ目の中心を上げる高さ (壁の面の高さ ≈0.85 の半分ほど)
    // 爆発の外側の輪: 形の縁からこれ以内の点は穴の口 (切った壁の端点そのもの) としてひびを入れない。
    // 端点は縁ちょうどにあり、d = ±0 の符号が端末の CPU で分かれる (PC は +0 でひび・Android は −0 で無し)
    private const float RingLip = 0.02f;

    // テスト用: 直前の破壊で切り取った壁の区間 (線分の両端)。見た目の穴と当たり判定の抜けが揃っているかを holecheck で調べる
    internal static readonly List<Vector2> LastRemoved = new();

    // 依頼を結果に決める (ホストだけが呼ぶ)。地形は変えない。何も起きない時 (届く所に壁が無い・外壁) は false
    public static bool TryResolve(in DamageEvent e, out ResolvedDamage r, out string why)
    {
        r = default;
        why = null;
        SolidMap.Ensure(); // 壁を切る前に歩ける所の地図を作っておく
        var p = DamageProfile.Of(e.Kind);
        if (e.Kind == DamageKind.Explosion)
        {
            r = new ResolvedDamage(e.Kind, TerrainWire.Q(e.Position), Vector2.zero, TerrainWire.QNormal(e.Direction),
                TerrainWire.QForce(e.Force), TerrainWire.QSize(e.Size), 0, e.Seed);
            return true;
        }
        if (e.Kind != DamageKind.Blunt) { why = "unknown kind"; return false; }

        Vector2 dir = e.Direction.sqrMagnitude > 1e-6f ? e.Direction.normalized : Vector2.right;
        bool wall = FindWall(e.Position, dir, p.Reach, out Vector2 hit, out Vector2 normal);
        if (BreakableProps.TryResolve(e, dir, p.Reach, wall, hit, out r)) return true; // 壁より手前の物ごと壊れる家具
        if (!wall) { why = "blunt no wall in reach"; return false; }
        // 叩いた辺りの多角形・箱の壁を先に折れ線へ (奥の面を辿れるように)
        WallsNear(hit, MaxDepth);
        float far = FarSide(hit, -normal);
        if (!FloorBeyond(hit, -normal)) far = 0f; // 厚い外壁は自分の裏の面が奥の面に見える。向こうに床が無ければ外壁
        // 裏の壁の中が線を越えずに空へつながる面 (外壁に付いた出っ張り) は、奥の面があっても外壁
        if (SolidMap.FacesSky(SolidMap.SkyNear(hit, StrikeSkyReach + 0.5f, new CircleShape(hit, StrikeSkyReach)), hit, normal))
        {
            why = "blunt outer wall (protected)";
            return false;
        }
        // 奥の面が近くに無くても、裏が船体の塊 (エアシップの部屋と部屋の間) なら外壁ではない。1 打ごとに ThickStep ずつ掘り進む
        bool thick = false;
        if (far <= 0f)
        {
            if (!ThickBehind(hit, -normal)) { why = "blunt outer wall (protected)"; return false; }
            far = ThickStep;
            thick = true;
        }

        // 抜く向きと、その向きに沿った壁の厚み (奥の面まで + 余白)。受け手は送った向きと長さをそのまま使う。
        // 斜めの先に奥の面が無い (斜めに進むと壁の外へ出る) 時は真っ直ぐ抜く
        Vector2 qn = TerrainWire.QNormal(normal), qd = TerrainWire.QNormal(dir);
        Vector2 axis = SlantAxis(-qn, qd, p.MaxSlantDeg, out _);
        float run = far;
        if (axis != -qn)
        {
            float slanted = FloorBeyond(hit, axis) ? FarSide(hit, axis) : 0f;
            if (slanted <= 0f && thick && ThickBehind(hit, axis)) slanted = ThickStep;
            if (slanted > 0f) run = slanted;
            else { axis = -qn; qd = -qn; }
        }
        float depth = Math.Max(p.BreachDepth, run + 0.25f);

        // 耐久の格子は送る値 (量子化した点) で数える。受け手も同じ点で同じ格子に書く
        hit = TerrainWire.Q(hit);
        // 強い振りは 2 削る (力は送る値で比べる)
        int damage = TerrainWire.QForce(e.Force) >= p.StrongForce ? p.StrongDamage : p.WallDamage;
        int hp = Math.Max(WallDurability.Remaining(hit) - damage, sbyte.MinValue);
        r = new ResolvedDamage(e.Kind, hit, qn, qd, TerrainWire.QForce(e.Force), TerrainWire.QSize(depth), (sbyte)hp, e.Seed);
        return true;
    }

    // 決まった結果を地形に適用する (ホストも含め全員が同じ順で呼ぶ)。
    // 穴の形は量子化済みの結果の値だけから作る (ホストと客が同じ入力に同じ計算を掛けるため)
    // decide = 大きな瓦礫の止まる所をここで決める (ホスト)。false なら r.Landings を使う (客)
    // 直前の適用で切った壁の数・割れた塊・大きな瓦礫の数 (壊れた音の大きさと高さに使う)。
    // 全員が同じ結果の値から同じ計算で作る物なので、ホストと客で同じになる
    internal static int LastCut, LastPieces, LastBlocks;
    // 直前の適用で壊した物の素材 (壊れた音を選ぶ。null = マップの壁の素材) と音の高さの倍率
    internal static string LastMaterial;
    internal static float LastPitch = 1f;

    public static string Apply(in ResolvedDamage r, bool decide, out RubbleLanding[] landings)
    {
        LastCut = LastPieces = LastBlocks = 0;
        LastMaterial = null;
        LastPitch = 1f;
        var profile = DamageProfile.Of(r.Kind);
        var given = decide ? null : r.Landings ?? Array.Empty<RubbleLanding>();
        landings = Array.Empty<RubbleLanding>();
        return r.Kind switch
        {
            DamageKind.Explosion => Explode(r, profile, given, ref landings),
            DamageKind.Blunt => Strike(r, profile, given, ref landings),
            _ => "unknown kind",
        };
    }

    // 爆発: 形の内側の壁はまとめて抜け、外側の輪の壁にはひびが入って耐久が減る。
    // 向きに偏った爆発 (力 > 0) は、向きの先へ伸びて後ろが縮んだ涙形に抜ける
    private static string Explode(in ResolvedDamage e, DamageProfile p, RubbleLanding[] given, ref RubbleLanding[] landings)
    {
        SolidMap.Ensure();
        CutShape core = e.Force > 0.02f
            ? ConvexShape.Cone(e.Position, e.Size, e.Direction, p.ConeStretch * e.Force, p.ConeShrink * e.Force)
            : new CircleShape(e.Position, e.Size);
        // 宇宙まで掘り抜ける外壁 (スケルド) は 1 発で ThickStep ずつ奥へ。残りが ThickStep 以内なら宇宙まで抜ける
        LastBreach = 0f;
        SkyCut.Clear();
        _skyAway = default;
        if (SolidMap.BreachableHull) core = DigHull(core, e.Position);
        float ring = e.Size * (p.OuterRingScale - 1f);
        float outer = core.BoundRadius + ring;
        Vector2 c = core.Center;
        var removed = new List<Vector2>();
        int cut = 0, cracked = 0;

        // 外壁は壊さない: 切り取る区間ごとに、爆心から見て壁の向こう側に奥の面があるか (動きの層の面)
        Vector2 blast = e.Position;
        var sky = SolidMap.SkyNear(c, core.BoundRadius + 0.5f, core);
        bool Inner(Vector2 a, Vector2 b)
        {
            Vector2 m = (a + b) * 0.5f, d = b - a;
            if (d.x * d.x + d.y * d.y < 1e-10f) return true;
            var n = new Vector2(-d.y, d.x);
            Vector2 away = (m.x - blast.x) * n.x + (m.y - blast.y) * n.y >= 0f ? n : -n;
            away = away.normalized;
            if (SolidMap.BreachableHull && SolidMap.HullDepth(m, away, BreachDepth) > 0f) return true; // 掘り抜ける外壁
            if (SolidMap.SkyHull && SolidMap.SkyAhead(m, away, SkyReach)) // エアシップ: 床を通らずに空へ出る外壁は爆発で抜ける
            {
                SkyCut.Add(a); SkyCut.Add(b);
                _skyAway += away;
                return true;
            }
            // 向こうに奥の面がある (内壁の手前の面) か、爆心との間に別の壁がある (厚い壁の奥の面) か、
            // 裏が船体の塊 (エアシップ) なら抜く。向こうに床が無い面 (厚い外壁の手前と裏) は外壁
            if (SolidMap.FacesOutside(m, away)) return false; // どちら側でも空・宇宙に面した面は外壁 (爆心の反対を向いた面も)
            if (SolidMap.FacesSky(sky, m, away)) return false; // 裏の壁の中が線を越えずに空へつながる (外壁に付いた出っ張り)
            if (!FloorBeyond(m, away)) return ThickBehind(m, away);
            if (HasFarSide(m, away)) return true;
            float toBlast = (m - blast).magnitude;
            float first = FirstFace(m, -away, Math.Min(toBlast, MaxDepth));
            return first > 0f || ThickBehind(m, away);
        }
        // 蓋も、裏の壁の中が空へつながる物は残す
        bool CapFacesSky(Vector2 a, Vector2 b)
        {
            float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-5f) return false;
            return SolidMap.FacesSky(sky, FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), FxMath.V2(-dy / len, dx / len));
        }
        var keep = DamageMap.FurnitureFor(core); // 家具の保護範囲は壁も残す (絵を抜かない所を通れないように)
        var walls = WallsNear(c, outer);
        // 壁の中の判定は切る前の壁の線で。爆心は歩ける場所にある前提 (武器は弾が止まった位置で依頼する)
        var body = new WallBody(ShipOnly(walls), e.Position) { OpenToSpace = SolidMap.BreachableHull };
        body.UseSolidMap(c, outer + 0.2f);
        // 区間ごとの可否は、全部を切る前の地形で先に決める (順に切りながら決めると、先に切った壁が奥の面や
        // 間の壁として見えなくなり、処理の順番で結果が変わる)
        // 蓋 (前の穴の側面) は穴の中に作った壁なので切る (裏が空へつながる物だけ残す)
        var allowed = new HashSet<(float, float, float, float)>();
        foreach (var col in walls)
        {
            if (col.gameObject.layer != ShipLayer || col.gameObject.name == WallBody.MouthName) continue; // 穴の口は残す
            bool cap = col.gameObject.name == WallBody.CapName;
            EdgeCutter.Cut(col, core, null, (a, b) =>
            {
                if (cap ? HullFace(a, b, blast) || !CapFacesSky(a, b) : Inner(a, b)) allowed.Add((a.x, a.y, b.x, b.y));
                return false;
            }, keep, dryRun: true);
        }
        var shipRemoved = new List<Vector2>(); // 動きの層で切った区間 (高さの違う所に縁を張る用)
        foreach (var col in walls)
        {
            if (col.gameObject.layer != ShipLayer) continue;
            int from = removed.Count;
            if (EdgeCutter.Cut(col, core, removed, (a, b) => allowed.Contains((a.x, a.y, b.x, b.y)), keep)) cut++;
            if (col.gameObject.name != WallBody.CapName) // 前の穴の蓋 (穴の中の壁) には縁を張らない
                for (int i = from; i < removed.Count; i++) shipRemoved.Add(removed[i]);
        }

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
            if (d <= RingLip || d > ring) continue;
            WallDurability.Hit(q, 1);
            DamageMap.Cracks(q, 0.35f, AngleOf(q - e.Position));
            cracked++;
        }

        // 穴の側面 (露出した壁の中との境) に蓋。ひびを付け終えてから作る (蓋にひびが付かないように)
        var capLines = cut > 0 ? body.Caps(core) : null;
        int caps = capLines != null ? WallBody.Build(capLines) : 0;
        if (SolidMap.BreachableHull && capLines != null) Mouth(capLines); // 抜けた穴を横へ広げた時も、宇宙に接した所に口を足す
        else if (SolidMap.SkyHull && cut > 0) SkyMouth();
        int ledges = cut > 0 ? HeightLevels.Build(shipRemoved) : 0; // 高さの違う床の境は、見た目と視界だけ抜けて歩いては越えられない
        LastRemoved.Clear(); LastRemoved.AddRange(removed);
        var pieces = new List<BreakPiece>();
        // 割れ目は爆心から放射状。向きに偏った爆発は向きの先へ伸び、先ほど大きな塊になる
        bool aimed = e.Force > 0.02f;
        var crack = new CrackPattern(e.Position, e.Seed, default, axis: aimed ? TerrainWire.AngleIndex(e.Direction) : (ushort)0,
            stretch: aimed ? 1f + p.CrackStretch * e.Force : 1f, bias: aimed ? p.CrackBias * e.Force : 0f);
        string visual = cut > 0 ? DamageMap.Breach(core, removed, p.Scorch, keep: keep, body: body, pieces: pieces, cracks: new List<CrackPattern> { crack }) : null;
        if (cut > 0) SolidMap.Carve(core, keep, blast); // 開いた所を歩ける所の地図に足す (蓋を作った後の壁で)
        // 塊が跳ね返る壁は切った後の壁 (蓋を含む) から
        if (visual == null)
            landings = TerrainFx.Explosion(e.Position, e.Size, e.Direction, e.Force, e.Seed, pieces, removed, WallSegments.Snapshot(c, outer + FxReach), given);
        LastCut = cut; LastPieces = pieces.Count; LastBlocks = landings.Length;
        int props = BreakableProps.Blast(core, e.Position, e.Seed); // 形に掛かる物ごと壊れる家具 (欠片の数は LastPieces に足す)
        return $"explosion cut={cut} cracked={cracked} caps={caps} ledges={ledges} pieces={pieces.Count} blocks={landings.Length} props={props} breach={LastBreach:0.00} visual={visual ?? "ok"}";
    }

    // 直前の爆発で外壁が宇宙まで抜けた時の穴の口の幅 (抜けなければ 0)
    internal static float LastBreach;
    internal static Vector2 LastBreachA, LastBreachB;

    internal const float BreachDepth = 2.5f;  // 宇宙までこの距離以内の外壁だけ掘り抜ける
    private const float BreachSlack = 0.15f; // 残りの厚みの測り方の刻み (HullDepth は 0.15 刻み)
    private const float ClipStep = 0.25f;    // 形を半平面で切る時の縁の点の間隔

    // 掘り抜ける外壁の面か (面の中点から爆心と反対の向きへ、船体の中だけを通って BreachDepth 以内に宇宙)
    private static bool HullFace(Vector2 a, Vector2 b, Vector2 blast)
    {
        if (!SolidMap.BreachableHull) return false;
        Vector2 m = (a + b) * 0.5f, d = b - a;
        if (d.x * d.x + d.y * d.y < 1e-10f) return false;
        var n = new Vector2(-d.y, d.x).normalized;
        if ((m.x - blast.x) * n.x + (m.y - blast.y) * n.y < 0f) n = -n;
        return SolidMap.HullDepth(m, n, BreachDepth) > 0f;
    }

    // 形に掛かる掘り抜ける外壁の面のうち、爆心にいちばん近い点の面から ThickStep より奥を切り落とす。
    // 残りの厚みが ThickStep 以内なら切らずに宇宙まで抜く。角で 2 面に掛かる時は近い方の面で決める
    private static CutShape DigHull(CutShape core, Vector2 blast)
    {
        bool found = false;
        float best = float.MaxValue, depth = 0f;
        Vector2 q = default, n = default;
        foreach (var col in WallsNear(core.Center, core.BoundRadius))
        {
            if (col.gameObject.layer != ShipLayer || col.gameObject.name == WallBody.MouthName) continue;
            EdgeCutter.Cut(col, core, null, (a, b) =>
            {
                Vector2 d = b - a;
                float l2 = d.x * d.x + d.y * d.y;
                if (l2 < 1e-10f) return false;
                var nn = new Vector2(-d.y, d.x) / MathF.Sqrt(l2);
                float s = Math.Clamp(((blast.x - a.x) * d.x + (blast.y - a.y) * d.y) / l2, 0f, 1f);
                Vector2 cp = a + d * s;
                if ((cp.x - blast.x) * nn.x + (cp.y - blast.y) * nn.y < 0f) nn = -nn;
                float dist = (cp - blast).sqrMagnitude;
                // 同じ距離の面が 2 つ (角) なら点の座標で決める (壁の部品の並び順は端末で同じとは限らない)
                if (dist > best || (dist == best && (cp.x > q.x || (cp.x == q.x && cp.y >= q.y)))) return false;
                float h = SolidMap.HullDepth(cp, nn, BreachDepth);
                if (h <= 0f) return false;
                best = dist; q = cp; n = nn; depth = h; found = true;
                return false;
            }, null, dryRun: true);
        }
        if (!found) return core;
        if (depth <= ThickStep + BreachSlack) return core;
        return ClipHalfPlane(core, q, n, ThickStep);
    }

    // 形を半平面 (q から n の向きに depth より手前) で切る。凸の形は凸のまま
    private static CutShape ClipHalfPlane(CutShape s, Vector2 q, Vector2 n, float depth)
    {
        var pts = s.Outline(ClipStep);
        var o = new List<Vector2>(pts.Count + 2);
        int k = pts.Count;
        for (int i = 0; i < k; i++)
        {
            Vector2 cur = pts[i], nxt = pts[(i + 1) % k];
            float dc = (cur.x - q.x) * n.x + (cur.y - q.y) * n.y - depth;
            float dn = (nxt.x - q.x) * n.x + (nxt.y - q.y) * n.y - depth;
            if (dc <= 0f) Add(cur);
            if ((dc < 0f && dn > 0f) || (dc > 0f && dn < 0f)) Add(cur + (nxt - cur) * (dc / (dc - dn)));
        }
        if (o.Count > 1 && (o[0] - o[^1]).sqrMagnitude < 1e-8f) o.RemoveAt(o.Count - 1);
        return o.Count >= 3 ? new ConvexShape(o.ToArray()) : s;

        void Add(Vector2 v)
        {
            if (o.Count == 0 || (v - o[^1]).sqrMagnitude >= 1e-8f) o.Add(v);
        }
    }

    // 宇宙まで抜けた穴の口: 蓋の端のうち船の外に接する点のうち、いちばん離れた 2 点を 1 本の線で結ぶ
    private static void Mouth(List<List<Vector2>> caps)
    {
        var ends = new List<Vector2>();
        foreach (var cap in caps)
        {
            if (cap.Count < 2) continue;
            if (SolidMap.NearOutside(cap[0].x, cap[0].y)) ends.Add(cap[0]);
            if (SolidMap.NearOutside(cap[^1].x, cap[^1].y)) ends.Add(cap[^1]);
        }
        if (ends.Count < 2) return;
        float best = -1f;
        // 口は穴の中か宇宙の上を通る (歩ける床を横切る線は作らない。切った所を地図に足す前に呼ぶ)
        Vector2 a = default, b = default;
        for (int i = 0; i < ends.Count; i++)
        for (int j = i + 1; j < ends.Count; j++)
        {
            float d = (ends[i] - ends[j]).sqrMagnitude;
            if (d > best) { best = d; a = ends[i]; b = ends[j]; }
        }
        Vector2 mid = (a + b) * 0.5f;
        if (!SolidMap.Solid(mid)) return;
        WallBody.BuildMouth(a, b);
        LastBreach = MathF.Sqrt(best);
        LastBreachA = a; LastBreachB = b;
    }

    // エアシップ: 外に面した厚い壁が抜けた所は空へ開いた口。空へ抜けると決めて切った壁の区間の端のうち、
    // いちばん離れた 2 点を結ぶ (口は元の壁の線の上)。壁の向こうに床がある所は区間に入らない (Inner で SkyAhead を見ている)
    private const float SkyReach = 20f; // エアシップの外周の船体は部屋の縁から空まで 9〜13 単位ある
    private const float SkyMouthMin = 0.3f;
    private static readonly List<Vector2> SkyCut = new();
    private static Vector2 _skyAway; // 切った区間の外向きの和

    private static void SkyMouth()
    {
        if (SkyCut.Count < 2) return;
        float best = -1f;
        Vector2 a = default, b = default;
        for (int i = 0; i < SkyCut.Count; i++)
        for (int j = i + 1; j < SkyCut.Count; j++)
        {
            float d = (SkyCut[i] - SkyCut[j]).sqrMagnitude;
            if (d > best) { best = d; a = SkyCut[i]; b = SkyCut[j]; }
        }
        if (best < SkyMouthMin * SkyMouthMin) return;
        // 角をまたいで切った時は 2 点を結ぶ線が床を横切る。床の上には口を張らない (切った所を地図に足す前に呼ぶ)
        // 壁の線の上の升は床に数えられることがあるので、外向きに少しずらした点で見る
        Vector2 aw = _skyAway.sqrMagnitude > 1e-6f ? _skyAway.normalized : default;
        if (!SolidMap.Solid((a + b) * 0.5f + aw * 0.25f))
        {
            Plugin.Logger.LogInfo($"[SkyMouth] line over floor {a.x:0.00},{a.y:0.00}-{b.x:0.00},{b.y:0.00}");
            return;
        }
        WallBody.BuildMouth(a, b);
        LastBreach = MathF.Sqrt(best);
        LastBreachA = a; LastBreachB = b;
    }

    // 打撃: ホストが決めた壁の点の耐久を書く。0 になったらその壁の区間が抜ける。
    // 斜めに振ると振った向きに傾いて抜け (上限 MaxSlantDeg)、強く振るほど広く抜ける
    private static string Strike(in ResolvedDamage e, DamageProfile p, RubbleLanding[] given, ref RubbleLanding[] landings)
    {
        SolidMap.Ensure();
        if (e.Size <= 0f) return BreakableProps.Strike(e); // 厚み 0 = 物ごと壊れる家具への打撃
        Vector2 hit = e.Position, normal = e.Normal, dir = -normal;
        float depth = e.Size; // 抜く向きに沿った長さ

        int hp = e.Hp;
        WallDurability.Set(hit, hp);
        if (hp > 0)
        {
            // 耐久が減るほどひびが育つ
            // 面を持つ壁は表面が剥げかける (崩れる時と同じ割れ目のひび)。他の壁はひびの板
            float reach = hp >= WallDurability.MaxHp - 1 ? 0.3f : 0.6f;
            if (WallPeel.Hit(hit, normal, e.Direction, hp, e.Seed) ||
                DamageMap.Cracks(hit, reach, AngleOf(SlantAxis(dir, e.Direction, p.MaxSlantDeg, out _))) == null) TerrainFx.Chip(hit, dir, e.Seed);
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
        var body = new WallBody(ShipOnly(walls), hit + normal * 0.1f); // 地図が無い時は叩いた側 (歩ける床) を基準に壁の中を判定
        body.UseSolidMap(center, shape.BoundRadius + 0.2f);
        var shipRemoved = new List<Vector2>(); // 動きの層で切った区間 (高さの違う所に縁を張る用)
        foreach (var col in walls)
        {
            int from = removed.Count;
            if (EdgeCutter.Cut(col, shape, removed, keep: keep)) cut++;
            if (col.gameObject.layer == ShipLayer && col.gameObject.name != WallBody.CapName) // 前の穴の蓋 (穴の中の壁) には縁を張らない
                for (int i = from; i < removed.Count; i++) shipRemoved.Add(removed[i]);
        }
        body.SetOpening(removed);
        int caps = cut > 0 ? WallBody.Build(body.Caps(shape)) : 0;
        int ledges = cut > 0 ? HeightLevels.Build(shipRemoved) : 0; // 高さの違う床の境は、見た目と視界だけ抜けて歩いては越えられない

        // 下から叩いた (壁の手前の床に立っている) 時は、叩いた点の少し上から下を見た目では抜かない
        // Polus だけ (壁の当たり判定が見た目の根元より下まで伸びている実測)。他のマップで掛けると壁の根元の線が残る
        bool skirt = ShipStatus.Instance && ShipStatus.Instance.TryCast<PolusShipStatus>() != null;
        float floorY = skirt && normal.y < -0.5f ? hit.y + 0.12f : float.NegativeInfinity;
        LastRemoved.Clear(); LastRemoved.AddRange(removed);
        // floorY は見た目だけ (その下の当たり判定は床の絵の上の見えない壁なので切ってよい)
        var pieces = new List<BreakPiece>();
        // 割れ目の中心は叩いた所。面を持つ壁を下から叩いた時は、絵が当たり判定の線より上に立っているので面の中ほどへ上げる。
        // 剥げかけていた壁は、そのひびの上にこの打撃の割れ目が重なる (前のひびをなぞって崩れる)
        Vector2 crackAt = normal.y < -0.5f ? hit + new Vector2(0f, CrackLift) : hit;
        var cracks = WallPeel.Cracks(hit);
        cracks.Add(StrikeCrack(crackAt, normal, e.Direction, e.Seed, default));
        string visual = cut > 0 ? DamageMap.Breach(shape, removed, p.Scorch, floorY, keep, body, pieces, cracks) : null;
        if (cut > 0) SolidMap.Carve(shape, keep, hit + normal * 0.1f); // 開いた所を歩ける所の地図に足す (蓋を作った後の壁で)
        if (cut > 0) WallPeel.Release(hit);
        // 壁が崩れ落ちて瓦礫の山になる (壁の線の少し奥を中心に、振った向きへ寄せて)
        // 全部が家具の裏で何も切れなかった時は崩さない (崩れた見た目なのに壁が残るのを避ける)
        if (cut == 0) TerrainFx.Chip(hit, dir, e.Seed);
        else if (visual == null)
            landings = TerrainFx.Crumble(hit + axis * (run * 0.3f), tangent, normal, axis, e.Force, length, e.Seed, pieces, removed,
                WallSegments.Snapshot(center, shape.BoundRadius + FxReach), hit + normal * 0.1f, given);
        LastCut = cut; LastPieces = pieces.Count; LastBlocks = landings.Length;
        return $"blunt breach cut={cut} caps={caps} ledges={ledges} pieces={pieces.Count} blocks={landings.Length} depth={depth:0.00} slant={MathF.Acos(cos) * 57.29578f:0} len={length:0.00} visual={visual ?? "ok"}";
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

    // 打撃の割れ目: 正面から叩くと丸く中心が細かく砕け、掠めるほど壁に沿って振った向きへ細長く伸び、先ほど大きく割れる。
    // 中心も振った向きの先へ少しずれる
    internal static CrackPattern StrikeCrack(Vector2 at, Vector2 normal, Vector2 swing, int seed, Rect area,
        float margin = FractureSites.Margin, int cap = FractureSites.Max, float shiftScale = 1f)
    {
        float g = FractureSites.Glance(normal, swing, out ushort along);
        Vector2 c = at + FractureSites.Dir(along) * (GlanceShift * shiftScale * g);
        return new CrackPattern(c, seed, area, margin, cap, along, 1f + GlanceStretch * g, GlanceBias * g, 0.75f + 0.25f * g);
    }

    private const float GlanceShift = 0.12f;   // 掠め打ちで割れ目の中心が振った向きへずれる距離
    private const float GlanceStretch = 1.6f;  // 掠め打ちで割れ目が伸びる倍率 (g = 1 で 2.6 倍)
    private const float GlanceBias = 0.35f;    // 掠め打ちで向きの先ほど大きく割れる割合

    // 力 0 → 0.7 倍・0.5 → 1 倍・1 → full 倍
    private static float ForceScale(float f, float full) => f < 0.5f ? 0.7f + 0.6f * f : 1f + (full - 1f) * (2f * f - 1f);

    // 範囲に掛かる壁を折れ線で返す。箱・多角形・円の壁はここで初めて折れ線に置き換える
    private static List<EdgeCollider2D> WallsNear(Vector2 c, float r)
    {
        var list = new List<EdgeCollider2D>();
        foreach (var c2 in Physics2D.OverlapCircleAll(c, r, WallMask))
        {
            if (!c2 || c2.isTrigger || !c2.enabled || IsProtected(c2, c)) continue;
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

    // 壊さない物: ゲームに関わる物 (当面)・家具や小物 (Ship 層に入っているマップがある)・マップの外周と地形
    private static readonly System.Text.RegularExpressions.Regex ProtectedName = new(
        @"^MrpRubbleBlock$|^MrpHullEdge$|^MrpLedge$|^MrpBreachMouth$|table|chair|desk|box|rock|ball|stand|panel|candle|parasite_|railing|mushroom|boundary|cliff|lava|^hole$|bridge|background|computer|office-|storage-|stump",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase); // Compiled は付けない (初回の破壊で 1 回だけ生成のために止まる・名前は短く数も少ない)

    // at の辺りで守るか。名前で守る物でも、マップの絵に「壊れてよい」と塗った所なら壊す。
    // 扉・この mod が置いた壁 (縁・船体の外周・瓦礫)・押されて動く家具は塗っても守る
    internal static bool IsProtected(Component c, Vector2 at)
    {
        if (!IsProtected(c)) return false;
        if (c.gameObject.name.StartsWith("Mrp", StringComparison.Ordinal) || c.GetComponentInParent<OpenableDoor>() || IsMovingFurniture(c) ||
            BreakableProps.Owns(c)) return true;
        return !MapNotes.InFree(at);
    }

    // 押されて動く家具 (当たり判定が家具と一緒に動くので、切ると動いた後の形と食い違う)
    private static bool IsMovingFurniture(Component c)
    {
        var ship = ShipStatus.Instance;
        return ship && FurnitureKinds.TryGet(c.transform, FurnitureKinds.ShipName(ship), out _);
    }

    internal static bool IsProtected(Component c)
    {
        if (c.GetComponentInParent<OpenableDoor>()) return true;
        if (IsMovingFurniture(c) || BreakableProps.Owns(c)) return true; // 物ごと壊れる家具は壁として削らない
        for (var t = c.transform; t && !t.GetComponent<ShipStatus>(); t = t.parent)
            if (ProtectedName.IsMatch(t.name)) return true;
        return false;
    }

    // 外壁 (向こう側が宇宙・マップの外) は壊さない (当面)。
    // 内壁には「奥の面」がある (部屋と部屋の隙間の向こうの枠・厚みのある壁の裏側)。壁の面から奥へ MaxDepth 以内に
    // 次の壁の面が無ければ、向こうは何も無い外側とみなす。部屋の範囲は屋外 (Polus など) を含まないので使わない
    private static bool HasFarSide(Vector2 surface, Vector2 inward) => FarSide(surface, inward) > 0f;

    // 面の向こう (inward の先、奥の面を探す深さ + 少し) に、船の外を通らずに試合の始めに歩けた床があるか。
    // 厚い外壁 (箱や二重線の壁) は手前の面から自分の裏の面が奥の面として見つかり、ミラでは空の隙間の向こうに
    // 別の部屋の床があるので、奥の面や床の有無だけでは外壁と分からない。地図が無い時は奥の面だけで決める (従来どおり)
    private static bool FloorBeyond(Vector2 surface, Vector2 inward)
        => !SolidMap.Valid || SolidMap.AlongFloorOrOutside(surface, inward, MaxDepth + FloorBeyondSlack) > 0;

    private const float FloorBeyondSlack = 0.4f; // 奥の面の先の床まで (面の継ぎ目と地図の升の誤差)
    private const float StrikeSkyReach = 1f; // 打撃で抜ける範囲の見積もり (叩いた点からこの距離の中で空とつながる壁の中は外壁)

    // 厚い壁 (船体の塊) を 1 打で掘り進む深さ
    private const float ThickStep = 1.0f;

    // 面の裏 (inward の先 ThickStep) が船体の塊 (エアシップの赤い板の中・歩けない所) か。塊の外 (空) へは掘らない
    private static bool ThickBehind(Vector2 surface, Vector2 inward)
    {
        if (!SolidMap.HasHull) return false;
        if (!SolidMap.Solid(surface + inward * 0.15f)) return false;
        for (float d = 0.15f; d <= ThickStep + 0.3f; d += 0.15f)
            if (!SolidMap.InHull(surface + inward * d)) return false;
        return true;
    }

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
            if (!h.collider || h.collider.isTrigger || IsProtected(h.collider, from)) continue;
            if (h.distance > 0.001f && h.distance < best) best = h.distance;
        }
        return best == float.MaxValue ? 0f : best;
    }

    // 点を含む部屋 (本編の部屋の範囲)。無ければ null (壁の中・屋外・宇宙)
    internal static PlainShipRoom RoomAt(Vector2 p)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return null;
        foreach (var r in ship.AllRooms)
            if (r && r.roomArea && r.roomArea.OverlapPoint(p)) return r;
        return null;
    }

    internal static bool FindWall(Vector2 from, Vector2 dir, float reach, out Vector2 point, out Vector2 normal)
    {
        point = default; normal = default;
        float best = float.MaxValue;
        float ledge = float.MaxValue; // 高さの違う床の縁 (壊れない)。縁の向こうの壁は叩けない
        foreach (var h in Physics2D.CircleCastAll(from, 0.1f, dir, reach, 1 << ShipLayer))
        {
            if (!h.collider || h.collider.isTrigger) continue;
            if (h.collider.gameObject.name == HeightLevels.LedgeName) { ledge = Math.Min(ledge, h.distance); continue; }
            if (IsProtected(h.collider, h.point) || h.distance >= best) continue;
            best = h.distance;
            point = h.point;
            normal = h.normal;
        }
        return best < float.MaxValue && best < ledge;
    }

    // Collider2D.ClosestPoint は Android の libunity に無いので、折れ線の頂点から自前で求める
    // 下から叩いた壁の面が左右どこまで続くか (叩いた点を通る、ほぼ水平で高さの揃った折れ線の連なり)。
    // 剥げかけのひびを壁の端の外 (扉の枠・隣の通路) へはみ出させないため。値は 1/64 に丸める (全員で同じ範囲)
    internal static bool FaceSpan(Vector2 hit, out float x0, out float x1)
    {
        x0 = x1 = hit.x;
        EdgeCollider2D best = null;
        int bestI = -1;
        float bestD = 0.0025f; // 叩いた点から 0.05 以内の線
        var pts = new List<Vector2>();
        foreach (var col in WallsNear(hit, 0.1f))
        {
            if (col.gameObject.layer != ShipLayer) continue;
            var t = col.transform;
            var raw = col.points;
            for (int i = 0; i + 1 < raw.Length; i++)
            {
                Vector2 a = t.TransformPoint(raw[i] + col.offset), b = t.TransformPoint(raw[i + 1] + col.offset), d = b - a;
                float l2 = d.x * d.x + d.y * d.y;
                float s = l2 > 0 ? Math.Clamp(((hit.x - a.x) * d.x + (hit.y - a.y) * d.y) / l2, 0f, 1f) : 0f;
                float ex = a.x + d.x * s - hit.x, ey = a.y + d.y * s - hit.y;
                if (ex * ex + ey * ey < bestD) { bestD = ex * ex + ey * ey; best = col; bestI = i; }
            }
        }
        if (!best) return false;
        {
            var t = best.transform;
            foreach (var p in best.points) pts.Add(t.TransformPoint(p + best.offset));
        }
        // 面の続き = ほぼ水平 (傾き 0.2 以下) で、両端が叩いた高さから 0.1 以内の区間
        bool Face(int i)
        {
            if (i < 0 || i + 1 >= pts.Count) return false;
            Vector2 a = pts[i], b = pts[i + 1];
            float dx = Math.Abs(b.x - a.x), dy = Math.Abs(b.y - a.y);
            return dx > 1e-4f && dy <= dx * 0.2f && Math.Abs(a.y - hit.y) <= 0.1f && Math.Abs(b.y - hit.y) <= 0.1f;
        }
        if (!Face(bestI)) return false;
        float lo = Math.Min(pts[bestI].x, pts[bestI + 1].x), hi = Math.Max(pts[bestI].x, pts[bestI + 1].x);
        for (int i = bestI - 1; Face(i); i--) { lo = Math.Min(lo, Math.Min(pts[i].x, pts[i + 1].x)); hi = Math.Max(hi, Math.Max(pts[i].x, pts[i + 1].x)); }
        for (int i = bestI + 1; Face(i); i++) { lo = Math.Min(lo, Math.Min(pts[i].x, pts[i + 1].x)); hi = Math.Max(hi, Math.Max(pts[i].x, pts[i + 1].x)); }
        x0 = MathF.Round(lo * 64f) / 64f;
        x1 = MathF.Round(hi * 64f) / 64f;
        return x1 > x0;
    }

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
