using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MoreRolesPlus.Bridge;

// フレームの重さを測る計器 (`perf [フレーム数]`)。次の行のコマンドは記録を始めてから動く (burst と同じ)。
// 1 フレームを Ticker の Update/LateUpdate で 2 つに割る:
//   mod  = この mod の Update + FixedUpdate の中 (演出・焼き・同期・ブリッジ)
//   rest = LateUpdate → 次の Update (描画・GPU 待ち・Present・本編の残り) から、その間に回ったこの mod の FixedUpdate を引いたもの。GPU が重いとここが伸びる
// 加えて損傷マスクの送り直し (回数・KB)・動いている演出の数・GC の回数と確保量を同じ窓で数え、
// CSV (bridge/perf_<時刻>.csv) と 1 行の要約を出す。標本は固定長配列で、記録中も確保と interop 呼び出しは増やさない。
// 測り方は EK の FrameTrace / FrameStats (1% low) と同じ。
internal static class Perf
{
    private const int Capacity = 3600;
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly float[] Total = new float[Capacity], Mod = new float[Capacity], Rest = new float[Capacity];
    private static readonly short[] Live = new short[Capacity];
    private static readonly byte[] Up = new byte[Capacity];

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
            int n = int.TryParse(args.Trim(), out int f) ? Math.Clamp(f, 60, Capacity) : 300;
            Array.Clear(Up, 0, Capacity);
            _n = 0;
            _lastUpd = 0;
            _lastLate = 0;
            _modTicks = 0;
            _uploads = 0;
            _uploadBytes = 0;
            _gc0 = GC.CollectionCount(0);
            _alloc = GC.GetTotalAllocatedBytes(false);
            _left = n;
            reply($"OK perf {n} frames");
        });
    }

    // Ticker の各入口の中身を囲む (記録していない間は 1 回の比較だけ)
    public static long Begin() => _left > 0 ? Stopwatch.GetTimestamp() : 0;
    public static void End(long start) { if (start != 0) _modTicks += Stopwatch.GetTimestamp() - start; }

    // FixedUpdate は LateUpdate と次の Update の間に回るので、rest から引けるよう別にも数える
    public static void EndFixed(long start)
    {
        if (start == 0) return;
        long d = Stopwatch.GetTimestamp() - start;
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
            _n++;
            if (--_left == 0) { Finish(); return; }
        }
        _modTicks = 0;
        _fixedTicks = 0;
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
        int gc0 = GC.CollectionCount(0) - _gc0;
        long allocKb = (GC.GetTotalAllocatedBytes(false) - _alloc) / 1024;
        string path = null;
        try
        {
            var sb = new StringBuilder(n * 40);
            sb.AppendLine("i,total_ms,mod_ms,rest_ms,live,uploads");
            for (int i = 0; i < n; i++)
                sb.Append(i).Append(',').Append(Total[i].ToString("F2")).Append(',').Append(Mod[i].ToString("F2")).Append(',')
                    .Append(Rest[i].ToString("F2")).Append(',').Append(Live[i]).Append(',').Append(Up[i]).AppendLine();
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

        TestBridge.Log($"perf done n={n} total avg={Avg(t):F2} p50={t[n / 2]:F2} p95={t[n * 95 / 100]:F2} max={t[n - 1]:F2}@{slowest} low1%fps={1000.0 / (sumWorst / worst):F0}"
            + $" | mod avg={Avg(m):F2} p95={m[n * 95 / 100]:F2} max={m[n - 1]:F2} | rest avg={Avg(r):F2} p95={r[n * 95 / 100]:F2} max={r[n - 1]:F2}"
            + $" | live max={maxLive} uploads={_uploads} {_uploadBytes / 1024}KB gc0={gc0} alloc={allocKb}KB -> {path}");
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
