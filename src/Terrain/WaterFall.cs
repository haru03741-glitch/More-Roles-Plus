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
// 量が少ない縁だけ列ごとの揺らぎで筋にちぎれる。水たまりの絵は落ちる縁の升まで床として見せる (床マスクの FloorLips) ので、膜は縁から描き始めれば水たまりとつながる。
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
    // 絵は世界の格子に揃えた一辺 TileW の升 (隣の升と重ならず、縁はかかる升の全部へ描く)。一辺の画素数が整数になる長さ
    private const float TileW = 4f;
    private const int TilePx = (int)(TileW * Ppu);
    private const float FallZMax = 0.12f;      // 絵を置く奥行きの上限 (クルーと同じ奥行きの帯のすぐ奥)
    private const float Pad = 0.75f;           // 縁が描く範囲の余白 (外へ投げ出す距離 + 着地の泡 + 放物線のふくらみ)
    private const float TopPad = 0.15f;        // 縁より上へ描く分 (縁に立つ物・手すりの高さの上)
    private const float FlowTau = 0.25f;       // 流量のならし (秒)。刻みの無いフレームでも点滅しない
    private const float ThrowMax = 0.35f;     // 外へ投げ出す距離の上限
    private const float ImpactR0 = 0.12f, ImpactR1 = 0.12f, ImpactFlow = 600f;
    private const float LipHalf = 0.13f, LipFlow = 300f; // 手すりを越える膜: 縁に沿った幅の半分・濃さが最大になる流量
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
        public int X0p, Y0p, X1p = -1, Y1p = -1;
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

    private const float EndSpread = 0.12f;     // 隣に流れる縁が無い側へ膜を薄れながら延ばす幅 (世界単位)         // 柱の間を抜けた水が横へ広がって 1 枚に戻るまでの落ちた割合

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
        // 手前へ落ちる縁は膜の頭を崖の縁まで内 (上) へずらすので、その分も (横と奥の縁は Pad に収まる)
        e.By1 = e.Y0 + ty + MathF.Max(e.LipH, e.BarH) + (e.Ny < 0f ? BrinkSteps * BrinkStep : 0f) + TopPad;
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

    // 画面の近くで流れている縁だけ描く (升は画面の近くにしか無い)
    private static bool Drawn(Edge e) => e.Live && InView(e.Bx0, e.By0, e.Bx1, e.By1, ViewIn);

    // 縁ごとに 1 回描いて、点を升へ振り分ける
    private static void Draw()
    {
        Pack();
        if (Native.MrpNative.Async)
        {
            _jobState = 1;
            _jobGen = _shipGen;
            return;
        }
        WaterSim.EnsureIdle(); // 作業スレッドと同じ水の配列を読む
        DrawNative();
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

    // 確認用: 点の近くの縁 (縁の両端・向き・着く所・流量・奈落か)
    internal static string DescribeNear(float x, float y, float r)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in Edges.Values)
        {
            if (MathF.Abs(e.X0 - x) > r || MathF.Abs(e.Y0 - y) > r) continue;
            sb.Append($" | ({e.X0:0.00},{e.Y0:0.00}) n({e.Nx:0},{e.Ny:0}) to({e.X1:0.00},{e.Y1:0.00}) q{e.Q:0}{(e.Void ? " void" : "")}{(e.Live ? "" : " idle")}");
        }
        return sb.ToString();
    }

    internal static string Describe()
    {
        float q = 0f;
        int live = 0;
        foreach (var e in Edges.Values) { q = Math.Max(q, e.Q); if (e.Live) live++; }
        return $"falls={Canvases.Count} fallEdges={Edges.Count} live={live} starved={_starved} clipped={_clipped} puts={_puts} qMax={q:0} fallMs={LastDrawMs:0.00}";
    }
}
