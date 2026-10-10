using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 縁を越えて落ちる水 (滝) の絵。WaterSim.Falls の縁ごとの量から絵だけの粒を出し、縁から着地まで落とした通り道を
// 滝の範囲の絵へ「水の濃さ」と「泡」として描き込む。見た目はシェーダ MRP/Water (_Mode = 1・流れは画面の下向き)。
// 量が多い縁は粒の線が重なって一枚の膜に、少ない縁は筋と雫にちぎれる (形は決め打ちせず量から出る)。
// 粒の着く時間は計算の落ちる遅れと同じなので、着いた頃に下の水たまりが増える。着地点には泡を描いて水たまりへつなぐ。
// 計算には戻らない (全員の結果に関わらない)。会議中は隠す
internal static class WaterFall
{
#if ANDROID
    // スマホは粒の線と画素の変換・転送が重いので、粗く・粒を少なく・描き直しを半分に
    private const float Ppu = 16f;
    private const float ParcelMass = 9f;
    private const float Redraw = 1f / 15f;
#else
    private const float Ppu = 24f;
    private const float ParcelMass = 6f;       // 粒 1 つの水の量 (WaterSim の量の単位)
    private const float Redraw = 1f / 30f;
#endif
    // 速い粒の線は長さに依らず同じ量を置くので、1 画素の濃さは画素の面積に反比例し、粒の量に比例する。PC と同じ濃さに揃える
    private const float LineScale = Ppu * Ppu / (24f * 24f) * (ParcelMass / 6f);
    private const float CanvasW = 5f;          // 1 枚の絵の幅 (単位)
    private const float MaxCanvasH = 9f;
    private const float Above = 0.4f, Below = 0.7f, Margin = 0.25f;
    private const int MaxCanvases = 4;
    private const int MaxParcels = 2000;
    private const int MaxSpawnPerFrame = 240;
    private const float FlowTau = 0.25f;       // 流量のならし (秒)。刻みの無いフレームでも点滅しない
    private const float LipSpread = 0.17f;     // 縁に沿って散らす幅の半分 (升の半分 + 隣と重なる分)
    private const float ThrowMax = 0.2f, ThrowFlow = 900f; // 外へ投げ出す距離と、それが最大になる流量 (量/秒)
    private const float ImpactR0 = 0.12f, ImpactR1 = 0.12f, ImpactFlow = 600f;
    private const float Hit = 0.55f, Side = 0.2f, Corner = 0.06f;
    private const float SprayEdge = 0.13f;     // 薄い筋も見えるように噴き出しより低く
    private const float IdleLife = 2.5f;       // 流れが止まってから縁と絵を片づけるまで
    private const float StealEvery = 0.5f;     // 絵の上限で描けない縁が画面の近くにあるか見る間隔
    private const float StealNear = 8f, StealGap = 3f; // その距離 (カメラから) より近い縁へ、それより StealGap 以上遠い絵を譲る

    private sealed class Edge
    {
        public float X0, Y0, X1, Y1, Nx, Ny;
        public float Q, Acc, Idle;
        public int Delay;
        public bool Void;
        public Canvas C;
        public int FrameSum;
    }

    private sealed class Canvas
    {
        public float X0, Y0;
        public int W, H;
        public float Idle;
        public bool Hidden, Gone;   // Gone = 片づけ済み (Unity の物の null 判定を毎回しない)
        // 描いた範囲 (画素)。前回の範囲だけ消し、前回と今回を合わせた範囲だけ変換する
        public int X0p, Y0p, X1p = -1, Y1p = -1, Bx0, By0, Bx1, By1;
        public GameObject Go;
        public SpriteRenderer Sr;
        public Texture2D Tex;
        public Sprite Sp;
        public Material Mat;
        public byte[] Px;
        public float[] Dens, Foam;
    }

    // 粒 (絵だけ)。地面の上の軌跡は (Gx,Gy) → (Ex,Ey)、高さは H0 から 2 次で 0 へ
    private struct Parcel
    {
        public float Gx, Gy, Ex, Ey, H0, T, Age, Lx, Ly;
        public bool Void, Drawn;
        public Canvas C;
    }

