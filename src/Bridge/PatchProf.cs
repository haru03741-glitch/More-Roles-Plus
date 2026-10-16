using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace MoreRolesPlus.Bridge;

// Harmony パッチの重さを測る計器 (`patchprof on|reset|[件数]`)。
// on の間、この mod がパッチを当てた本編の関数 (本編 + 全部のパッチ込み) と、この mod のパッチの関数そのものに
// 計時の前後を足す。Ticker の外 (本編の流れの中) で走る mod の処理がどこで重いかを、関数ごとの合計・最大・回数で出す。
// 測る間は呼び出しのたびに辞書を引き、先回りのコンパイルも遅くなる。外すと試合中に固まったことがあるので、
// on はメニューで打ち、普段の計測は起動し直してから。
internal static class PatchProf
{
    private const string Id = "mrp.patchprof";
    private static Harmony _h;
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private sealed class Stat { public string Name; public bool Mod; public long Sum, Max; public int Calls; }
    private static readonly Dictionary<MethodBase, Stat> Stats = new();

    public static void Register()
    {
        TestBridge.Register("patchprof", "[on | reset | 件数=20] この mod のパッチの重さ: 本編の関数 (パッチ込み) とパッチの関数ごとの合計・最大・回数 (on はメニューで・外せない)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "on") { reply(On()); return; }
            if (a == "reset") { foreach (var s in Stats.Values) { s.Sum = s.Max = 0; s.Calls = 0; } reply("OK patchprof reset"); return; }
            int n = int.TryParse(a, out int k) ? k : 20;
            foreach (var line in Report(n)) reply(line);
            reply("OK patchprof");
        });
    }

    private static string On()
    {
        if (_h != null) return "OK patchprof already on";
        _h = new Harmony(Id);
        var pre = new HarmonyMethod(typeof(PatchProf), nameof(Pre)) { priority = Priority.First + 100 };
        var post = new HarmonyMethod(typeof(PatchProf), nameof(Post)) { priority = Priority.Last - 100 };
        int orig = 0, mine = 0, fail = 0;
        var targets = Harmony.GetAllPatchedMethods().ToList();
        var patchMethods = new HashSet<MethodInfo>();
        foreach (var m in targets)
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null || !info.Owners.Contains(Plugin.Guid)) continue;
            foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                if (p.owner == Plugin.Guid) patchMethods.Add(p.PatchMethod);
            if (m.IsGenericMethodDefinition) continue;
            try { _h.Patch(m, pre, post); Stats[m] = new Stat { Name = $"{m.DeclaringType?.Name}.{m.Name}" }; orig++; }
            catch (Exception e) { fail++; Plugin.Logger.LogWarning($"[patchprof] {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }
        foreach (var m in patchMethods)
        {
            try { _h.Patch(m, pre, post); Stats[m] = new Stat { Name = $"{m.DeclaringType?.Name}.{m.Name}", Mod = true }; mine++; }
            catch (Exception e) { fail++; Plugin.Logger.LogWarning($"[patchprof] {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }
        return $"OK patchprof on originals={orig} patches={mine} failed={fail}";
    }

    private static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Post(long __state, MethodBase __originalMethod)
    {
        long d = Stopwatch.GetTimestamp() - __state;
        if (__originalMethod == null || !Stats.TryGetValue(__originalMethod, out var s)) return;
        s.Sum += d;
        s.Calls++;
        if (d > s.Max) s.Max = d;
    }

    private static IEnumerable<string> Report(int n)
    {
        foreach (bool mod in new[] { false, true })
        {
            var list = Stats.Values.Where(s => s.Mod == mod && s.Calls > 0).OrderByDescending(s => s.Max).Take(n).ToList();
            yield return mod ? "PATCHPROF patch methods (mod only) by max:" : "PATCHPROF originals (vanilla + patches) by max:";
            foreach (var s in list)
                yield return $"PATCHPROF   {s.Name} max={s.Max * MsPerTick:0.00} sum={s.Sum * MsPerTick:0.0} n={s.Calls}";
        }
    }
}
