using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 縁を越えて落ちる水 (滝) の絵。WaterSim.Falls の縁ごとの量から、縁から着地までの膜を滝の範囲の絵へ「水の濃さ」と「泡」として描き込む。
// 見た目はシェーダ MRP/Water (_Mode = 1・流れは画面の下向き)。
// 膜は縁に沿って 1 画素ごとの列で、列の位置は世界の画素の格子に揃える (隣の縁・隣の絵と隙間も重なりも無くつながる)。
// 列ごとの濃さは隣の縁との間で量をなめらかにつなぎ、縁に立つ物 (柵の柱・網) の絵がある列だけ物を越える量に減らす。
// 量が少ない縁だけ列ごとの揺らぎで筋にちぎれる。水たまりの絵は落ちる縁の升まで床として見せる (WaterSim.StampLips) ので、膜は縁から描き始めれば水たまりとつながる。
// 流れ始めの膜の先は計算の落ちる遅れと同じ時間で着くので、着いた頃に下の水たまりが増える。着地点には泡を描いて水たまりへつなぐ。
// 計算には戻らない (全員の結果に関わらない)。会議中は隠す
internal static class WaterFall
{
#if ANDROID
    // スマホは画素の変換・転送が重いので、粗く・描き直しを半分に
    private const float Ppu = 16f;
    private const float Redraw = 1f / 15f;
    private const int MaxCanvases = 6;
#else
    private const float Ppu = 24f;
    private const float Redraw = 1f / 30f;
    private const int MaxCanvases = 8;
#endif
    private const float SheetFull = 240f;      // 列の濃さが 1 になる縁の流量 (WaterSim の量の単位/秒)
    private const float DensMax = 1.3f;
    private const float ThinD = 0.35f;         // これより薄い膜は列ごとに筋へちぎれる
    private const float PoolFade = 0.6f;      // 落ち始めてから水たまりの色が噴き出しの色へ移り切るまでの落ちた割合
    private const float SideBody = 0.75f;      // 横の縁の膜の厚さ (縁から投げ出された先までのうち、先の側から何割を埋めるか)
    private const float Thin = 0.5f;           // 落ちるほど速くなって膜が薄くなる割合 (着地で 1 / (1 + Thin))
    private const float CanvasW = 5f;          // 1 枚の絵の幅 (単位)
    private const float MaxCanvasH = 9f;
    private const float FallZMax = 0.12f;      // 絵を置く奥行きの上限 (クルーと同じ奥行きの帯のすぐ奥)
    private const float Above = 0.4f, Below = 0.7f, Margin = 0.25f;
    private const float FlowTau = 0.25f;       // 流量のならし (秒)。刻みの無いフレームでも点滅しない
    private const float ThrowMax = 0.35f;     // 外へ投げ出す距離の上限
    private const float ImpactR0 = 0.12f, ImpactR1 = 0.12f, ImpactFlow = 600f;
    private const float LipHalf = 0.13f, LipDrop = 0.3f, LipFlow = 300f, LipDens = 0.9f; // 手すりを越える膜: 縁に沿った幅の半分・垂れる長さ・濃さが最大になる流量・濃さ
    private const float SprayEdge = 0.13f;     // 薄い筋も見えるように噴き出しより低く
    private const float IdleLife = 2.5f;       // 流れが止まってから縁と絵を片づけるまで
    private const float StealEvery = 0.5f;     // 画面の範囲を測り直し、絵の上限で描けない縁が画面の近くにあるか見る間隔
    private const float ViewIn = 1.5f, ViewOut = 4f; // 画面の外へこれだけ離れた縁には絵を付けない / これだけ離れた絵は片づける
    private const float StealNear = 8f, StealGap = 3f; // その距離 (カメラから) より近い縁へ、それより StealGap 以上遠い絵を譲る

    private sealed class Edge
    {
        public float X0, Y0, X1, Y1, Nx, Ny;
        public float Q, Idle;
        public float Head, Tail; // 流れ始めてから・止まってからの秒 (膜の先と後ろの端の落ちた割合 = それ ÷ 落ちる時間)
        public int Delay, K, D;
        public bool Void, Lip;   // Lip = 低い壁 (ガラスの手すり) を越えてこぼれる縁
        public float LipH;       // その壁の高さ (絵では縁がそのぶん上にある)
        public float Depth, BarH; // 縁の水の深さ・縁に立つ物の高さ (単位)
        public float BarF;       // 縁に立つ物で止まる割合 (0..1)
        public float Cut = float.MinValue; // 外へ落ちる水が手前の床の裏に隠れる y (WaterSim.FrontCut)
        public Canvas C;
        public int FrameSum;
    }

