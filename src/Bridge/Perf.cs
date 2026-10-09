// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/FrameTrace.cs, Modules/FrameStats.cs (GPL-3.0)
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MoreRolesPlus.Bridge;

// フレームの重さを測る計器 (`perf [フレーム数]`)。次の行のコマンドは記録を始めてから動く (burst と同じ)。
// 1 フレームを Ticker の Update/LateUpdate で 2 つに割る:
//   mod  = この mod の Update + FixedUpdate の中 (演出・焼き・同期・ブリッジ)
//   rest = LateUpdate → 次の Update (描画・GPU 待ち・Present・本編の残り) から、その間に回ったこの mod の FixedUpdate を引いたもの。GPU が重いとここが伸びる
// 加えて損傷マスクの送り直し (回数・KB)・動いている演出の数・GC の回数と確保量を同じ窓で数え、
// il2cpp 側 (Boehm) のヒープの使用量も毎フレーム読み、減ったフレーム = ゲーム側の GC が走ったフレームとして数える (EK の GcPrepass と同じ口)。
// CSV (bridge/perf_<時刻>.csv) と 1 行の要約を出す。標本は固定長配列で、記録中も確保と interop 呼び出しは増やさない。
// 測り方は EK の FrameTrace / FrameStats (1% low) と同じ。
internal static class Perf
{
    private const int Capacity = 3600;
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly float[] Total = new float[Capacity], Mod = new float[Capacity], Rest = new float[Capacity];
    private static readonly short[] Live = new short[Capacity];
    private static readonly byte[] Up = new byte[Capacity];
    private static readonly int[] BoehmKb = new int[Capacity];
    private static readonly bool[] BoehmGc = new bool[Capacity];
    private static long _lastBoehm;

    // Tick ごとの内訳 (Ticker の Update / FixedUpdate の順番。Plugin.cs の Lane 番号と対応)
    internal static readonly string[] LaneNames =
    {
        "GameClock", "TerrainFx", "BreakNoise", "HammerSwing", "BombFuse", "DustCloud", "WaterSim", "Decompression", "CrewGrip", "FoamArt",
        "DecompFx", "DecompSound", "PropSim", "WaterLeak", "WaterArt", "WaterSpray", "FxHands", "RubbleBake", "ShadowPatch", "Burst",
        "DevConsole", "Shortcuts", "Zoom", "ChatInput",
        "MrpBundle", "TerrainWarm", "TerrainSync", "VersionCheck", "KillAbility", "Abilities", "ModButton", "MeetingEndWatch", "TestBridge", "Remote",
        "DamageMapFlush",
    };
    private static readonly long[] LaneSum = new long[LaneNames.Length], LaneMax = new long[LaneNames.Length];
    private static readonly int[] LaneSlow = new int[LaneNames.Length]; // 1ms を超えた回数
    private static readonly byte[] TopLane = new byte[Capacity];
    private static readonly float[] TopMs = new float[Capacity];
    private static long _topTicks;
    private static int _topLane;
    // vsync を外して測る (PC は 60fps 上限で描画側の重さが見えない)
    private static bool _nosync, _syncRestore;
    private static int _vsync, _target;

    private static long BoehmUsed() => Health.BoehmUsed();

    private static int _left, _n;
    private static long _lastUpd, _lastLate, _modTicks, _fixedTicks;
    private static int _gc0, _uploads;
    private static long _alloc, _uploadBytes;

    // 損傷マスク・種点の送り直し (DamageMap / FractureSites から)
    internal static void Upload(long bytes)
    {
        if (_left <= 0) return;
        _uploads++;
        _uploadBytes += bytes;
        if (_n < Capacity && Up[_n] < 255) Up[_n]++;
    }

