using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 粉塵: 壁を叩いた点・崩れた点・爆発した点から砂煙が上がる。破壊が適用されるたびに全員の手元で作る (電文は増やさない)。
// 煙は歩ける所の地図 (SolidMap) の上を当たった点から塗り広げるので、壁で止まり、出入口や開けた穴から流れ込む。
// 広がる量 (升の数) は種類ごとの半径の円の面積で、狭い所ほど遠くまで流れる (半径の 2.5 倍まで)。
// 閉じた扉は地図に入っていないので抜ける。穴の開かない打撃は叩いた側だけに出る (種を面の手前に置く)。
// 煙の中では自分の視界が狭くなる (幽霊は除く)。中に続けて 0.5 秒いた人は、出てから 8 秒間足跡を残す (全員に見える)。
// 濃さと中にいるかは升の距離の表を引くだけ。人の位置は 0.1 秒ごとに 1 回読む。足跡の位置は端末ごとに少しずれてよい
internal static class DustCloud
{
    private enum Kind { Hit, Crumble, Boom }

    private readonly struct Spec
    {
        public readonly float Radius, Expand, Hold, Fade, Alpha;
        public readonly int Puffs;
        public Spec(float radius, float expand, float hold, float fade, int puffs, float alpha)
        {
            Radius = radius; Expand = expand; Hold = hold; Fade = fade; Puffs = puffs; Alpha = alpha;
        }
    }

    // 爆発の Radius は爆発の半径に掛ける倍率。Alpha = 板の濃さ (叩いた煙は小さいので濃くして床に溶けないように)
    private static readonly Spec[] Specs =
    {
        new(1.4f, 0.6f, 2f, 1.5f, 6, 0.65f),
        new(2.0f, 0.6f, 6f, 3f, 9, 0.5f),
        new(1.4f, 0.4f, 7f, 3f, 12, 0.5f),
    };

    private const int MaxClouds = 4;
    private const long BurstWindowMs = 300;  // 途中から入った時などに過去の破壊がまとめて届いても、塗り広げは窓あたり MaxClouds 回まで
    private const float MaxRadius = 4.5f;
    private const float ReachCap = 2.5f;      // 狭い所で流れてよい距離 (半径の倍率)
    private const float SeedBack = 0.3f;      // 穴の開かない打撃の種を面から叩いた側へ戻す距離
    private const float SeedSearch = 0.5f;    // 種の点が歩けない時に近くの歩ける升を探す範囲

    private const float PuffZ = -1f;          // クルー (z ≈ y/1000) より手前・影の板 (-5) より奥 = 影の中では見えない
    private const float VisionCenter = 0.5f;  // 煙の中心での視界の倍率 (縁で 0.85 前後)
    private const float VisionRate = 2.5f;    // 視界の倍率が追いつく速さ (1/秒・0.4 秒)
    private const float DustMin = 0.15f;      // これより薄い所は「中」に数えない

    private const float ScanInterval = 0.1f;
    private const float DustAfter = 0.5f;     // 煙の中に続けてこれだけいたら粉が付く
    private const float PrintsFor = 8f;       // 煙から出た後、足跡を残す秒数
    private const float StepDist = 0.5f;      // 足跡の間隔
    private const float StepSide = 0.07f;     // 左右の足のずれ
    private const float PrintLength = 0.26f;
    private const float PrintLife = 12f;
    private const float PrintAlphaFresh = 0.55f, PrintAlphaOld = 0.2f;
    private const int MaxPrints = 60;

    private sealed class Puff
    {
        public Transform Tr;
        public SpriteRenderer Sr;
        public float X, Y, D, Size, Vx, Vy, Z, Alpha = -1f;
    }

    private sealed class Cloud : FloorFlood.Area
    {
        public Kind Kind;
        public Spec Spec;
        public float T;
        public readonly List<Puff> Puffs = new();
    }

    private sealed class Print
    {
        public Transform Tr;
        public SpriteRenderer Sr;
        public float Age = PrintLife, Alpha0;
        public float R, G, B;
    }

    private static readonly List<Cloud> Clouds = new();
    private static readonly Queue<long> Recent = new();
    private static readonly Print[] Prints = new Print[MaxPrints];
    private static int _nextPrint, _livePrints;
    private static readonly float[] InDust = new float[256];
    private static readonly float[] DustyUntil = new float[256];
    private static readonly float[] LastX = new float[256], LastY = new float[256], Walked = new float[256];
    private static readonly bool[] Tracked = new bool[256];
    private static readonly bool[] LeftFoot = new bool[256];
    private static bool _anyDusty;
    private static IntPtr _matShip;
    private static int _shipGen;
    private static Color _tint;
    private static long _lastMs;
    private static float _clock, _scanAcc, _drawAcc, _visionTarget = 1f;