    private sealed class Canvas
    {
        public float X0, Y0;
        public int W, H;
        public float Idle;
        public bool Hidden, Gone;   // Gone = 片づけ済み (Unity の物の null 判定を毎回しない)
        public bool Void;           // 奈落へ落ちる縁の絵 (置く奥行きが違うので床へ落ちる縁とは分ける)
        // 描いた範囲 (画素)。前回の範囲だけ消し、前回と今回を合わせた範囲だけ変換する
        public int X0p, Y0p, X1p = -1, Y1p = -1, Bx0, By0, Bx1, By1;
        public GameObject Go;
        public SpriteRenderer Sr;
        public Texture2D Tex;
        public Sprite Sp;
        public Material Mat;
        public byte[] Px;
        public float[] Dens, Foam, Dep, Pool;
    }

    private static readonly Dictionary<long, Edge> Edges = new();
    private static readonly List<Canvas> Canvases = new();
    private static float _redrawAcc, _meetAcc, _stealAcc = StealEvery; // 最初の刻みで画面の範囲を測る
    private static float _vx0 = float.MinValue, _vy0 = float.MinValue, _vx1 = float.MaxValue, _vy1 = float.MaxValue; // 画面の範囲 (世界)
    private static int _half;
    private static bool _meeting;
    private static long _lastMs;
    private static int _shipGen = -1;
    private static readonly List<long> Dead = new();
    internal static double LastDrawMs { get; private set; }

    // 列と時間の値ノイズ (0..1)。列は世界の画素の格子の番号なので、隣の縁・隣の絵でも同じ列は同じ値
    private static float Hash(int a, int b)
    {
        uint h = (uint)a * 374761393u + (uint)b * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
    }

