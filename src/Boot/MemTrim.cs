using System;
using System.Diagnostics;
using System.Runtime;

namespace MoreRolesPlus.Boot;

// mod 側 (CoreCLR) の GC ヒープを詰めて、使っていない分を OS に返す。
// 起動 (パッチ当て・先回しコンパイル) の後はヒープの大半が空きのまま確保されているので、
// 止まっても見えない時 (メニューに着いた直後・試合の準備のまとめの後) に 1 回だけ詰める
internal static class MemTrim
{
    // 1 回だけ詰める (GC.Collect の代わり)
    public static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // 後始末 (finalizer) の分まで回して詰め、結果をログに出す
    public static void Compact(string reason)
    {
        var sw = Stopwatch.StartNew();
        long before = GC.GetGCMemoryInfo().TotalCommittedBytes;
        Collect();
        GC.WaitForPendingFinalizers();
        Collect();
        long after = GC.GetGCMemoryInfo().TotalCommittedBytes;
        Plugin.Logger.LogInfo($"mem trim ({reason}): clr committed {before >> 20}MB -> {after >> 20}MB in {sw.ElapsedMilliseconds}ms");
    }
}