    // 自分の視界に掛ける倍率 (1 = 掛けない)。VisionPatch が読む
    internal static float VisionMul { get; private set; } = 1f;
    internal static byte LocalId { get; private set; } = 255;

    // テスト用
    internal static int Made;
    internal static string Last = "-";

    // 破壊を適用した直後に呼ぶ (ホスト・客・一人の全員)
    public static void OnApplied(in ResolvedDamage r)
    {
        try { Add(r); }
        catch (Exception e) { Plugin.Logger.LogError($"[DustCloud] {e}"); }
    }

    private static void Add(in ResolvedDamage r)
    {
        if (!ShipStatus.Instance || MeetingHud.Instance) return;
        long now = Environment.TickCount64;
        while (Recent.Count > 0 && now - Recent.Peek() > BurstWindowMs) Recent.Dequeue();
        if (Recent.Count >= MaxClouds) { Last = "dropped (burst)"; return; }
        Recent.Enqueue(now);
        Kind kind;
        if (r.Kind == DamageKind.Explosion) kind = Kind.Boom;
        else if (r.Hp <= 0) kind = Kind.Crumble;
        else kind = Kind.Hit;
        var spec = Specs[(int)kind];
        float radius = kind == Kind.Boom ? Math.Min(MaxRadius, r.Size * spec.Radius) : spec.Radius;

        Vector2 seed = r.Position;
        if (kind == Kind.Hit) { seed.x += r.Normal.x * SeedBack; seed.y += r.Normal.y * SeedBack; }
        else if (kind == Kind.Crumble) { seed.x += r.Normal.x * 0.15f; seed.y += r.Normal.y * 0.15f; }

        // 種は 1 点でなく範囲: 爆発は爆心から半径の半分・崩れた所は穴の両側に届く幅。
        // 1 点だと壁の中の切り抜いた隙間や船の外側の袋小路に入って、部屋へ出てこない
        float spread = SeedSearch;
        if (kind == Kind.Boom) spread = Math.Max(SeedSearch, r.Size * 0.5f);
        else if (kind == Kind.Crumble) spread = Math.Max(SeedSearch, r.Size * 0.6f);
        var c = FloorFlood.Fill<Cloud>(seed, radius, ReachCap, spread, kind == Kind.Hit);
        if (c == null) { Last = $"{kind} @{seed.x:0.0},{seed.y:0.0} no floor"; return; }
        c.Kind = kind;
        c.Spec = spec;
        EnsureTint();
        try { MakePuffs(c, radius, r.Seed); }
        catch { Destroy(c); throw; }
        while (Clouds.Count >= MaxClouds) { Destroy(Clouds[0]); Clouds.RemoveAt(0); }
        Clouds.Add(c);
        Made++;
        Last = $"{kind} @{seed.x:0.0},{seed.y:0.0} r={radius:0.0} reach={c.Reach:0.0} cells={c.Cells} puffs={c.Puffs.Count}";
    }

    // ── 塗り広げ ───────────────────────────────────────────────────────

    // その点の濃さ 0..1 (中心 1・届いた端で 0.25・広がる前と薄れた後は 0)
    private static float Density(Cloud c, float px, float py)
    {
        float ppu = SolidMap.Valid ? SolidMap.Ppu : 1f / DamageMap.TexelSize;
        float ox = SolidMap.Valid ? SolidMap.Origin.x : 0f, oy = SolidMap.Valid ? SolidMap.Origin.y : 0f;
        int x = (int)MathF.Floor((px - ox) * ppu) - c.X0, y = (int)MathF.Floor((py - oy) * ppu) - c.Y0;
        if (x < 0 || y < 0 || x >= c.W || y >= c.H) return 0f;
        ushort raw = c.Dist[y * c.W + x];
        if (raw == ushort.MaxValue) return 0f;
        float d = raw / (float)FloorFlood.CostStraight / ppu;
        float front = Front(c);
        if (d > front) return 0f;
        float edge = Math.Min(1f, (front - d) / 0.3f);
        return (1f - 0.75f * d / c.Reach) * edge * Envelope(c);
    }

    private static float Front(Cloud c)
    {
        float u = Math.Min(1f, c.T / c.Spec.Expand);
        return c.Reach * (1f - (1f - u) * (1f - u));
    }

