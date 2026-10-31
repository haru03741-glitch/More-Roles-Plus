using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// メモリの内訳 (`mem [件数]`): .NET の GC ヒープ・il2cpp のヒープ・読み込み済みの絵 (大きい順)・メッシュ・音
internal static class MemProbe
{
    public static void Register()
    {
        TestBridge.Register("mem", "[件数=15] メモリの内訳 (.NET / il2cpp / 絵・メッシュ・音の大きい順)", (args, reply) =>
        {
            if (args.Trim() == "compact") Boot.MemTrim.Compact("bridge");
            int top = int.TryParse(args.Trim(), out int n) ? n : 15;
            var gi = GC.GetGCMemoryInfo();
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            reply($"MEM ws={proc.WorkingSet64 >> 20}MB private={proc.PrivateMemorySize64 >> 20}MB clrHeap={gi.HeapSizeBytes >> 20}MB clrCommitted={gi.TotalCommittedBytes >> 20}MB clrTotal={GC.GetTotalMemory(false) >> 20}MB boehm={Health.BoehmUsed() >> 20}MB");
            Sum<Texture2D>("tex", top, reply, o => { var t = o.Cast<Texture2D>(); return (long)t.width * t.height * Bpp(t.format) / 8 * (t.mipmapCount > 1 ? 4 : 3) / 3; }, o => { var t = o.Cast<Texture2D>(); return $"{t.width}x{t.height} {t.format}"; });
            Sum<RenderTexture>("rt", top, reply, o => { var t = o.Cast<RenderTexture>(); return (long)t.width * t.height * 4; }, o => { var t = o.Cast<RenderTexture>(); return $"{t.width}x{t.height}"; });
            Sum<Mesh>("mesh", top, reply, o => { var m = o.Cast<Mesh>(); return m.isReadable ? (long)m.vertexCount * 48 : 0; }, o => $"v={o.Cast<Mesh>().vertexCount}");
            Sum<AudioClip>("audio", top, reply, o => { var a = o.Cast<AudioClip>(); return (long)a.samples * a.channels * 4; }, o => $"{o.Cast<AudioClip>().length:F1}s");
            reply($"MEM env gen0MaxBudget={Environment.GetEnvironmentVariable("DOTNET_GCgen0MaxBudget") ?? "-"} freeSpaceDivisor={Environment.GetEnvironmentVariable("GC_FREE_SPACE_DIVISOR") ?? "-"}");
            reply("OK mem");
        });
    }

    private static int Bpp(TextureFormat f) => f switch
    {
        TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 or TextureFormat.RFloat => 32,
        TextureFormat.RGB24 => 24,
        TextureFormat.RGBAHalf => 64,
        TextureFormat.RGBAFloat => 128,
        TextureFormat.R8 or TextureFormat.Alpha8 => 8,
        TextureFormat.R16 or TextureFormat.RHalf or TextureFormat.RG16 or TextureFormat.RGB565 or TextureFormat.RGBA4444 or TextureFormat.ARGB4444 => 16,
        TextureFormat.DXT1 or TextureFormat.BC4 or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB => 4,
        TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC7 or TextureFormat.ETC2_RGBA8 => 8,
        _ => 32,
    };

    private static void Sum<T>(string tag, int top, Action<string> reply, Func<UnityEngine.Object, long> size, Func<UnityEngine.Object, string> desc) where T : UnityEngine.Object
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>());
        var list = new List<(long B, string Name, string D, bool Mrp)>();
        long total = 0, mrp = 0;
        foreach (var o in all)
        {
            if (!o) continue;
            long b;
#if ANDROID
            try { b = size(o); } catch { continue; } // Android の libunity には Profiler の大きさ取得が無い
#else
            try { b = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o); if (b <= 0) b = size(o); } catch { continue; }
#endif
            // この mod が作った物は名前が無いか hideFlags で見分ける (本編の資産は名前と保存の印がある)
            bool mine = string.IsNullOrEmpty(o.name) || (o.hideFlags & HideFlags.DontSave) != 0;
            total += b;
            if (mine) mrp += b;
            list.Add((b, o.name, desc(o), mine));
        }
        reply($"MEM {tag} n={list.Count} total={total >> 20}MB madeAtRuntime={mrp >> 20}MB");
        foreach (var e in list.OrderByDescending(e => e.B).Take(top))
            reply($"MEM   {tag} {e.B >> 10}KB {(e.Mrp ? "*" : " ")} {e.Name} {e.D}");
    }
}
