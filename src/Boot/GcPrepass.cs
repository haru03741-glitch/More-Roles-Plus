using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace MoreRolesPlus.Boot;

// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/GcPrepass.cs (GPL-3.0)
// 止まっても見えない瞬間 (試合開始のイントロの頭・終了画面の構築中) に、mod 側 (CoreCLR) と
// ゲーム側 (il2cpp の Boehm) の両方のフル GC を先に回しておき、遊んでいる最中の GC の引っかかりを減らす。
// Resources.UnloadUnusedAssets は呼ばない (全オブジェクトの走査で数秒止まることがある)。
// incremental GC を切っている時 (IncrementalGcInvalidator) は 1 回の GC が長くなるので、この先回しと組で使う。
// Nebula on the Ship が読み込み完了時に GC.Collect(2) を打っているのと同じ考え方。
internal static class GcPrepass
{
#if ANDROID
    private const string Il2CppLib = "il2cpp";
#else
    private const string Il2CppLib = "GameAssembly";
#endif
    [DllImport(Il2CppLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_collect(int maxGenerations);

    private static long _lastMs = -10000;
    private static bool _noExport;

    // force = 続けて呼ばれた時の間引きをしない (船の準備のごみは必ずその場で回収する)
    public static void Collect(string reason, bool force = false)
    {
        if (!Plugin.PreemptiveGc.Value) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastMs < 3000) return; // 同じ場面で続けて呼ばれても 1 回だけ
        _lastMs = now;

        var sw = Stopwatch.StartNew();
        MemTrim.Collect(); // 詰めて空きを OS に返す (普通の GC と数 ms しか違わない)
        long clrMs = sw.ElapsedMilliseconds;
        if (!_noExport)
        {
            try { il2cpp_gc_collect(2); }
            catch { _noExport = true; } // 関数が無い環境では mod 側だけ
        }
        Plugin.Logger.LogInfo($"gc prepass ({reason}): clr={clrMs}ms total={sw.ElapsedMilliseconds}ms");
    }
}

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
internal static class GcOnIntroPatch
{
    public static void Prefix()
    {
        Terrain.TerrainWarm.OnIntro(); // 船の準備のまとめ (ごみは続く GC が回収する)
        GcPrepass.Collect("intro");
    }
}

[HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
internal static class GcOnOutroPatch
{
    public static void Postfix() => GcPrepass.Collect("outro");
}
