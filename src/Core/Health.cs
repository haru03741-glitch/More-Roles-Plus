// Hitch detection based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/HealthLog.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MoreRolesPlus;

// 常駐の健康診断。全員のログ (LogOutput.log) に、試合ごとの重さとエラーの要約を残す。
// - 引っかかり: 前のフレームから 50ms 以上空いたら数え、試合中は 1 行書く (10 秒に 5 行まで・250ms 以上は必ず)。
//   その間のこの mod の処理時間と、ゲーム側の GC (il2cpp のヒープが減った) があったかを添える。
// - エラーの嵐: 本編の例外を出どころ (スタックの最初の行) ごとに数え、同じ所が 1 秒に StormPerSec 回以上出たら
//   始まりと終わりを 1 行ずつ書く (毎回は書かない)。開発者には画面にも出す。
// - 試合の要約: 試合の船が消えた時に、時間・フレーム数・平均・引っかかり・エラーを 1 行。
// 毎フレームの経路は Stopwatch の差だけ (Unity / il2cpp は呼ばない)。ヒープの読みは 1 秒ごとと引っかかりの時だけ。
internal static class Health
{
    private const double HitchMs = 50;
    private const double HitchAlwaysMs = 250;
    private const int HitchLinesPerWindow = 5;
    private const long HitchWindowMs = 10_000;
    private const int StormPerSec = 20;
    private const int MaxSignatures = 64;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

#if ANDROID
    private const string Il2CppLib = "il2cpp";
#else
    private const string Il2CppLib = "GameAssembly";
#endif
    [DllImport(Il2CppLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern long il2cpp_gc_get_used_size();
    private static bool _boehmMissing;

    // il2cpp (Boehm) のヒープの使用量。読めなければ -1
    internal static long BoehmUsed()
    {
        if (_boehmMissing) return -1;
        try { return il2cpp_gc_get_used_size(); }
        catch { _boehmMissing = true; return -1; }
    }

    private sealed class Sig
    {
        public string Name;
        public int Total, ThisSecond, StormSeconds, StormCount;
        public bool Storming;
    }

    private static readonly Dictionary<string, Sig> Sigs = new();
    private static Sig _lastSig;
    private static string _lastMessage, _lastStack;
    private static int _otherErrors; // 出どころの表が一杯になった後のエラー
    private static bool _installed;

    private static long _lastFrame, _modTicks, _lastSecond, _lastBoehm, _windowStart;
    private static int _linesInWindow, _suppressed;

    // 試合の集計 (船が出た時に始め、消えた時に書く)
    private static int _matchGen = -1;
    private static long _matchStart;
    private static int _frames, _hitches, _gcHitches, _errors;
    private static double _frameMsSum, _worstMs;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            Application.add_logMessageReceived((Application.LogCallback)(Action<string, string, LogType>)OnUnityLog);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"health: log hook failed: {e.Message}"); }
    }

    // 確かめ用: 残りのフレーム数だけ毎フレーム本編のログにエラーを書く (ブリッジ `health storm`)
    internal static int TestErrorFrames;

    // この mod の処理時間 (Ticker の入口ごと)
    public static void AddMod(long ticks) => _modTicks += ticks;

    // Ticker.Update の先頭で毎フレーム 1 回
    public static void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        long mod = _modTicks;
        if (TestErrorFrames > 0) { TestErrorFrames--; UnityEngine.Debug.LogError("MRP health test error"); }
        _modTicks = 0;
        if (_lastFrame == 0) { _lastFrame = now; _lastSecond = now; return; }
        double gap = (now - _lastFrame) * MsPerTick;
        _lastFrame = now;

        bool inMatch = _matchGen >= 0;
        if (inMatch)
        {
            _frames++;
            _frameMsSum += gap;
            if (gap > _worstMs) _worstMs = gap;
        }
        if (gap >= HitchMs) Hitch(now, gap, mod * MsPerTick, inMatch);

        if ((now - _lastSecond) * MsPerTick >= 1000) { _lastSecond = now; Second(); }
    }

    private static void Hitch(long now, double gap, double modMs, bool inMatch)
    {
        long boehm = BoehmUsed();
        bool gc = boehm >= 0 && _lastBoehm > 0 && boehm < _lastBoehm;
        _lastBoehm = boehm;
        if (!inMatch) return; // 読み込み中・メニューの引っかかりは数えない
        _hitches++;
        if (gc) _gcHitches++;

        if ((now - _windowStart) * MsPerTick >= HitchWindowMs)
        {
            if (_suppressed > 0) Plugin.Logger.LogInfo($"HITCH (+{_suppressed} more not shown)");
            _windowStart = now;
            _linesInWindow = 0;
            _suppressed = 0;
        }
        if (gap < HitchAlwaysMs && _linesInWindow >= HitchLinesPerWindow) { _suppressed++; return; }
        _linesInWindow++;
        Plugin.Logger.LogInfo($"HITCH {gap:F0}ms mod={modMs:F1}ms{(gc ? " gc" : "")} heap={(boehm >= 0 ? boehm >> 20 : -1)}MB");
    }

    // 1 秒ごと: 試合の始め/終わり・ヒープの標本・エラーの嵐の判定
    private static void Second()
    {
        int gen = Terrain.GameClock.ShipGen;
        if (Terrain.GameClock.ShipAlive && _matchGen != gen) BeginMatch(gen);

        long heap = BoehmUsed();
        if (heap >= 0) _lastBoehm = heap;

        // 画面への知らせはループの後 (知らせる処理がエラーを出すと、数えている表が増えてループが壊れる)
        string notice = null;
        foreach (var s in Sigs.Values)
        {
            int n = s.ThisSecond;
            s.ThisSecond = 0;
            if (n >= StormPerSec)
            {
                s.StormSeconds++;
                s.StormCount += n;
                if (!s.Storming)
                {
                    s.Storming = true;
                    Plugin.Logger.LogWarning($"ERRSTORM start {s.Name} {n}/s");
                    notice ??= $"<color=#FF8040>エラーが連発しています</color>: {s.Name} {n}/秒";
                }
            }
            else if (s.Storming)
            {
                s.Storming = false;
                Plugin.Logger.LogWarning($"ERRSTORM end {s.Name} after {s.StormSeconds}s ({s.StormCount} errors)");
                s.StormSeconds = 0;
                s.StormCount = 0;
            }
        }
        if (notice != null && Dev.DevUsers.AmDev) Dev.DevChat.Notice(Vanilla.Chat, notice);
    }

    private static void BeginMatch(int gen)
    {
        _matchGen = gen;
        _matchStart = Stopwatch.GetTimestamp();
        _frames = _hitches = _gcHitches = _errors = 0;
        _frameMsSum = _worstMs = 0;
        foreach (var s in Sigs.Values) s.Total = 0;
        _otherErrors = 0;
        // 船が消えた・替わった時に要約を書く
        Terrain.GameClock.Ship.OnRelease(EndMatch);
    }

    private static void EndMatch()
    {
        if (_matchGen < 0) return;
        _matchGen = -1;
        double sec = (Stopwatch.GetTimestamp() - _matchStart) * MsPerTick / 1000;
        var sb = new StringBuilder();
        sb.Append($"HEALTH match {sec:F0}s frames={_frames} avg={(_frames > 0 ? _frameMsSum / _frames : 0):F1}ms worst={_worstMs:F0}ms");
        sb.Append($" hitches(>{HitchMs:F0}ms)={_hitches} (gc {_gcHitches}) errors={_errors}");
        if (_errors > 0)
        {
            var top = new List<Sig>();
            foreach (var s in Sigs.Values) if (s.Total > 0) top.Add(s);
            top.Sort((a, b) => b.Total.CompareTo(a.Total));
            sb.Append(" top=[");
            for (int i = 0; i < top.Count && i < 3; i++) sb.Append(i > 0 ? ", " : "").Append(top[i].Name).Append(" x").Append(top[i].Total);
            if (_otherErrors > 0) sb.Append(top.Count > 0 ? ", " : "").Append("other x").Append(_otherErrors);
            sb.Append(']');
        }
        Plugin.Logger.LogInfo(sb.ToString());
    }

    private static void OnUnityLog(string message, string stackTrace, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
        if (_matchGen >= 0) _errors++;
        // 連発は同じ文とスタックが続くので、直前と同じなら出どころを作り直さない (比べるだけで確保しない)
        Sig s = _lastSig;
        if (s == null || message != _lastMessage || stackTrace != _lastStack)
        {
            string name = Signature(message, stackTrace);
            if (!Sigs.TryGetValue(name, out s))
            {
                if (Sigs.Count >= MaxSignatures) { _otherErrors++; return; }
                s = new Sig { Name = name };
                Sigs[name] = s;
            }
            _lastSig = s;
            _lastMessage = message;
            _lastStack = stackTrace;
        }
        s.Total++;
        s.ThisSecond++;
    }

    // 出どころ = スタックの中で最初の本編/mod の関数 (例外の種類か文の頭を後ろに付ける)。
    // Unity のエラーのスタックは「型:関数(引数)」、例外は「  at 型.関数 () [...]」の形。ログの中継の関数 (System. / UnityEngine. /
    // Logger: / Sentry.) は飛ばす。見つからなければ文の頭だけ
    private static readonly string[] NoiseFrames = { "System.", "UnityEngine.", "Logger:", "Sentry.", "Il2CppInterop.", "MoreRolesPlus.Health" };

    private static string Signature(string message, string stackTrace)
    {
        string kind = message ?? "";
        int nl0 = kind.IndexOf('\n');
        if (nl0 >= 0) kind = kind.Substring(0, nl0);
        int colon = kind.IndexOf(':');
        if (colon > 0 && colon < 60) kind = kind.Substring(0, colon);
        else if (kind.Length > 60) kind = kind.Substring(0, 60);

        if (string.IsNullOrEmpty(stackTrace)) return kind;
        int pos = 0;
        while (pos < stackTrace.Length)
        {
            int nl = stackTrace.IndexOf('\n', pos);
            if (nl < 0) nl = stackTrace.Length;
            int a = pos;
            while (a < nl && stackTrace[a] == ' ') a++;
            if (string.CompareOrdinal(stackTrace, a, "at ", 0, 3) == 0) a += 3;
            pos = nl + 1;
            if (a >= nl) continue;
            bool noise = false;
            foreach (var n in NoiseFrames)
                if (string.CompareOrdinal(stackTrace, a, n, 0, n.Length) == 0) { noise = true; break; }
            if (noise) continue;
            int b = stackTrace.IndexOf('(', a, nl - a);
            if (b < 0) b = nl;
            while (b > a && stackTrace[b - 1] == ' ') b--;
            return $"{stackTrace.Substring(a, Math.Min(b - a, 80))} {kind}";
        }
        return kind;
    }

    // 確かめ用 (ブリッジ `health`)
    internal static string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"match={(_matchGen >= 0 ? "on" : "off")} frames={_frames} hitches={_hitches} (gc {_gcHitches}) worst={_worstMs:F0}ms errors={_errors} heap={BoehmUsed() >> 20}MB");
        foreach (var s in Sigs.Values)
            sb.Append($"\n  {s.Name} x{s.Total}{(s.Storming ? " STORM" : "")}");
        return sb.ToString();
    }
}