    public static void Register()
    {
        TestBridge.Register("perf", "[フレーム数=300 (60..3600)] フレームの重さを記録して要約と CSV を出す (mod = この mod の処理 / rest = 描画・GPU 待ち・本編の残り)。次の行のコマンドは記録を始めてから動く", (args, reply) =>
        {
            if (_left > 0) { reply("ERR perf busy"); return; }
            string a = args.Trim();
            _nosync = a.Contains("nosync");
            a = a.Replace("nosync", "").Trim();
            int n = int.TryParse(a, out int f) ? Math.Clamp(f, 60, Capacity) : 300;
            Array.Clear(Up, 0, Capacity);
            Array.Clear(LaneSum, 0, LaneSum.Length);
            Array.Clear(LaneMax, 0, LaneMax.Length);
            Array.Clear(LaneSlow, 0, LaneSlow.Length);
            _topTicks = 0;
            _topLane = 0;
            if (_nosync)
            {
                try
                {
                    // Android の libunity には vSyncCount の読みが無い (ICall 照合)。端末は targetFrameRate だけ外す
#if !ANDROID
                    _vsync = UnityEngine.QualitySettings.vSyncCount;
                    UnityEngine.QualitySettings.vSyncCount = 0;
#endif
                    _target = UnityEngine.Application.targetFrameRate;
                    UnityEngine.Application.targetFrameRate = -1;
                    _syncRestore = true;
                }
                catch (Exception e) { TestBridge.Log("perf nosync failed: " + e.Message); _nosync = false; }
            }
            _n = 0;
            _lastUpd = 0;
            _lastLate = 0;
            _modTicks = 0;
            _uploads = 0;
            _uploadBytes = 0;
            _gc0 = GC.CollectionCount(0);
            _alloc = GC.GetTotalAllocatedBytes(false);
            _lastBoehm = BoehmUsed();
            _left = n;
            reply($"OK perf {n} frames{(_nosync ? " nosync" : "")}");
        });
    }

    // Ticker の各入口の中身を囲む (常駐の健康診断にも渡す)
    public static long Begin() => Stopwatch.GetTimestamp();
    public static void End(long start)
    {
        long d = Stopwatch.GetTimestamp() - start;
        Health.AddMod(d);
        if (_left > 0) _modTicks += d;
    }

    // 各 Tick の後に呼ぶ。前の Tick の終わり t から今までをレーン i に積む (interop も確保も無し)
    public static long Lane(int i, long t)
    {
        long now = Stopwatch.GetTimestamp();
        if (_left > 0)
        {
            long d = now - t;
            LaneSum[i] += d;
            if (d > LaneMax[i]) LaneMax[i] = d;
            if (d * MsPerTick > 1.0) LaneSlow[i]++;
            if (d > _topTicks) { _topTicks = d; _topLane = i; }
        }
        return now;
    }

    // FixedUpdate は LateUpdate と次の Update の間に回るので、rest から引けるよう別にも数える
    public static void EndFixed(long start)
    {
        long d = Stopwatch.GetTimestamp() - start;
        Health.AddMod(d);
        if (_left <= 0) return;
        _modTicks += d;
        _fixedTicks += d;
    }

    // Ticker.Update の先頭
    public static void FrameStart()
    {
        if (_left <= 0) return;
        long now = Stopwatch.GetTimestamp();
        if (_lastUpd != 0 && _n < Capacity)
        {
            Total[_n] = (float)((now - _lastUpd) * MsPerTick);
            Rest[_n] = _lastLate != 0 ? (float)((now - _lastLate - _fixedTicks) * MsPerTick) : -1f;
            Mod[_n] = (float)(_modTicks * MsPerTick);
            Live[_n] = (short)Math.Min(short.MaxValue, Terrain.TerrainFx.LiveCount);
            long b = BoehmUsed();
            BoehmKb[_n] = (int)(b / 1024);
            BoehmGc[_n] = b >= 0 && b < _lastBoehm;
            _lastBoehm = b;
            TopLane[_n] = (byte)_topLane;
            TopMs[_n] = (float)(_topTicks * MsPerTick);
            _n++;
            if (--_left == 0) { Finish(); return; }
        }
        _modTicks = 0;
        _fixedTicks = 0;
        _topTicks = 0;
        _topLane = 0;
        _lastUpd = now;
    }

    // Ticker.LateUpdate
    public static void FrameLate()
    {
        if (_left > 0) _lastLate = Stopwatch.GetTimestamp();
    }

