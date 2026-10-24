using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の計算 (WaterSim) の高さを絵にする。WaterSim.TileCells 升四方のタイルごとに 1 枚の絵を持ち、
// 水が変わったタイルだけを Redraw 秒ごと・1 フレーム TilesPerFrame 枚まで描き直す。
// 絵に書くのは水の量だけ (R = 深さ / DeepScale・G = 見せてよいか・B = 濡れているか)。色・縁・水面の揺れときらめきはシェーダ MRP/Water が決める。
// 高さは 3×3 (1-2-1) でならしてから升の真ん中の値として 3 次の B スプラインで引く。
// 縁は深さの低い線でなく「濡れた升か」(0/1) をならした値の中ほどの線で引く: 深さの低い線はならしの裾野を通るので
// 升ごとのこぶが並んだ階段になる。中ほどの線は濡れた升と乾いた升の間を通るので、斜めの前線もまっすぐな線になる。
// 床マスク (FloorMask・部屋の絵の画素ごとの床) があるマップでは、水の縁はシェーダがそのマスクで画素ごとに切る
// (壁の面・家具の天板の上は見せない。水は家具の下を通っている)。
// マスクが無い時は、壁の中 (SolidMap で歩けない所) と家具の当たり判定の中を見せない (1 画素 = SolidMap の 1 升 = 1/16 単位)。
// 影の中の焼いた絵 (ShadowPatch) には描き込まない: 水は部屋いっぱいに広がり、焼く升が数十になって読み戻しで止まる (実測 127 回・1 回 30ms 前後)。
// 水は視界の中だけに見える
internal static class WaterArt
{
    private const int Px = 4;                 // 升 1 つの画素
    private const float DeepScale = 1600f;    // R = 1 の深さ (シェーダの _RUnits と同じ)
    // 濡れ具合 = 深さ WetMin で 0・WetMin + WetRamp で 1 (B = それを B スプラインでならした値。シェーダはその 0.35 の線 ≒ 深さ 30 を水の縁にする)。
    // 0/1 で切ると、薄く広がった水 (深さ 30 前後) で升ごとに濡れた・乾いたが入れ替わり、縁がでこぼこになる
    private const int WetMin = 15, WetRamp = 45;
    private const float Redraw = 0.1f;
    private const int TilesPerFrame = 3;
    private const int FurnSub = Px;           // 家具の型抜きの細かさ (= 画素)
    // A = 壁の面の上の水の厚さ (単位) × FaceScale + 128。128 の線が壁に這い上がった水面の線。壁の面でない画素は 0
    private const float FaceScale = 96f;
    private const int FaceBaseScan = 3;       // 壁の根元の升が閉じている (壁の線を太らせた分) 時に、下へ探す升の数
    private const float BaseIn = 1f / 32f;    // 面の根元を升の縁ちょうどから手前へずらす量 (縁ちょうどは奥の升に入る)
    // 流れの絵: R, G = 128 + 速さ (単位/秒) / FlowMax × 127。浅い縁では 量 ÷ 深さ が跳ねるので深さは FlowMinDepth より浅く数えない
    internal const float FlowMax = 4f;
    private const int FlowMinDepth = WaterSim.Full / 8;

    private static void Enable(Tile t, bool on)
    {
        on &= !t.FloorWait; // 最初の床の切り抜きが届くまでは見せない (壁の上まで水が乗る)
        t.Sr.enabled = on;
        if (t.Shadow) t.Shadow.enabled = on;
    }

    private sealed class Tile
    {
        public int Tx, Ty;
        public GameObject Go;
        public SpriteRenderer Sr, Shadow; // Shadow = 影の中に見せる写し (ShadowView)
        public Texture2D Tex;
        public Sprite Sp;
        public byte[] Px;
        public bool[] Furn;
        public byte[] Face;       // 壁の面の画素の床からの高さ (FloorMask.FaceHeights)。壁の面が無いタイルは null
        public bool FaceDone;
        public bool FloorDirty;   // 床の切り抜きを次に描く時に作り直す (壁の穴・低い壁が変わった)
        public int FurnVersion;
        public MaterialPropertyBlock Block;
        public Texture2D Floor; // 床マスク (部屋の絵の画素ごとの床) をこのタイルの範囲で切り出した物
        public Texture2D Front; // 影の中の写しで抜く、水より手前の家具の絵の形 (家具の無いタイルは null)
        public Texture2D Flow;  // 升ごとの流れの速さ (R, G = x, y)。シェーダが水面の筋をこの向きと速さで流す
        public byte[] FlowPx;
        public MaterialPropertyBlock ShadowBlock;
        public bool FloorOn;
        public bool FloorJob;   // 床の切り抜きを作業スレッドへ積んである
        public bool FloorWait;  // 最初の床の切り抜きを待っている (届くまで隠す)
        public float LastDraw = -10f;
        public bool Waiting;
        public bool Empty = true;
        public float Z;
    }

    // 爆発のくぼみ (絵だけ)。水の計算は全員の結果を揃えるため Delay 刻み遅れて爆心をえぐるので、
    // それまでの間は描く高さから差し引いて同じ形を先に見せ、計算の水が追いついたら消す
    private const float CraterIn = 0.08f, CraterOut = 0.25f;
    private const float RimBulge = 0.5f;
    private struct Crater { public float X, Y, R, T0; }
    private static readonly List<Crater> Craters = new();

