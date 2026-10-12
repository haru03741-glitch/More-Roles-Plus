using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    private const int MaxCanvases = 20;
#else
    private const float Ppu = 24f;
    private const float Redraw = 1f / 30f;
    private const int MaxCanvases = 32;
#endif
    private const float SheetFull = 240f;      // 列の濃さが 1 になる縁の流量 (WaterSim の量の単位/秒)
    private const float DensMax = 1.3f;
    private const float ThinD = 0.35f;         // これより薄い膜は列ごとに筋へちぎれる
    private const float PoolFade = 0.6f;      // 落ち始めてから水たまりの色が噴き出しの色へ移り切るまでの落ちた割合
    private const float SideBody = 0.75f;      // 横の縁の膜の厚さ (縁から投げ出された先までのうち、先の側から何割を埋めるか)
    private const float Thin = 0.5f;           // 落ちるほど速くなって膜が薄くなる割合 (着地で 1 / (1 + Thin))
    // 絵は世界の格子に揃えた一辺 TileW の升 (隣の升と重ならず、縁はかかる升の全部へ描く)。一辺の画素数が整数になる長さ
    private const float TileW = 4f;
    private const int TilePx = (int)(TileW * Ppu);
    private const float FallZMax = 0.12f;      // 絵を置く奥行きの上限 (クルーと同じ奥行きの帯のすぐ奥)
    private const float Pad = 0.75f;           // 縁が描く範囲の余白 (外へ投げ出す距離 + 着地の泡 + 放物線のふくらみ)
    private const float TopPad = 0.15f;        // 縁より上へ描く分 (縁に立つ物・手すりの高さの上)
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
        public float Bx0, By0, Bx1, By1;   // 描く範囲 (世界・Extent)
        public bool Live;                  // 流れているか、止まった後の膜の後ろの端がまだ落ちている
        public int FrameSum;
    }

    private sealed class Canvas
    {
        public long Key;
        public int Tx, Ty;
        public float X0, Y0;
        public int W, H;
        public float Idle;
        public bool Hidden;
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
        public GCHandle HPx, HDens, HFoam, HDep, HPool; // C++ と作業スレッドが使うので作ってから片づけるまで固定する
    }

    private static readonly Dictionary<long, Edge> Edges = new();
    private static readonly List<Canvas> Canvases = new();
    private static readonly Dictionary<long, Canvas> Tiles = new();
    private static long _clipped, _puts; // 升の絵が無くて置けなかった画素・置いた画素 (leak の表示)
    private static int _starved;  // 前の描き直しで升の上限のために描けなかった画面の中の縁の数
    private static float _redrawAcc, _meetAcc, _stealAcc = StealEvery; // 最初の刻みで画面の範囲を測る
    private static float _vx0 = float.MinValue, _vy0 = float.MinValue, _vx1 = float.MaxValue, _vy1 = float.MaxValue; // 画面の範囲 (世界)
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
        if (_jobState != 0)
        {
            try { FinishJobs(); }
            catch (Exception e) { Plugin.Logger.LogError($"[WaterFall] job: {e}"); Clear(); }
        }
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
                e.Live = e.Head > 0f && e.Tail < fall;
                continue;
            }
            // 止まってから流れ直した: 後ろの端が落ち切っていれば膜の先から出し直す
            if (e.Tail > 0f) { if (e.Tail >= fall) e.Head = 0f; e.Tail = 0f; }
            e.Head = MathF.Min(e.Head + dt, fall);
            e.Idle = 0f;
            e.Live = true;
        }
        foreach (var key in Dead) Edges.Remove(key);
        // 描く範囲は隣の縁の着く所も見るので、全部の縁を進めてから測る
        foreach (var e in Edges.Values)
        {
            if (!e.Live) continue;
            Extent(e);
            // 外へ落ちる水は手前の床 (と奥の縁の手すり・その上に盛り上がった水) より下では裏に隠れる (画面の外の縁は描かないので測らない)
            if (e.Void && InView(e.Bx0, e.By0, e.Bx1, e.By1, ViewIn)) e.Cut = WaterSim.FrontCut(e.X0, CutFrom(e), e.Y1);
        }

        // 会議の確認は Unity の物の生存確認になるので間引く
        _meetAcc += dt;
        if (_meetAcc >= 0.25f) { _meetAcc = 0f; _meeting = MeetingHud.Instance; }
        bool meeting = _meeting;
        for (int i = Canvases.Count - 1; i >= 0; i--)
        {
            var c = Canvases[i];
            c.Idle += dt;
            if (c.Idle > IdleLife) { Drop(i); continue; }
            if (c.Hidden != meeting) { c.Hidden = meeting; c.Sr.enabled = !meeting; }
        }
        // 縁ごとに 1 回描いて、点を升へ振り分ける
        _redrawAcc += dt;
        if (meeting || _redrawAcc < Redraw) return;
        _redrawAcc = 0f;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Assign();
        Draw();
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // 手前の床を探し始める y。奥の縁は縁の床そのもの (手すりの絵の根元は升の縁とずれている所があるので上の縁のすぐ下から)、
    // 手前と横の縁は縁の外側から
    private static float CutFrom(Edge e) => (e.Ny > 0f ? e.Y0 : e.Y0 - e.LipH) - WaterSim.Cell * 0.01f;

    private const float ThrowGain = 0.5f;      // 越えた深さ 1 単位あたりの投げ出す距離 (√ に掛ける)

    private static float FallTime(Edge e) => MathF.Max(0.12f, e.Delay / (float)GameClock.Hz);

    // 縁に沿って u の所に、縁に立つ物の絵があるか (絵だけの判定・計算には使わない)
    private static bool Blocked(in FallEdge e, float u)
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

    private const float Spread = 0.4f;
    private const float EndSpread = 0.12f;     // 隣に流れる縁が無い側へ膜を薄れながら延ばす幅 (世界単位)         // 柱の間を抜けた水が横へ広がって 1 枚に戻るまでの落ちた割合

    // 縁から着地までの膜。縁に沿った世界の画素の格子の列ごとに、縁 (水たまりの縁の内側) から落ちた先までを 1 本の線で描く。
    // 落ちる道: 地面の上を (縁 - 落ちる高さ) → 着く所へまっすぐ、高さは落ちる高さから 2 次で 0 へ。
    // 量と着く所のずれは隣の縁との間で縁に沿ってなめらかにつなぐ (縁ごとに道が違っても膜が割れない)。
    // ずれが列の間隔より大きく変わる所は列を細かく分けて隙間を埋める
    // 縁 (水の升の境) から床マスクの崖の縁 (床が終わる所) までの、外向きを正とする距離 (C++ の Brink と同じ)。
    // 縁の上が床なら外へ、床でなければ内へ BrinkSteps まで探す。見つからない (下の床まで続いている・升の中に床が無い) なら 0
    private const float BrinkStep = 0.02f;
    private const int BrinkSteps = 15;

    private static int MaskAt(float x, float y)
    {
        int mx = (int)MathF.Floor((x - _mox) * _mppu), my = (int)MathF.Floor((y - _moy) * _mppu);
        if (mx < 0 || my < 0 || mx >= _mw || my >= _mh) return -1;
        int i = my * _mw + mx;
        return (_mask[i >> 3] & (1 << (i & 7))) != 0 ? 1 : 0;
    }

    private static float Brink(float x, float y, float nx, float ny)
    {
        if (_mask == null) return 0f;
        int at = MaskAt(x, y);
        if (at < 0) return 0f;
        float sgn = at != 0 ? 1f : -1f;
        for (int k = 1; k <= BrinkSteps; k++)
        {
            float d = k * BrinkStep * sgn;
            int m = MaskAt(x + nx * d, y + ny * d);
            if (m < 0) return 0f;
            if (m != at) return at != 0 ? d : d + BrinkStep;
        }
        return 0f;
    }

    private static void SheetCs(in FallEdge e, float now)
    {
        float fall = e.Fall;
        float tau0 = Math.Min(1f, e.Tail / fall), tau1 = Math.Min(1f, e.Head / fall);
        float tx = -e.Ny, ty = e.Nx, half = WaterSim.Cell * 0.5f;
        float s0 = e.X0 * tx + e.Y0 * ty;
        int k0 = (int)MathF.Ceiling((s0 - half - e.Ea) * Ppu), k1 = (int)MathF.Ceiling((s0 + half + e.Eb) * Ppu) - 1;
        if (k1 < k0) return;
        float dx = e.Dx, dy = e.Dy;
        float qa = e.Qa, qb = e.Qb, xa = e.Xa, xb = e.Xb, ya = e.Ya, yb = e.Yb;
        int cols = k1 - k0 + 1;
        float jump = MathF.Max(MathF.Abs(xb - xa), MathF.Abs(yb - ya)) * Ppu;
        int sub = Math.Clamp((int)MathF.Ceiling(jump / cols), 1, 6);
        // 縁に立つ物の無い列と物の上を越える列の流量の比 (越えた高さの 1.5 乗)。縁全体の平均で割って量を保つ
        float wo = 1f, wb = 1f;
        if (e.Lip == 0 && e.BarF > 0f)
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
            // 列の深さと膜の先端・後ろの端は隣の縁との間でつなぎ (升ごとの段を作らない)、縁の外へ延ばした列は端の値のまま薄れる
            float fc = MathF.Min(1f, (u0 < 0f ? -u0 : u0) / half);
            float fade = u0 < -half && e.Ea > 0f ? 1f - (-u0 - half) / e.Ea : u0 > half && e.Eb > 0f ? 1f - (u0 - half) / e.Eb : 1f;
            if (fade <= 0f) continue;
            float dep = u0 < 0f ? e.Depth + (e.Da - e.Depth) * fc : e.Depth + (e.Db - e.Depth) * fc;
            float t0 = u0 < 0f ? tau0 + (e.Ta - tau0) * fc : tau0 + (e.Tb - tau0) * fc;
            float t1 = u0 < 0f ? tau1 + (e.Ha - tau1) * fc : tau1 + (e.Hb - tau1) * fc;
            if (t1 <= t0) continue;
            bool top = e.Lip == 0 && e.BarF > 0f && Blocked(e, u0);
            // 物の上を越える水がある列だけ物の上から落ちる。越えない列 (水面が物より低い) は隣の列から回り込んだ水なので水面から落ちる
            float z0 = top && e.Depth > e.BarH ? e.BarH : 0f;
            float thin = 1f;
            float thr = MathF.Min(ThrowMax, ThrowGain * MathF.Sqrt(MathF.Max(0f, dep - z0))) * (0.9f + 0.2f * Noise(col + 7919, now * 0.6f));
            for (int si = 0; si < sub; si++)
            {
                float u = u0 + si / (Ppu * sub);
                float f = MathF.Min(1f, u < 0f ? -u / half : u / half);
                float q = u < 0f ? e.Q + (qa - e.Q) * f : e.Q + (qb - e.Q) * f;
                float ldx = u < 0f ? dx + (xa - dx) * f : dx + (xb - dx) * f;
                float ldy = u < 0f ? dy + (ya - dy) * f : dy + (yb - dy) * f;
                float dm = q * fade / SheetFull;
                if (dm < ThinD) { if (si == 0) thin = 1f + (Noise(col, now * 1.3f) * 1.6f - 0.8f) * (1f - dm / ThinD) * 1.25f; dm *= thin; }
                if (dm > DensMax) dm = DensMax;
                float dc = dm * (top ? wb : wo);
                if (dc > DensMax) dc = DensMax;
                if (dm <= 0.01f) continue;
                dm /= sub; dc /= sub;
                float sx = e.X0 + tx * u, sy = e.Y0 + ty * u;
                float h0 = MathF.Max(0.3f, -ldy);
                float ex = sx + ldx + e.Nx * thr, ey = sy + ldy + e.Ny * thr;
                // 膜は絵の崖の縁から始める (縁と崖の縁の間の水は床の水の絵が描く)。着く所は変えない
                float br = Brink(sx, sy, e.Nx, e.Ny);
                sx += e.Nx * br; sy += e.Ny * br;
                float gx = sx, gy = sy - h0, hh = h0 + z0;
                // 手前の床は縁の外側の升の列で探す (横の縁は縁の線ちょうどだと縁の内側の升 = 落ちる元の床を拾い、膜が丸ごと隠れる)
                float cut = e.Void != 0 ? WaterSim.FrontCut(sx + e.Nx * WaterSim.Cell * 0.25f, e.CutFrom, ey) : float.MinValue;
                if (sy + z0 <= cut) continue;
                // 奥の縁を越えた水は床の向こう側へ落ちて見えない (手前に幕を描くと奥の柵の前に滝が立って見える)
                if (e.Ny > 0f && e.Lip == 0) continue;
                float len = (MathF.Abs(ex - sx) + MathF.Abs(ey - sy) + 2f * hh) * Ppu * (t1 - t0);
                int steps = Math.Clamp((int)MathF.Ceiling(len), 1, 600);
                float w = MathF.Min(1f, len / steps) * 0.85f;
                for (int j = 0; j <= steps; j++)
                {
                    float tau = t0 + (t1 - t0) * j / steps;
                    float x = gx + (ex - gx) * tau, y = gy + (ey - gy) * tau + hh * (1f - tau * tau);
                    if (y < cut) break; // ここから先は手前の床の裏
                    float foam = tau > 0.92f ? 0.8f : tau < 0.03f ? 0.4f : 0.05f + 0.3f * tau * tau;
                    float a = e.Void != 0 && tau > 0.7f ? (1f - tau) / 0.3f : 1f;
                    // 柱の間を抜けた水は落ちながら横へ広がり、柱の下も埋まって 1 枚に戻る
                    float sp = tau >= Spread ? 1f : tau / Spread;
                    float d = dc + (dm - dc) * sp * sp * (3f - 2f * sp);
                    if (d <= 0.005f) continue;
                    float v = d * w * a / (1f + Thin * tau);
                    // 色は縁の水たまりの深さのまま、水たまりの色から噴き出しの色へ移る割合だけを落ちた割合で変える
                    // (縁に立つ物の有無で深さを変えると、物の形どおりに色が割れる)
                    float pf = tau >= PoolFade ? 0f : 1f - tau / PoolFade;
                    float pool = pf * pf * (3f - 2f * pf);
                    if (e.Nx == 0f) { Splat(e.Void != 0, x, y, v, foam, dep, pool); continue; }
                    // 横の縁の膜は縁に沿った面なので画面では縦の帯になる。投げ出された先から縁の側へ厚さを付けて、縁と膜の間を空けない
                    float span = (x - sx) * SideBody;
                    int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(span) * Ppu));
                    for (int b = 0; b < n; b++) Splat(e.Void != 0, x - span * b / n, y, v, foam, dep, pool);
                }
            }
        }
    }

    // 画面の範囲を測り直して画面から離れた絵を片づける (上限を画面の中の滝に回す)
    private static void Steal()
    {
        var cam = Camera.main;
        if (!cam) return;
        var cp = cam.transform.position;
        float cx = cp.x, cy = cp.y, hh = cam.orthographicSize, hw = hh * cam.aspect;
        _vx0 = cx - hw; _vx1 = cx + hw; _vy0 = cy - hh; _vy1 = cy + hh;
        _camX = cx; _camY = cy;
        for (int i = Canvases.Count - 1; i >= 0; i--)
        {
            var c = Canvases[i];
            if (!InView(c.X0, c.Y0, c.X0 + TileW, c.Y0 + TileW, ViewOut)) Drop(i);
        }
    }

    private static float _camX, _camY;

    private static bool InView(float x0, float y0, float x1, float y1, float m)
        => x1 >= _vx0 - m && x0 <= _vx1 + m && y1 >= _vy0 - m && y0 <= _vy1 + m;

    // 縁が描く範囲 (世界): 縁と着く所 (隣の縁とならした着く所も) を囲み、外へ投げ出す距離・着地の泡・縁に立つ物や手すりの上の分を足す
    private static void Extent(Edge e)
    {
        float half = WaterSim.Cell * 0.5f;
        float dx = LandDx(e), dy = LandDy(e);
        float dx0 = dx, dx1 = dx, dy0 = dy;
        for (int side = -1; side <= 1; side += 2)
        {
            var n = Neighbor(e, side);
            if (n == null) continue;
            float ax = (LandDx(n) + dx) * 0.5f, ay = (LandDy(n) + dy) * 0.5f;
            if (ax < dx0) dx0 = ax;
            if (ax > dx1) dx1 = ax;
            if (ay < dy0) dy0 = ay;
        }
        float tx = MathF.Abs(e.Ny) * half, ty = MathF.Abs(e.Nx) * half;
        e.Bx0 = e.X0 - tx + MathF.Min(0f, dx0) - Pad;
        e.Bx1 = e.X0 + tx + MathF.Max(0f, dx1) + Pad;
        e.By0 = e.Y0 - ty + MathF.Min(0f, dy0) - Pad;
        e.By1 = e.Y0 + ty + MathF.Max(e.LipH, e.BarH) + TopPad;
    }

    private static long TileKey(int tx, int ty, bool v) => ((long)(tx + 32768) << 17) | ((long)(ty + 32768) << 1) | (v ? 1L : 0L);

    // 流れている縁がかかる画面の中の升に絵を用意する。上限で作れない升が残ったら、カメラにいちばん近いその升へ、カメラからいちばん遠い絵を譲る
    // (先に流れ始めた遠くの崖が絵を持ったまま、目の前の滝が描かれないことがある)。描き直しごとに 1 枚だけ
    private static void Assign()
    {
        _starved = 0;
        if (!MrpBundle.WaterMaterial) return; // 絵が作れない間は升を譲らない (捨てるだけになる)
        float wd = StealNear * StealNear;
        int wtx = 0, wty = 0;
        Edge want = null;
        foreach (var e in Edges.Values)
        {
            if (!e.Live || !InView(e.Bx0, e.By0, e.Bx1, e.By1, ViewIn)) continue;
            int tx0 = (int)MathF.Floor(e.Bx0 / TileW), tx1 = (int)MathF.Floor(e.Bx1 / TileW);
            int ty0 = (int)MathF.Floor(e.By0 / TileW), ty1 = (int)MathF.Floor(e.By1 / TileW);
            bool miss = false;
            for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                float x0 = tx * TileW, y0 = ty * TileW;
                if (Tiles.TryGetValue(TileKey(tx, ty, e.Void), out var c)) { c.Idle = 0f; continue; }
                if (!InView(x0, y0, x0 + TileW, y0 + TileW, ViewIn)) continue;
                if (Canvases.Count < MaxCanvases && Make(e, tx, ty) != null) continue;
                miss = true;
                float ddx = x0 + TileW * 0.5f - _camX, ddy = y0 + TileW * 0.5f - _camY, d = ddx * ddx + ddy * ddy;
                if (d < wd) { wd = d; want = e; wtx = tx; wty = ty; }
            }
            if (miss) _starved++;
        }
        if (want == null) return;
        int far = -1;
        float fd = 0f;
        for (int i = 0; i < Canvases.Count; i++)
        {
            var c = Canvases[i];
            float dx = c.X0 + TileW * 0.5f - _camX, dy = c.Y0 + TileW * 0.5f - _camY, d = dx * dx + dy * dy;
            if (d > fd) { fd = d; far = i; }
        }
        float need = MathF.Sqrt(wd) + StealGap;
        if (far < 0 || fd < need * need) return;
        Drop(far);
        Make(want, wtx, wty);
    }

    private static void Drop(int i)
    {
        var c = Canvases[i];
        Destroy(c);
        Tiles.Remove(c.Key);
        Canvases.RemoveAt(i);
    }

    // 升 (tx, ty) の絵を作る。置く奥行きは作るきっかけの縁で決める
    private static unsafe Canvas Make(Edge e, int tx, int ty)
    {
        var mat0 = MrpBundle.WaterMaterial;
        if (!mat0) return null;
        float x0 = tx * TileW, y0 = ty * TileW;
        var n = new Canvas { Key = TileKey(tx, ty, e.Void), Tx = tx, Ty = ty, Void = e.Void, X0 = x0, Y0 = y0, W = TilePx, H = TilePx };
        try
        {
            n.Px = new byte[n.W * n.H * 4];
            for (int i = 3; i < n.Px.Length; i += 4) n.Px[i] = 255; // 描いていない所も不透明の濃さ 0 にしておく
            n.Dens = new float[n.W * n.H];
            n.Foam = new float[n.W * n.H];
            n.Dep = new float[n.W * n.H];
            n.Pool = new float[n.W * n.H];
            n.HPx = GCHandle.Alloc(n.Px, GCHandleType.Pinned);
            n.HDens = GCHandle.Alloc(n.Dens, GCHandleType.Pinned);
            n.HFoam = GCHandle.Alloc(n.Foam, GCHandleType.Pinned);
            n.HDep = GCHandle.Alloc(n.Dep, GCHandleType.Pinned);
            n.HPool = GCHandle.Alloc(n.Pool, GCHandleType.Pinned);
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
        }
        catch
        {
            Destroy(n); // 途中で落ちたら固定と作りかけの物を戻す
            throw;
        }
        Canvases.Add(n);
        Tiles[n.Key] = n;
        return n;
    }

    // C++ へ渡す縁 (FallEdge)・升 (FallTile)・引数。並びは native/src/water_fall.cpp と同じ
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FallEdge
    {
        public float X0, Y0, Nx, Ny, Q, Head, Tail, Fall, Depth, BarH, BarF, LipH, Cut, CutFrom;
        public float Dx, Dy, Qa, Xa, Ya, Qb, Xb, Yb;
        public float Da, Db, Ha, Hb, Ta, Tb;   // 隣の縁とならした深さ・膜の先端と後ろの端 (落ちた割合・縁の両端)
        public float Ea, Eb;                   // 隣に流れる縁が無い側へ膜を薄れながら延ばす幅 (無ければ 0)
        public int Lip, Void;
        public int Disc; public float DiscX, DiscY, DiscR, DiscS;
        public int LipMode; public float LipS;
        public fixed float Wob[12];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FallTile
    {
        public float* Dens, Foam, Dep, Pool;
        public byte* Px;
        public int Tx, Ty, Void;
        public int X0p, Y0p, X1p, Y1p;
        public int Bx0, By0, Bx1, By1;
        public int Changed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FallArgs
    {
        public FallTile* Tiles; public int TileN, TilePx;
        public FallEdge* Edges; public int EdgeN;
        public float Ppu, Clock;
        public byte* Open; public int* Hgt; public int W, H; public float Ox, Oy, Cell;
        public float* Low; public int LowN;
        public byte* Bits; public int Bw, Bh; public float Box, Boy, Bppu;
        public byte* Mask; public int Mw, Mh; public float Mox, Moy, Mppu;
        public long Puts, Clipped;
    }

    // 描き直しは水の作業スレッドへ積み (LateUpdate で投げる)、次のフレームの Tick の頭で受け取って転送する (絵は 1 フレーム遅れ)
    private static FallEdge[] _edges = new FallEdge[64];
    private static readonly FallTile[] TileArgs = new FallTile[MaxCanvases];
    private static readonly Canvas[] JobTiles = new Canvas[MaxCanvases];
    private static int _edgeN, _tileN, _jobState, _jobGen; // _jobState: 0 = 無し / 1 = 積んだ / 2 = 描いた
    private static float _clock;
    private static byte[] _open, _bits, _mask;
    private static int[] _hgt;
    private static float[] _low = new float[0];
    private static int _lowN, _gw, _gh, _bw, _bh, _mw, _mh;
    private static float _gox, _goy, _box, _boy, _bppu, _mox, _moy, _mppu;
    internal static bool HasJobs => _jobState == 1;
    internal static Exception JobError;
    internal static long VerifyDraws, VerifyDrawDiff; // 答え合わせ (native verify): C# でも描いて画素を 1 バイトずつ比べる

    // 画面の近くで流れている縁だけ描く (升は画面の近くにしか無い)
    private static bool Drawn(Edge e) => e.Live && InView(e.Bx0, e.By0, e.Bx1, e.By1, ViewIn);

    // 縁ごとに 1 回描いて、点を升へ振り分ける
    private static void Draw()
    {
        Pack();
        if (Native.MrpNative.Async && !Native.MrpNative.Verify)
        {
            _jobState = 1;
            _jobGen = _shipGen;
            return;
        }
        WaterSim.EnsureIdle(); // 作業スレッドと同じ水の配列を読む
        if (Native.MrpNative.Verify) Verify();
        else DrawNative();
        Finish();
    }

    // 縁と升を詰める (メイン)。描く間は縁と升を変えない (変えるのは次のフレームの受け取りの後)
    private static unsafe void Pack()
    {
        double sec = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        _clock = (float)(sec % 4096.0);
        float t = (float)sec;
        int n = 0;
        foreach (var e in Edges.Values)
        {
            if (!Drawn(e)) continue;
            if (n == _edges.Length) Array.Resize(ref _edges, n * 2);
            ref var p = ref _edges[n++];
            p.X0 = e.X0; p.Y0 = e.Y0; p.Nx = e.Nx; p.Ny = e.Ny; p.Q = e.Q; p.Head = e.Head; p.Tail = e.Tail; p.Fall = FallTime(e);
            p.Depth = e.Depth; p.BarH = e.BarH; p.BarF = e.BarF; p.LipH = e.LipH; p.Cut = e.Cut; p.CutFrom = CutFrom(e);
            p.Lip = e.Lip ? 1 : 0; p.Void = e.Void ? 1 : 0;
            // 量と着く所のずれは隣の縁との間でならす (縁の両端は隣との平均)
            var na = Neighbor(e, -1);
            var nb = Neighbor(e, 1);
            float dx = LandDx(e), dy = LandDy(e);
            p.Dx = dx; p.Dy = dy;
            p.Qa = na == null ? e.Q : (na.Q + e.Q) * 0.5f; p.Qb = nb == null ? e.Q : (nb.Q + e.Q) * 0.5f;
            p.Xa = na == null ? dx : (LandDx(na) + dx) * 0.5f; p.Xb = nb == null ? dx : (LandDx(nb) + dx) * 0.5f;
            p.Ya = na == null ? dy : (LandDy(na) + dy) * 0.5f; p.Yb = nb == null ? dy : (LandDy(nb) + dy) * 0.5f;
            // 深さと膜の先端・後ろの端 (落ちた割合) も隣とならす。隣に流れる縁が無い側は膜を少し外へ薄れながら延ばす (並びの端の角を丸める)
            float hd = Math.Min(1f, e.Head / p.Fall), tl = Math.Min(1f, e.Tail / p.Fall);
            p.Da = na == null ? e.Depth : (na.Depth + e.Depth) * 0.5f; p.Db = nb == null ? e.Depth : (nb.Depth + e.Depth) * 0.5f;
            p.Ha = na == null ? hd : (Math.Min(1f, na.Head / FallTime(na)) + hd) * 0.5f; p.Hb = nb == null ? hd : (Math.Min(1f, nb.Head / FallTime(nb)) + hd) * 0.5f;
            p.Ta = na == null ? tl : (Math.Min(1f, na.Tail / FallTime(na)) + tl) * 0.5f; p.Tb = nb == null ? tl : (Math.Min(1f, nb.Tail / FallTime(nb)) + tl) * 0.5f;
            p.Ea = na == null ? EndSpread : 0f; p.Eb = nb == null ? EndSpread : 0f;
            // 着地の泡 (下の水たまりへつなぐ)
            p.Disc = !e.Void && e.Q >= 1f && e.Head >= p.Fall ? 1 : 0;
            if (p.Disc != 0)
            {
                float thr = MathF.Min(ThrowMax, ThrowGain * MathF.Sqrt(e.Depth)) + 0.05f;
                p.DiscX = e.X0 + dx + e.Nx * thr; p.DiscY = e.Y1 + e.Ny * thr;
                p.DiscR = ImpactR0 + ImpactR1 * Math.Min(1f, e.Q / ImpactFlow);
                p.DiscS = Math.Min(1f, e.Q / ImpactFlow * 2f);
            }
            // ガラスの手すりを越える水: 手すりの上の縁を覆う膜。揺れの sin はここで求める (C++ と C# で同じ値を使う)
            p.LipMode = e.Lip && e.Q >= 1f ? (e.Void && e.Ny > 0f ? 2 : 1) : 0;
            if (p.LipMode != 0)
            {
                p.LipS = Math.Min(1f, e.Q / LipFlow);
                float tx = -e.Ny;
                int kn = Math.Min(11, (int)MathF.Ceiling(LipHalf * 2f * Ppu));
                for (int k = 0; k <= kn; k++)
                {
                    float x = e.X0 + tx * (-LipHalf + k / Ppu);
                    p.Wob[k] = p.LipMode == 2 ? 0.5f + 0.5f * MathF.Sin(x * 19f + t * 7f) : 0.5f + 0.5f * MathF.Sin(x * 23f + t * 9f);
                }
            }
        }
        _edgeN = n;
        _tileN = Canvases.Count;
        for (int i = 0; i < _tileN; i++)
        {
            var c = Canvases[i];
            JobTiles[i] = c;
            TileArgs[i] = new FallTile
            {
                Dens = (float*)c.HDens.AddrOfPinnedObject(), Foam = (float*)c.HFoam.AddrOfPinnedObject(), Dep = (float*)c.HDep.AddrOfPinnedObject(),
                Pool = (float*)c.HPool.AddrOfPinnedObject(), Px = (byte*)c.HPx.AddrOfPinnedObject(),
                Tx = c.Tx, Ty = c.Ty, Void = c.Void ? 1 : 0, X0p = c.X0p, Y0p = c.Y0p, X1p = c.X1p, Y1p = c.Y1p,
            };
        }
        // 手前の床 (WaterSim.FrontCut) と縁に立つ物の絵 (WaterBarrier) の地図。配列が差し替わっても古い方は残るので参照だけ持つ
        if (!WaterSim.CutMaps(out _open, out _hgt, out _gw, out _gh, out _gox, out _goy)) { _open = null; _hgt = null; }
        if (!WaterBarrier.Bits(out _bits, out _bw, out _bh, out _box, out _boy, out _bppu)) _bits = null;
        if (FloorMask.Off || !FloorMask.MaskBits(out _mask, out _mw, out _mh, out _mox, out _moy, out _mppu)) _mask = null;
        var low = LowWalls.All;
        if (_low.Length < low.Count * 6) _low = new float[low.Count * 6];
        _lowN = 0;
        for (int i = 0; i < low.Count; i++)
        {
            var w = low[i];
            if (w.Dir != 2) continue;
            int o = _lowN++ * 6;
            _low[o] = w.X0; _low[o + 1] = w.X1; _low[o + 2] = w.Y0; _low[o + 3] = w.Y1; _low[o + 4] = w.Top; _low[o + 5] = w.Base;
        }
    }

    // 積んだ絵を描く (作業スレッド。投げられなかった時はメイン)。Unity には触らない
    internal static void RunJobs()
    {
        if (_jobState != 1) return;
        DrawNative();
        _jobState = 2;
    }

    private static unsafe void DrawNative()
    {
        fixed (FallEdge* ep = _edges) fixed (FallTile* tp = TileArgs) fixed (byte* op = _open) fixed (int* hp = _hgt) fixed (float* lp = _low) fixed (byte* bp = _bits) fixed (byte* mp = _mask)
        {
            var a = new FallArgs
            {
                Tiles = tp, TileN = _tileN, TilePx = TilePx, Edges = ep, EdgeN = _edgeN, Ppu = Ppu, Clock = _clock,
                Open = op, Hgt = hp, W = _gw, H = _gh, Ox = _gox, Oy = _goy, Cell = WaterSim.Cell,
                Low = lp, LowN = _lowN, Bits = bp, Bw = _bw, Bh = _bh, Box = _box, Boy = _boy, Bppu = _bppu,
                Mask = mp, Mw = _mw, Mh = _mh, Mox = _mox, Moy = _moy, Mppu = _mppu,
            };
            Native.MrpNative.FallDraw(&a);
            _puts += a.Puts;
            _clipped += a.Clipped;
        }
    }

    // 描いた絵を受け取って転送する (メイン)。船が替わっていたら絵ごと壊れているので捨てる
    private static void FinishJobs()
    {
        try
        {
            WaterSim.EnsureIdle();
            if (JobError != null)
            {
                var e = JobError;
                JobError = null;
                throw new InvalidOperationException("fall job", e);
            }
            if (_jobGen != GameClock.ShipGen || _jobGen != _shipGen) return;
            if (_jobState == 1) DrawNative(); // 投げられなかった
            Finish();
        }
        finally
        {
            _jobState = 0;
            Array.Clear(JobTiles, 0, JobTiles.Length);
        }
    }

    private static unsafe void Finish()
    {
        for (int i = 0; i < _tileN; i++)
        {
            var c = JobTiles[i];
            ref var t = ref TileArgs[i];
            c.X0p = t.X0p; c.Y0p = t.Y0p; c.X1p = t.X1p; c.Y1p = t.Y1p;
            if (t.Changed == 0) continue;
            c.Tex.LoadRawTextureData((IntPtr)t.Px, c.Px.Length);
            c.Tex.Apply(false, false);
        }
    }

    // 照合: C++ で描いた後、描く前の升に戻して C# でも描き、画素と範囲を比べる (残すのは C# の結果)
    private static void Verify()
    {
        var keep = new (float[] d, float[] f, float[] dp, float[] pl, byte[] px)[_tileN];
        var rect0 = new FallTile[_tileN];
        for (int i = 0; i < _tileN; i++)
        {
            var c = JobTiles[i];
            keep[i] = ((float[])c.Dens.Clone(), (float[])c.Foam.Clone(), (float[])c.Dep.Clone(), (float[])c.Pool.Clone(), (byte[])c.Px.Clone());
            rect0[i] = TileArgs[i];
        }
        DrawNative();
        var native = new byte[_tileN][];
        var rect1 = new FallTile[_tileN];
        for (int i = 0; i < _tileN; i++)
        {
            var c = JobTiles[i];
            native[i] = (byte[])c.Px.Clone();
            rect1[i] = TileArgs[i];
            TileArgs[i] = rect0[i];
            Array.Copy(keep[i].d, c.Dens, c.Dens.Length);
            Array.Copy(keep[i].f, c.Foam, c.Foam.Length);
            Array.Copy(keep[i].dp, c.Dep, c.Dep.Length);
            Array.Copy(keep[i].pl, c.Pool, c.Pool.Length);
            Array.Copy(keep[i].px, c.Px, c.Px.Length);
        }
        DrawCs();
        long diff = 0;
        for (int i = 0; i < _tileN; i++)
        {
            ref var t = ref TileArgs[i];
            var px = JobTiles[i].Px;
            for (int j = 0; j < px.Length; j++) if (px[j] != native[i][j]) diff++;
            var r = rect1[i];
            if (r.X0p != t.X0p || r.Y0p != t.Y0p || r.X1p != t.X1p || r.Y1p != t.Y1p || r.Changed != t.Changed) diff++;
        }
        VerifyDraws++;
        VerifyDrawDiff += diff;
    }

    // C# 版 (照合用)。C++ と同じ縁と升に描いて画素へ直す
    private static unsafe void DrawCs()
    {
        for (int k = 0; k < _tileN; k++)
        {
            var c = JobTiles[k];
            ref var t = ref TileArgs[k];
            int w = c.W;
            // 前回描いた範囲だけを消す (それ以外は 0 のまま)
            for (int y = t.Y0p; y <= t.Y1p; y++)
            {
                int o = y * w + t.X0p, len = t.X1p - t.X0p + 1;
                Array.Clear(c.Dens, o, len);
                Array.Clear(c.Foam, o, len);
                Array.Clear(c.Dep, o, len);
                Array.Clear(c.Pool, o, len);
            }
            c.Bx0 = w; c.By0 = c.H; c.Bx1 = -1; c.By1 = -1;
        }
        _hit = null; _hitTx = int.MinValue;
        for (int i = 0; i < _edgeN; i++) SheetCs(in _edges[i], _clock);
        for (int i = 0; i < _edgeN; i++)
        {
            ref var e = ref _edges[i];
            if (e.Disc != 0) Disc(e.Void != 0, e.DiscX, e.DiscY, e.DiscR, e.DiscS);
        }
        for (int i = 0; i < _edgeN; i++)
        {
            ref var e = ref _edges[i];
            if (e.LipMode == 2) LipCrest(in e);
            else if (e.LipMode == 1) LipSheet(in e);
        }
        for (int k = 0; k < _tileN; k++)
        {
            var c = JobTiles[k];
            ref var t = ref TileArgs[k];
            int w = c.W;
            bool now = c.Bx1 >= 0, before = t.X1p >= 0;
            t.Changed = now || before ? 1 : 0;
            if (t.Changed == 0) continue;
            int ux0 = Math.Min(now ? c.Bx0 : w, before ? t.X0p : w), uy0 = Math.Min(now ? c.By0 : c.H, before ? t.Y0p : c.H);
            int ux1 = Math.Max(c.Bx1, t.X1p), uy1 = Math.Max(c.By1, t.Y1p);
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
            }
            t.X0p = c.Bx0; t.Y0p = c.By0; t.X1p = c.Bx1; t.Y1p = c.By1;
        }
    }

    // 手すりの上の縁 (Y0) から手すりの面を LipDrop だけ垂れる膜。縁に沿って 1 画素ごとに縦の線を置き、長さを少し揺らす
    private static unsafe void LipSheet(in FallEdge e)
    {
        float tx = -e.Ny, ty = e.Nx; // 縁に沿った向き
        int n = (int)MathF.Ceiling(LipHalf * 2f * Ppu);
        for (int k = 0; k <= n && k < 12; k++)
        {
            float u = -LipHalf + k / Ppu;
            float x = e.X0 + tx * u, y = e.Y0 + ty * u;
            float wob = e.Wob[k];
            int ax = (int)MathF.Floor(x * Ppu), ay = (int)MathF.Floor(y * Ppu);
            float drop = LipDrop * (0.7f + 0.3f * wob);
            if (e.Void != 0) drop = MathF.Min(drop, y - e.Cut);
            int len = (int)(drop * Ppu);
            for (int j = 0; j <= len; j++)
                Put(e.Void != 0, ax, ay - j, LipDens * e.LipS, j == 0 ? 0.9f : 0.15f, 0f, 0f);
        }
    }

    // 奥の手すりを越えて向こうへこぼれる水: 水面 (手すりの上の縁か、その上に盛り上がった水面) から CrestUp だけ盛り上がって奥へ消える膜。上ほど薄く、先端は泡で白い
    private const float CrestUp = 0.06f, CrestDens = 0.6f;
    private static unsafe void LipCrest(in FallEdge e)
    {
        float tx = -e.Ny, ty = e.Nx;
        float top = MathF.Max(e.Y0, e.Cut);
        int n = (int)MathF.Ceiling(LipHalf * 2f * Ppu);
        for (int k = 0; k <= n && k < 12; k++)
        {
            float u = -LipHalf + k / Ppu;
            float x = e.X0 + tx * u, y = top + ty * u;
            float wob = e.Wob[k];
            int ax = (int)MathF.Floor(x * Ppu), ay = (int)MathF.Floor(y * Ppu);
            int len = Math.Max(1, (int)((CrestUp * (0.6f + 0.4f * wob)) * Ppu));
            for (int j = 0; j <= len; j++)
                Put(e.Void != 0, ax, ay + j, LipDens * CrestDens * e.LipS * (1f - 0.6f * j / (len + 1f)), j == len ? 0.9f : 0.2f, 0f, 0f);
        }
    }

    // 世界の点へ濃さ w を周りの 4 画素に割り振って置く (隣の列・隣の点と継ぎ目なく重なる)。画素は世界の画素の格子で数え、升の境目にかかる点は両方の升へ置く
    private static void Splat(bool v, float wx, float wy, float w, float foam, float dep = 0f, float pool = 0f)
    {
        float fx = wx * Ppu - 0.5f, fy = wy * Ppu - 0.5f;
        int x = (int)MathF.Floor(fx), y = (int)MathF.Floor(fy);
        float ax = fx - x, ay = fy - y;
        Put(v, x, y, (1f - ax) * (1f - ay) * w, foam, dep, pool);
        Put(v, x + 1, y, ax * (1f - ay) * w, foam, dep, pool);
        Put(v, x, y + 1, (1f - ax) * ay * w, foam, dep, pool);
        Put(v, x + 1, y + 1, ax * ay * w, foam, dep, pool);
    }

    // 1 つ前に置いた升 (膜の点は続けて同じ升に落ちるので引き直さない)
    private static Canvas _hit;
    private static int _hitTx = int.MinValue, _hitTy;
    private static bool _hitVoid;

    // 世界の画素 (gx, gy) へ置く。その升の絵が無ければ置かない
    private static void Put(bool v, int gx, int gy, float w, float foam, float dep, float pool)
    {
        int tx = (gx < 0 ? gx - (TilePx - 1) : gx) / TilePx, ty = (gy < 0 ? gy - (TilePx - 1) : gy) / TilePx;
        if (tx != _hitTx || ty != _hitTy || v != _hitVoid)
        {
            Tiles.TryGetValue(TileKey(tx, ty, v), out _hit);
            _hitTx = tx; _hitTy = ty; _hitVoid = v;
        }
        var c = _hit;
        if (c == null) return;
        int x = gx - tx * TilePx, y = gy - ty * TilePx, i = y * TilePx + x;
        if (x < c.Bx0) c.Bx0 = x;
        if (y < c.By0) c.By0 = y;
        if (x > c.Bx1) c.Bx1 = x;
        if (y > c.By1) c.By1 = y;
        c.Dens[i] += w;
        c.Foam[i] += w * foam;
        if (dep > 0f && c.Dep[i] < dep) c.Dep[i] = dep;
        if (pool > 0f && c.Pool[i] < pool) c.Pool[i] = pool;
    }

    // 着地の泡の円 (真ん中ほど濃く・奥行きで縦につぶす)
    private static void Disc(bool v, float wx, float wy, float r, float s)
    {
        int cx = (int)MathF.Floor(wx * Ppu), cy = (int)MathF.Floor(wy * Ppu), rx = (int)(r * Ppu), ry = Math.Max(1, (int)(r * 0.6f * Ppu));
        for (int y = -ry; y <= ry; y++)
        for (int x = -rx; x <= rx; x++)
        {
            float q = (float)x * x / (rx * rx) + (float)y * y / (ry * ry);
            if (q >= 1f) continue;
            Put(v, cx + x, cy + y, (1f - q) * 0.6f * s, 0.85f, 0f, 0f);
        }
    }

    private static void Destroy(Canvas c)
    {
        if (c.Go) UnityEngine.Object.Destroy(c.Go);
        if (c.Sp) UnityEngine.Object.Destroy(c.Sp);
        if (c.Tex) UnityEngine.Object.Destroy(c.Tex);
        if (c.Mat) UnityEngine.Object.Destroy(c.Mat);
        if (c.HPx.IsAllocated) c.HPx.Free();
        if (c.HDens.IsAllocated) c.HDens.Free();
        if (c.HFoam.IsAllocated) c.HFoam.Free();
        if (c.HDep.IsAllocated) c.HDep.Free();
        if (c.HPool.IsAllocated) c.HPool.Free();
    }

    internal static void Clear()
    {
        WaterSim.EnsureIdle(); // 作業スレッドが升の配列に描いている間は片づけない
        _jobState = 0;
        JobError = null;
        Array.Clear(JobTiles, 0, JobTiles.Length);
        _starved = 0;
        foreach (var c in Canvases) Destroy(c);
        Canvases.Clear();
        Tiles.Clear();
        Edges.Clear();
        _lastMs = 0;
        _vx0 = _vy0 = float.MinValue; _vx1 = _vy1 = float.MaxValue;
        _stealAcc = StealEvery;
    }

    internal static string Describe()
    {
        float q = 0f;
        int live = 0;
        foreach (var e in Edges.Values) { q = Math.Max(q, e.Q); if (e.Live) live++; }
        return $"falls={Canvases.Count} fallEdges={Edges.Count} live={live} starved={_starved} clipped={_clipped} puts={_puts} qMax={q:0} fallMs={LastDrawMs:0.00}";
    }
}
