using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MoreRolesPlus.Terrain;

// 水の 1 刻みの小刻み (C++) を描画の裏で回す。TerrainStep がフレームの最後の刻みの前置きまで進めて預け (Defer)、
// LateUpdate で作業スレッドへ投げ (Kick)、次のフレームの TerrainStep の頭で受け取って後始末する (Settle)。
// 投げてから受け取るまでの間に水の配列を読む・書く入口は EnsureIdle で作業の終わりを待つ (結果は同期で回した時と同じ)。
// 外から水を変える出来事は全部刻みの番号付きで積まれ、前置きでしか読まないので、刻みの途中にフレームの境目が入っても結果は変わらない
internal static unsafe partial class WaterSim
{
    private enum Phase { None, Prepared, Running, Joined }
    private static Phase _phase;
    private static int _defStep;        // 預けた刻み
    private static bool _simJob;        // 投げた仕事に水の刻みが入っているか (false = 水の絵だけ)
    [ThreadStatic] private static bool _onWorker;
    internal static bool Deferred => _phase != Phase.None;
    internal static int DeferredStep => _defStep;

    // 作業スレッド (1 本だけ常駐)。引数の構造体は動かない所に置く
    private static Thread _worker;
    private static readonly SemaphoreSlim Go = new(0), Done = new(0);
    private static StepArgs* _wa;
    private static int _wr;
    private static long _wTicks;
    internal static long ArtThreadTicks; // 作業スレッドで水の絵に掛かった時間 (water prof)

    // 区間の時間 (water prof): 裏で回った刻み・その場で回した刻み・受け取りで待った時間・Tick より前に読み手が待った回数
    internal static long AsyncSteps, SyncSteps, JoinWaitTicks, EarlyJoins;

    private static readonly GCHandle[] Pins = new GCHandle[40];
    private static int _pinN;

    private static void* Pin(Array arr)
    {
        if (arr == null) return null;
        var h = GCHandle.Alloc(arr, GCHandleType.Pinned);
        Pins[_pinN++] = h;
        return (void*)h.AddrOfPinnedObject();
    }

    private static void Unpin()
    {
        for (int i = 0; i < _pinN; i++) Pins[i].Free();
        _pinN = 0;
    }

    private static StepArgs* Args()
    {
        if (_wa == null) _wa = (StepArgs*)NativeMemory.AllocZeroed((nuint)sizeof(StepArgs));
        return _wa;
    }

    // 小刻みの引数を詰める (配列は Unpin まで固定)。step = 回す刻み
    private static void Fill(StepArgs* a, bool check, int step)
    {
        if (_dirtyBuf.Length < _tw * _th) _dirtyBuf = new int[_tw * _th];
        if (_infK.Length < Inflows.Count) { _infK = new int[Inflows.Count * 2]; _infRate = new int[Inflows.Count * 2]; }
        for (int i = 0; i < Inflows.Count; i++) { _infK[i] = Inflows[i].K; _infRate[i] = Inflows[i].Rate; }
        if (_fs.Length < _fallAt.Length * 3) _fs = new int[_fallAt.Length * 3];
        var isl = (byte*)Pin(_cellIsl);
        *a = new StepArgs
        {
            F = new FlowArgs
            {
                Hgt = (int*)Pin(_hgt), Flux = (int*)Pin(_flux), Move = (int*)Pin(_move),
                Open = (byte*)Pin(_open), Edge = (byte*)Pin(_edge), Lvl = (sbyte*)Pin(_lvl), Bed = (short*)Pin(_bed), Furn = (short*)Pin(_furn),
                StepMask = (byte*)Pin(_stepMask), FallMask = (byte*)Pin(_fallMask), SeaMask = (byte*)Pin(_seaMask), LipMask = (byte*)Pin(_lipMask),
                LipH = (short*)Pin(_lipH), BarB = (byte*)Pin(_barB), BarH = (short*)Pin(_barH), StepTo = (int*)Pin(_stepTo), Slot = (int*)Pin(_eSlot),
                Act = (int*)Pin(_act), InList = (byte*)Pin(_inList), Fo = (int*)Pin(_fo), TileDirty = (byte*)Pin(_tileDirty), DirtyOut = (int*)Pin(_dirtyBuf),
                ActN = _actN, FoN = _foN, W = _w, H = _h, Tw = _tw, Th = _th, TileCells = TileCells,
                SubN = SubN, SeaLevel = SeaLevel,
                Stir = (byte*)Pin(_stir), CheckSleep = check ? 1 : 0,
                // 照合の時は C# と同じ古いルールで回して 1 バイトずつ比べる
                Isl = isl, IslBase = (int*)Pin(_islBase), IslN = _islBase.Length, UseZ = ZFlow && !check && isl != null ? 1 : 0,
                FoCap = _fo.Length / 3,
            },
            InfK = (int*)Pin(_infK), InfRate = (int*)Pin(_infRate), InfN = Inflows.Count,
            DryStep = (step & 3) == 0 ? 1 : 0,
            FallStep = (int*)Pin(_fs), FsN = _fsN, FsCap = _fs.Length / 3, FallAt = (int*)Pin(_fallAt),
        };
    }