    private static float Noise(int col, float t)
    {
        int i = (int)MathF.Floor(t);
        float f = t - i;
        f = f * f * (3f - 2f * f);
        float a = Hash(col, i);
        return a + (Hash(col, i + 1) - a) * f;
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
        if (falls.Count == 0 && Edges.Count == 0) { _lastMs = 0; return; }
        if (!WaterSim.Ready) { Clear(); return; }
        // TickCount64 は約 15ms 刻みで同じ値が続き、流量を時間で割ると 0 除算になるので細かい時計で測る
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _lastMs == 0 ? 0.016f : Math.Clamp((float)((now - _lastMs) / (double)System.Diagnostics.Stopwatch.Frequency), 0.001f, 0.1f);
        _lastMs = now;

        foreach (var f in falls)
        {
            long key = (long)f.K * 4 + f.Dir;
            if (!Edges.TryGetValue(key, out var e))
            {
                int d = f.Dir;
                e = new Edge { K = f.K, D = d, Nx = d == 0 ? 1 : d == 1 ? -1 : 0, Ny = d == 2 ? 1 : d == 3 ? -1 : 0 };
                Edges[key] = e;
            }
            e.X0 = f.X0; e.Y0 = f.Y0; e.X1 = f.X1; e.Y1 = f.Y1; e.Delay = f.Delay; e.Void = f.Void; e.Lip = f.Lip > 0f; e.LipH = f.Lip;
            e.Depth = f.Depth; e.BarF = f.BarB / 256f; e.BarH = f.BarH;
            e.FrameSum += f.Amount;
        }

        _stealAcc += dt;
        if (_stealAcc >= StealEvery) { _stealAcc = 0f; Steal(); }

        // 流量をならし、膜の先と後ろの端を進める
        float k = Math.Min(1f, dt / FlowTau);
        Dead.Clear();
        foreach (var kv in Edges)
        {
            var e = kv.Value;
            e.Q += (e.FrameSum / dt - e.Q) * k;
            e.FrameSum = 0;
            float fall = FallTime(e);
            if (e.Q < 1f)
            {
                if (e.Head > 0f) e.Tail += dt;
                e.Idle += dt;
                if (e.Idle > IdleLife && (e.Head <= 0f || e.Tail >= fall)) Dead.Add(kv.Key);
                if (e.C != null && !e.C.Gone && e.Head > 0f && e.Tail < fall) e.C.Idle = 0f;
                continue;
            }
            // 止まってから流れ直した: 後ろの端が落ち切っていれば膜の先から出し直す
            if (e.Tail > 0f) { if (e.Tail >= fall) e.Head = 0f; e.Tail = 0f; }
            e.Head = MathF.Min(e.Head + dt, fall);
            e.Idle = 0f;
            // 画面の外の縁には絵を付けない (粒は絵だけなので、画面に入ってから出しても計算は変わらない)
            if ((e.C == null || e.C.Gone) && InView(MathF.Min(e.X0, e.X1), MathF.Min(e.Y0, e.Y1), MathF.Max(e.X0, e.X1), MathF.Max(e.Y0, e.Y1), ViewIn)) e.C = CanvasFor(e);
            if (e.C == null) continue;
            e.C.Idle = 0f;
            // 外へ落ちる水は手前の床 (と奥の縁の手すり・その上に盛り上がった水) より下では裏に隠れる
            if (e.Void) e.Cut = WaterSim.FrontCut(e.X0, CutFrom(e), e.Y1);
        }
        foreach (var key in Dead) Edges.Remove(key);

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

    // 手前の床を探し始める y。奥の縁は縁の床そのもの (手すりの絵の根元は升の縁とずれている所があるので上の縁のすぐ下から)、
    // 手前と横の縁は縁の外側から
    private static float CutFrom(Edge e) => (e.Ny > 0f ? e.Y0 : e.Y0 - e.LipH) - WaterSim.Cell * 0.01f;

    private const float ThrowGain = 0.5f;      // 越えた深さ 1 単位あたりの投げ出す距離 (√ に掛ける)

    private static float FallTime(Edge e) => MathF.Max(0.12f, e.Delay / (float)GameClock.Hz);

    // 縁に沿って u の所に、縁に立つ物の絵があるか (絵だけの判定・計算には使わない)
    private static bool Blocked(Edge e, float u)
    {
        float x = e.X0 - e.Ny * u, y = e.Y0 + e.Nx * u;
        return e.Ny != 0f ? WaterBarrier.At(x, y + 0.03f) || WaterBarrier.At(x, y - 0.03f) : WaterBarrier.At(x, y);
    }

    // 縁に沿った隣の縁 (同じ向き・流れている物)。無ければ null
    private static Edge Neighbor(Edge e, int side)
    {
        int tx = -(int)e.Ny * side, ty = (int)e.Nx * side;
        long key = (long)(e.K + tx + ty * WaterSim.W) * 4 + e.D;
        return Edges.TryGetValue(key, out var n) && n.Q >= 1f ? n : null;
    }

    // 縁から着く所までのずれ。横の縁は画面のまっすぐ下へ落ちる (縁に沿った面の膜は画面では縦の帯にしか見えない)。
    // 流された水が着く升は下の水たまりの広がりが見せる
    private static float LandDx(Edge e) => e.Nx != 0f ? 0f : e.X1 - e.X0;
    private static float LandDy(Edge e) => e.Y1 - e.Y0;

    private const float Spread = 0.4f;         // 柱の間を抜けた水が横へ広がって 1 枚に戻るまでの落ちた割合

    // 縁から着地までの膜。縁に沿った世界の画素の格子の列ごとに、縁 (水たまりの縁の内側) から落ちた先までを 1 本の線で描く。
    // 落ちる道: 地面の上を (縁 - 落ちる高さ) → 着く所へまっすぐ、高さは落ちる高さから 2 次で 0 へ。
    // 量と着く所のずれは隣の縁との間で縁に沿ってなめらかにつなぐ (縁ごとに道が違っても膜が割れない)。
    // ずれが列の間隔より大きく変わる所は列を細かく分けて隙間を埋める
    private static void Sheet(Canvas c, Edge e, float now)
    {
        float fall = FallTime(e);
        float tau0 = Math.Min(1f, e.Tail / fall), tau1 = Math.Min(1f, e.Head / fall);
        if (tau1 <= tau0) return;
        float tx = -e.Ny, ty = e.Nx, half = WaterSim.Cell * 0.5f;
        float s0 = e.X0 * tx + e.Y0 * ty;
        int k0 = (int)MathF.Ceiling((s0 - half) * Ppu), k1 = (int)MathF.Ceiling((s0 + half) * Ppu) - 1;
        if (k1 < k0) return;
        var na = Neighbor(e, -1);
        var nb = Neighbor(e, 1);
        float dx = LandDx(e), dy = LandDy(e);
        float qa = na == null ? e.Q : (na.Q + e.Q) * 0.5f, qb = nb == null ? e.Q : (nb.Q + e.Q) * 0.5f;
        float xa = na == null ? dx : (LandDx(na) + dx) * 0.5f, xb = nb == null ? dx : (LandDx(nb) + dx) * 0.5f;
        float ya = na == null ? dy : (LandDy(na) + dy) * 0.5f, yb = nb == null ? dy : (LandDy(nb) + dy) * 0.5f;
        int cols = k1 - k0 + 1;
        float jump = MathF.Max(MathF.Abs(xb - xa), MathF.Abs(yb - ya)) * Ppu;
        int sub = Math.Clamp((int)MathF.Ceiling(jump / cols), 1, 6);
        // 縁に立つ物の無い列と物の上を越える列の流量の比 (越えた高さの 1.5 乗)。縁全体の平均で割って量を保つ
        float wo = 1f, wb = 1f;
        if (!e.Lip && e.BarF > 0f)
        {
            float eb = MathF.Max(0f, e.Depth - e.BarH);
            wo = e.Depth * MathF.Sqrt(e.Depth);
            wb = eb * MathF.Sqrt(eb);
            float mean = (1f - e.BarF) * wo + e.BarF * wb;
            if (mean > 0f) { wo /= mean; wb /= mean; } else { wo = 1f; wb = 0f; }
        }
        for (int col = k0; col <= k1; col++)
        {
            float u0 = col / Ppu - s0;
            bool top = !e.Lip && e.BarF > 0f && Blocked(e, u0);
            // 物の上を越える水がある列だけ物の上から落ちる。越えない列 (水面が物より低い) は隣の列から回り込んだ水なので水面から落ちる
            float z0 = top && e.Depth > e.BarH ? e.BarH : 0f;
            float thin = 1f;
            float thr = MathF.Min(ThrowMax, ThrowGain * MathF.Sqrt(MathF.Max(0f, e.Depth - z0))) * (0.9f + 0.2f * Noise(col + 7919, now * 0.6f));
            for (int si = 0; si < sub; si++)
            {
                float u = u0 + si / (Ppu * sub);
                float f = u < 0f ? -u / half : u / half;
                float q = u < 0f ? e.Q + (qa - e.Q) * f : e.Q + (qb - e.Q) * f;
                float ldx = u < 0f ? dx + (xa - dx) * f : dx + (xb - dx) * f;
                float ldy = u < 0f ? dy + (ya - dy) * f : dy + (yb - dy) * f;
                float dm = q / SheetFull;
                if (dm < ThinD) { if (si == 0) thin = 1f + (Noise(col, now * 1.3f) * 1.6f - 0.8f) * (1f - dm / ThinD) * 1.25f; dm *= thin; }
                if (dm > DensMax) dm = DensMax;
                float dc = dm * (top ? wb : wo);
                if (dc > DensMax) dc = DensMax;
                if (dm <= 0.01f) continue;
                dm /= sub; dc /= sub;
                float sx = e.X0 + tx * u, sy = e.Y0 + ty * u;
                float h0 = MathF.Max(0.3f, -ldy);
                float gx = sx, gy = sy - h0, hh = h0 + z0;
                float ex = sx + ldx + e.Nx * thr, ey = sy + ldy + e.Ny * thr;
                // 手前の床は縁の外側の升の列で探す (横の縁は縁の線ちょうどだと縁の内側の升 = 落ちる元の床を拾い、膜が丸ごと隠れる)
                float cut = e.Void ? WaterSim.FrontCut(sx + e.Nx * WaterSim.Cell * 0.25f, CutFrom(e), ey) : float.MinValue;
                if (sy + z0 <= cut) continue;
                // 奥の縁を越えた水は床の向こう側へ落ちて見えない (手前に幕を描くと奥の柵の前に滝が立って見える)
                if (e.Ny > 0f && !e.Lip) continue;
                float len = (MathF.Abs(ex - sx) + MathF.Abs(ey - sy) + 2f * hh) * Ppu * (tau1 - tau0);
                int steps = Math.Clamp((int)MathF.Ceiling(len), 1, 600);
                float w = MathF.Min(1f, len / steps) * 0.85f;
                for (int j = 0; j <= steps; j++)
                {
                    float tau = tau0 + (tau1 - tau0) * j / steps;
                    float x = gx + (ex - gx) * tau, y = gy + (ey - gy) * tau + hh * (1f - tau * tau);
                    if (y < cut) break; // ここから先は手前の床の裏
                    float foam = tau > 0.92f ? 0.8f : tau < 0.03f ? 0.4f : 0.05f + 0.3f * tau * tau;
                    float a = e.Void && tau > 0.7f ? (1f - tau) / 0.3f : 1f;
                    // 柱の間を抜けた水は落ちながら横へ広がり、柱の下も埋まって 1 枚に戻る
                    float sp = tau >= Spread ? 1f : tau / Spread;
                    float d = dc + (dm - dc) * sp * sp * (3f - 2f * sp);
                    if (d <= 0.005f) continue;
                    float v = d * w * a / (1f + Thin * tau);
                    // 色は縁の水たまりの深さのまま、水たまりの色から噴き出しの色へ移る割合だけを落ちた割合で変える
                    // (縁に立つ物の有無で深さを変えると、物の形どおりに色が割れる)
                    float pf = tau >= PoolFade ? 0f : 1f - tau / PoolFade;
                    float pool = pf * pf * (3f - 2f * pf);
                    if (e.Nx == 0f) { Splat(c, x, y, v, foam, e.Depth, pool); continue; }
                    // 横の縁の膜は縁に沿った面なので画面では縦の帯になる。投げ出された先から縁の側へ厚さを付けて、縁と膜の間を空けない
                    float span = (x - sx) * SideBody;
                    int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(span) * Ppu));
                    for (int b = 0; b < n; b++) Splat(c, x - span * b / n, y, v, foam, e.Depth, pool);
                }
            }
        }
    }

    // 画面の範囲を測り直して画面から離れた絵を片づけ、それでも上限なら、絵の上限で描けない縁のうちカメラにいちばん近い物へ、カメラからいちばん遠い絵を譲る (水が多いと縁が上限を超え、
    // 先に流れ始めた遠くの崖が絵を持ったまま、目の前の滝が描かれないことがある)。半秒に 1 枚だけ
    private static void Steal()
    {
        var cam = Camera.main;
        if (!cam) return;
        var cp = cam.transform.position;
        float cx = cp.x, cy = cp.y, hh = cam.orthographicSize, hw = hh * cam.aspect;
        _vx0 = cx - hw; _vx1 = cx + hw; _vy0 = cy - hh; _vy1 = cy + hh;
        // 画面から離れた絵を片づける (上限を画面の中の滝に回す)
        for (int i = Canvases.Count - 1; i >= 0; i--)
        {
            var c = Canvases[i];
            if (InView(c.X0, c.Y0, c.X0 + c.W / Ppu, c.Y0 + c.H / Ppu, ViewOut)) continue;
            Destroy(c);
            Canvases.RemoveAt(i);
        }
        if (Canvases.Count < MaxCanvases) return;
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

    private static bool InView(float x0, float y0, float x1, float y1, float m)
        => x1 >= _vx0 - m && x0 <= _vx1 + m && y1 >= _vy0 - m && y0 <= _vy1 + m;

    // その縁の落ちる範囲が入る絵 (無ければ作る・上限を超えたら流れていない絵を使い回す)
    private static unsafe Canvas CanvasFor(Edge e)
    {
        float lx = Math.Min(e.X0, e.X1), hx = Math.Max(e.X0, e.X1);
        float ly = Math.Min(e.Y0, e.Y1) - (e.Void ? 0f : 0.35f), hy = Math.Max(e.Y0, e.Y1);
        foreach (var c in Canvases)
            if (c.Void == e.Void && lx - Margin >= c.X0 && hx + Margin <= c.X0 + c.W / Ppu && ly - Margin >= c.Y0 && hy + Margin <= c.Y0 + c.H / Ppu) return c;
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
        var n = new Canvas { Void = e.Void, X0 = x0, Y0 = y0, W = (int)MathF.Ceiling((x1 - x0) * Ppu), H = (int)MathF.Ceiling((y1 - y0) * Ppu) };
        n.Px = new byte[n.W * n.H * 4];
        for (int i = 3; i < n.Px.Length; i += 4) n.Px[i] = 255; // 描いていない所も不透明の濃さ 0 にしておく
        n.Dens = new float[n.W * n.H];
        n.Foam = new float[n.W * n.H];
        n.Dep = new float[n.W * n.H];
        n.Pool = new float[n.W * n.H];
        n.Tex = new Texture2D(n.W, n.H, TextureFormat.RGBA32, false) { name = "MrpFall", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        // 作ったままの中身は決まっていない (白く出ると絵の四角い範囲いっぱいに滝が見える) ので、最初の描き直しの前に空を送る
        fixed (byte* b = n.Px) n.Tex.LoadRawTextureData((IntPtr)b, n.Px.Length);
        n.Tex.Apply(false, false);
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
        // 奈落へ落ちる水は縁の手前の絵 (エアシップのデッキの床と手すりはクルーと同じ奥行き) より手前を落ちる
        if (e.Void) floor = Math.Min(floor, DamageMap.FrontZ(FxMath.V2(e.X0, e.Y0), true));
        // 屋外の崖 (ファングル) の縁には部屋の絵が無く、遠くの絵の奥行きを拾って崖の絵の裏に隠れるので、クルーのすぐ奥より奥へは置かない
        floor = Math.Min(floor, FallZMax);
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
            Array.Clear(c.Dep, o, len);
            Array.Clear(c.Pool, o, len);
        }
        c.Bx0 = w; c.By0 = c.H; c.Bx1 = -1; c.By1 = -1;
        float clock = (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency % 4096.0);
        foreach (var e in Edges.Values)
            if (e.C == c) Sheet(c, e, clock);
        // 着地の泡 (下の水たまりへつなぐ)
        foreach (var e in Edges.Values)
        {
            if (e.C != c || e.Void || e.Q < 1f || e.Head < FallTime(e)) continue;
            float r = ImpactR0 + ImpactR1 * Math.Min(1f, e.Q / ImpactFlow);
            float thr = MathF.Min(ThrowMax, ThrowGain * MathF.Sqrt(e.Depth)) + 0.05f;
            Disc(c, e.X0 + LandDx(e) + e.Nx * thr, e.Y1 + e.Ny * thr, r, Math.Min(1f, e.Q / ImpactFlow * 2f));
        }
        // ガラスの手すりを越える水: 手すりの上の縁を覆う膜 (縁に沿って切れ目なく・上の縁は泡で白く) を、手すりの面に沿って垂らす
        foreach (var e in Edges.Values)
        {
            if (e.C != c || !e.Lip || e.Q < 1f) continue;
            float s = Math.Min(1f, e.Q / LipFlow);
            if (e.Void && e.Ny > 0f) LipCrest(c, e, s);
            else LipSheet(c, e, s);
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
                float dp = c.Dep[i] * 0.5f;
                uint bl = dp >= 1f ? 255u : (uint)(dp * 255f);
                float pl = c.Pool[i];
                uint al = 255u - (pl >= 1f ? 255u : (uint)(pl * 255f));
                px[i] = (al << 24) | (bl << 16) | (g << 8) | r; // RGBA32 を little endian で (R = 濃さ・G = 泡・B = 深さ / 2・A = 1 - 水たまりの色の割合)
            }
            c.Tex.LoadRawTextureData((IntPtr)b, c.Px.Length);
        }
        c.Tex.Apply(false, false);
        c.X0p = c.Bx0; c.Y0p = c.By0; c.X1p = c.Bx1; c.Y1p = c.By1;
    }

    // 手すりの上の縁 (Y0) から手すりの面を LipDrop だけ垂れる膜。縁に沿って 1 画素ごとに縦の線を置き、長さを少し揺らす
    private static void LipSheet(Canvas c, Edge e, float s)
    {
        float tx = -e.Ny, ty = e.Nx; // 縁に沿った向き
        int n = (int)MathF.Ceiling(LipHalf * 2f * Ppu);
        float t = (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        for (int k = 0; k <= n; k++)
        {
            float u = -LipHalf + k / Ppu;
            float x = e.X0 + tx * u, y = e.Y0 + ty * u;
            float wob = 0.5f + 0.5f * MathF.Sin(x * 23f + t * 9f);
            int ax = (int)((x - c.X0) * Ppu), ay = (int)((y - c.Y0) * Ppu);
            float drop = LipDrop * (0.7f + 0.3f * wob);
            if (e.Void) drop = MathF.Min(drop, y - e.Cut);
            int len = (int)(drop * Ppu);
            for (int j = 0; j <= len; j++)
                Put(c, ax, ay - j, LipDens * s, j == 0 ? 0.9f : 0.15f);
        }
    }

    // 奥の手すりを越えて向こうへこぼれる水: 水面 (手すりの上の縁か、その上に盛り上がった水面) から CrestUp だけ盛り上がって奥へ消える膜。上ほど薄く、先端は泡で白い
    private const float CrestUp = 0.06f, CrestDens = 0.6f;
    private static void LipCrest(Canvas c, Edge e, float s)
    {
        float tx = -e.Ny, ty = e.Nx;
        float top = MathF.Max(e.Y0, e.Cut);
        int n = (int)MathF.Ceiling(LipHalf * 2f * Ppu);
        float t = (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        for (int k = 0; k <= n; k++)
        {
            float u = -LipHalf + k / Ppu;
            float x = e.X0 + tx * u, y = top + ty * u;
            float wob = 0.5f + 0.5f * MathF.Sin(x * 19f + t * 7f);
            int ax = (int)((x - c.X0) * Ppu), ay = (int)((y - c.Y0) * Ppu);
            int len = Math.Max(1, (int)((CrestUp * (0.6f + 0.4f * wob)) * Ppu));
            for (int j = 0; j <= len; j++)
                Put(c, ax, ay + j, LipDens * CrestDens * s * (1f - 0.6f * j / (len + 1f)), j == len ? 0.9f : 0.2f);
        }
    }

    // 世界の点へ濃さ w を周りの 4 画素に割り振って置く (隣の列・隣の点と継ぎ目なく重なる)
    private static void Splat(Canvas c, float wx, float wy, float w, float foam, float dep = 0f, float pool = 0f)
    {
        float fx = (wx - c.X0) * Ppu - 0.5f, fy = (wy - c.Y0) * Ppu - 0.5f;
        int x = (int)MathF.Floor(fx), y = (int)MathF.Floor(fy);
        if (x < 0 || y < 0 || x >= c.W - 1 || y >= c.H - 1) return;
        float ax = fx - x, ay = fy - y;
        Grow(c, x, y, x + 1, y + 1);
        int n = c.W, i = y * n + x;
        Add(c, i, (1f - ax) * (1f - ay) * w, foam);
        Add(c, i + 1, ax * (1f - ay) * w, foam);
        Add(c, i + n, (1f - ax) * ay * w, foam);
        Add(c, i + n + 1, ax * ay * w, foam);
        if (dep > 0f)
        {
            if (c.Dep[i] < dep) c.Dep[i] = dep;
            if (c.Dep[i + 1] < dep) c.Dep[i + 1] = dep;
            if (c.Dep[i + n] < dep) c.Dep[i + n] = dep;
            if (c.Dep[i + n + 1] < dep) c.Dep[i + n + 1] = dep;
        }
        if (pool > 0f)
        {
            if (c.Pool[i] < pool) c.Pool[i] = pool;
            if (c.Pool[i + 1] < pool) c.Pool[i + 1] = pool;
            if (c.Pool[i + n] < pool) c.Pool[i + n] = pool;
            if (c.Pool[i + n + 1] < pool) c.Pool[i + n + 1] = pool;
        }
    }

    private static void Put(Canvas c, int x, int y, float w, float foam)
    {
        if (x < 0 || y < 0 || x >= c.W || y >= c.H) return;
        Grow(c, x, y, x, y);
        Add(c, y * c.W + x, w, foam);
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
        _lastMs = 0;
        _vx0 = _vy0 = float.MinValue; _vx1 = _vy1 = float.MaxValue;
        _stealAcc = StealEvery;
    }

    internal static string Describe()
    {
        float q = 0f;
        int held = 0;
        foreach (var e in Edges.Values) { q = Math.Max(q, e.Q); if (e.C != null && !e.C.Gone) held++; }
        return $"falls={Canvases.Count} fallEdges={Edges.Count} held={held} qMax={q:0} fallMs={LastDrawMs:0.00}";
    }
}