    private static readonly Dictionary<int, Tile> Tiles = new();
    private static readonly List<int> Waiting = new();
    private static int[] _raw;     // 描く間の高さ (タイル + 縁 3 升)
    private static int[] _depth;   // それを 3×3 でならした物 (タイル + 縁 2 升)
    private static bool[] _closed; // 描く間の升が閉じているか (タイル + 縁 3 升)
    private static int[] _wet;     // 濡れ具合 (0..WetRamp)。縁を引くための物 (タイル + 縁 2 升)
    private static float _clock;
    private static int _furnVersion;
    private static long _lastMs;
    internal static double LastDrawMs { get; private set; }
    internal static int Drawn { get; private set; }
    // 切り分け用: 水たまりの絵を全部隠す (計算と描き直しは続ける)。bridge `layer water 0`
    internal static bool Hidden;
    internal static int DebugHide(bool hide)
    {
        Hidden = hide;
        int n = 0;
        foreach (var t in Tiles.Values) if (t.Sr) { Enable(t, !hide && !t.Empty); n++; }
        return n;
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterArt] {e}"); Clear(); WaterSim.Reset(); }
    }

    private static void TickCore()
    {
        if (_spareGen != GameClock.ShipGen) { _spareGen = GameClock.ShipGen; DropSpare(); }
        // 水が出る前 (水の格子が無い) から作り置く: 最初の水が広がる時がいちばん多く要る
        if (!WaterSim.Ready) { if (Tiles.Count > 0) Clear(); _lastMs = 0; FillSpare(); return; }
        if (!MrpBundle.WaterMaterial) return;
        bool spike = TerrainStep.SpikeMs > 0;
        long s0 = spike ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        long e0 = EnsureTicks, f0 = FurnTicks, c0 = FaceTicks;
        Span<long> ep0 = stackalloc long[9], pt0 = stackalloc long[6];
        if (spike)
        {
            for (int q = 0; q < 9; q++) ep0[q] = EnsureParts[q];
            for (int q = 0; q < 6; q++) pt0[q] = ProfTicks[q];
        }
        FinishJobs();
        if (spike) s1 = System.Diagnostics.Stopwatch.GetTimestamp();
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        _clock += dt;

        if (Craters.Count > 0) TickCraters();
        if (Tiles.Count > 0) { ShadowView.Check(now); CheckDoors(now); }
        if (spike) s2 = System.Diagnostics.Stopwatch.GetTimestamp();
        RemakeQueuedFloors();
        if (spike) s3 = System.Diagnostics.Stopwatch.GetTimestamp();
        var dirty = WaterSim.DirtyTiles;
        if (dirty.Count > 0)
        {
            foreach (int t in dirty)
            {
                WaterSim.Clean(t);
                Queue(t);
                // 水が上の壁を這い上がるので、上のタイルも描き直す
                if (t + WaterSim.TilesW < WaterSim.TilesW * WaterSim.TilesH) Queue(t + WaterSim.TilesW);
                QueueFillTargets(t);
            }
            dirty.Clear();
        }
        if (spike) s4 = System.Diagnostics.Stopwatch.GetTimestamp();
        int drawn = 0;
        bool created = false;
        for (int i = 0; i < Waiting.Count && drawn < TilesPerFrame; i++)
        {
            var tile = Tiles[Waiting[i]];
            if (_clock - tile.LastDraw < Redraw) continue;
            // 新しいタイルは家具の型抜き (当たり判定を画素ごとに引く) が重いので 1 フレーム 1 枚まで
            if (tile.Px == null) { if (created) continue; created = true; }
            tile.Waiting = false;
            Waiting.RemoveAt(i--);
            if (CanDefer(tile)) Defer(tile);
            else Draw(tile);
            drawn++;
        }
        if (drawn == 0 && Waiting.Count == 0) FillSpare();
        if (spike)
        {
            long s5 = System.Diagnostics.Stopwatch.GetTimestamp();
            double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if ((s5 - s0) * k >= TerrainStep.SpikeMs)
                Bridge.TestBridge.Log($"artspike {(s5 - s0) * k:0.00}ms finish={(s1 - s0) * k:0.00} pre={(s2 - s1) * k:0.00} remake={(s3 - s2) * k:0.00} dirty={(s4 - s3) * k:0.00} draw={(s5 - s4) * k:0.00} (n={drawn} new={(created ? 1 : 0)} ensure={(EnsureTicks - e0) * k:0.00} furn={(FurnTicks - f0) * k:0.00} face={(FaceTicks - c0) * k:0.00}) tiles={Tiles.Count} waiting={Waiting.Count} ep[vent,tex,flow,z,shadow,front]={(EnsureParts[0] - ep0[0]) * k:0.00},{(EnsureParts[1] - ep0[1]) * k:0.00},{(EnsureParts[2] - ep0[2]) * k:0.00},{(EnsureParts[3] - ep0[3]) * k:0.00},{(EnsureParts[4] - ep0[4]) * k:0.00},{(EnsureParts[5] - ep0[5]) * k:0.00} mf[props,fill,tex]={(EnsureParts[6] - ep0[6]) * k:0.00},{(EnsureParts[7] - ep0[7]) * k:0.00},{(EnsureParts[8] - ep0[8]) * k:0.00} p[raw,face,prep,pix,up,flow]={(ProfTicks[0] - pt0[0]) * k:0.00},{(ProfTicks[1] - pt0[1]) * k:0.00},{(ProfTicks[2] - pt0[2]) * k:0.00},{(ProfTicks[3] - pt0[3]) * k:0.00},{(ProfTicks[4] - pt0[4]) * k:0.00},{(ProfTicks[5] - pt0[5]) * k:0.00}");
        }
    }

    // 重かったフレームの内訳 (water spikes): タイルの部品作り・家具の型抜き・壁の面の高さ
    internal static long EnsureTicks, FurnTicks, FaceTicks;

    // 確認用 (water sweep)。描き待ちのタイルの数
    internal static int ReviewPending => Waiting.Count;

    // その升を含む水の板の奥行き (板がまだ無ければ NaN)
    internal static float ReviewZ(int cx, int cy)
    {
        int t = cy / WaterSim.TileCells * WaterSim.TilesW + cx / WaterSim.TileCells;
        return Tiles.TryGetValue(t, out var tile) && tile.Go ? tile.Z : float.NaN;
    }

    // 水の板を 0 = 隠す / 1 = 元の奥行き / 2 = 一番手前 / 3 = 元の奥行きのわずかに手前 にする。撮り終えたら必ず 1 へ戻す
    internal static void ReviewMode(int mode)
    {
        foreach (var t in Tiles.Values)
        {
            if (!t.Go) continue;
            t.Go.SetActive(mode != 0);
            var p = t.Go.transform.position;
            t.Go.transform.position = FxMath.V3(p.x, p.y, mode == 2 ? -100f : mode == 3 ? t.Z - 0.0001f : t.Z);
        }
    }

    private static void Queue(int t)
    {
        if (!Tiles.TryGetValue(t, out var tile)) Tiles[t] = tile = new Tile { Tx = t % WaterSim.TilesW, Ty = t / WaterSim.TilesW };
        if (!tile.Waiting) { tile.Waiting = true; Waiting.Add(t); }
    }

    // 爆発の瞬間に呼ぶ (WaterLeak から・自分の手元だけ)。r = 水の計算の衝撃と同じ半径
    internal static void AddCrater(Vector2 c, float r)
    {
        if (!WaterSim.Ready || r <= 0f) return;
        Craters.Add(new Crater { X = c.x, Y = c.y, R = r, T0 = _clock });
    }

    private static float CraterHold => (float)WaterSim.Delay / GameClock.Hz;

    // くぼみの掛かるタイルを毎フレーム描き直しに回す (描き直しの間隔は Redraw のまま)。消えた後にもう 1 回
    private static void TickCraters()
    {
        float end = CraterHold + CraterOut;
        float cell = WaterSim.Cell, ts = cell * WaterSim.TileCells;
        Vector2 org = WaterSim.Origin;
        for (int i = Craters.Count - 1; i >= 0; i--)
        {
            var c = Craters[i];
            bool done = _clock - c.T0 > end, fresh = _clock - c.T0 < CraterIn * 2f;
            float rr = c.R * (1f + RimBulge);
            int tx0 = (int)FxMath.Floor((c.X - rr - org.x) / ts), tx1 = (int)FxMath.Floor((c.X + rr - org.x) / ts);
            int ty0 = (int)FxMath.Floor((c.Y - rr - org.y) / ts), ty1 = (int)FxMath.Floor((c.Y + rr - org.y) / ts);
            for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                if (tx < 0 || ty < 0 || tx >= WaterSim.TilesW) continue;
                int t = ty * WaterSim.TilesW + tx;
                if (!Tiles.TryGetValue(t, out var tile)) continue;
                if (fresh) tile.LastDraw = -10f;   // 出始めは間隔を待たずに描く (爆発と同じ頃に見せる)
                if (!tile.Waiting) { tile.Waiting = true; Waiting.Add(t); }
            }
            if (done) Craters.RemoveAt(i);
        }
    }

    // くぼみの効き (0 = まだ/もう無い・1 = いちばん深い)
    private static float CraterAmount(Crater c)
    {
        float age = _clock - c.T0, hold = CraterHold;
        return age < CraterIn ? age / CraterIn : age < hold ? 1f : 1f - (age - hold) / CraterOut;
    }

    // 描き直し 1 枚の区間ごとの時間 (water prof): 高さの写し・面・準備・画素・転送・流れの絵・壁の面の水・にじませ
    internal static readonly long[] ProfTicks = new long[8];
    internal static long ProfTiles;
    private static long _profT;
    private static void Prof(int i)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfTicks[i] += t - _profT;
        _profT = t;
    }

    private static unsafe void Draw(Tile t)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _profT = t0;
        ProfTiles++;
        int tc = WaterSim.TileCells;
        int x0 = t.Tx * tc, y0 = t.Ty * tc;
        EnsureScratch();
        var fill = EnsureFill();
        bool any = RawNative(x0, y0, fill, CraterArgs());
        t.LastDraw = _clock;
        Prof(0);
        int n0 = tc * Px;
        PrepFace(t, x0, y0);
        Prof(1);
        bool faceWet = t.Face != null && FaceWet(t, x0, y0, n0);
        if (!any && !faceWet)
        {
            if (!t.Empty && t.Sr) Enable(t, false);
            t.Empty = true;
            return;
        }
        Ensure(t);

        int n = tc * Px;
        PrepFloor(t);
        Prof(2);
        DrawPixels(t, x0, y0, n);
        Prof(3);
        fixed (byte* b = t.Px) t.Tex.LoadRawTextureData((IntPtr)b, t.Px.Length);
        t.Tex.Apply(false, false);
        Prof(4);
        DrawFlow(t, x0, y0);
        Prof(5);
        if (t.Empty) { Enable(t, !Hidden); t.Empty = false; }
        Drawn++;
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // 高さを縁 3 升ぶん広げて写し (光の筋はずらした所を見るので余分に)、1-2-1 でならす作業場所
    private static void EnsureScratch()
    {
        int tc = WaterSim.TileCells, dw = tc + 4, rw = tc + 6;
        _raw ??= new int[rw * rw];
        _depth ??= new int[dw * dw];
        _wet ??= new int[dw * dw];
        _closed ??= new bool[rw * rw];
    }

    // 床の切り抜きの作り直しと壁の面の高さ (Unity に触るのでメインで)
    private static void PrepFace(Tile t, int x0, int y0)
    {
        int tc = WaterSim.TileCells, n0 = tc * Px;
        float cs = WaterSim.Cell, org0x = WaterSim.Origin.x, org0y = WaterSim.Origin.y;
        if (t.FloorDirty)
        {
            t.FloorDirty = false;
            if ((t.Floor || t.FloorWait) && t.Sr)
            {
                int key = t.Ty * WaterSim.TilesW + t.Tx;
                if (RequestFloor(t, org0x + x0 * cs, org0y + y0 * cs, tc * cs, false)) FloorFromDirty++;
                else if (FloorQueued.Add(key)) FloorQueue.Add(key); // 積む枠が埋まった: 待ち行列で次のフレームへ
            }
        }
        if (!t.FaceDone)
        {
            long ft = System.Diagnostics.Stopwatch.GetTimestamp();
            t.FaceDone = true;
            t.Face = null;
            var face = new byte[n0 * n0];
            float fx = org0x + x0 * cs, fy = org0y + y0 * cs, fs = tc * cs;
            bool got = FloorMask.Active && FloorMask.FaceHeights(fx, fy, fs, n0, face);
            if (!got) Array.Fill(face, (byte)255);
            got |= WaterHeight.StampFace(face, n0, fx, fy, fs);
            if (got) { FurnitureLift.CutStillFaces(fx, fy, fs, n0, face); WaterHeight.CutHoles(face, n0, fx, fy, fs); t.Face = face; }
            FaceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ft;
        }
    }

    // 床マスクの切り替えと家具の型抜き (Unity に触るのでメインで)
    private static void PrepFloor(Tile t)
    {
        int tc = WaterSim.TileCells;
        float cell = WaterSim.Cell;
        if (t.Floor != null && t.FloorOn == FloorMask.Off)
        {
            SetFloor(t, !FloorMask.Off);
            t.FurnVersion = -1;
        }
        if (!t.FloorOn && t.FurnVersion != _furnVersion)
        {
            t.FurnVersion = _furnVersion;
            long ft = System.Diagnostics.Stopwatch.GetTimestamp();
            t.Furn = FurnitureMask(WaterSim.Origin.x + t.Tx * tc * cell, WaterSim.Origin.y + t.Ty * tc * cell, tc * cell);
            FurnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ft;
        }
    }

    // ── 作業スレッドで描く ──────────────────────────────────────────
    // 描き直すタイルの準備 (Unity に触る所) だけをメインで済ませて積み、LateUpdate で水の刻みと一緒に作業スレッドへ投げる
    // (刻みの後の水を描く)。次のフレームの Tick の頭で受け取って転送する。新しいタイル・隠れているタイルはその場で描く
    private sealed class Job { public Tile T; public bool Any, Wet, Flow; }
    private static readonly Job[] Jobs = { new(), new(), new() };
    private static int _jobN, _jobState; // 0 = 無し / 1 = 積んだ / 2 = 描いた
    private static int[] _jobFill;
    private static int _jobCraterN;
    internal static bool HasJobs => _jobState == 1;
    internal static Exception JobError;
    internal static long ProfThreadTiles;

    // 隠れているタイル (乾いていた) は水があると分かってから家具の型抜きを払うよう、その場で描く
    private static bool CanDefer(Tile t) =>
        t.Px != null && !t.Empty && _jobN < Jobs.Length && Native.MrpNative.Async;

    private static void Defer(Tile t)
    {
        _profT = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_jobN == 0)
        {
            EnsureScratch();
            _jobFill = EnsureFill();
            _jobCraterN = CraterArgs();
        }
        int tc = WaterSim.TileCells;
        t.LastDraw = _clock;
        PrepFace(t, t.Tx * tc, t.Ty * tc);
        PrepFloor(t);
        var j = Jobs[_jobN++];
        j.T = t;
        j.Flow = t.Flow != null;
        _jobState = 1;
        ProfTiles++;
        Prof(2);
    }

    // 積んだタイルを描く (作業スレッド。投げられなかった時はメイン)。Unity には触らない
    internal static void RunJobs()
    {
        if (_jobState != 1) return;
        int tc = WaterSim.TileCells, n = tc * Px;
        for (int i = 0; i < _jobN; i++)
        {
            var j = Jobs[i];
            var t = j.T;
            int x0 = t.Tx * tc, y0 = t.Ty * tc;
            j.Any = RawNative(x0, y0, _jobFill, _jobCraterN);
            j.Wet = t.Face != null && FaceWet(t, x0, y0, n);
            if (!j.Any && !j.Wet) continue;
            PixelsNative(t, x0, y0, n, t.Px);
            if (t.Face != null)
            {
                FaceNative(t, x0, y0, n);
                SpreadFaceNative(t, n);
            }
            if (j.Flow) FlowPixels(t, x0, y0);
        }
        long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var f in FloorJobs) FloorMask.FillFrom(f.Src, f.X, f.Y, f.Size, f.M, f.Buf, FloorScratch, false);
        FloorThreadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - f0;
        _jobState = 2;
    }

    // 描いたタイルを受け取って転送する (メイン)
    private static unsafe void FinishJobs()
    {
        if (_jobState == 0) return;
        WaterSim.EnsureIdle();
        if (JobError != null)
        {
            var e = JobError;
            JobError = null;
            throw new InvalidOperationException("water art job", e);
        }
        if (_jobState == 1) RunJobs(); // 投げられなかった
        _profT = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < _jobN; i++)
        {
            var j = Jobs[i];
            var t = j.T;
            j.T = null;
            if (!j.Any && !j.Wet)
            {
                if (!t.Empty && t.Sr) Enable(t, false);
                t.Empty = true;
                continue;
            }
            fixed (byte* b = t.Px) t.Tex.LoadRawTextureData((IntPtr)b, t.Px.Length);
            t.Tex.Apply(false, false);
            if (j.Flow)
            {
                fixed (byte* b = t.FlowPx) t.Flow.LoadRawTextureData((IntPtr)b, t.FlowPx.Length);
                t.Flow.Apply(false, false);
            }
            if (t.Empty) { Enable(t, !Hidden); t.Empty = false; }
            Drawn++;
            ProfThreadTiles++;
        }
        Prof(4);
        _jobN = 0;
        _jobState = 0;
        FinishFloors();
    }

    private static float[] _craterArgs = new float[16];
    // くぼみの効きはタイルの間で変わらないので先に出す (効きが 0 以下のくぼみは倍率を変えないので渡さない)。渡す数
    private static int CraterArgs()
    {
        int cn = 0;
        if (_craterArgs.Length < Craters.Count * 4) _craterArgs = new float[Craters.Count * 4];
        foreach (var c in Craters)
        {
            float amt = CraterAmount(c);
            if (amt <= 0f) continue;
            _craterArgs[cn * 4] = c.X; _craterArgs[cn * 4 + 1] = c.Y; _craterArgs[cn * 4 + 2] = c.R; _craterArgs[cn * 4 + 3] = amt;
            cn++;
        }
        return cn;
    }

    private static unsafe bool RawNative(int x0, int y0, int[] fill, int cn)
    {
        var org = WaterSim.Origin;
        fixed (int* hgt = WaterSim.HeightCells) fixed (short* furn = WaterSim.FurnCells) fixed (byte* open = WaterSim.OpenCells)
        fixed (int* fl = fill) fixed (float* cr = _craterArgs)
        fixed (int* raw = _raw) fixed (bool* closed = _closed) fixed (int* depth = _depth) fixed (int* wet = _wet)
        {
            var a = new RawArgs
            {
                Hgt = hgt, Furn = furn, Open = open, Fill = fl,
                W = WaterSim.W, H = WaterSim.H, X0 = x0, Y0 = y0, Tc = WaterSim.TileCells,
                Ox = org.x, Oy = org.y, Cs = WaterSim.Cell,
                Crater = cr, CraterN = cn,
                Raw = raw, Closed = (byte*)closed, Depth = depth, Wet = wet,
            };
            Native.MrpNative.WaterRaw(&a);
            return a.Any != 0;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct RawArgs
    {
        public int* Hgt; public short* Furn; public byte* Open; public int* Fill;
        public int W, H, X0, Y0, Tc;
        public float Ox, Oy, Cs;
        public float* Crater; public int CraterN;
        public int* Raw; public byte* Closed; public int* Depth; public int* Wet;
        public int Any;
    }

    // 画素 (深さ・見せてよいか・濡れ) と壁の面の水
    private static void DrawPixels(Tile t, int x0, int y0, int n)
    {
        PixelsNative(t, x0, y0, n, t.Px);
        if (t.Face == null) return;
        Prof(3);
        FaceNative(t, x0, y0, n);
        Prof(6);
        SpreadFaceNative(t, n);
        Prof(7);
    }

    private static unsafe void PixelsNative(Tile t, int x0, int y0, int n, byte[] dst)
    {
        int tc = WaterSim.TileCells;
        var org = WaterSim.Origin;
        var sorg = SolidMap.Origin;
        var outside = SolidMap.OutsideCells;
        fixed (int* depth = _depth) fixed (int* wet = _wet) fixed (byte* open = SolidMap.OpenCells)
        fixed (bool* furn = t.FloorOn ? null : t.Furn) fixed (byte* outs = outside) fixed (byte* px = dst)
            Native.MrpNative.WaterPixels(depth, wet, tc + 4, n, x0, y0, org.x, org.y, WaterSim.Cell, (float)tc / (n - 1),
                open, SolidMap.W, SolidMap.H, sorg.x, sorg.y, SolidMap.Ppu,
                t.FloorOn ? 1 : 0, (byte*)furn, outs, SolidMap.BareSkyReach, 255f / DeepScale, 1f / WetRamp, px);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct FaceArgs
    {
        public byte* Px; public byte* Face; public int N, X0, Y0;
        public float Edge, Ox, Oy, Cell;
        public byte* Open; public short* Furn; public int* Hgt; public int W, H;
    }

    private static unsafe void FaceNative(Tile t, int x0, int y0, int n)
    {
        var org = WaterSim.Origin;
        fixed (byte* px = t.Px) fixed (byte* face = t.Face) fixed (byte* open = WaterSim.OpenCells)
        fixed (short* furn = WaterSim.FurnCells) fixed (int* hgt = WaterSim.HeightCells)
        {
            var a = new FaceArgs
            {
                Px = px, Face = face, N = n, X0 = x0, Y0 = y0,
                Edge = (float)WaterSim.TileCells / (n - 1), Ox = org.x, Oy = org.y, Cell = WaterSim.Cell,
                Open = open, Furn = furn, Hgt = hgt, W = WaterSim.W, H = WaterSim.H,
            };
            Native.MrpNative.WaterFace(&a);
        }
    }

    private static unsafe void SpreadFaceNative(Tile t, int n)
    {
        if (_spread == null || _spread.Length < n * n) _spread = new byte[n * n];
        fixed (byte* px = t.Px) fixed (byte* face = t.Face) fixed (byte* tmp = _spread)
            Native.MrpNative.WaterSpreadFace(px, face, n, SpreadPx, tmp);
    }

    // 当たり判定は家具や壁の絵より大きく取ってある (家具の手前・壁の根元に、絵では床なのに水の升が閉じた帯ができる)。
    // 閉じた水の升のうち、床マスクで床の所に、近くの開いた水の升の水を描く。写し元は歩ける所から床の画素だけを通って
    // FillReach まで地図の升 (1/16 単位) で塗り広げて決める: 壁と家具の絵は床でないので、その向こうの乾いた所へは写らない。
    // 絵だけの話で、水の計算 (量・流れ・指紋) は変えない
    private const float FillReach = 2f; // 縦横の升の数で数える (斜めは短くなる)
    private static int[] _fillFrom;          // 水の升ごとの写し元の水の升 (-1 = 写さない)
    internal static int FillOf(int k) => _fillFrom == null || k < 0 || k >= _fillFrom.Length ? -2 : _fillFrom[k]; // 確認用 (-2 = 写し元の表が無い)
    private static byte[] _fillTargets;      // タイルごとに、そこを写し元にしている隣のタイルの向き (8 方向のビット)
    private static int _fillGen = -1;
    private static bool _fillDirty = true;
    internal static double FillMs { get; private set; }
    internal static int FillCells { get; private set; }

    private static readonly int[] TileDx = { -1, 0, 1, -1, 1, -1, 0, 1 }, TileDy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    // 床マスクか歩ける所が変わった範囲 (家具が動いた・壁が壊れた)。次に描く時にその周りだけ写し元を決め直す
    private static readonly List<Rect> FillPending = new();
    private static int[] _fillSrc = Array.Empty<int>(), _fillQueue = Array.Empty<int>();
    private static byte[] _fillDist = Array.Empty<byte>();

    private static int[] EnsureFill()
    {
        if (!_fillDirty && _fillGen == GameClock.ShipGen)
        {
            if (FillPending.Count == 0 || _fillFrom == null) return _fillFrom;
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float size = WaterSim.Cell;
            var o = WaterSim.Origin;
            foreach (var r in FillPending)
                Fill((int)MathF.Floor((r.xMin - o.x) / size), (int)MathF.Floor((r.yMin - o.y) / size),
                    (int)MathF.Ceiling((r.xMax - o.x) / size), (int)MathF.Ceiling((r.yMax - o.y) / size));
            FillPending.Clear();
            FillMs = (System.Diagnostics.Stopwatch.GetTimestamp() - p0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return _fillFrom;
        }
        _fillDirty = false;
        _fillGen = GameClock.ShipGen;
        _fillFrom = null;
        _fillTargets = null;
        FillPending.Clear();
        FillCells = 0;
        if (!FloorMask.Active || FloorMask.Off || !SolidMap.Valid) return null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int w = WaterSim.W, h = WaterSim.H;
        _fillFrom = new int[w * h];
        Array.Fill(_fillFrom, -1);
        _fillTargets = new byte[WaterSim.TilesW * WaterSim.TilesH];
        Fill(0, 0, w, h);
        // 地図全体の作業場所は大きいので持ち続けない (後の決め直しは家具 1 つ・穴 1 つの周りだけ)
        _fillSrc = Array.Empty<int>(); _fillQueue = Array.Empty<int>(); _fillDist = Array.Empty<byte>();
        FillMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"water art fill: cells={FillCells} ms={FillMs:0.0}");
        return _fillFrom;
    }

    // 水の升 [x0, x1) × [y0, y1) の写し元を決め直す。塗り広げは FillReach ぶん外側の升から始める
    private static void Fill(int x0, int y0, int x1, int y1)
    {
        int w = WaterSim.W, h = WaterSim.H, sub = WaterSim.Sub, sw = SolidMap.W, sh = SolidMap.H;
        int max = (int)(FillReach * SolidMap.Ppu);
        int margin = max / sub + 1;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(w, x1); y1 = Math.Min(h, y1);
        if (x0 >= x1 || y0 >= y1) return;
        // 地図の升で数えた塗る範囲 (左下 rx0, ry0 から rw × rh)
        int rx0 = Math.Max(0, (x0 - margin) * sub), ry0 = Math.Max(0, (y0 - margin) * sub);
        int rx1 = Math.Min(sw, (x1 + margin) * sub), ry1 = Math.Min(sh, (y1 + margin) * sub);
        int rw = rx1 - rx0, rh = ry1 - ry0, n = rw * rh;
        if (_fillSrc.Length < n) { _fillSrc = new int[n]; _fillDist = new byte[n]; _fillQueue = new int[n]; }
        FillCells += FillNative(x0, y0, x1, y1, max, rx0, ry0, rw, rh);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct FillArgs
    {
        public byte* SolidOpen; public int Sw;
        public byte* WaterOpen; public int W, H, Sub;
        public byte* Mask; public int Mw, Mh;
        public float Mox, Moy, Mppu;
        public float Sox, Soy, Cell;
        public int Max;
        public int X0, Y0, X1, Y1;
        public int Rx0, Ry0, Rw, Rh;
        public int* Src; public byte* Dist; public int* Queue;
        public int* FillFrom;
        public byte* Targets; public int Tw, Tc;
        public int Cells;
    }

    // C++ (WaterArt.Fill と同じ結果)。写し元のある升の数の増減を返す
    private static unsafe int FillNative(int x0, int y0, int x1, int y1, int max, int rx0, int ry0, int rw, int rh)
    {
        bool mask = FloorMask.MaskBits(out var bits, out int mw, out int mh, out float mox, out float moy, out float mppu);
        var org = SolidMap.Origin;
        fixed (byte* so = SolidMap.OpenCells) fixed (byte* wo = WaterSim.OpenCells) fixed (byte* mb = mask ? bits : null)
        fixed (int* src = _fillSrc) fixed (byte* dist = _fillDist) fixed (int* queue = _fillQueue)
        fixed (int* from = _fillFrom) fixed (byte* tg = _fillTargets)
        {
            var a = new FillArgs
            {
                SolidOpen = so, Sw = SolidMap.W, WaterOpen = wo, W = WaterSim.W, H = WaterSim.H, Sub = WaterSim.Sub,
                Mask = mb, Mw = mw, Mh = mh, Mox = mox, Moy = moy, Mppu = mppu,
                Sox = org.x, Soy = org.y, Cell = 1f / SolidMap.Ppu, Max = max,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Rx0 = rx0, Ry0 = ry0, Rw = rw, Rh = rh,
                Src = src, Dist = dist, Queue = queue, FillFrom = from, Targets = tg, Tw = WaterSim.TilesW, Tc = WaterSim.TileCells,
            };
            Native.MrpNative.WaterFill(&a);
            return a.Cells;
        }
    }

    // 写し元のタイルの水が変わった: そこから写している隣のタイルも描き直す
    private static void QueueFillTargets(int t)
    {
        if (_fillTargets == null || t >= _fillTargets.Length || _fillTargets[t] == 0) return;
        int tw = WaterSim.TilesW, th = WaterSim.TilesH, tx = t % tw, ty = t / tw;
        for (int i = 0; i < 8; i++)
        {
            if ((_fillTargets[t] & (1 << i)) == 0) continue;
            int x = tx + TileDx[i], y = ty + TileDy[i];
            if (x >= 0 && y >= 0 && x < tw && y < th) Queue(y * tw + x);
        }
    }

    // 壁の面の画素 (wx, 床からの高さ z の所) の真下の床の水の深さ (単位)。横は升の真ん中の間を線形に引く (升ごとの段を水面の線に出さない)。
    // 真下に水の升が無い列 (壁の角・家具が壁に沿って並ぶ所) は隣の列の水面を続ける: 0 へ引くと水面の線が床まで V 字に落ちて尖る
    private static float BaseDepth(float wx, float wyBase)
    {
        float cell = WaterSim.Cell;
        var org = WaterSim.Origin;
        float fx = (wx - org.x) / cell - 0.5f;
        int cx = (int)MathF.Floor(fx), cy = (int)MathF.Floor((wyBase - org.y) / cell);
        float f = fx - cx;
        int a = CellBase(cx, cy), b = CellBase(cx + 1, cy);
        if (a < 0 && b < 0)
        {
            a = CellBase(cx - 1, cy);
            b = CellBase(cx + 2, cy);
            if (a < 0 && b < 0) return 0f;
        }
        if (a < 0) a = b;
        else if (b < 0) b = a;
        return (a * (1f - f) + b * f) * (1f / WaterSim.Full);
    }

    // 升 (cx, cy) から下へ探した最初の水の升の見せる深さ。家具の足元の升 (水は家具の周りにある) は飛ばして先を探す。
    // 見つからなければ -1
    private const int FaceFurnScan = 8;
    private static int CellBase(int cx, int cy)
    {
        int w = WaterSim.W;
        if (cx < 1 || cx >= w - 1) return -1;
        int open = 0;
        for (int d = 0; d <= FaceFurnScan; d++)
        {
            int y = cy - d;
            if (y < 1 || y >= WaterSim.H - 1) return -1;
            int k = y * w + cx;
            if (!WaterSim.Open(k))
            {
                // 壁の根元の閉じた升 (壁の線を太らせた分) は FaceBaseScan 升まで
                if (open == 0 && d < FaceBaseScan) continue;
                return -1;
            }
            open++;
            if (WaterSim.FurnAt(k) == 0 || WaterSim.Height(k) > WaterSim.TopFilm) return WaterSim.ShownDepth(k);
        }
        return -1;
    }

    // 壁の面に水が掛かっているか (タイルを出すか)
    private static bool FaceWet(Tile t, int x0, int y0, int n)
    {
        float cell = WaterSim.Cell, edge = (float)WaterSim.TileCells / (n - 1);
        var org = WaterSim.Origin;
        for (int py = 0; py < n; py += 2)
        for (int px = 0; px < n; px += 2)
        {
            byte z = t.Face[py * n + px];
            if (z > FloorMask.FaceTop) continue;
            float wx = org.x + (x0 + px * edge) * cell, wy = org.y + (y0 + py * edge) * cell, h = z * (1f / FloorMask.FaceUnit);
            if (BaseDepth(wx, wy - h - BaseIn) > h) return true;
        }
        return false;
    }

    // A を面の外へ SpreadPx 画素にじませる (横 → 縦の 2 回で四角く広げる・値は近くの面の大きい方)。
    // 面のすぐ外の画素の A が 0 だと、双線形で引いた A が面の縁で必ず 128 を横切り、沈んだベッドや壁の輪郭が
    // 水面の線 (明るい縁と泡) としてなぞられる。床の画素は A を持っても床として描くので見た目は変わらない。
    // 床でない画素は G を 0 にして、面の縁はこれまでどおり G で切る。
    // 面の画素は自分の水の厚さを持っているので上書きしない (乾いた面へにじませると、床の端の横の壁に水の四角が出る)
    private const int SpreadPx = 3;
    private static byte[] _spread;

    // 水を見せてよい画素か: 歩ける所か、左右 Undilate 升・下 UndilateDown 升・上 UndilateUp 升以内に歩ける所がある所。
    // SolidMap は継ぎ目を塞ぐために壁の線を両側へ太らせてあり、そのままだと壁の手前に乾いた帯 (溝) が残る。
    // 太らせた幅までしか戻さないので、元の壁の線は越えない (向こう側の部屋へは描かない)。
    // 上の壁は当たり判定の線が床の絵の際より手前の所 (ミラの廊下) と、ロッカーのように絵が線より手前へ出ている所があり、
    // 上へ 2 升戻すとロッカーの下に水が掛かるので 1 升だけ戻す
    private const int Undilate = 2;
    private const int UndilateDown = 2;
    private const int UndilateUp = 1;

    private static bool NearOpen(int sx, int sy, int sw)
    {
        int k = sy * sw + sx;
        if (SolidMap.OpenCell(k)) return true;
        if (sx >= Undilate && sx < sw - Undilate)
            for (int d = 1; d <= Undilate; d++)
                if (SolidMap.OpenCell(k + d) || SolidMap.OpenCell(k - d)) return true;
        // 下に歩ける所がある = 上の壁の手前の帯 / 上に歩ける所がある = 下の壁の手前の帯
        if (sy >= UndilateUp)
            for (int d = 1; d <= UndilateUp; d++)
                if (SolidMap.OpenCell(k - d * sw)) return true;
        if (sy < SolidMap.H - UndilateDown)
            for (int d = 1; d <= UndilateDown; d++)
                if (SolidMap.OpenCell(k + d * sw)) return true;
        return false;
    }

    // タイルの中の位置 (cu, cv: 升の単位・タイルの左下 = 0) での高さ (升の真ん中の値を 3 次の B スプラインで)。
    // 双線形だと升の境目で傾きが折れ、水の縁の線が升の形の階段になる。B スプラインは境目でも曲がり方まで続くので縁が丸い線になる
    private static float Sample(int[] src, float cu, float cv)
    {
        int dw = WaterSim.TileCells + 4;
        float u = cu - 0.5f + 2f, v = cv - 0.5f + 2f;
        int u0 = (int)MathF.Floor(u), v0 = (int)MathF.Floor(v);
        if (u0 < 1 || v0 < 1 || u0 + 2 >= dw || v0 + 2 >= dw) return 0f;
        float fu = u - u0, fv = v - v0;
        Weights(fu, out float a0, out float a1, out float a2, out float a3);
        Weights(fv, out float b0, out float b1, out float b2, out float b3);
        int r = (v0 - 1) * dw + u0 - 1;
        return b0 * Row(src, r, a0, a1, a2, a3) + b1 * Row(src, r + dw, a0, a1, a2, a3)
             + b2 * Row(src, r + dw * 2, a0, a1, a2, a3) + b3 * Row(src, r + dw * 3, a0, a1, a2, a3);
    }

    private static float Row(int[] src, int i, float a0, float a1, float a2, float a3)
        => a0 * src[i] + a1 * src[i + 1] + a2 * src[i + 2] + a3 * src[i + 3];

    private static void Weights(float t, out float w0, out float w1, out float w2, out float w3)
    {
        float t2 = t * t, t3 = t2 * t, s = 1f - t;
        w0 = s * s * s * (1f / 6f);
        w1 = (3f * t3 - 6f * t2 + 4f) * (1f / 6f);
        w2 = (-3f * t3 + 3f * t2 + 3f * t + 1f) * (1f / 6f);
        w3 = t3 * (1f / 6f);
    }

    // 升ごとの流れの速さを流れの絵へ (水を描き直す時だけ)
    private static unsafe void DrawFlow(Tile t, int x0, int y0)
    {
        if (t.Flow == null) return;
        FlowPixels(t, x0, y0);
        fixed (byte* b = t.FlowPx) t.Flow.LoadRawTextureData((IntPtr)b, t.FlowPx.Length);
        t.Flow.Apply(false, false);
    }

    private static void FlowPixels(Tile t, int x0, int y0)
    {
        int tc = WaterSim.TileCells, w = WaterSim.W, h = WaterSim.H;
        // 量/刻み → 単位/秒 (2 面の和なので半分)、それを FlowMax で 127 に
        float f = 0.5f * WaterSim.Cell * GameClock.Hz * (127f / FlowMax);
        var px = t.FlowPx;
        int fn = tc + 2;
        for (int y = 0; y < fn; y++)
        for (int x = 0; x < fn; x++)
        {
            int cx = x0 + x - 1, cy = y0 + y - 1, i = (y * fn + x) * 4;
            int vx = 128, vy = 128;
            if (cx >= 1 && cy >= 1 && cx < w - 1 && cy < h - 1)
            {
                int k = cy * w + cx, d = WaterSim.Height(k);
                if (d > 0 && WaterSim.Open(k))
                {
                    WaterSim.FlowThrough(k, out int qx, out int qy);
                    float s = f / Math.Max(d, FlowMinDepth);
                    vx = 128 + (int)Math.Clamp(qx * s, -127f, 127f);
                    vy = 128 + (int)Math.Clamp(qy * s, -127f, 127f);
                }
            }
            px[i] = (byte)vx; px[i + 1] = (byte)vy; px[i + 2] = 0; px[i + 3] = 255;
        }
    }

    private static void SetFloor(Tile t, bool on)
    {
        t.FloorOn = on;
        var mpb = t.Block ??= new MaterialPropertyBlock();
        // SpriteRenderer にブロックを付けるとスプライトの絵が外れるので、絵もブロックで渡す
        mpb.SetTexture(MainTexId, t.Tex);
        mpb.SetTexture(FloorTexId, t.Floor);
        mpb.SetFloat(FloorOnId, on ? 1f : 0f);
        mpb.SetTexture(FlowTexId, t.Flow);
        mpb.SetFloat(FlowMaxId, FlowMax);
        t.Sr.SetPropertyBlock(mpb);
        ApplyShadow(t);
    }

    // 影の中の写しのブロック: 本体と同じ絵と床マスクに、家具の形の抜き型を足す
    private static void ApplyShadow(Tile t)
    {
        if (!t.Shadow) return;
        var mpb = t.ShadowBlock ??= new MaterialPropertyBlock();
        mpb.SetTexture(MainTexId, t.Tex);
        if (t.Floor) mpb.SetTexture(FloorTexId, t.Floor);
        mpb.SetFloat(FloorOnId, t.Floor && t.FloorOn ? 1f : 0f);
        if (t.Front) mpb.SetTexture(FrontTexId, t.Front);
        mpb.SetFloat(FrontOnId, t.Front ? 1f : 0f);
        mpb.SetTexture(FlowTexId, t.Flow);
        mpb.SetFloat(FlowMaxId, FlowMax);
        t.Shadow.SetPropertyBlock(mpb);
    }

    // 影の中の写しは影の板のすぐ手前 (家具より手前) にあるので、そのままだと影の中では水が家具の上に乗って見える。
    // 視界の中で水より手前に描かれる家具 (PropSim の物・部屋の絵から持ち上げた物は床マスクが抜く) の絵の網目をタイルの抜き型に塗り、写しはそこを描かない。
    // 絵のテクスチャは読めないので形は網目の多角形 (透明な縁が少し入る)。作るのはタイルを作った時と家具が動いて止まった時だけ
    internal const float FrontPpu = 32f;
    private static readonly List<SpriteRenderer> FrontSrs = new();
    private static byte[] _frontBuf = Array.Empty<byte>();
    // 家具が退いて要らなくなった抜き型 (大きさはマップごとに同じ)。次に家具の掛かるタイルで使う
    private const int FrontPoolMax = 8;
    private static readonly Stack<Texture2D> FrontPool = new();

    private static unsafe void MakeFront(Tile t, float wx, float wy, float size)
    {
        if (!t.Shadow) return;
        var old = t.Front;
        t.Front = null;
        FrontSrs.Clear();
        long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var fr = Rect.MinMaxRect(wx, wy, wx + size, wy + size);
        PropSim.FrontSprites(fr, t.Z, FrontSrs);
        DoorFronts(fr, t.Z, FrontSrs);
        long q1 = System.Diagnostics.Stopwatch.GetTimestamp();
        EnsureParts[6] += q1 - q0;
        if (FrontSrs.Count > 0)
        {
            int m = (int)MathF.Round(size * FrontPpu);
            if (_frontBuf.Length != m * m) _frontBuf = new byte[m * m];
            else Array.Clear(_frontBuf, 0, _frontBuf.Length);
            var buf = _frontBuf;
            float k = m / size;
            bool any = false;
            foreach (var sr in FrontSrs) any |= FillSprite(sr, wx, wy, k, m, buf);
            long q2 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureParts[7] += q2 - q1;
            q1 = q2;
            if (any)
            {
                // 作り直しは前の抜き型へ書き直す (読み書きできるまま持つ・1 枚 m×m バイト)
                if (old && old.width == m) { t.Front = old; old = null; }
                else t.Front = FrontPool.Count > 0 && FrontPool.Peek() && FrontPool.Peek().width == m ? FrontPool.Pop() : new Texture2D(m, m, TextureFormat.R8, false) { name = "MrpWaterFront", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                fixed (byte* b = buf) t.Front.LoadRawTextureData((IntPtr)b, buf.Length);
                t.Front.Apply(false, false);
            }
            EnsureParts[8] += System.Diagnostics.Stopwatch.GetTimestamp() - q1;
            FrontSrs.Clear();
        }
        if (old)
        {
            if (FrontPool.Count < FrontPoolMax) FrontPool.Push(old);
            else UnityEngine.Object.Destroy(old);
        }
        ApplyShadow(t);
    }

    // 絵の網目の三角形を、画素の真ん中が入る所だけ 255 で塗る (k = 1 単位の画素数)。塗るのは C++
    private static float[] _fpx = new float[64], _fpy = new float[64], _fmu = new float[64], _fmv = new float[64];
    private static ushort[] _ftri = new ushort[192];

    private static unsafe bool FillSprite(SpriteRenderer sr, float wx, float wy, float k, int m, byte[] buf)
    {
        var sp = sr.sprite;
        var v = sp.vertices;
        var tri = sp.triangles;
        var mx = sr.transform.localToWorldMatrix;
        float fx = sr.flipX ? -1f : 1f, fy = sr.flipY ? -1f : 1f;
        int nv = v.Length, nt = tri.Length;
        if (_fpx.Length < nv) { _fpx = new float[nv]; _fpy = new float[nv]; _fmu = new float[nv]; _fmv = new float[nv]; }
        if (_ftri.Length < nt) _ftri = new ushort[nt];
        // 絵の不透明な所の型があれば、頂点の型の上の座標も渡す (反転の前の絵の中の座標で引く)
        var mask = SpriteAlphaMask.Get(sp);
        float ku = mask != null ? mask.W / mask.Size.x : 0f, kv = mask != null ? mask.H / mask.Size.y : 0f;
        for (int i = 0; i < nv; i++)
        {
            var q = v[i];
            float lx = q.x * fx, ly = q.y * fy;
            _fpx[i] = (mx.m00 * lx + mx.m01 * ly + mx.m03 - wx) * k - 0.5f;
            _fpy[i] = (mx.m10 * lx + mx.m11 * ly + mx.m13 - wy) * k - 0.5f;
            if (mask != null) { _fmu[i] = (q.x - mask.Min.x) * ku; _fmv[i] = (q.y - mask.Min.y) * kv; }
        }
        for (int i = 0; i < nt; i++) _ftri[i] = tri[i];
        bool any;
        fixed (float* pxp = _fpx) fixed (float* pyp = _fpy) fixed (float* mup = _fmu) fixed (float* mvp = _fmv) fixed (ushort* tp = _ftri) fixed (byte* bp = buf)
        fixed (byte* mp = mask?.Px)
        {
            var a = new FrontFillArgs { Px = pxp, Py = pyp, Tri = tp, TriN = nt, M = m, Buf = bp, Mu = mup, Mv = mvp, Mask = mp, Mw = mask?.W ?? 0, Mh = mask?.H ?? 0 };
            Native.MrpNative.FrontFill(&a);
            any = a.Any != 0;
        }
        return any;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct FrontFillArgs
    {
        public float* Px, Py;
        public ushort* Tri; public int TriN;
        public int M;
        public byte* Buf;
        public int Any;
        public float* Mu, Mv;
        public byte* Mask;
        public int Mw, Mh;
    }

    // 家具が a から b へ動いて止まった (r = 足元からの絵の広がり): 掛かるタイルの抜き型を作り直す
    internal static void FrontMoved(Vector2 a, Vector2 b, float r)
    {
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var t in Tiles.Values)
        {
            if (!t.Shadow) continue;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (!Near(a) && !Near(b)) continue;
            MakeFront(t, x0, y0, size);

            bool Near(Vector2 c) => c.x + r > x0 && c.x - r < x0 + size && c.y + r > y0 && c.y - r < y0 + size;
        }
    }

    // 家具の一覧ができた (タイルが先にできていたら抜き型が空のまま)
    internal static void FrontAll()
    {
        FrontSrs.Clear();
        PropSim.AllFrontSprites(FrontSrs);
        SpriteAlphaMask.Prepare(FrontSrs);
        FrontSrs.Clear();
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var t in Tiles.Values)
            if (t.Shadow) MakeFront(t, org.x + t.Tx * size, org.y + t.Ty * size, size);
    }

    // 扉は視界の中で水より手前に描かれる (開いた扉も枠の両側が残る)。動く物なので床マスクに焼かず、抜き型に塗る。
    // 開け閉めはアニメーションの初めに記録されるので、絵が落ち着いた頃に掛かるタイルだけ作り直す
    internal const long DoorSettleMs = 700;
    private static SpriteRenderer[][] _doorSrs = Array.Empty<SpriteRenderer[]>();
    private static int _doorSrGen = -1, _doorVer;
    private static ulong _doorSeen;
    private static readonly List<(long At, int Door)> DoorRedo = new();

    private static void ListDoorSrs()
    {
        int n = Decompression.DoorCount;
        if (n == 0) return;
        _doorSrGen = GameClock.ShipGen;
        _doorSrs = new SpriteRenderer[n][];
        for (int i = 0; i < n; i++)
        {
            var d = Decompression.DoorPartAtIndex(i);
            var list = new List<SpriteRenderer>();
            if (d)
                foreach (var sr in d.GetComponentsInChildren<SpriteRenderer>(true))
                    if (sr && !sr.gameObject.name.StartsWith("Mrp", StringComparison.Ordinal)) list.Add(sr);
            _doorSrs[i] = list.ToArray();
        }
        _doorVer = Decompression.DoorVersion;
        _doorSeen = Decompression.LatestDoorBits;
        DoorRedo.Clear();
    }

    // 扉 i の絵 (船が替わったら作り直す)
    internal static SpriteRenderer[] DoorRenderers(int i)
    {
        if (_doorSrGen != GameClock.ShipGen) ListDoorSrs();
        return _doorSrGen == GameClock.ShipGen && i < _doorSrs.Length ? _doorSrs[i] : Array.Empty<SpriteRenderer>();
    }

    private static void DoorFronts(Rect r, float z, List<SpriteRenderer> into)
    {
        if (_doorSrGen != GameClock.ShipGen) ListDoorSrs();
        if (_doorSrGen != GameClock.ShipGen) return;
        foreach (var srs in _doorSrs)
            foreach (var sr in srs)
            {
                if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
                var b = sr.bounds;
                if (b.max.x <= r.xMin || b.min.x >= r.xMax || b.max.y <= r.yMin || b.min.y >= r.yMax) continue;
                if (sr.transform.position.z >= z) continue;
                into.Add(sr);
            }
    }

    // 扉の記録が増えた: 開きの変わった扉を、落ち着く頃に作り直す予約へ (比べるのは int と ulong だけ)
    private static void CheckDoors(long now)
    {
        if (_doorSrGen != GameClock.ShipGen) return;
        int ver = Decompression.DoorVersion;
        if (ver != _doorVer)
        {
            _doorVer = ver;
            ulong bits = Decompression.LatestDoorBits;
            ulong changed = bits ^ _doorSeen;
            _doorSeen = bits;
            for (int i = 0; i < _doorSrs.Length && changed != 0; i++, changed >>= 1)
                if ((changed & 1) != 0) DoorRedo.Add((now + DoorSettleMs, i));
        }
        for (int k = DoorRedo.Count - 1; k >= 0; k--)
        {
            if (now < DoorRedo[k].At) continue;
            int i = DoorRedo[k].Door;
            DoorRedo.RemoveAt(k);
            foreach (var sr in _doorSrs[i])
            {
                if (!sr) continue;
                var b = sr.bounds;
                var c = FxMath.V2(b.center.x, b.center.y);
                FrontMoved(c, c, b.extents.magnitude);
            }
        }
    }

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), FloorTexId = Shader.PropertyToID("_FloorMask"), FloorOnId = Shader.PropertyToID("_FloorOn");
    private static readonly int FlowTexId = Shader.PropertyToID("_FlowTex"), FlowMaxId = Shader.PropertyToID("_FlowMax");
    private static readonly int FrontTexId = Shader.PropertyToID("_FrontMask"), FrontOnId = Shader.PropertyToID("_FrontOn");

    // ── タイルの部品の作り置き ──────────────────────────────
    // 新しいタイルの絵・流れの絵・板・影の写しを作るのは端末で 1 枚 0.6〜1ms (偶に 10ms 級)。水が広がる最中に作らないよう、
    // 描くタイルが無いフレームに 1 枚ずつ作っておく。水のタイルは船が替わるまで残るので戻ってこない (使った分を補う)
    private const float ShadowDz = -0.002f; // 写しの奥行きのずらし (火の写しより奥)
    private const int SpareTarget = 12;
    private sealed class Parts
    {
        public GameObject Go;
        public SpriteRenderer Sr, Shadow;
        public Texture2D Tex, Flow;
        public Sprite Sp;
        public byte[] Px, FlowPx;
        public float Cell;
    }
    private static int _spareGen = -1;
    private static readonly Stack<Parts> Spare = new();
    internal static int SpareMade, SpareMiss;

    private static unsafe Parts MakeParts(float cell)
    {
        int tc = WaterSim.TileCells, n = tc * Px;
        var p = new Parts { Px = new byte[n * n * 4], Cell = cell };
        p.Tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpWater", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        p.Sp = Sprite.Create(p.Tex, new Rect(0, 0, n, n), Vector2.zero, Px / cell, 0, SpriteMeshType.FullRect);
        p.Sp.name = "MrpWater";
        p.Go = new GameObject("MrpWater") { layer = 0 };
        p.Sr = p.Go.AddComponent<SpriteRenderer>();
        p.Sr.enabled = false;
        p.Sr.sprite = p.Sp;
        p.Sr.sharedMaterial = MrpBundle.WaterMaterial;
        // 流れの絵は升 1 つ = 1 画素 (画素の真ん中 = 升の真ん中) で、周りに隣のタイルの升を 1 列ずつ持つ (継ぎ目で流れがつながり、筋が切れない)。
        // 最初は流れ無し (128)
        int fn = tc + 2;
        p.FlowPx = new byte[fn * fn * 4];
        for (int i = 0; i < p.FlowPx.Length; i++) p.FlowPx[i] = 128;
        p.Flow = new Texture2D(fn, fn, TextureFormat.RGBA32, false) { name = "MrpWaterFlow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* fb = p.FlowPx) p.Flow.LoadRawTextureData((IntPtr)fb, p.FlowPx.Length);
        p.Flow.Apply(false, false);
        p.Shadow = ShadowView.Copy(p.Go, p.Sp, ShadowView.Water, FxMath.Rgba(1f, 1f, 1f, 1f), ShadowDz);
        return p;
    }

    private static Parts TakeParts()
    {
        while (Spare.Count > 0)
        {
            var p = Spare.Pop();
            if (p.Go && p.Tex && p.Flow && p.Cell == WaterSim.Cell) return p;
            DropParts(p);
        }
        SpareMiss++;
        return MakeParts(WaterSim.Cell);
    }

    // 描くタイルが無いフレームに 1 枚
    private static void FillSpare()
    {
        if (Spare.Count >= SpareTarget || !SolidMap.Valid || !MrpBundle.WaterMaterial || !ShadowView.Ready(out _)) return;
        Spare.Push(MakeParts(WaterSim.Sub / SolidMap.Ppu)); // 水の升の大きさ (水の格子を作る時と同じ式)
        SpareMade++;
    }

    private static void DropSpare()
    {
        while (Spare.Count > 0) DropParts(Spare.Pop());
        while (FrontPool.Count > 0) { var f = FrontPool.Pop(); if (f) UnityEngine.Object.Destroy(f); }
    }

    private static void DropParts(Parts p)
    {
        if (p.Go) UnityEngine.Object.Destroy(p.Go);
        if (p.Sp) UnityEngine.Object.Destroy(p.Sp);
        if (p.Tex) UnityEngine.Object.Destroy(p.Tex);
        if (p.Flow) UnityEngine.Object.Destroy(p.Flow);
    }

    private const float DeckZ = -0.001f; // エアシップの警備室の下のデッキの床の絵の z (手すりは -0.1 で水より手前)

    // タイル (tx, ty) の水の絵の奥行き。手前の絵 (PropSim.FrontCover) もこの値と比べる
    private static readonly Dictionary<int, float> TileZs = new(); // 船ごと (部屋の絵・FloorCovers は試合中に変わらない)
    private static int _tileZGen = -1;

    internal static float TileZ(int tx, int ty)
    {
        FloorCovers.Ensure(); // 床の絵を移す前の値を覚えない
        if (_tileZGen != GameClock.ShipGen) { TileZs.Clear(); _tileZGen = GameClock.ShipGen; }
        int key = ty * 65536 + tx;
        if (TileZs.TryGetValue(key, out float cached)) return cached;
        float z = TileZCore(tx, ty);
        // 水の格子 (動く台の帯) と部屋の絵の一覧ができる前の値は覚えない
        if (WaterSim.Ready && DamageMap.Ready()) TileZs[key] = z;
        return z;
    }

    // 確認用 (water tilez): 床の層の部屋の絵 (床マスクに入っている絵) のうち、タイルの水の絵より手前にあって水を隠している物
    internal static string TileZReport(Action<string> reply)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !SolidMap.Ensure()) return "no ship";
        float back = FloorMask.BackZ(FurnitureKinds.ShipName(ship)), size = TileSize;
        var org = SolidMap.Origin;
        int n = 0;
        foreach (var sr in DamageMap.RoomArts)
        {
            if (!sr || !sr.enabled || !sr.sprite) continue;
            float z = sr.transform.position.z;
            if (z < back || z >= 20f) continue;
            var b = sr.bounds;
            int tx0 = (int)MathF.Floor((b.min.x - org.x) / size), tx1 = (int)MathF.Floor((b.max.x - org.x) / size);
            int ty0 = (int)MathF.Floor((b.min.y - org.y) / size), ty1 = (int)MathF.Floor((b.max.y - org.y) / size);
            var sb = new System.Text.StringBuilder();
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    float tz = TileZ(tx, ty);
                    if (z < tz) sb.Append($" ({tx},{ty}) tz={tz:0.###}");
                }
            if (sb.Length == 0) continue;
            n++;
            reply($"TILEZ {sr.name} z={z:0.###} bounds=({b.min.x:0.##},{b.min.y:0.##})-({b.max.x:0.##},{b.max.y:0.##}){sb}");
        }
        return $"hidden={n} back={back}";
    }

    private static float TileZCore(int tx, int ty)
    {
        float size = TileSize;
        var org = SolidMap.Origin; // 水の格子の原点 (WaterSim.Origin) と同じ
        float wx = org.x + tx * size, wy = org.y + ty * size, front = float.MaxValue;
        // タイルは部屋の境をまたぐので、4 隅と真ん中で部屋の絵を引いて一番手前より前に置く
        // (真ん中だけだと、隣の部屋の絵のほうが手前の所で水がその下に隠れて四角く欠けた)
        for (int k = 0; k < 5; k++)
        {
            float fx = k == 4 ? 0.5f : (k & 1), fy = k == 4 ? 0.5f : (k >> 1);
            float px = wx + size * (0.05f + 0.9f * fx), py = wy + size * (0.05f + 0.9f * fy);
            front = Math.Min(front, DamageMap.TileFrontZ(new Vector2(px, py), wx, wy, wx + size, wy + size));
            // 屋外のデッキの床の絵はクルーと同じ奥行きにあり、部屋の絵の z では床の下に隠れる
            if (WaterSim.OnDeck(px, py)) front = Math.Min(front, DeckZ);
        }
        // 床マスクに入っている床の層の絵が 5 点から外れていても、タイルに掛かるならその手前へ (Skeld のシールドの壁の側面など)
        var ship = ShipStatus.Instance;
        if (ship) front = Math.Min(front, DamageMap.BackLayerFrontZ(wx, wy, wx + size, wy + size, FloorMask.BackZ(FurnitureKinds.ShipName(ship))));
        // 動く床 (昇降機の台) は部屋の絵より手前にあるので、台に載った水が台の下に隠れないよう台より手前へ
        front = WaterSim.PlatformFront(wx, wy, size, front);
        // 部屋の絵より手前にある歩ける床の絵 (ドロップシップの坂) は水のすぐ奥へ移してあるので、それより手前へ
        front = FloorCovers.Front(wx, wy, size, front);
        // 床の上・足跡 (−0.001) と波紋より奥
        return front - 0.0004f * DamageMap.ZScale(front);
    }

    // 範囲 (x0, y0)-(x1, y1) に掛かるタイルの水の絵の奥行きのうち一番奥
    // 水の格子を作る前 (floorexport) にも使えるよう、升の大きさは水の格子を作る時と同じ式から出す
    private static float TileSize => WaterSim.TileCells * (WaterSim.Sub / SolidMap.Ppu);

    internal static float MaxTileZ(float x0, float y0, float x1, float y1)
    {
        if (!SolidMap.Ensure()) return float.NaN;
        float size = TileSize;
        var org = SolidMap.Origin;
        int tx0 = (int)MathF.Floor((x0 - org.x) / size), tx1 = (int)MathF.Floor((x1 - org.x) / size);
        int ty0 = (int)MathF.Floor((y0 - org.y) / size), ty1 = (int)MathF.Floor((y1 - org.y) / size);
        float z = float.MinValue;
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
                z = Math.Max(z, TileZ(tx, ty));
        return z;
    }

    private static unsafe void Ensure(Tile t)
    {
        if (t.Go) return;
        long et = System.Diagnostics.Stopwatch.GetTimestamp();
        try { EnsureCore(t); }
        finally { EnsureTicks += System.Diagnostics.Stopwatch.GetTimestamp() - et; }
    }

    internal static readonly long[] EnsureParts = new long[9];
    private static long _epT;
    private static void EP(int i) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); EnsureParts[i] += n - _epT; _epT = n; }

    private static unsafe void EnsureCore(Tile t)
    {
        _epT = System.Diagnostics.Stopwatch.GetTimestamp();
        SinkVents();
        EP(0);
        int tc = WaterSim.TileCells, n = tc * Px;
        float cell = WaterSim.Cell;
        var org = WaterSim.Origin;
        var parts = TakeParts();
        t.Px = parts.Px;
        t.Tex = parts.Tex;
        t.Sp = parts.Sp;
        t.Go = parts.Go;
        t.Sr = parts.Sr;
        t.Sr.enabled = true;
        EP(1);
        t.FlowPx = parts.FlowPx;
        t.Flow = parts.Flow;
        EP(2);
        float wx = org.x + t.Tx * tc * cell, wy = org.y + t.Ty * tc * cell;
        float size = tc * cell;
        t.Z = TileZ(t.Tx, t.Ty);
        EP(3);
        t.Go.transform.position = FxMath.V3(wx, wy, t.Z);
        // 作り置きの写しは作った時の親の位置で置いてあるので、親を動かした後に置き直す
        if (parts.Shadow && ShadowView.Place(parts.Shadow, ShadowDz)) t.Shadow = parts.Shadow;
        else
        {
            if (parts.Shadow) UnityEngine.Object.Destroy(parts.Shadow.gameObject);
            t.Shadow = ShadowView.Copy(t.Go, t.Sp, ShadowView.Water, FxMath.Rgba(1f, 1f, 1f, 1f), ShadowDz);
        }
        EP(4);
        MakeFront(t, wx, wy, size);
        EP(5);
        if (FloorMask.Active)
        {
            // 床マスクがあれば水の縁は部屋の絵の床の画素で切る (家具の型抜きも要らない)
            RequestFloor(t, wx, wy, size, true);
            if (t.FloorJob) { t.FloorWait = true; Enable(t, false); }
            return;
        }
        // 床マスクの無いマップでも流れの絵はブロックで渡す (ブロックを付けるとスプライトの絵が外れるので絵も渡す)
        var mpb = t.Block ??= new MaterialPropertyBlock();
        mpb.SetTexture(MainTexId, t.Tex);
        mpb.SetTexture(FlowTexId, t.Flow);
        mpb.SetFloat(FlowMaxId, FlowMax);
        t.Sr.SetPropertyBlock(mpb);
        ApplyShadow(t);
        t.Furn = FurnitureMask(wx, wy, tc * cell);
        t.FurnVersion = _furnVersion;
    }

    private static int _ventGen = -1;

    // ベントは床の穴なので水の下に見せる。本編のベントの絵は床より手前 (Skeld で床 8・ベント 6.8) にあり、そのままだと水の上に出る。
    // 絵を床と水の板の間 (板は床の 0.0004 手前) へ下げる。子 (矢印のボタンなど) は元の奥行きのまま残す。
    // 本編はベントの位置を 2 次元でしか使わない (入る時の位置合わせも x, y だけ)
    private static void SinkVents()
    {
        if (_ventGen == GameClock.ShipGen) return;
        _ventGen = GameClock.ShipGen;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        foreach (var v in ship.AllVents)
        {
            if (!v) continue;
            var tr = v.transform;
            var p = tr.position;
            float front = DamageMap.FrontZ(new Vector2(p.x, p.y));
            float z = front - 0.0002f * DamageMap.ZScale(front);
            if (p.z >= z) continue;
            int n = tr.childCount;
            var keep = new Vector3[n];
            for (int i = 0; i < n; i++) keep[i] = tr.GetChild(i).position;
            tr.position = FxMath.V3(p.x, p.y, z);
            for (int i = 0; i < n; i++) tr.GetChild(i).position = keep[i];
        }
    }

    // 床マスクの作り直しの回数 (water prof): 出来事の待ち行列から・描く時の壊れから・合計時間
    internal static long FloorFromQueue, FloorFromDirty, FloorTicks;

    private static byte[] _floorBuf = Array.Empty<byte>();

    private static void MakeFloor(Tile t, float wx, float wy, float size)
    {
        long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int m = (int)MathF.Round(size * FloorMask.Ppu);
        if (_floorBuf.Length != m * m) _floorBuf = new byte[m * m];
        FloorMask.Fill(wx, wy, size, m, _floorBuf);
        UploadFloor(t, _floorBuf, m, wx, wy, size);
        FloorTicks += System.Diagnostics.Stopwatch.GetTimestamp() - f0;
    }

    // 作った床の切り抜きに低い壁を書いてテクスチャにする (メイン)
    private static unsafe void UploadFloor(Tile t, byte[] buf, int m, float wx, float wy, float size)
    {
        WaterHeight.StampFloor(buf, m, wx, wy, size);
        t.Floor = new Texture2D(m, m, TextureFormat.R8, false) { name = "MrpWaterFloor", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = buf) t.Floor.LoadRawTextureData((IntPtr)b, buf.Length);
        t.Floor.Apply(false, true);
        SetFloor(t, !FloorMask.Off);
        t.FloorWait = false; // 床が付いたら隠す理由は無い (見せるのは呼び手)
    }

    // ── 床の切り抜きを作業スレッドで作る ──────────────────────────────
    // 作り直しと新しいタイルの床 (1 枚 1.5ms 前後・中身は C++) を水の絵と一緒に作業スレッドへ積み、次のフレームの頭でテクスチャにする。
    // 動く床・持ち上げた家具が掛かるタイル (メインの状態を読む)・同期で回す時はその場で作る
    private sealed class FloorJob { public Tile T; public float X, Y, Size; public int M; public byte[] Buf; public FloorMask.Source Src; }
    private static readonly List<FloorJob> FloorJobs = new();
    private static readonly Stack<FloorJob> FloorJobPool = new();
    private static readonly FloorMask.Scratch FloorScratch = new();
    private const int FloorJobsPerFrame = 3; // 作業スレッドで 1 フレームに作る枚数 (多いと次のフレームの頭で待つ)
    internal static long FloorFromThread, FloorThreadTicks;

    // 床の切り抜きを作る (作業スレッドへ積むか、その場で)。積む枠が埋まっていて now でなければ何もせず false (呼び手が後で回す)
    private static bool RequestFloor(Tile t, float wx, float wy, float size, bool now)
    {
        if (t.FloorJob) return true; // 積んだ物はまだ作られていない (作る時に今の地図を読む)
        WaterSim.EnsureIdle();
        int m = (int)MathF.Round(size * FloorMask.Ppu);
        bool defer = Native.MrpNative.Async && _jobState != 2 && !FloorMask.MovingAt(wx, wy, size, m);
        if (defer && FloorJobs.Count < FloorJobsPerFrame)
        {
            var j = FloorJobPool.Count > 0 ? FloorJobPool.Pop() : new FloorJob();
            j.T = t; j.X = wx; j.Y = wy; j.Size = size; j.M = m; j.Src = FloorMask.Take();
            if (j.Buf == null || j.Buf.Length != m * m) j.Buf = new byte[m * m];
            FloorJobs.Add(j);
            t.FloorJob = true;
            _jobState = 1;
            return true;
        }
        if (defer && !now) return false;
        if (t.Floor) UnityEngine.Object.Destroy(t.Floor);
        MakeFloor(t, wx, wy, size);
        return true;
    }

    // 作業スレッドで作った床を受け取ってテクスチャにする (メイン・作業の終わりを待った後)
    private static void FinishFloors()
    {
        if (FloorJobs.Count == 0) return;
        long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var j in FloorJobs)
        {
            var t = j.T;
            j.T = null;
            t.FloorJob = false;
            // 片付けたタイル (船が替わった・Clear) は捨てる
            int key = t.Ty * WaterSim.TilesW + t.Tx;
            if (!t.Sr || !Tiles.TryGetValue(key, out var cur) || cur != t) continue;
            bool wait = t.FloorWait, wasOn = t.FloorOn;
            if (t.Floor) UnityEngine.Object.Destroy(t.Floor);
            UploadFloor(t, j.Buf, j.M, j.X, j.Y, j.Size);
            FloorFromThread++;
            if (wait && !t.Empty) Enable(t, !Hidden);
            // 切り抜きが付く前に描いた画素は「見せてよいか」(G) を切り抜き無しの出し方で作ってあり、壁の画素が 0 で壁の面の水が隠れる。
            // 水が動かないとタイルは描き直されないので、すぐ描き直す
            if (!wasOn && t.FloorOn) { t.LastDraw = -10f; Queue(key); }
        }
        foreach (var j in FloorJobs) FloorJobPool.Push(j);
        FloorJobs.Clear();
        FloorTicks += System.Diagnostics.Stopwatch.GetTimestamp() - f0;
    }

    private static void DropFloorJobs()
    {
        foreach (var j in FloorJobs)
        {
            if (j.T != null) j.T.FloorJob = false;
            j.T = null;
            FloorJobPool.Push(j);
        }
        FloorJobs.Clear();
    }

    // 確認用: タイルの画素を PPM へ (R = A (壁の面の上の水の厚さ)・G = 見せてよいか・B = 壁の面の高さ (床 = 128・面でない = 0)・上の行から)
    internal static string TileDump(float x, float y)
    {
        int tc = WaterSim.TileCells;
        float size = tc * WaterSim.Cell;
        var org = WaterSim.Origin;
        int tx = (int)MathF.Floor((x - org.x) / size), ty = (int)MathF.Floor((y - org.y) / size), key = ty * WaterSim.TilesW + tx;
        if (!Tiles.TryGetValue(key, out var t) || t.Px == null) return $"tile=({tx},{ty}) none";
        int n = tc * Px;
        var o = new byte[n * n * 3];
        for (int py = 0; py < n; py++)
            for (int px = 0; px < n; px++)
            {
                int j = py * n + px, d = ((n - 1 - py) * n + px) * 3;
                byte f = t.Face != null ? t.Face[j] : (byte)255;
                o[d] = t.Px[j * 4 + 3];
                o[d + 1] = t.Px[j * 4 + 1];
                o[d + 2] = f <= FloorMask.FaceTop ? (byte)(255 - f / 2) : f == FloorMask.FaceFloor ? (byte)128 : (byte)0;
            }
        string path = System.IO.Path.Combine(Bridge.TestBridge.ScreensDir, $"tile_{tx}_{ty}.ppm");
        using (var fs = System.IO.File.Create(path))
        {
            var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{n} {n}\n255\n");
            fs.Write(head, 0, head.Length);
            fs.Write(o, 0, o.Length);
        }
        return $"tile=({tx},{ty}) origin={org.x + tx * size:0.###},{org.y + ty * size:0.###} size={size} n={n} -> {path}";
    }

    // 確認用 (water tile x y): その点を含むタイルの床の切り抜き・壁の面・描き待ちの状態と、点に一番近い画素の値 (RGBA)
    internal static unsafe string TileInfo(float x, float y, bool upload = false)
    {
        int tc = WaterSim.TileCells;
        float size = tc * WaterSim.Cell;
        var org = WaterSim.Origin;
        int tx = (int)MathF.Floor((x - org.x) / size), ty = (int)MathF.Floor((y - org.y) / size), key = ty * WaterSim.TilesW + tx;
        if (!Tiles.TryGetValue(key, out var t)) return $"tile=({tx},{ty}) none";
        // up = 描き直さずに今の画素をテクスチャへ送り直す (画素と画面が食い違う時に、転送が抜けたのかを見る)
        if (upload && t.Px != null && t.Tex)
        {
            fixed (byte* b = t.Px) t.Tex.LoadRawTextureData((IntPtr)b, t.Px.Length);
            t.Tex.Apply(false, false);
        }
        string px = "-";
        if (t.Px != null)
        {
            int n = tc * Px;
            int ix = Math.Clamp((int)MathF.Round((x - org.x - tx * size) / size * (n - 1)), 0, n - 1);
            int iy = Math.Clamp((int)MathF.Round((y - org.y - ty * size) / size * (n - 1)), 0, n - 1);
            int i = (iy * n + ix) * 4;
            px = $"{t.Px[i]},{t.Px[i + 1]},{t.Px[i + 2]},{t.Px[i + 3]} face={(t.Face != null ? t.Face[iy * n + ix] : -1)}";
        }
        // 床の切り抜きをその場の作り方 (Fill) と作業スレッドの作り方 (FillFrom・moving なし) で作り直し、点の値と食い違いの数
        int fm = (int)MathF.Round(size * FloorMask.Ppu);
        float fx0 = org.x + tx * size, fy0 = org.y + ty * size;
        var f1 = new byte[fm * fm];
        var f2 = new byte[fm * fm];
        FloorMask.Fill(fx0, fy0, size, fm, f1);
        FloorMask.FillFrom(FloorMask.Take(), fx0, fy0, size, fm, f2, new FloorMask.Scratch(), false);
        int fdiff = 0;
        for (int i = 0; i < f1.Length; i++) if (f1[i] != f2[i]) fdiff++;
        int fi = Math.Clamp((int)MathF.Round((y - fy0) / size * (fm - 1)), 0, fm - 1) * fm + Math.Clamp((int)MathF.Round((x - fx0) / size * (fm - 1)), 0, fm - 1);
        string fl = $" cut={f1[fi]}/{f2[fi]} cutDiff={fdiff} moving={FloorMask.MovingAt(fx0, fy0, size, fm)}";
        return $"tile=({tx},{ty}){fl} floor={(bool)t.Floor} floorOn={t.FloorOn} floorJob={t.FloorJob} floorWait={t.FloorWait} floorDirty={t.FloorDirty} queued={FloorQueued.Contains(key)} faceDone={t.FaceDone} face={t.Face != null} empty={t.Empty} waiting={t.Waiting} lastDraw={_clock - t.LastDraw:0.00}s ago srOn={(t.Sr ? t.Sr.enabled : false)} px={px}";
    }

    // 床マスクを作り直すタイル (1 タイル数 ms かかるので 1 フレームに FloorBudgetMs まで。作り終わるまでは前の床のまま)
    private static readonly List<int> FloorQueue = new();
    private static readonly HashSet<int> FloorQueued = new();
    private const double FloorBudgetMs = 2.0;

    private static float FloorDist(int key, float mx, float my, float size)
    {
        if (!Tiles.TryGetValue(key, out var t)) return float.MaxValue;
        float dx = t.Tx * size - mx, dy = t.Ty * size - my;
        return dx * dx + dy * dy;
    }

    private static void RemakeQueuedFloors()
    {
        if (FloorQueue.Count == 0) return;
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budget = (long)(FloorBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        int done = 0;
        while (done < FloorQueue.Count)
        {
            int key = FloorQueue[done];
            if (Tiles.TryGetValue(key, out var t) && (t.Floor || t.FloorWait) && t.Sr)
            {
                if (!RequestFloor(t, org.x + t.Tx * size, org.y + t.Ty * size, size, false)) break; // 積む枠が埋まった: 残りは次のフレーム
                FloorFromQueue++;
            }
            FloorQueued.Remove(key);
            done++;
            if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= budget) break;
        }
        FloorQueue.RemoveRange(0, done);
    }

    // 床マスクの r の範囲が変わった (持ち上げた家具の跡が床になった): 掛かるタイルのマスクを作り直す。
    // マスクは CPU 側の写しを捨てて作ってあるので、書き換えずに作り直す
    internal static void FloorChanged(Rect r) => Changed(r, true);

    // 歩ける所が r の範囲で変わった (床マスクの形は別の道で作り直す): 水の写し元だけ決め直して描き直す
    internal static void FillChanged(Rect r) => Changed(r, false);

    private static void Changed(Rect r, bool floor)
    {
        if (!FloorMask.Active) return;
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        // 写し元は FillReach まで離れた所から来るので、その外側の升も写し直して水を描き直す
        var wide = Rect.MinMaxRect(r.xMin - FillReach, r.yMin - FillReach, r.xMax + FillReach, r.yMax + FillReach);
        if (_fillFrom != null) FillPending.Add(wide);
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (wide.xMax <= x0 || wide.xMin >= x0 + size || wide.yMax <= y0 || wide.yMin >= y0 + size) continue;
            if (_fillFrom != null && !t.Empty && !t.Waiting) { t.Waiting = true; Waiting.Add(kv.Key); }
            if (!floor || (!t.Floor && !t.FloorWait) || !t.Sr) continue;
            if (r.xMax <= x0 || r.xMin >= x0 + size || r.yMax <= y0 || r.yMin >= y0 + size) continue;
            if (FloorQueued.Add(kv.Key)) FloorQueue.Add(kv.Key);
        }
        if (!floor) return;
        // 変わった所の真ん中に近いタイルから作り直す
        float mx = r.center.x - org.x - size * 0.5f, my = r.center.y - org.y - size * 0.5f;
        FloorQueue.Sort((a, b) => FloorDist(a, mx, my, size).CompareTo(FloorDist(b, mx, my, size)));
    }

    // 画素ごとの高さの元 (壁の穴・低い壁) が r の範囲で変わった: 掛かるタイルの高さと床の切り抜きを次に描く時に作り直す
    internal static void HeightChanged(Rect r)
    {
        if (Tiles.Count == 0) return;
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (r.xMax <= x0 || r.xMin >= x0 + size || r.yMax <= y0 || r.yMin >= y0 + size) continue;
            t.FaceDone = false;
            t.FloorDirty = true;
            if (!t.Empty && !t.Waiting) { t.Waiting = true; Waiting.Add(kv.Key); }
        }
    }

    // テスト用: 床マスクの使う / 使わないを切り替えた。今あるタイルを全部その方式で描き直す
    internal static void FloorMaskToggled()
    {
        _fillDirty = true;
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t.Floor && t.Sr) SetFloor(t, !FloorMask.Off);
            t.FurnVersion = -1;
            if (t.Empty || t.Waiting) continue;
            t.Waiting = true;
            Waiting.Add(kv.Key);
        }
    }

    // 家具 (部屋の絵から持ち上げた物) が a から b へ動いて止まった: 型抜きを作り直す (どのタイルも次に描く時に)。
    // すぐ描き直すのは、元の場所か止まった所から r 以内に掛かるタイルだけ
    internal static void FurnitureMoved(Vector2 a, Vector2 b, float r)
    {
        _furnVersion++;
        // 床マスクで切っているタイルは、マスクに家具の今いる所を抜き直す
        FloorChanged(Rect.MinMaxRect(a.x - r, a.y - r, a.x + r, a.y + r));
        FloorChanged(Rect.MinMaxRect(b.x - r, b.y - r, b.x + r, b.y + r));
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t.Empty || t.Waiting) continue;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (!Near(a) && !Near(b)) continue;
            t.Waiting = true;
            Waiting.Add(kv.Key);

            bool Near(Vector2 c) => c.x + r > x0 && c.x - r < x0 + size && c.y + r > y0 && c.y - r < y0 + size;
        }
    }

    // 部屋の絵に描き込まれた家具 (机・ベッド) の範囲。どちらも絵は当たり判定の中に収まっているので、当たり判定の内側だけ隠す
    private static bool[] FurnitureMask(float wx, float wy, float size)
    {
        var cols = new List<Collider2D>();
        DamageMap.FurnitureColliders(new Vector2(wx + size * 0.5f, wy + size * 0.5f), size * 0.75f, cols);
        if (cols.Count == 0) return null;
        int m = WaterSim.TileCells * FurnSub;
        var mask = new bool[m * m];
        float step = size / (m - 1); // 端の画素はタイルの境目ちょうど (水の絵と同じ並び)
        foreach (var col in cols)
        {
            var b = col.bounds;
            float bx0 = b.min.x, bx1 = b.max.x, by0 = b.min.y, by1 = b.max.y;
            for (int y = 0; y < m; y++)
            {
                float qy = wy + y * step;
                if (qy < by0 || qy > by1) continue;
                for (int x = 0; x < m; x++)
                {
                    float qx = wx + x * step;
                    if (qx < bx0 || qx > bx1 || mask[y * m + x]) continue;
                    if (Inside(col, qx, qy))
                        mask[y * m + x] = true;
                }
            }
        }
        return mask;
    }

    // 当たり判定の内側か (縁を FurnitureInset だけ削る: 当たり判定は絵より少し大きく、そのまま隠すと家具の際に乾いた線が残った)
    private const float FurnitureInset = 0.025f;
    internal static bool Inside(Collider2D col, float x, float y) =>
        col.OverlapPoint(new Vector2(x, y))
        && col.OverlapPoint(new Vector2(x + FurnitureInset, y)) && col.OverlapPoint(new Vector2(x - FurnitureInset, y))
        && col.OverlapPoint(new Vector2(x, y + FurnitureInset)) && col.OverlapPoint(new Vector2(x, y - FurnitureInset));

    internal static void Clear()
    {
        WaterSim.EnsureIdle();
        for (int i = 0; i < Jobs.Length; i++) Jobs[i].T = null;
        _jobN = 0;
        _jobState = 0;
        JobError = null;
        DropFloorJobs();
        foreach (var t in Tiles.Values)
        {
            if (t.Go) UnityEngine.Object.Destroy(t.Go);
            if (t.Sp) UnityEngine.Object.Destroy(t.Sp);
            if (t.Tex) UnityEngine.Object.Destroy(t.Tex);
            if (t.Floor) UnityEngine.Object.Destroy(t.Floor);
            if (t.Front) UnityEngine.Object.Destroy(t.Front);
            if (t.Flow) UnityEngine.Object.Destroy(t.Flow);
        }
        Tiles.Clear();
        DropSpare();
        Waiting.Clear();
        FloorQueue.Clear();
        FloorQueued.Clear();
        Craters.Clear();
        _clock = 0f;
    }

    internal static string Describe() => $"tiles={Tiles.Count} waiting={Waiting.Count} spare={Spare.Count} made={SpareMade} miss={SpareMiss} drawn={Drawn} drawMs={LastDrawMs:0.00} fill={FillCells}/{FillMs:0.0}ms";
}