    // C++ の結果を受け取る (回す升の数・縁を越えた量の数・合計・描き直すタイル)
    private static void Apply(StepArgs* a)
    {
        _actN = a->F.ActN;
        _foN = a->F.FoN;
        _fsN = a->FsN;
        if (a->F.FoOver != 0) Plugin.Logger.LogError($"[WaterSim] fo over {a->F.FoOver} cap={_fo.Length / 3}");
        if (a->FsOver != 0) Plugin.Logger.LogError($"[WaterSim] fall step over {a->FsOver} cap={_fs.Length / 3}");
        VoidTotal += a->F.VoidAdd;
        InflowTotal += a->InflowAdd;
        SeaOutTotal += a->SeaOutAdd;
        VerifySleepViol += a->F.SleepViol;
        for (int i = 0; i < a->F.DirtyN; i++) DirtyTiles.Add(_dirtyBuf[i]);
    }

    // 前置きまで済んだ刻み step の小刻みを預ける (投げるのは LateUpdate)
    private static void Defer(int step)
    {
        _defStep = step;
        _phase = Phase.Prepared;
    }

    // LateUpdate の最後: 預けた刻みと水の絵の描き直しを作業スレッドへ (絵は刻みの後に描く)
    internal static void Kick()
    {
        bool sim = _phase == Phase.Prepared, art = WaterArt.HasJobs && (sim || _phase == Phase.None);
        if (!sim && !art) return;
        if (_worker == null)
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MRP water" };
            _worker.Start();
        }
        // 詰める途中で落ちたら固定を外して預けたままにする (次の Settle がその場で回す。絵は次の WaterArt.Tick がその場で描く)
        if (sim)
        {
            try { Fill(Args(), false, _defStep); }
            catch (Exception e) { Unpin(); Plugin.Logger.LogError($"[WaterSim] kick: {e}"); return; }
        }
        _simJob = sim;
        _phase = Phase.Running;
        Go.Release();
    }

    private static void WorkerLoop()
    {
        // 作業スレッドの上では EnsureIdle を素通りする (自分の終わりを待って止まらないように)
        _onWorker = true;
        while (true)
        {
            Go.Wait();
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            // 落ちても必ず終わりを知らせる (待つ側が止まらないように。0 なら C# 版で回し直す)
            try
            {
                if (_simJob)
                {
                    try { _wr = Native.MrpNative.WaterStep(_wa); }
                    catch { _wr = 0; }
                }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                _wTicks = t1 - t;
                // 刻みが C# 版へ回る時は絵も受け取り側で描き直す (その刻みの後の水を描くように)
                if (!_simJob || _wr != 0) WaterArt.RunJobs();
                ArtThreadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            }
            catch (Exception e) { WaterArt.JobError = e; }
            finally { Done.Release(); }
        }
    }

    // 水の配列に触る前に: 裏で回っていれば終わりを待って結果を受け取る (後始末は Settle)
    internal static void EnsureIdle()
    {
        if (_phase != Phase.Running || _onWorker) return;
        Join();
        EarlyJoins++;
    }

    private static void Join()
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        Done.Wait();
        JoinWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        Unpin();
        if (!_simJob) { _phase = Phase.None; return; }
        ProfTicks[4] += _wTicks;
        _phase = Phase.Joined;
        if (_wr != 0) { Apply(_wa); AsyncSteps++; }
        else NativeRefused(); // 定数が C++ と違う (起こらないはず)
    }

    // 預けた刻みを終わらせる (投げる前ならその場で回す)。終わらせた刻みを返す (無ければ −1)。続きの火は TerrainStep が回す
    internal static int Settle()
    {
        if (_phase == Phase.Running && !_simJob) Join(); // 絵だけの仕事
        if (_phase == Phase.None) return -1;
        try
        {
            _profT = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_phase == Phase.Running) Join();
            else if (_phase == Phase.Prepared) { RunSubsteps(false, _defStep); SyncSteps++; }
            _profT = System.Diagnostics.Stopwatch.GetTimestamp();
            _phase = Phase.None;
            PostStep(_defStep, false);
        }
        catch (Exception e) { _phase = Phase.None; Fail("settle", e); }
        return _defStep;
    }

    // 預けた刻みを捨てる (片付け)。裏で回っていれば終わりを待ってから
    private static void DropDeferred()
    {
        if (_phase == Phase.Running)
        {
            Done.Wait();
            Unpin();
        }
        _phase = Phase.None;
    }
}