    private static readonly Dictionary<long, Edge> Edges = new();
    private static readonly List<Canvas> Canvases = new();
    private static readonly Parcel[] Parcels = new Parcel[MaxParcels];
    private static int _parcels;
    private static float _redrawAcc, _meetAcc, _stealAcc;
    private static int _half;
    private static bool _meeting;
    private static long _lastMs;
    private static int _shipGen = -1;
    private static uint _rng = 2463534242u;
    private static readonly List<long> Dead = new();
    internal static double LastDrawMs { get; private set; }

    private static float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng & 0xFFFFFF) / 16777216f;
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterFall] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Clear(); }
        var falls = WaterSim.Falls;
        if (falls.Count == 0 && Edges.Count == 0 && _parcels == 0) { _lastMs = 0; return; }
        if (!WaterSim.Ready) { Clear(); return; }
        // TickCount64 は約 15ms 刻みで同じ値が続き、流量を時間で割ると 0 除算になるので細かい時計で測る
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _lastMs == 0 ? 0.016f : Math.Clamp((float)((now - _lastMs) / (double)System.Diagnostics.Stopwatch.Frequency), 0.001f, 0.1f);
        _lastMs = now;

        foreach (var f in falls)
        {
            long key = ((long)MathF.Round(f.X0 * 64f) << 32) ^ (uint)(int)MathF.Round(f.Y0 * 64f);
            if (!Edges.TryGetValue(key, out var e))
            {
                int d = f.Dir;
                e = new Edge { X0 = f.X0, Y0 = f.Y0, Nx = d == 0 ? 1 : d == 1 ? -1 : 0, Ny = d == 2 ? 1 : d == 3 ? -1 : 0 };
                Edges[key] = e;
            }
            e.X1 = f.X1; e.Y1 = f.Y1; e.Delay = f.Delay; e.Void = f.Void;
            e.FrameSum += f.Amount;
        }

        // 流量をならして粒を出す
        int budget = MaxSpawnPerFrame;
        float k = Math.Min(1f, dt / FlowTau);
        Dead.Clear();
        foreach (var kv in Edges)
        {
            var e = kv.Value;
            e.Q += (e.FrameSum / dt - e.Q) * k;
            e.FrameSum = 0;
            if (e.Q < 1f) { e.Idle += dt; if (e.Idle > IdleLife) Dead.Add(kv.Key); continue; }
            e.Idle = 0f;
            if (e.C == null || e.C.Gone) e.C = CanvasFor(e);
            if (e.C == null) continue;
            e.C.Idle = 0f;
            e.Acc += e.Q * dt / ParcelMass;
            while (e.Acc >= 1f && budget > 0 && _parcels < MaxParcels)
            {
                e.Acc -= 1f;
                budget--;
                Spawn(e);
            }
            if (e.Acc > 8f) e.Acc = 8f;
        }
        foreach (var key in Dead) Edges.Remove(key);
        _stealAcc += dt;
        if (_stealAcc >= StealEvery) { _stealAcc = 0f; Steal(); }

        // 粒を進める (着いた物は消す)
        for (int i = _parcels - 1; i >= 0; i--)
        {
            ref var p = ref Parcels[i];
            p.Age += dt;
            if (p.Age >= p.T + Redraw * 1.5f || p.C == null || p.C.Gone) Parcels[i] = Parcels[--_parcels];
        }

        // 会議の確認は Unity の物の生存確認になるので間引く
        _meetAcc += dt;
        if (_meetAcc >= 0.25f) { _meetAcc = 0f; _meeting = MeetingHud.Instance; }
        bool meeting = _meeting;
        for (int i = Canvases.Count - 1; i >= 0; i--)
        {
            var c = Canvases[i];
            c.Idle += dt;
            if (c.Idle > IdleLife) { Destroy(c); Canvases.RemoveAt(i); continue; }
            if (c.Hidden != meeting) { c.Hidden = meeting; c.Sr.enabled = !meeting; }
        }
        // 絵を半分ずつ交互に描き直す (1 枚ごとの間隔は Redraw のまま・1 フレームの山が半分になる)
        _redrawAcc += dt;
        if (meeting || _redrawAcc < Redraw * 0.5f) return;
        _redrawAcc = 0f;
        _half ^= 1;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = _half; i < Canvases.Count; i += 2) Draw(Canvases[i]);
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static int _spawned;
    private static void Spawn(Edge e)
    {
        _spawned++;
        float u = (Rand() * 2f - 1f) * LipSpread, v = Rand();
        // 縁に沿った向き (縁の外向きに直交)
        float sx = e.X0 - e.Ny * u, sy = e.Y0 + e.Nx * u;
        float h0 = MathF.Max(0.3f, sy - e.Y1);
        float thr = ThrowMax * MathF.Min(1f, MathF.Pow(e.Q / ThrowFlow, 1f / 3f)) * (0.8f + 0.4f * v);
        ref var p = ref Parcels[_parcels++];
        p.Gx = sx; p.Gy = sy - h0;
        p.Ex = e.X1 - e.Ny * u * 0.8f + e.Nx * thr;
        p.Ey = e.Y1 + e.Nx * u * 0.8f + e.Ny * thr;
        p.H0 = h0;
        p.T = MathF.Max(0.12f, e.Delay / (float)GameClock.Hz) * (0.95f + 0.1f * Rand());
        p.Age = 0f;
        p.Lx = sx; p.Ly = sy;
        p.Void = e.Void;
        p.Drawn = false;
        p.C = e.C;
    }

    // 絵の上限で描けない縁のうちカメラにいちばん近い物へ、カメラからいちばん遠い絵を譲る (水が多いと縁が上限を超え、
    // 先に流れ始めた遠くの崖が絵を持ったまま、目の前の滝が描かれないことがある)。半秒に 1 枚だけ
    private static void Steal()
    {
        if (Canvases.Count < MaxCanvases) return;
        var cam = Camera.main;
        if (!cam) return;
        var cp = cam.transform.position;
        float cx = cp.x, cy = cp.y;
        Edge want = null;
        float wd = StealNear * StealNear;
        foreach (var e in Edges.Values)
        {
            if (e.Q < 1f || (e.C != null && !e.C.Gone)) continue;
            float dx = e.X0 - cx, dy = e.Y0 - cy, d = dx * dx + dy * dy;
            if (d < wd) { wd = d; want = e; }
        }
        if (want == null) return;
        int far = -1;
        float fd = 0f;
        for (int i = 0; i < Canvases.Count; i++)
        {
            var c = Canvases[i];
            float dx = c.X0 + c.W / Ppu * 0.5f - cx, dy = c.Y0 + c.H / Ppu * 0.5f - cy, d = dx * dx + dy * dy;
            if (d > fd) { fd = d; far = i; }
        }
        float need = MathF.Sqrt(wd) + StealGap;
        if (far < 0 || fd < need * need) return;
        Destroy(Canvases[far]);
        Canvases.RemoveAt(far);
        want.C = CanvasFor(want);
    }

    // その縁の落ちる範囲が入る絵 (無ければ作る・上限を超えたら流れていない絵を使い回す)
    private static Canvas CanvasFor(Edge e)
    {
        float lx = Math.Min(e.X0, e.X1), hx = Math.Max(e.X0, e.X1);
        float ly = Math.Min(e.Y0, e.Y1) - (e.Void ? 0f : 0.35f), hy = Math.Max(e.Y0, e.Y1);
        foreach (var c in Canvases)
            if (lx - Margin >= c.X0 && hx + Margin <= c.X0 + c.W / Ppu && ly - Margin >= c.Y0 && hy + Margin <= c.Y0 + c.H / Ppu) return c;
        if (Canvases.Count >= MaxCanvases)
        {
            int idle = -1;
            for (int i = 0; i < Canvases.Count; i++) if (Canvases[i].Idle > 0.5f && (idle < 0 || Canvases[i].Idle > Canvases[idle].Idle)) idle = i;
            if (idle < 0) return null;
            Destroy(Canvases[idle]);
            Canvases.RemoveAt(idle);
        }
        var mat0 = MrpBundle.WaterMaterial;
        if (!mat0) return null;
        float cx = (lx + hx) * 0.5f;
        float x0 = Math.Min(cx - CanvasW * 0.5f, lx - Margin), x1 = Math.Max(cx + CanvasW * 0.5f, hx + Margin);
        float y0 = ly - Below, y1 = hy + Above;
        if (y1 - y0 > MaxCanvasH) y0 = y1 - MaxCanvasH; // 絵が大きいほど消す・変換・転送が重いので高さに上限 (それより下は描かない)
        var n = new Canvas { X0 = x0, Y0 = y0, W = (int)MathF.Ceiling((x1 - x0) * Ppu), H = (int)MathF.Ceiling((y1 - y0) * Ppu) };
        n.Px = new byte[n.W * n.H * 4];
        for (int i = 3; i < n.Px.Length; i += 4) n.Px[i] = 255; // 描いていない所も不透明の濃さ 0 にしておく
        n.Dens = new float[n.W * n.H];
        n.Foam = new float[n.W * n.H];
        n.Tex = new Texture2D(n.W, n.H, TextureFormat.RGBA32, false) { name = "MrpFall", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        n.Sp = Sprite.Create(n.Tex, new Rect(0, 0, n.W, n.H), Vector2.zero, Ppu, 0, SpriteMeshType.FullRect);
        n.Sp.name = "MrpFall";
        n.Mat = new Material(mat0) { name = "MrpFall" };
        n.Mat.SetFloat("_Mode", 1f);
        n.Mat.SetFloat("_SprayEdge", SprayEdge);
        n.Mat.SetVector("_Flow", new Vector4(0f, -1f, 0f, 0f));
        n.Go = new GameObject("MrpFall") { layer = 0 };
        n.Sr = n.Go.AddComponent<SpriteRenderer>();
        n.Sr.sprite = n.Sp;
        n.Sr.sharedMaterial = n.Mat;
        // 縁と着地の床のうち手前の方よりわずかに手前 (崖の面の上に見える)
        float floor = Math.Min(DamageMap.FrontZ(FxMath.V2(e.X0, e.Y0)), DamageMap.FrontZ(FxMath.V2(e.X1, e.Y1)));
        n.Go.transform.position = FxMath.V3(x0, y0, floor - 0.003f * DamageMap.ZScale(floor));
        Canvases.Add(n);
        return n;
    }

    private static unsafe void Draw(Canvas c)
    {
        int w = c.W;
        // 前回描いた範囲だけを消す (それ以外は 0 のまま)
        for (int y = c.Y0p; y <= c.Y1p; y++)
        {
            int o = y * w + c.X0p, len = c.X1p - c.X0p + 1;
            Array.Clear(c.Dens, o, len);
            Array.Clear(c.Foam, o, len);
        }
        c.Bx0 = w; c.By0 = c.H; c.Bx1 = -1; c.By1 = -1;
        for (int i = 0; i < _parcels; i++)
        {
            ref var p = ref Parcels[i];
            if (p.C != c) continue;
            float tau = Math.Min(1f, p.Age / p.T);
            float h = p.H0 * (1f - tau * tau);
            float sx = p.Gx + (p.Ex - p.Gx) * tau, sy = p.Gy + (p.Ey - p.Gy) * tau + h;
            float foam = tau > 0.92f ? 0.8f : 0.05f + 0.3f * tau * tau;
            float a = p.Void && tau > 0.7f ? (1f - tau) / 0.3f : 1f;
            Line(c, p.Lx, p.Ly, sx, sy, a, foam);
            p.Lx = sx; p.Ly = sy;
        }
        // 着地の泡 (下の水たまりへつなぐ)
        foreach (var e in Edges.Values)
        {
            if (e.C != c || e.Void || e.Q < 1f) continue;
            float r = ImpactR0 + ImpactR1 * Math.Min(1f, e.Q / ImpactFlow);
            Disc(c, e.X1 + e.Nx * 0.05f, e.Y1 + e.Ny * 0.05f, r, Math.Min(1f, e.Q / ImpactFlow * 2f));
        }
        bool now = c.Bx1 >= 0, before = c.X1p >= 0;
        // 流れが止まったら 1 回だけ空の絵を書いて、それ以降は描かない
        if (!now && !before) return;
        int ux0 = Math.Min(now ? c.Bx0 : w, before ? c.X0p : w), uy0 = Math.Min(now ? c.By0 : c.H, before ? c.Y0p : c.H);
        int ux1 = Math.Max(c.Bx1, c.X1p), uy1 = Math.Max(c.By1, c.Y1p);
        fixed (byte* b = c.Px)
        {
            uint* px = (uint*)b;
            for (int y = uy0; y <= uy1; y++)
            for (int i = y * w + ux0, end = y * w + ux1; i <= end; i++)
            {
                float d = c.Dens[i];
                if (d <= 0f) { px[i] = 0xFF000000u; continue; }
                uint r = d >= 1f ? 255u : (uint)(d * 255f);
                float f = c.Foam[i] / d;
                uint g = f >= 1f ? 255u : (uint)(f * 255f);
                px[i] = 0xFF000000u | (g << 8) | r; // RGBA32 を little endian で (R = 濃さ・G = 泡)
            }
            c.Tex.LoadRawTextureData((IntPtr)b, c.Px.Length);
        }
        c.Tex.Apply(false, false);
        c.X0p = c.Bx0; c.Y0p = c.By0; c.X1p = c.Bx1; c.Y1p = c.By1;
    }

    private static void Line(Canvas c, float wx0, float wy0, float wx1, float wy1, float a, float foam)
    {
        float ax = (wx0 - c.X0) * Ppu, ay = (wy0 - c.Y0) * Ppu, bx = (wx1 - c.X0) * Ppu, by = (wy1 - c.Y0) * Ppu;
        float dx = bx - ax, dy = by - ay;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy)));
        if (steps > 400) return;
        float w = a * LineScale / steps * Math.Min(steps, 3); // 速い所は細い筋
        for (int k = 0; k <= steps; k++)
        {
            float f = (float)k / steps;
            Splat(c, (int)(ax + dx * f), (int)(ay + dy * f), w, foam);
        }
    }

    private static void Splat(Canvas c, int x, int y, float w, float foam)
    {
        int n = c.W;
        if (x < 1 || y < 1 || x >= n - 1 || y >= c.H - 1) return;
        Grow(c, x - 1, y - 1, x + 1, y + 1);
        int i = y * n + x;
        Add(c, i, Hit * w, foam);
        Add(c, i - 1, Side * w, foam); Add(c, i + 1, Side * w, foam); Add(c, i - n, Side * w, foam); Add(c, i + n, Side * w, foam);
        Add(c, i - n - 1, Corner * w, foam); Add(c, i - n + 1, Corner * w, foam); Add(c, i + n - 1, Corner * w, foam); Add(c, i + n + 1, Corner * w, foam);
    }

    private static void Grow(Canvas c, int x0, int y0, int x1, int y1)
    {
        if (x0 < c.Bx0) c.Bx0 = x0;
        if (y0 < c.By0) c.By0 = y0;
        if (x1 > c.Bx1) c.Bx1 = x1;
        if (y1 > c.By1) c.By1 = y1;
    }

    private static void Add(Canvas c, int i, float v, float foam)
    {
        c.Dens[i] += v;
        c.Foam[i] += v * foam;
    }

    // 着地の泡の円 (真ん中ほど濃く・奥行きで縦につぶす)
    private static void Disc(Canvas c, float wx, float wy, float r, float s)
    {
        int cx = (int)((wx - c.X0) * Ppu), cy = (int)((wy - c.Y0) * Ppu), rx = (int)(r * Ppu), ry = Math.Max(1, (int)(r * 0.6f * Ppu));
        int gx0 = Math.Max(0, cx - rx), gy0 = Math.Max(0, cy - ry), gx1 = Math.Min(c.W - 1, cx + rx), gy1 = Math.Min(c.H - 1, cy + ry);
        if (gx0 > gx1 || gy0 > gy1) return;
        Grow(c, gx0, gy0, gx1, gy1);
        for (int y = -ry; y <= ry; y++)
        for (int x = -rx; x <= rx; x++)
        {
            int px = cx + x, py = cy + y;
            if (px < 0 || py < 0 || px >= c.W || py >= c.H) continue;
            float q = (float)x * x / (rx * rx) + (float)y * y / (ry * ry);
            if (q >= 1f) continue;
            float v = (1f - q) * 0.6f * s;
            Add(c, py * c.W + px, v, 0.85f);
        }
    }

    private static void Destroy(Canvas c)
    {
        if (c.Go) UnityEngine.Object.Destroy(c.Go);
        if (c.Sp) UnityEngine.Object.Destroy(c.Sp);
        if (c.Tex) UnityEngine.Object.Destroy(c.Tex);
        if (c.Mat) UnityEngine.Object.Destroy(c.Mat);
        c.Gone = true;
    }

    internal static void Clear()
    {
        foreach (var c in Canvases) Destroy(c);
        Canvases.Clear();
        Edges.Clear();
        _parcels = 0;
        _lastMs = 0;
    }

    internal static string Describe()
    {
        float q = 0f;
        int held = 0;
        foreach (var e in Edges.Values) { q = Math.Max(q, e.Q); if (e.C != null && !e.C.Gone) held++; }
        return $"falls={Canvases.Count} fallEdges={Edges.Count} held={held} qMax={q:0} spawned={_spawned} parcels={_parcels} fallMs={LastDrawMs:0.00}";
    }
}