    private static float Envelope(Cloud c)
    {
        float t = c.T - c.Spec.Expand - c.Spec.Hold;
        return t <= 0f ? 1f : Math.Max(0f, 1f - t / c.Spec.Fade);
    }

    private static float Life(Cloud c) => c.Spec.Expand + c.Spec.Hold + c.Spec.Fade;

    // ── 煙の絵 ─────────────────────────────────────────────────────────

    // 煙の板は届いた升を近い順に並べて等間隔に拾った所に置く (= 広がった形の上に均等に散る)
    private static void MakePuffs(Cloud c, float radius, ushort seed)
    {
        var order = new List<int>(c.Cells);
        for (int i = 0; i < c.Dist.Length; i++)
            if (c.Dist[i] != ushort.MaxValue) order.Add(i);
        if (order.Count == 0) return;
        order.Sort((a, b) => c.Dist[a].CompareTo(c.Dist[b]));

        float ppu = SolidMap.Valid ? SolidMap.Ppu : 1f / DamageMap.TexelSize;
        float ox = SolidMap.Valid ? SolidMap.Origin.x : 0f, oy = SolidMap.Valid ? SolidMap.Origin.y : 0f;
        var rnd = new System.Random(seed);
        int n = c.Spec.Puffs;
        float area = order.Count / (ppu * ppu);
        float size = Math.Clamp(MathF.Sqrt(area / n) * 1.8f, 0.7f, 2.4f);
        var sprite = DebrisArt.DustBlob;
        float sw = Math.Max(0.0001f, sprite.bounds.size.x);
        float zBase = PuffZ - 0.02f * (Made % MaxClouds);
        for (int i = 0; i < n; i++)
        {
            float f = (i + 0.2f + 0.6f * (float)rnd.NextDouble()) / n;
            int k = order[Math.Min(order.Count - 1, (int)(f * order.Count))];
            var p = new Puff
            {
                X = ox + (c.X0 + k % c.W + 0.5f) / ppu,
                Y = oy + (c.Y0 + k / c.W + 0.5f) / ppu,
                D = c.Dist[k] / (float)FloorFlood.CostStraight / ppu,
                Size = size * (0.8f + 0.4f * (float)rnd.NextDouble()) / sw,
                Vx = ((float)rnd.NextDouble() - 0.5f) * 0.12f,
                Vy = ((float)rnd.NextDouble() - 0.5f) * 0.12f + 0.03f,
                Z = zBase - 0.001f * i,
            };
            var go = new GameObject("MrpDust") { layer = 0 };
            p.Tr = go.transform;
            p.Sr = go.AddComponent<SpriteRenderer>();
            p.Sr.sprite = sprite;
            p.Sr.color = FxMath.Rgba(_tint.r, _tint.g, _tint.b, 0f);
            p.Tr.position = FxMath.V3(p.X, p.Y, p.Z);
            p.Tr.localRotation = FxMath.RotZ((float)rnd.NextDouble() * 360f);
            p.Tr.localScale = FxMath.V3(p.Size * 0.5f, p.Size * 0.5f, 1f);
            c.Puffs.Add(p);
        }
    }

    // 煙の色: マップの壁の素材の粉 (船体の金属 = 灰・岩 = 赤茶・木 = 黄土)
    private static void EnsureTint()
    {
        var ship = ShipStatus.Instance;
        if (ship.Pointer == _matShip) return;
        _matShip = ship.Pointer;
        _tint = ship.Type switch
        {
            ShipStatus.MapType.Pb => FxMath.Rgba(0.74f, 0.56f, 0.44f, 1f),
            ShipStatus.MapType.Fungle => FxMath.Rgba(0.74f, 0.64f, 0.5f, 1f),
            _ => FxMath.Rgba(0.68f, 0.65f, 0.6f, 1f),
        };
    }

    private static void DrawCloud(Cloud c, float dt)
    {
        float front = Front(c), env = Envelope(c);
        float grow = 0.5f + 0.5f * Math.Min(1f, c.T / c.Spec.Expand) + 0.25f * Math.Min(1f, c.T / Life(c));
        // 板は Clear / Destroy でしか消さないので、ここでは生存確認 (Unity の ==) をしない
        foreach (var p in c.Puffs)
        {
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            float shown = Math.Clamp((front - p.D) / 0.4f, 0f, 1f);
            float a = c.Spec.Alpha * shown * env * (1f - 0.6f * p.D / c.Reach);
            p.Tr.position = FxMath.V3(p.X, p.Y, p.Z);
            float s = p.Size * grow;
            p.Tr.localScale = FxMath.V3(s, s, 1f);
            if (MathF.Abs(a - p.Alpha) < 0.004f) continue;
            p.Alpha = a;
            p.Sr.color = FxMath.Rgba(_tint.r, _tint.g, _tint.b, a);
        }
    }