    private static void Finish()
    {
        int n = _n;
        if (_syncRestore)
        {
            _syncRestore = false;
            try
            {
#if !ANDROID
                UnityEngine.QualitySettings.vSyncCount = _vsync;
#endif
                UnityEngine.Application.targetFrameRate = _target;
            }
            catch { }
        }
        int gc0 = GC.CollectionCount(0) - _gc0;
        long allocKb = (GC.GetTotalAllocatedBytes(false) - _alloc) / 1024;
        string path = null;
        try
        {
            var sb = new StringBuilder(n * 40);
            sb.AppendLine("i,total_ms,mod_ms,rest_ms,live,uploads,boehm_kb,boehm_gc,top_lane,top_ms");
            for (int i = 0; i < n; i++)
                sb.Append(i).Append(',').Append(Total[i].ToString("F2")).Append(',').Append(Mod[i].ToString("F2")).Append(',')
                    .Append(Rest[i].ToString("F2")).Append(',').Append(Live[i]).Append(',').Append(Up[i]).Append(',')
                    .Append(BoehmKb[i]).Append(',').Append(BoehmGc[i] ? 1 : 0).Append(',')
                    .Append(LaneNames[TopLane[i]]).Append(',').Append(TopMs[i].ToString("F2")).AppendLine();
            path = Path.Combine(TestBridge.Dir, $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception e) { path = "write failed: " + e.Message; }

        var t = Sorted(Total, n);
        var m = Sorted(Mod, n);
        var r = Sorted(Rest, n);
        int worst = Math.Max(1, n / 100);
        double sumWorst = 0;
        for (int i = n - worst; i < n; i++) sumWorst += t[i];
        int maxLive = 0;
        for (int i = 0; i < n; i++) maxLive = Math.Max(maxLive, Live[i]);
        int slowest = Array.IndexOf(Total, t[n - 1], 0, n);
        // ゲーム側の GC が走ったフレームの数と、そのうち 20ms を超えたフレームの数
        int bgc = 0, bgcSlow = 0;
        for (int i = 0; i < n; i++)
            if (BoehmGc[i]) { bgc++; if (Total[i] > 20f) bgcSlow++; }
        int slow = 0;
        for (int i = 0; i < n; i++) if (Total[i] > 20f) slow++;

        TestBridge.Log($"perf done n={n} total avg={Avg(t):F2} p50={t[n / 2]:F2} p95={t[n * 95 / 100]:F2} max={t[n - 1]:F2}@{slowest} low1%fps={1000.0 / (sumWorst / worst):F0}"
            + $" | mod avg={Avg(m):F2} p95={m[n * 95 / 100]:F2} max={m[n - 1]:F2} | rest avg={Avg(r):F2} p95={r[n * 95 / 100]:F2} max={r[n - 1]:F2}"
            + $" | slow(>20ms)={slow} boehmGc={bgc} (slow {bgcSlow}) boehm={BoehmKb[n - 1] / 1024}MB"
            + $" | live max={maxLive} uploads={_uploads} {_uploadBytes / 1024}KB gc0={gc0} alloc={allocKb}KB{(_nosync ? " nosync" : "")} -> {path}");
        TestBridge.Log("perf lanes " + Lanes(n));
    }

    // レーンの内訳: 合計の大きい順に 8 本 (avg / max ms・1ms 超の回数)。合計が 0 の物は出さない
    private static string Lanes(int n)
    {
        var order = new int[LaneNames.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => LaneSum[b].CompareTo(LaneSum[a]));
        var sb = new StringBuilder();
        for (int k = 0; k < 8 && k < order.Length; k++)
        {
            int i = order[k];
            if (LaneSum[i] == 0) break;
            if (k > 0) sb.Append(' ');
            sb.Append(LaneNames[i]).Append('=').Append((LaneSum[i] * MsPerTick / n).ToString("F3")).Append('/').Append((LaneMax[i] * MsPerTick).ToString("F2"));
            if (LaneSlow[i] > 0) sb.Append("(>1ms×").Append(LaneSlow[i]).Append(')');
        }
        return sb.ToString();
    }

    private static float[] Sorted(float[] src, int n)
    {
        var a = new float[n];
        Array.Copy(src, a, n);
        Array.Sort(a);
        return a;
    }

    private static double Avg(float[] a)
    {
        double s = 0;
        foreach (float v in a) s += v;
        return a.Length > 0 ? s / a.Length : 0;
    }
}
