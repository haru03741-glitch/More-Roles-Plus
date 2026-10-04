using System;
using System.Runtime.InteropServices;

namespace MoreRolesPlus.Boot;

// incremental GC を実行時に無効化する (Nebula on the Ship の IncrementalGcInvalidator と同じ手法)。
// Ported from https://github.com/waffle-ful/Aeterna-End-K-not Modules/IncrementalGcInvalidator.cs (GPL-3.0)
// Based on https://github.com/Dolly1016/Nebula-Public/blob/master/NebulaPluginNova/Utilities/IncrementalGcInvalidator.cs (GPL-3.0)
//
// 速くするためでなく安全のため: 書き込みバリア無しで il2cpp のオブジェクトへ書く interop が、
// incremental GC の途中だと生きているオブジェクトを回収させてしまう (落ちる) ことがある。
// incremental を切ればこの種の事故は原理的に起きない。代わりに 1 回の GC の停止が少し長くなる
// (その分は GcPrepass で見えない瞬間に先に回収しておく)。
// boot.config の gc-max-time-slice を消しても incremental は切れない (既定の 3ms で走る) ので、
// il2cpp_gc_is_incremental() が読む Boehm のフラグを直接 0 にする。
//
// フラグの場所の探し方:
//   GetProcAddress("il2cpp_gc_is_incremental") → jmp の中継 (E9) を辿る →
//   関数の先頭 24 バイトから `8B 05 disp32` (x64: mov eax,[rip+disp32]) / `A1 abs32` (x86) を探す →
//   無ければ最初の `E8 rel32` (call) を 1 段だけ掘って同じように探す。
//
// 安全側に足したこと:
//   - 書き込む前に VirtualQuery でページが書き込めるか確かめる (読み取り専用への書き込みは .NET で
//     捕まえられないアクセス違反 = 即終了になる)。
//   - 書いた後に il2cpp_gc_is_incremental() が false になったか確かめ、ならなければ元に戻す。

public static unsafe class IncrementalGcInvalidator
{
    private const byte OpCallRel32 = 0xE8;
    private const byte OpJmpRel32 = 0xE9;
    private const byte OpMovEaxRipRel = 0x8B; // 8B 05 disp32 = mov eax, [rip+disp32] (x64)
    private const byte ModRmRipRel = 0x05;
    private const byte OpMovEaxAbs32 = 0xA1;  // A1 abs32   = mov eax, [abs32]      (x86)

    private const int ScanBytes = 24;
    private const int MaxThunkHops = 5;

    // VirtualQuery の Protect 値のうち「書き込み可能」なもの。
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;
    private const uint MemCommit = 0x1000;

    private static bool _applied;