    private static void Destroy(Cloud c)
    {
        foreach (var p in c.Puffs)
            if (p.Tr) UnityEngine.Object.Destroy(p.Tr.gameObject);
        c.Puffs.Clear();
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Clear(); }
        if (Clouds.Count == 0 && !_anyDusty && _livePrints == 0 && VisionMul == 1f) { _lastMs = 0; return; }

        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.05f, (now - _lastMs) / 1000f);
        _lastMs = now;
        if (!GameClock.ShipAlive || MeetingHud.Instance) { Clear(); return; }
        _clock += dt;

        for (int i = Clouds.Count - 1; i >= 0; i--)
        {
            var c = Clouds[i];
            c.T += dt;
            if (c.T >= Life(c)) { Destroy(c); Clouds.RemoveAt(i); }
        }

        // 煙の板はゆっくりしか動かないので 15 回/秒で動かす
        _drawAcc += dt;
        if (_drawAcc >= 0.066f)
        {
            foreach (var c in Clouds) DrawCloud(c, _drawAcc);
            _drawAcc = 0f;
        }

        _scanAcc += dt;
        if (_scanAcc >= ScanInterval)
        {
            Scan(_scanAcc);
            _scanAcc = 0f;
        }

        float rate = Math.Min(1f, VisionRate * dt);
        float v = VisionMul + (_visionTarget - VisionMul) * rate;
        VisionMul = MathF.Abs(v - _visionTarget) < 0.002f ? _visionTarget : v;
    }

    // 0.1 秒ごと: 全員の位置で煙の中か・粉が付いたか・足跡を置くか。自分の視界の目標
    private static void Scan(float dt)
    {
        var lp = PlayerControl.LocalPlayer;
        LocalId = lp ? lp.PlayerId : (byte)255;
        _visionTarget = 1f;
        _anyDusty = false;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var data = pc.Data;
            byte id = pc.PlayerId;
            if (data == null || data.IsDead || data.Disconnected) { Tracked[id] = false; InDust[id] = 0f; DustyUntil[id] = 0f; continue; }
            Vector2 pos = pc.GetTruePosition();
            float dens = 0f;
            foreach (var c in Clouds) dens = Math.Max(dens, Density(c, pos.x, pos.y));

            if (dens >= DustMin)
            {
                InDust[id] += dt;
                if (InDust[id] >= DustAfter) DustyUntil[id] = _clock + PrintsFor;
            }
            else InDust[id] = 0f;
            if (id == LocalId) _visionTarget = 1f - (1f - VisionCenter) * dens;
            if (DustyUntil[id] > _clock) _anyDusty = true;

            if (!Tracked[id]) { Tracked[id] = true; LastX[id] = pos.x; LastY[id] = pos.y; Walked[id] = 0f; continue; }
            float mx = pos.x - LastX[id], my = pos.y - LastY[id];
            float step = MathF.Sqrt(mx * mx + my * my);
            LastX[id] = pos.x; LastY[id] = pos.y;
            if (step > 2f) { Walked[id] = 0f; continue; } // 飛んだ (通気口・転送) 所は足跡でつながない
            if (DustyUntil[id] <= _clock) { Walked[id] = 0f; continue; }
            if (pc.inVent || pc.shouldAppearInvisible || !pc.Visible) continue;
            Walked[id] += step;
            if (Walked[id] < StepDist || step < 0.0001f) continue;
            Walked[id] = 0f;
            LeftFoot[id] = !LeftFoot[id];
            float fresh = (DustyUntil[id] - _clock) / PrintsFor;
            PlacePrint(pos.x, pos.y, mx / step, my / step, LeftFoot[id], PrintAlphaOld + (PrintAlphaFresh - PrintAlphaOld) * fresh,
                PrintTint(_tint.r), PrintTint(_tint.g), PrintTint(_tint.b));
        }

        AgePrints(dt);
    }

    // ── 足跡 ───────────────────────────────────────────────────────────

    // 足跡を 1 つ置く (粉と水で同じ使い回しの列)。fx, fy = 歩いた向き (長さ 1)
    internal static void PlacePrint(float x, float y, float fx, float fy, bool left, float alpha, float r, float g, float b)
    {
        var pr = Prints[_nextPrint];
        if (pr == null || !pr.Tr)
        {
            var go = new GameObject("MrpFootprint") { layer = 0 };
            pr = Prints[_nextPrint] = new Print { Tr = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
            pr.Sr.sprite = DebrisArt.Footprint;
            float sw = Math.Max(0.0001f, pr.Sr.sprite.bounds.size.x);
            float s = PrintLength / sw;
            pr.Tr.localScale = FxMath.V3(s, s, 1f);
        }
        _nextPrint = (_nextPrint + 1) % MaxPrints;
        if (pr.Age >= PrintLife) _livePrints++;

        float side = left ? StepSide : -StepSide;
        float px = x - fy * side, py = y + fx * side;
        var at = new Vector2(px, py);
        float front = DamageMap.FrontZ(at), zs = DamageMap.ZScale(front);
        pr.Tr.position = FxMath.V3(px, py, front - 0.001f * zs);
        pr.Tr.localRotation = FxMath.RotZ(MathF.Atan2(fy, fx) * (180f / MathF.PI));
        pr.Age = 0f;
        pr.Alpha0 = alpha;
        pr.R = r; pr.G = g; pr.B = b;
        pr.Sr.enabled = true;
        pr.Sr.color = FxMath.Rgba(r, g, b, alpha);
    }

    // 足跡は粉の色を暗くした汚れ (明るい床でも暗い床でも形が読める濃さ)
    private static float PrintTint(float v) => 0.55f * v;

    private static void AgePrints(float dt)
    {
        if (_livePrints == 0) return;
        int live = 0;
        foreach (var pr in Prints)
        {
            if (pr == null || pr.Age >= PrintLife) continue;
            pr.Age += dt;
            if (pr.Age >= PrintLife) { pr.Sr.enabled = false; continue; }
            live++;
            // 最後の 4 割で薄れる。それまでは色を書かない
            float k = (PrintLife - pr.Age) / (PrintLife * 0.4f);
            if (k >= 1f) continue;
            pr.Sr.color = FxMath.Rgba(pr.R, pr.G, pr.B, pr.Alpha0 * k);
        }
        _livePrints = live;
    }

    private static void Clear()
    {
        foreach (var c in Clouds) Destroy(c);
        Clouds.Clear();
        for (int i = 0; i < MaxPrints; i++)
        {
            var pr = Prints[i];
            if (pr == null) continue;
            if (pr.Tr) UnityEngine.Object.Destroy(pr.Tr.gameObject);
            Prints[i] = null;
        }
        _nextPrint = _livePrints = 0;
        Array.Clear(InDust);
        Array.Clear(DustyUntil);
        Array.Clear(Tracked);
        _anyDusty = false;
        _visionTarget = 1f;
        VisionMul = 1f;
        _clock = _scanAcc = _drawAcc = 0f;
        _lastMs = 0;
    }

    internal static void Register()
    {
        TestBridge.Register("dust", "[clear | here] 粉塵: 煙・粉の付いた人・足跡・自分の視界の倍率 (here = 自分の足元で爆発の煙を出す・壁は壊さない)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "clear") { Clear(); Made = 0; Last = "-"; }
            else if (a == "here")
            {
                var lp = PlayerControl.LocalPlayer;
                if (lp) OnApplied(new ResolvedDamage(DamageKind.Explosion, lp.GetTruePosition(), Vector2.zero, Vector2.zero, 1f, 1.5f, 0, 1));
            }
            var sb = new System.Text.StringBuilder();
            foreach (var c in Clouds) sb.Append($" {c.Kind}@{c.Cx:0.0},{c.Cy:0.0} t={c.T:0.0} reach={c.Reach:0.0} cells={c.Cells} front={Front(c):0.0}");
            var dusty = new System.Text.StringBuilder();
            for (int i = 0; i < 256; i++)
                if (DustyUntil[i] > _clock) dusty.Append($" {i}({DustyUntil[i] - _clock:0.0}s)");
            float here = 0f;
            var me = PlayerControl.LocalPlayer;
            if (me) { var p = me.GetTruePosition(); foreach (var c in Clouds) here = Math.Max(here, Density(c, p.x, p.y)); }
            reply($"OK dust made={Made} last={Last} clouds={Clouds.Count}{sb} here={here:0.00} vision={VisionMul:0.00} prints={_livePrints} dusty=[{dusty}] map={(SolidMap.Valid ? "walls" : "none")}");
        });
    }
}
