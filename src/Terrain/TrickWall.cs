using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 仕掛け壁: 後から床に立てる壁。歩き (層 9)・視界 (層 10)・水 (歩ける所の地図) を止め、ハンマーと爆発で物ごと壊れる
// (壊れ方は BreakableProps)。立てるのも地形の出来事 (DamageKind.Wall) として連番で配るので、水と破壊の順番が全員で揃う。
// ホストが置ける形に決め (床の上・端は壁まで・人や扉や端末に掛からない)、全員はその丸めた値だけで立てる
internal static class TrickWall
{
    public const float DefaultLength = 2.4f; // クルー 3 人分ほど
    public const float Thickness = 0.3f;
    private const float Height = 0.85f;      // 見た目の面の高さ (マップの壁と同じ)
    private const float MinLength = 0.6f;
    private const float EndOverlap = 0.1f;   // 端が壁に当たった時、その壁へ食い込ませる長さ (隙間を残さない)
    private const float PlayerClear = 0.3f;  // 人の足元からこれより近くには立てない
    private const float CarveMargin = 0.15f; // 壊れた時に歩ける所へ戻す範囲の余白 (地図は壁を太らせて作るため)
    private const float Ppu = 100f;
    private const float ProbeStep = 0.15f;   // 立てる所を調べる円の間隔 (円の半径 = 厚みの半分より細かく)
    private const float FaceSlope = 0.5f;    // 向きの x がこれ以上 (横から 60° 以内) の斜めの壁は、絵を縦の短冊に切って人と前後させる
    private const float ShadowInset = 0.07f; // 視界の線を絵の縁から内側へ寄せる量 (影のぼけの幅ほど)
    private const float FarSlack = 0.01f;    // 視点が辺の延長線上を滑る時に線が替わり続けない幅
    private const float BodyLift = 0.3636f;  // 人の奥行きは体の y/1000 で、体は足元 (当たり判定) よりこれだけ上
    private const int ShipLayer = 9;
    private const int ShadowLayer = 10;
    private const int FurnitureLayer = 12;
    internal const string Name = "MrpTrickWall";

    private static int _count; // 立てた数 (名前の番号・試合をまたいで増えるだけ)

    // ホスト: 置く依頼を置ける形に決める
    public static bool TryResolve(in DamageEvent e, out ResolvedDamage r, out string why)
    {
        r = default;
        why = null;
        if (!SolidMap.Ensure() || !SolidMap.Valid) { why = "wall no map"; return false; }
        Vector2 t = e.Direction.sqrMagnitude > 1e-6f ? e.Direction.normalized : Vector2.right;
        Vector2 c = e.Position;
        if (SolidMap.Solid(c)) { why = "wall not on floor"; return false; }
        float half = Math.Clamp(e.Size, MinLength, TerrainPermit.MaxWallLength) * 0.5f;
        float plus = Reach(c, t, half), minus = Reach(c, -t, half);
        if (plus + minus < MinLength) { why = "wall too short here"; return false; }
        c += t * ((plus - minus) * 0.5f);
        float len = plus + minus;
        c = TerrainWire.Q(c);
        t = TerrainWire.QNormal(t);
        len = TerrainWire.QSize(len);
        string bad = Blocked(c, t, len);
        if (bad != null) { why = "wall " + bad; return false; }
        r = new ResolvedDamage(DamageKind.Wall, c, Vector2.zero, t, 0f, len, 0, e.Seed);
        return true;
    }

    // 真ん中から dir へ、壁 (当たり判定) か歩けない所に当たるまでの長さ (当たれば少し食い込ませる)
    private static float Reach(Vector2 c, Vector2 dir, float half)
    {
        float end = half;
        foreach (var h in Physics2D.CircleCastAll(c, 0.01f, dir, half, 1 << ShipLayer)) // RaycastAll は Android の本編に無い
        {
            if (!h.collider || h.collider.isTrigger) continue;
            end = Math.Min(end, h.distance + EndOverlap);
        }
        const float step = 0.05f;
        for (float s = step; s < end; s += step)
            if (SolidMap.Solid(c.x + dir.x * s, c.y + dir.y * s)) { end = Math.Min(end, s + EndOverlap); break; }
        return end;
    }