    private static BepInEx.Logging.ManualLogSource Log => Plugin.Logger;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, UIntPtr dwLength);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, EntryPoint = "il2cpp_gc_is_incremental")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IsIncrementalNative();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    // Plugin.Load() から一度だけ呼ぶ。ここから例外が漏れるとプラグインごと読み込まれなくなるので全体を守る
    public static void ApplyIfConfigured()
    {
        if (_applied) return;
        _applied = true;

        try { Apply(); }
        catch (Exception e) { Log.LogWarning($"INCGC: unexpected failure ({e.GetType().Name}: {e.Message}) — leaving the GC mode untouched"); }
    }

    private static void Apply()
    {
        // 場所の特定と保護の確認は kernel32 に頼るので Windows だけ (Android では何もしない)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.LogInfo("INCGC: skipped (Windows-only; the flag lookup needs kernel32)");
            return;
        }

        bool before;
        try { before = IsIncrementalNative(); }
        catch (Exception e)
        {
            Log.LogWarning($"INCGC: il2cpp_gc_is_incremental unavailable ({e.Message}) — skipped");
            return;
        }

        if (!Plugin.DisableIncrementalGc.Value)
        {
            Log.LogInfo($"INCGC: incremental={before} (left as-is; DisableIncrementalGc is off)");
            return;
        }

        if (!before)
        {
            Log.LogInfo("INCGC: incremental already off — nothing to do");
            return;
        }

        Log.LogInfo($"INCGC: disabling incremental GC (before={before})");
        Log.LogInfo($"INCGC: result={Disable()}");
    }

    private static string Disable()
    {
        IntPtr flagAddr;
        try { flagAddr = DiscoverFlagAddress(); }
        catch (Exception e) { return $"FAILED (scan threw: {e.Message})"; }

        if (flagAddr == IntPtr.Zero)
            return "FAILED (flag address not found — instruction pattern did not match on this build)";

        if (!IsWritable(flagAddr))
            return $"FAILED (page at 0x{flagAddr.ToInt64():X} is not writable — refusing to write)";

        int* flag = (int*)flagAddr;
        int original = *flag;

        // 掴んだグローバルが本当に incremental フラグかの一致確認。
        // native の戻り値と読み値の真偽が食い違うなら別の変数を指している。
        if (original != 0 != IsIncrementalNative())
            return $"FAILED (sanity check mismatch at 0x{flagAddr.ToInt64():X}: value={original})";

        *flag = 0;

        bool after;
        try { after = IsIncrementalNative(); }
        catch (Exception e)
        {
            *flag = original;
            return $"FAILED (verification threw: {e.Message}; rolled back)";
        }

        if (after)
        {
            *flag = original;
            return "FAILED (still incremental after write; rolled back)";
        }

        return $"OK (flag at 0x{flagAddr.ToInt64():X}, {original} -> 0)";
    }

    private static bool HasProtection(IntPtr addr, uint mask)
    {
        if (VirtualQuery(addr, out MemoryBasicInformation mbi, (UIntPtr)(uint)sizeof(MemoryBasicInformation)) == UIntPtr.Zero)
            return false;

        if (mbi.State != MemCommit) return false;
        if ((mbi.Protect & PageGuard) != 0) return false;

        return (mbi.Protect & mask) != 0;
    }

    private static bool IsWritable(IntPtr addr)
    {
        return HasProtection(addr, PageReadWrite | PageWriteCopy | PageExecuteReadWrite | PageExecuteWriteCopy);
    }

    // 走査は生ポインタの追跡なので、読む前に必ず通す。バイト列一致 (0xE8 等) は命令境界を見ないため、
    // 別命令の即値が偶然一致すると無効アドレスを指しうる — 無効アドレスの逆参照は
    // .NET では catch できないアクセス違反 = プロセス即死になる (起動不能ブリックと同じ障害クラス)。
    // ページ境界をまたぐ場合があるので先頭と末尾の両方を確認する。
    private static bool CanRead(IntPtr addr, int length)
    {
        if (addr == IntPtr.Zero || length <= 0) return false;

        const uint readable = PageReadOnly | PageReadWrite | PageWriteCopy | PageExecuteRead | PageExecuteReadWrite | PageExecuteWriteCopy;

        return HasProtection(addr, readable) && HasProtection((IntPtr)((long)addr + length - 1), readable);
    }

    private static IntPtr DiscoverFlagAddress()
    {
        IntPtr module = GetModuleHandleW("GameAssembly.dll");
        if (module == IntPtr.Zero) return IntPtr.Zero;

        IntPtr export = GetProcAddress(module, "il2cpp_gc_is_incremental");
        if (export == IntPtr.Zero) return IntPtr.Zero;

        IntPtr func = ResolveJmpThunk(export);

        IntPtr direct = FindGlobalReadTarget(func);
        if (direct != IntPtr.Zero) return direct;

        // インライン化されていない場合、本体は最初の call の先に居る (1 段だけ掘る)。
        IntPtr callee = FindFirstCallTarget(func);
        if (callee == IntPtr.Zero) return IntPtr.Zero;

        return FindGlobalReadTarget(ResolveJmpThunk(callee));
    }

    // インポートサンクの E9 (jmp rel32) を辿って実体へ降りる。
    private static IntPtr ResolveJmpThunk(IntPtr addr)
    {
        for (int i = 0; i < MaxThunkHops; i++)
        {
            if (!CanRead(addr, 5)) return IntPtr.Zero;

            var p = (byte*)addr;
            if (*p != OpJmpRel32) break;
            addr = (IntPtr)((long)addr + 5 + *(int*)(p + 1));
        }

        return addr;
    }

    private static IntPtr FindFirstCallTarget(IntPtr funcAddr)
    {
        if (!CanRead(funcAddr, ScanBytes)) return IntPtr.Zero;

        var b = (byte*)funcAddr;

        for (int i = 0; i < ScanBytes - 4; i++)
        {
            if (b[i] != OpCallRel32) continue;
            return (IntPtr)((long)funcAddr + i + 5 + *(int*)(b + i + 1));
        }

        return IntPtr.Zero;
    }

    // グローバル変数読み出し命令のオペランドから、変数そのもののアドレスを割り出す。
    private static IntPtr FindGlobalReadTarget(IntPtr funcAddr)
    {
        if (!CanRead(funcAddr, ScanBytes)) return IntPtr.Zero;

        var b = (byte*)funcAddr;

        for (int i = 0; i < ScanBytes - 6; i++)
        {
            if (b[i] == OpMovEaxRipRel && b[i + 1] == ModRmRipRel)
            {
                int disp = *(int*)(b + i + 2);
                // x64 は RIP 相対 (次命令の先頭が基準)、x86 は絶対アドレスがそのまま入る。
                return Environment.Is64BitProcess ? (IntPtr)((long)funcAddr + i + 6 + disp) : (IntPtr)disp;
            }

            if (!Environment.Is64BitProcess && b[i] == OpMovEaxAbs32)
                return (IntPtr)(*(int*)(b + i + 1));
        }

        return IntPtr.Zero;
    }
}