    // 立てる所に掛かる物 (人・扉・通気口・端末・はしご・死体)。無ければ null
    private static string Blocked(Vector2 c, Vector2 t, float len)
    {
        var shape = new RectShape(c, t, len, Thickness);
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var data = pc.Data;
            if (data == null || data.IsDead || data.Disconnected) continue;
            var p = pc.GetTruePosition();
            if (shape.SignedDistance(p.x, p.y) < PlayerClear) return "player in the way";
        }
        // 壁の芯に沿って円を並べて調べる (OverlapBoxAll は Android の本編に無い)。
        // 端は壁へ食い込ませてあるので、その分を除いた所に船の層・家具の層の物があれば立てない
        float inner = Math.Max(0f, len * 0.5f - EndOverlap - 0.05f);
        int steps = (int)MathF.Ceiling(len * 0.5f / ProbeStep);
        for (int i = -steps; i <= steps; i++)
        {
            float s = Math.Clamp(i * ProbeStep, -len * 0.5f, len * 0.5f);
            Vector2 at = c + t * s;
            if (MathF.Abs(s) <= inner)
                foreach (var col in Physics2D.OverlapCircleAll(at, Thickness * 0.5f, (1 << ShipLayer) | (1 << FurnitureLayer)))
                    if (col && !col.isTrigger) return "something in the way";
            foreach (var col in Physics2D.OverlapCircleAll(at, Thickness * 0.5f + 0.05f, ~0))
            {
                if (!col) continue;
                if (col.GetComponentInParent<OpenableDoor>()) return "door in the way";
                if (col.GetComponentInParent<Vent>()) return "vent in the way";
                if (col.GetComponentInParent<global::Console>()) return "console in the way";
                if (col.GetComponentInParent<Ladder>()) return "ladder in the way";
                if (col.GetComponentInParent<DeadBody>()) return "body in the way";
            }
        }
        return null;
    }

    // 全員: 決まった形で立てる
    public static string Place(in ResolvedDamage r)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return "wall no ship";
        Vector2 c = r.Position, t = r.Direction, n = new(-t.y, t.x);
        float len = r.Size;
        Vector2 ht = t * (len * 0.5f), hn = n * (Thickness * 0.5f);
        // 足元の四角 (左回り)
        var foot = new[] { c - ht - hn, c + ht - hn, c + ht + hn, c - ht + hn };

        string name = $"{Name}{++_count}";
        var root = new GameObject(name);
        root.transform.SetParent(ship.transform, false);
        root.transform.position = FxMath.V3(c.x, c.y, 0f);

        var cols = new List<Collider2D>();
        var body = new GameObject(name + "-body") { layer = ShipLayer };
        body.transform.SetParent(root.transform, false);
        var poly = body.AddComponent<PolygonCollider2D>();
        poly.points = Local(body.transform, foot);
        cols.Add(poly);

        // 視界の線は視点 (自分の光) の場所ごとに選んで 1 本だけ出す (Shade・毎フレーム ShadeTick)
        Vector2 d = n.y < 0f ? n : -n, back = -d;
        var loop = new Vector2[5];
        for (int i = 0; i < 4; i++) loop[i] = foot[i];
        loop[4] = loop[0];
        AddShade(new Shade(name, root.transform, poly, Hull(foot), loop, MathF.Abs(t.x) >= FaceMin, c, back));

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var art = Art(root.transform, name, foot, c, t, len);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        float bx0 = Math.Min(Math.Min(foot[0].x, foot[1].x), Math.Min(foot[2].x, foot[3].x));
        float by0 = Math.Min(Math.Min(foot[0].y, foot[1].y), Math.Min(foot[2].y, foot[3].y));
        float bx1 = Math.Max(Math.Max(foot[0].x, foot[1].x), Math.Max(foot[2].x, foot[3].x));
        float by1 = Math.Max(Math.Max(foot[0].y, foot[1].y), Math.Max(foot[2].y, foot[3].y));

        int filled = SolidMap.Fill(new RectShape(c, t, len, Thickness));
        BreakableProps.AddWall(name, art.Master, art.Slices, cols, bx0, by0, bx1, by1,
            new RectShape(c, t, len + CarveMargin * 2f, Thickness + CarveMargin * 2f), art.Owned, art.Seeds);
        _riseGen = GameClock.ShipGen;
        Rising.Add(new Rise { Root = art.Root, ScaleX = art.ScaleX, ScaleY = art.ScaleY, Start = Environment.TickCount64, X = c.x, Y = c.y, Slices = art.Slices });
        BreakNoise.PlayAt("noise_rubble_metal", c.x, c.y, SoundRange, SoundMuffle, 0.55f, 0.75f);
        DustCloud.Kick(c + n * (Thickness * 0.5f + 0.3f), DustRadius, r.Seed);
        DustCloud.Kick(c - n * (Thickness * 0.5f + 0.3f), DustRadius, (ushort)(r.Seed + 1));
        string pushed = PushOut(c, t, n, len);
        double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"wall placed {name} at=({c.x:0.00},{c.y:0.00}) dir=({t.x:0.00},{t.y:0.00}) len={len:0.00} cells={filled} slices={art.Slices.Count}{pushed} art={(t1 - t0) * k:0.0}ms {ArtMs} rest={(System.Diagnostics.Stopwatch.GetTimestamp() - t1) * k:0.0}ms";
    }

    // 立った所に自分が掛かっていたら (届くまでの間に歩いて入った)、近い側の床へ出す。動かすのは自分の体だけ
    private static string PushOut(Vector2 c, Vector2 t, Vector2 n, float len)
    {
        var lp = PlayerControl.LocalPlayer;
        // はしご・昇降機・ジップラインの途中と会議中は本編が体を動かしているので触らない
        if (!lp || lp.Data == null || lp.Data.IsDead || lp.inVent || lp.inMovingPlat || lp.onLadder || MeetingHud.Instance) return "";
        Vector2 me = lp.GetTruePosition();
        float dx = me.x - c.x, dy = me.y - c.y;
        float along = dx * t.x + dy * t.y, across = dx * n.x + dy * n.y;
        if (MathF.Abs(along) > len * 0.5f + PushClear || MathF.Abs(across) > Thickness * 0.5f + PushClear) return "";
        float side = across >= 0f ? 1f : -1f;
        float want = side * (Thickness * 0.5f + PushClear + 0.05f) - across;
        float tx = me.x + n.x * want, ty = me.y + n.y * want;
        if (SolidMap.Solid(tx, ty))
        {
            if (!SolidMap.NearestFloor(tx, ty, 1.5f, 0.2f, out var floor)) return " push=none";
            tx = floor.x; ty = floor.y;
        }
        var rootPos = lp.transform.position;
        var to = FxMath.V2(tx - (me.x - rootPos.x), ty - (me.y - rootPos.y));
        var nt = lp.NetTransform;
        if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
            nt.SnapTo(to, (ushort)(nt.lastSequenceId + 328)); // 同じ sequence id だと古い位置として捨てられる
        else
            nt.RpcSnapTo(to);
        return $" push=({tx:0.00},{ty:0.00})";
    }

    // 床からせり上がる: 絵の根を下の縁を支点に縦へ伸ばし、少し行き過ぎて戻る。当たり判定は最初から全部ある
    private sealed class Rise
    {
        public Transform Root;
        public float ScaleX, ScaleY, X, Y;
        public long Start;
        public List<SpriteRenderer> Slices;
    }

    private static readonly List<Rise> Rising = new();
    private static int _riseGen = -1;

    // ---- 視界の線: 視点ごとに選ぶ ----
    // 影は見えている絵の縁から出す (マップの壁も、手前の面の上の縁と奥の側に視界の線がある)。どれも視点 (自分の光) に背を向けた
    // 辺だけをつないだ線で、手前を向いた辺まで入れるとその辺の影が絵に掛かる (壁の端の先から見ると壁が黒くなる)。
    // 手前 (面が見える側) からは外周 (足元の四角を面の高さだけ立ち上げた形) の線。奥からは面が見えないので上面の四角の線
    // (影は上の縁から出て面は影に入る)。視点が上面の上なら上面の手前の縁だけ、上面より手前 (光が絵の高さの中) なら足元の四角の線。
    // 面がほぼ見えない縦の壁は両側とも外周の線。外周や足元の中に視点が入ると、どの辺も背を向けていて周りが全部影になるので選び直す。
    // 線は辺の組ごとに初めて要った時に作り、1 本だけ有効にする
    private const int TopBit = 1 << 8, FootBit = 1 << 9, KeyTopFront = 1 << 10;
    private const float FaceMin = 0.05f; // 向きの x がこれ以上の壁は手前を向いた長い面を持つ

    private sealed class Shade
    {
        private readonly string _name;
        private readonly Transform _root;
        private readonly Collider2D _body;
        private readonly Vector2[] _hull, _top, _foot; // 閉じた左回りの輪
        private readonly float[] _hull4, _top4, _foot4; // 線と同じだけ内側へ寄せた輪の辺ごとの (始点 x, y, 外向きの法線 x, y)
        private readonly bool _hasFace;
        private readonly int _topFront; // 上面の手前の縁の辺の番号
        private readonly float _cx, _cy, _bx, _by;
        private readonly Dictionary<int, EdgeCollider2D> _lines = new();
        private EdgeCollider2D _on;
        public int Key = -1;

        public Shade(string name, Transform root, Collider2D body, Vector2[] hull, Vector2[] foot, bool hasFace, Vector2 c, Vector2 back)
        {
            _name = name; _root = root; _body = body; _hull = hull; _foot = foot; _hasFace = hasFace;
            _cx = c.x; _cy = c.y; _bx = back.x; _by = back.y;
            _top = new Vector2[foot.Length];
            for (int i = 0; i < foot.Length; i++) _top[i] = new Vector2(foot[i].x, foot[i].y + Height);
            _hull4 = Edges(Inset(hull, ShadowInset));
            _top4 = Edges(Inset(_top, ShadowInset));
            _foot4 = Edges(Inset(foot, ShadowInset));
            float best = float.MaxValue;
            for (int i = 0; i < _top4.Length / 4; i++)
            {
                float dot = _top4[i * 4 + 2] * back.x + _top4[i * 4 + 3] * back.y; // 手前の縁 = 外向きが奥と逆
                if (dot < best) { best = dot; _topFront = i; }
            }
        }

        private static float[] Edges(Vector2[] loop)
        {
            int n = loop.Length - 1;
            var e = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                Vector2 a = loop[i], b = loop[i + 1];
                float ex = b.x - a.x, ey = b.y - a.y, m = MathF.Sqrt(ex * ex + ey * ey);
                e[i * 4] = a.x; e[i * 4 + 1] = a.y;
                if (m > 1e-6f) { e[i * 4 + 2] = ey / m; e[i * 4 + 3] = -ex / m; }
            }
            return e;
        }

        // 視点に背を向けた辺 (視点が辺の外側にない) の組
        private static int FarMask(float[] e, float vx, float vy)
        {
            int mask = 0;
            for (int i = 0, n = e.Length / 4; i < n; i++)
                if ((vx - e[i * 4]) * e[i * 4 + 2] + (vy - e[i * 4 + 1]) * e[i * 4 + 3] <= FarSlack) mask |= 1 << i;
            return mask;
        }

        private static int All(float[] e) => (1 << (e.Length / 4)) - 1;

        // 使う線の番号。-1 = 替えない (視点が足元の中)
        public int KeyFor(float vx, float vy)
        {
            int m;
            if (_hasFace && (vx - _cx) * _bx + (vy - _cy) * _by >= 0f)
            {
                m = FarMask(_top4, vx, vy);
                if (m == All(_top4)) return KeyTopFront;
                if ((m >> _topFront & 1) != 0) return TopBit | m;
            }
            else
            {
                m = FarMask(_hull4, vx, vy);
                if (m != All(_hull4)) return m;
            }
            m = FarMask(_foot4, vx, vy);
            return m != All(_foot4) ? FootBit | m : -1;
        }

        private Vector2[] World(int key)
        {
            if (key == KeyTopFront) return new[] { _top[_topFront], _top[_topFront + 1] };
            if ((key & TopBit) != 0) return Chain(_top, key & 0xFF);
            if ((key & FootBit) != 0) return Chain(_foot, key & 0xFF);
            return Chain(_hull, key);
        }

        // 線を替える。壁が壊れていたら false (線を全部止める)
        public bool Switch(int key)
        {
            if (!_body || !_body.enabled)
            {
                if (_on) _on.enabled = false;
                return false;
            }
            if (!_lines.TryGetValue(key, out var line))
            {
                line = Shadow(_root, $"{_name}-shadow{key}", World(key));
                _lines[key] = line;
                if (!BreakableProps.AddWallCollider(_name, line)) { line.enabled = false; return false; }
            }
            if (_on) _on.enabled = false;
            line.enabled = true;
            _on = line;
            Key = key;
            return true;
        }

        // 閉じた輪のうち mask の辺をつないだ折れ線 (背を向けた辺は輪の上で 1 続き)
        private static Vector2[] Chain(Vector2[] loop, int mask)
        {
            int n = loop.Length - 1;
            if (mask == (1 << n) - 1) return loop;
            int start = 0;
            for (int i = 0; i < n; i++)
                if ((mask >> i & 1) != 0 && (mask >> ((i + n - 1) % n) & 1) == 0) { start = i; break; }
            var chain = new List<Vector2>(n + 1);
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;
                if ((mask >> i & 1) == 0) break;
                if (chain.Count == 0) chain.Add(loop[i]);
                chain.Add(loop[i + 1]);
            }
            return chain.ToArray();
        }
    }

    private static readonly List<Shade> Shades = new();
    private static int _shadeGen = -1;
    private static Transform _light;

    private static void AddShade(Shade s)
    {
        if (_shadeGen != GameClock.ShipGen) { Shades.Clear(); _light = null; _shadeGen = GameClock.ShipGen; }
        Shades.Add(s);
    }

    // 自分の光の場所で各壁の線を選ぶ。小数の計算だけで、Unity を触るのは光の場所の読み取りと線を替える時だけ
    private static void ShadeTick()
    {
        if (Shades.Count == 0) return;
        if (_shadeGen != GameClock.ShipGen) { Shades.Clear(); _light = null; return; }
        if (!_light)
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || !lp.lightSource) return;
            _light = lp.lightSource.transform; // 乗り移り中はダミーへ付け替えられる同じ Transform
        }
        var p = _light.position;
        float vx = p.x, vy = p.y;
        for (int i = Shades.Count - 1; i >= 0; i--)
        {
            var s = Shades[i];
            int key = s.KeyFor(vx, vy);
            if (key < 0 || key == s.Key) continue;
            if (!s.Switch(key)) Shades.RemoveAt(i);
        }
    }

    public static void Tick()
    {
        ShadeTick();
        if (Rising.Count == 0) return;
        if (_riseGen != GameClock.ShipGen) { Rising.Clear(); return; }
        long now = Environment.TickCount64;
        for (int i = Rising.Count - 1; i >= 0; i--)
        {
            var w = Rising[i];
            try { Step(w, now); }
            catch (Exception e)
            {
                // 失敗した壁は伸ばすのをやめる (毎フレーム同じ所で落ちて後ろの処理を止めない)
                Plugin.Logger.LogError($"[TrickWall] rise: {e}");
                Rising.Remove(w);
            }
        }
    }

    private static void Step(Rise w, long now)
    {
        if (!w.Root) { Rising.Remove(w); return; }
        float p = (now - w.Start) / RiseMs;
        if (p >= 1f)
        {
            w.Root.localScale = FxMath.V3(w.ScaleX, w.ScaleY, 1f);
            BreakNoise.PlayAt("noise_hit_metal", w.X, w.Y, SoundRange, SoundMuffle, 0.8f, 0.6f);
            Rising.Remove(w);
            foreach (var sr in w.Slices) PropSim.AddFront(sr); // 水面の板は伸び切った形で測る
            return;
        }
        // 後ろへ戻る曲線 (行き過ぎの山 ≈ 6%)
        float u = p - 1f, s = 1f + (RiseBack + 1f) * u * u * u + RiseBack * u * u;
        w.Root.localScale = FxMath.V3(w.ScaleX, w.ScaleY * FxMath.Max(s, 0.02f), 1f);
    }

    private const float RiseMs = 450f;
    private const float RiseBack = 1.3f;
    private const float SoundRange = 14f, SoundMuffle = 6f;
    private const float DustRadius = 0.9f;
    private const float PushClear = 0.25f; // 人の足元の半径ほど

    // ---- 見た目: 金属のパネルの仕切り ----
    // 足元の四角を面の高さだけ立ち上げた箱。太い輪郭・ベタ塗り・一段の影 (本編の壁の描き方)。
    // 手前の面は 0.6 ごとのパネル (継ぎ目の暗い線 + 明るい線・継ぎ目の両脇の上下にボルト)、上の縁の下に一段暗い帯、足元に暗い帯
    private static readonly byte[] LineColor = { 30, 32, 41 };
    private static readonly byte[] TopColor = { 169, 180, 198 };
    private static readonly byte[] TopSeam = { 128, 138, 156 };
    private static readonly byte[] FaceColor = { 110, 122, 144 };
    private static readonly byte[] FaceLip = { 140, 152, 172 };
    private static readonly byte[] FaceBand = { 96, 107, 128 };
    private static readonly byte[] FaceFoot = { 74, 82, 100 };
    private static readonly byte[] SeamDark = { 46, 52, 66 };
    private static readonly byte[] SeamLight = { 150, 160, 180 };
    private static readonly byte[] BoltColor = { 176, 186, 202 };
    private static readonly byte[] CapColor = { 84, 94, 112 };
    private const float PanelWidth = 0.6f;
    private const float LipH = 0.05f, BandH = 0.12f, FootH = 0.08f;
    private const float SeamW = 0.012f, SeamLightW = 0.01f;
    private const float BoltInset = 0.07f, BoltLow = 0.2f, BoltHigh = 0.28f, BoltR = 0.018f, BoltRim = 0.027f;
    private const int Outline = 3;          // 輪郭の太さ (画素)
    private const float SliceH = 0.25f;     // 縦・斜めの壁を人と前後させる帯の高さ
    private const float SliceRise = 0.2f;   // 斜めの壁の縦の短冊 1 本の中で、手前の縁の足元が上下する幅
    private const float SliceWMin = 0.08f;
    private const float SideMin = 0.01f;    // 面が見えるとみなす辺の横幅

    private static string ArtMs = ""; // 確認用 (bridge `wall` の返事): 絵を作る内訳

    private sealed class WallArt
    {
        public Transform Root;
        public float ScaleX, ScaleY;
        public SpriteRenderer Master;       // 欠片を切り出す元 (描かない)
        public readonly List<SpriteRenderer> Slices = new();
        public readonly List<UnityEngine.Object> Owned = new();
        public readonly List<Vector2> Seeds = new(); // 欠片の細胞の種 (絵の画素)
    }

    private static unsafe WallArt Art(Transform root, string name, Vector2[] foot, Vector2 c, Vector2 t, float len)
    {
        long tp0 = System.Diagnostics.Stopwatch.GetTimestamp();
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in foot) { x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); }
        float footLo = y0, footHi = y1;
        y1 += Height;
        float pad = (Outline + 1f) / Ppu;
        x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
        int w = (int)MathF.Ceiling((x1 - x0) * Ppu), h = (int)MathF.Ceiling((y1 - y0) * Ppu);

        // 画素の回りは Unity の算術を通さず float だけで回す (Vector2 の生成・演算子は 1 回ごとに interop)
        float tx = t.x, ty = t.y, nx = -t.y, ny = t.x;
        float sx0 = c.x - tx * len * 0.5f, sy0 = c.y - ty * len * 0.5f; // 長さの始まり (u = 0)
        var fx = new float[4];
        var fy = new float[4];
        for (int i = 0; i < 4; i++) { fx[i] = foot[i].x; fy[i] = foot[i].y; }
        // 側面: 外向きの法線が下 (手前) を向いた辺 (x が増える向きの辺) だけが見える。辺 → 立ち上げた四角。辺 1・3 は厚みの辺 (端の面)
        var qx4 = new float[16];
        var qy4 = new float[16];
        var sideOn = new bool[4];
        for (int e = 0; e < 4; e++)
        {
            int j = (e + 1) & 3;
            sideOn[e] = fx[j] - fx[e] > SideMin; // ほぼ縦の辺は見えない扱い (端末ごとの丸めで符号が割れない)
            qx4[e * 4] = fx[e]; qy4[e * 4] = fy[e];
            qx4[e * 4 + 1] = fx[j]; qy4[e * 4 + 1] = fy[j];
            qx4[e * 4 + 2] = fx[j]; qy4[e * 4 + 2] = fy[j] + Height;
            qx4[e * 4 + 3] = fx[e]; qy4[e * 4 + 3] = fy[e] + Height;
        }
        int panels = Math.Max(1, (int)MathF.Round(len / PanelWidth));
        float panel = len / panels;

        // 画素ごとの面: 0 = 無し / 1 = 上面 / 2.. = 手前を向いた側面 (足元の辺の番号 + 2)。行ごとに四角の区間を出して塗る
        // (側面を先に塗り、上面で上書きする)。色は面の中の位置 (u = 長さの向き・v = 床からの高さ) から
        var face = new byte[w * h];
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float qy = y0 + (y + 0.5f) / Ppu;
            int row = y * w;
            for (int e = 0; e < 4; e++)
                if (sideOn[e]) FillSpan(face, row, w, x0, qy, qx4, qy4, e * 4, (byte)(e + 2));
            FillSpan(face, row, w, x0, qy - Height, fx, fy, 0, 1);
            for (int x = 0; x < w; x++)
            {
                byte f = face[row + x];
                if (f == 0) continue;
                float qx = x0 + (x + 0.5f) / Ppu;
                byte[] col;
                if (f == 1)
                {
                    float u = (qx - sx0) * tx + (qy - Height - sy0) * ty;
                    col = NearSeam(u, panel, panels, SeamW * 0.7f) ? TopSeam : TopColor;
                }
                else
                {
                    int e = f - 2, j = (e + 1) & 3;
                    float k = (qx - fx[e]) / (fx[j] - fx[e]);
                    float ey = fy[e] + (fy[j] - fy[e]) * k, v = qy - ey;
                    if ((e & 1) == 1) col = v < FootH ? FaceFoot : CapColor;
                    else col = FacePixel((fx[e] + (fx[j] - fx[e]) * k - sx0) * tx + (ey - sy0) * ty, v, panel, panels);
                }
                int i4 = (row + x) * 4;
                px[i4] = col[0]; px[i4 + 1] = col[1]; px[i4 + 2] = col[2]; px[i4 + 3] = 255;
            }
        }
        // 輪郭 (形の外まで Outline 画素以内) と面の境 (隣の画素が別の面)。横の向きの「形の外までの距離」を先に出し、縦は ±Outline 行だけ見る
        const int far = Outline + 1;
        var hd = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, d = far;
            for (int x = 0; x < w; x++) { d = face[row + x] == 0 ? 0 : Math.Min(d + 1, far); hd[row + x] = (byte)d; }
            d = far;
            for (int x = w - 1; x >= 0; x--) { d = face[row + x] == 0 ? 0 : Math.Min(d + 1, far); if (d < hd[row + x]) hd[row + x] = (byte)d; }
        }
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int k = y * w + x;
            byte f = face[k];
            if (f == 0) continue;
            bool line = false;
            for (int dy = -Outline; dy <= Outline && !line; dy++)
            {
                int yy = y + dy;
                // 絵の端の外は形の外 (縦の向き)
                int dx = yy < 0 || yy >= h ? 0 : hd[yy * w + x];
                if (dx * dx + dy * dy <= Outline * Outline + 1) line = true;
            }
            for (int dy = -1; dy <= 1 && !line; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                byte g = face[yy * w + xx];
                if (g != 0 && g != f) { line = true; break; }
            }
            if (!line) continue;
            int i4 = k * 4;
            px[i4] = LineColor[0]; px[i4 + 1] = LineColor[1]; px[i4 + 2] = LineColor[2];
        }

        long ta = System.Diagnostics.Stopwatch.GetTimestamp();
        var art = new WallArt();
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, false); // 壊れた時に欠片を切り出すので読めるまま
        art.Owned.Add(tex);
        var full = Sprite.Create(tex, new Rect(0f, 0f, w, h), Vector2.zero, Ppu, 0, SpriteMeshType.FullRect);
        full.name = name;
        art.Owned.Add(full);

        // 船が反転していても絵は反転させない (根の倍率を打ち消す)。奥行きも船の倍率が掛かるので打ち消す (人の奥行きは倍率なしの y/1000)
        Vector3 ls = root.lossyScale;
        float scx = 1f / ls.x, scy = 1f / ls.y, scz = 1f / ls.z;

        // 欠片を切り出す元: 絵の左下に置いて描かない (伸びる途中で壊れても欠片は伸び切った形から)
        var master = new GameObject(name + "-whole");
        master.transform.SetParent(root, false);
        master.transform.position = FxMath.V3(x0, y0, footLo / 1000f);
        master.transform.localScale = FxMath.V3(scx, scy, 1f);
        art.Master = master.AddComponent<SpriteRenderer>();
        art.Master.sprite = full;
        art.Master.enabled = false;

        // 描く絵: 伸びる支点 (絵の下の縁) に根を置き、切った欠片ごとに人と前後させる (奥行きは人と同じ決め方 = 足元の y に体の高さを足した y)。
        // 横の壁は 1 枚。手前の面が見える斜めの壁は縦の短冊に切り、短冊の奥行きは手前の縁の足元 (面の画素の足元はその x の縁・上面はそれより奥)。
        // 縦に近い壁は横の帯に切る (見えるのはほぼ上面だけで、帯の中の上面の画素は、足元が帯の下の縁から面の高さだけ下にある)
        var rise = new GameObject(name + "-art"); // 絵は層 0 (影のカメラは層 9 の物を自前の描き方で描き直す)
        rise.transform.SetParent(root, false);
        rise.transform.position = FxMath.V3(x0, y0, 0f);
        rise.transform.localScale = FxMath.V3(scx, scy * 0.02f, 1f);
        art.Root = rise.transform;
        art.ScaleX = scx;
        art.ScaleY = scy;
        float ax = MathF.Abs(tx), ay = MathF.Abs(ty);
        if (ay >= 0.05f && ax >= FaceSlope)
        {
            // 見えている長い辺 (0 か 2) の x の範囲で足元の y を出す (範囲の外は端の値)
            int e = sideOn[0] ? 0 : 2, j = e + 1;
            float ex0 = fx[e], ey0 = fy[e], edx = fx[j] - fx[e], edy = fy[j] - fy[e];
            int cols = Math.Max(1, (int)(Math.Clamp(SliceRise * ax / ay, SliceWMin, SliceH) * Ppu));
            for (int c0 = 0; c0 < w; c0 += cols)
            {
                int cw = Math.Min(cols, w - c0);
                bool any = false;
                for (int y = 0; y < h && !any; y++)
                    for (int k = y * w + c0, end = k + cw; k < end && !any; k++) any = face[k] != 0;
                if (!any) continue;
                float xa = x0 + (float)c0 / Ppu, xb = x0 + (float)(c0 + cw) / Ppu;
                float ya = ey0 + edy * Math.Clamp((xa - ex0) / edx, 0f, 1f), yb = ey0 + edy * Math.Clamp((xb - ex0) / edx, 0f, 1f);
                AddSlice(art, tex, rise.transform, name + "-c" + c0, new Rect(c0, 0f, cw, h), c0 / Ppu, 0f, Math.Max(ya, yb), scz);
            }
        }
        else
        {
            int rows = ay < 0.05f ? h : Math.Max(1, (int)(SliceH * Ppu));
            for (int r0 = 0; r0 < h; r0 += rows)
            {
                int rh = Math.Min(rows, h - r0);
                bool any = false;
                for (int k = r0 * w, end = (r0 + rh) * w; k < end && !any; k++) any = face[k] != 0;
                if (!any) continue;
                float footY = Math.Clamp(y0 + (float)r0 / Ppu - Height, footLo, footHi);
                AddSlice(art, tex, rise.transform, name + "-" + r0, new Rect(0f, r0, w, rh), 0f, r0 / Ppu, footY, scz);
            }
        }

        double kk = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ArtMs = $"(px={(ta - tp0) * kk:0.0} sprites={(System.Diagnostics.Stopwatch.GetTimestamp() - ta) * kk:0.0} {w}x{h})";
        // 欠片の種: 見えている面の上に並べる (絵の四角の透明な所を割らない)。手前の面が無い縦の壁は上面に
        int pieces = Math.Max(4, panels * 2 + 2);
        bool front = sideOn[0] || sideOn[2];
        float half = Thickness * 0.5f, fs = sideOn[0] ? -1f : 1f;
        for (int i = 0; i < pieces; i++)
        {
            float u = (i + 0.5f) / pieces * len, hi = (i & 1) == 0 ? 0.3f : 0.7f;
            float wx = sx0 + tx * u, wy = sy0 + ty * u;
            if (front) { wx += nx * half * fs; wy += ny * half * fs + Height * hi; }
            else { wx += nx * half * (hi - 0.5f); wy += ny * half * (hi - 0.5f) + Height; }
            art.Seeds.Add(new Vector2((wx - x0) * Ppu, (wy - y0) * Ppu));
        }
        return art;
    }

    // 絵の一部を切り出した欠片を 1 枚足す (footY = 人と前後させる足元の y)
    private static void AddSlice(WallArt art, Texture2D tex, Transform parent, string name, Rect rect, float lx, float ly, float footY, float scz)
    {
        var sp = Sprite.Create(tex, rect, Vector2.zero, Ppu, 0, SpriteMeshType.Tight);
        sp.name = name;
        art.Owned.Add(sp);
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(lx, ly, (footY + BodyLift) / 1000f * scz);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        art.Slices.Add(sr);
    }

    // 手前の面の 1 画素の色 (u = 長さの向きの位置・v = 床からの高さ)
    private static byte[] FacePixel(float u, float v, float panel, int panels)
    {
        if (v < FootH) return FaceFoot;
        if (v > Height - LipH) return FaceLip;
        // ボルト: 各パネルの両端の内側の上下
        int pi = Math.Clamp((int)(u / panel), 0, panels - 1);
        float pa = pi * panel, pb = pa + panel;
        float du = u - (u - pa < pb - u ? pa + BoltInset : pb - BoltInset);
        for (int b = 0; b < 2; b++)
        {
            float dv = v - (b == 0 ? BoltLow : Height - BoltHigh), d2 = du * du + dv * dv;
            if (d2 < BoltR * BoltR) return BoltColor;
            if (d2 < BoltRim * BoltRim) return SeamDark;
        }
        // 継ぎ目 (暗い線と、その右の明るい線)
        for (int k = 1; k < panels; k++)
        {
            float d = u - k * panel;
            if (MathF.Abs(d) < SeamW) return SeamDark;
            if (d >= SeamW && d < SeamW + SeamLightW) return SeamLight;
        }
        return v > Height - LipH - BandH ? FaceBand : FaceColor;
    }

    private static bool NearSeam(float u, float panel, int panels, float half)
    {
        for (int k = 1; k < panels; k++) if (MathF.Abs(u - k * panel) < half) return true;
        return false;
    }

    // 足元の四角と、それを面の高さだけ上げた四角を合わせた凸包 (閉じた輪)
    private static Vector2[] Hull(Vector2[] foot)
    {
        var pts = new List<Vector2>(8);
        foreach (var f in foot) { pts.Add(f); pts.Add(new Vector2(f.x, f.y + Height)); }
        pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        var h = new List<Vector2>(16);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = h.Count;
            for (int k = 0; k < pts.Count; k++)
            {
                var p = pts[pass == 0 ? k : pts.Count - 1 - k];
                while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], p) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(p);
            }
            h.RemoveAt(h.Count - 1);
        }
        h.Add(h[0]);
        return h.ToArray();
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    private static EdgeCollider2D Shadow(Transform root, string name, Vector2[] world)
    {
        var go = new GameObject(name) { layer = ShadowLayer };
        go.transform.SetParent(root, false);
        var edge = go.AddComponent<EdgeCollider2D>();
        edge.points = Local(go.transform, Inset(world, ShadowInset));
        return edge;
    }

    // 左回りの線 (閉じた輪は最初の点を最後にも持つ) を内側へ by だけ寄せる。影の板はぼかして描かれるので、
    // 線を絵の縁ぴったりに置くと縁の外に明るい帯が残る (マップの壁も視界の線は絵の少し内側にある)
    private static Vector2[] Inset(Vector2[] pts, float by)
    {
        int n = pts.Length;
        bool closed = n > 2 && pts[0] == pts[n - 1];
        int m = closed ? n - 1 : n;
        var o = new Vector2[n];
        for (int i = 0; i < m; i++)
        {
            // 前後の辺の内向きの法線 (左回りなので辺の左)
            bool hasPrev = closed || i > 0, hasNext = closed || i < m - 1;
            Vector2 a = pts[(i - 1 + m) % m], p = pts[i], b = pts[(i + 1) % m];
            float nx = 0f, ny = 0f, k = 0;
            if (hasPrev) { LeftNormal(a, p, out float lx, out float ly); nx += lx; ny += ly; k++; }
            if (hasNext) { LeftNormal(p, b, out float lx, out float ly); nx += lx; ny += ly; k++; }
            if (k == 2)
            {
                // 角では二つの辺の両方から by 離れる点 (法線の和を 1 + 内積で割る)
                float dot = nx * nx + ny * ny - 2f; // |n1 + n2|^2 - 2 = 2 n1・n2
                float scale = by / FxMath.Max(1f + dot * 0.5f, 0.2f);
                o[i] = new Vector2(p.x + nx * scale, p.y + ny * scale);
            }
            else o[i] = new Vector2(p.x + nx * by, p.y + ny * by);
        }
        if (closed) o[n - 1] = o[0];
        return o;
    }

    private static void LeftNormal(Vector2 a, Vector2 b, out float x, out float y)
    {
        float ex = b.x - a.x, ey = b.y - a.y, len = MathF.Sqrt(ex * ex + ey * ey);
        if (len < 1e-6f) { x = y = 0f; return; }
        x = -ey / len; y = ex / len;
    }

    // ワールドの点の並び → その物の中の点 (船が左右反転していても形はワールドのまま)
    private static Vector2[] Local(Transform tr, Vector2[] world)
    {
        var o = new Vector2[world.Length];
        for (int i = 0; i < world.Length; i++) o[i] = tr.InverseTransformPoint(FxMath.V3(world[i].x, world[i].y, 0f));
        return o;
    }

    // 凸の四角 (xs/ys の o から 4 点) と高さ qy の横線が重なる区間の画素を v で塗る (画素の真ん中が区間に入る物)
    private static void FillSpan(byte[] face, int row, int w, float x0, float qy, float[] xs, float[] ys, int o, byte v)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            float ax = xs[o + i], ay = ys[o + i], bx = xs[o + j], by = ys[o + j];
            if ((ay > qy) == (by > qy)) continue;
            float x = ax + (bx - ax) * (qy - ay) / (by - ay);
            if (x < lo) lo = x;
            if (x > hi) hi = x;
        }
        if (hi < lo) return;
        int a = Math.Max(0, (int)MathF.Ceiling((lo - x0) * Ppu - 0.5f)), b = Math.Min(w - 1, (int)MathF.Floor((hi - x0) * Ppu - 0.5f));
        for (int x = a; x <= b; x++) face[row + x] = v;
    }

    internal static void Register()
    {
        TestBridge.Register("wall", "<x> <y> <角度°> [長さ] | me [角度°]  仕掛け壁を立てる (x y = プレイヤーでない壁・ホストか一人の時 / me = 自分の依頼として向いている方向の先に・客も可。0° = 横・90° = 縦)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (a.Length >= 1 && a[0] == "me")
            {
                float? d = a.Length >= 2 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, inv, out float dv) ? dv : null;
                var mine = SandboxCommands.WallAhead(d);
                reply($"{(mine.Ok ? "OK" : "ERR")} wall me {mine.Why}");
                return;
            }
            if (a.Length < 3 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float, inv, out float x)
                || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, inv, out float y)
                || !float.TryParse(a[2], System.Globalization.NumberStyles.Float, inv, out float deg))
            { reply("ERR wall <x> <y> <deg> [len]"); return; }
            float len = a.Length >= 4 && float.TryParse(a[3], System.Globalization.NumberStyles.Float, inv, out float l) ? l : DefaultLength;
            float rad = deg * (MathF.PI / 180f);
            var res = TerrainApi.WorldWall(new Vector2(x, y), new Vector2(MathF.Cos(rad), MathF.Sin(rad)), len);
            reply($"{(res.Ok ? "OK" : "ERR")} wall {res.Why}");
        });
    }
}
