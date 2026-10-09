using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MoreRolesPlus.Terrain;

// 試合 (船) が出たら、最初の破壊で払っていた準備 (PC 実測 365ms) を先に済ませる。
// 地形のメソッドを先にコンパイルし (初回の呼び出しのコンパイルが準備の大半だった)、損傷マスク・瓦礫/ひび/船体の中の絵・種点の板・焼くカメラを作っておく。
// 1 回の FixedUpdate に 1 段ずつ (コンパイルは 4ms ずつ) に分けて、1 フレームを長く止めない。
internal static class TerrainWarm
{
    private const double TickBudgetMs = 4.0;
    private static ShipStatus _ship;
    private static int _stage;
    private static List<RuntimeMethodHandle> _list;
    private static int _at;
    private static readonly Stopwatch Clock = new();
    private static readonly double[] StageMs = new double[9];
    private static int _methods;
    private static double _maxTick;

    public static string Report =>
        $"stage={_stage} jit={StageMs[0]:F1}ms ({_methods} methods, max {_maxTick:F1}ms/tick) map={StageMs[1]:F1} ({DamageMap.EnsureBreakdown}) art={StageMs[2]:F1} sites={StageMs[3]:F1} bake={StageMs[4]:F1} solid={StageMs[5]:F1} props={StageMs[6]:F1} breakables={StageMs[7]:F1} decompfx={StageMs[8]:F1}";

    public static void Tick()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (_ship != ship) { _ship = ship; _stage = 0; _at = 0; _methods = 0; _maxTick = 0; Array.Clear(StageMs, 0, StageMs.Length); }
        if (_stage >= 9 || !Fx.MrpBundle.Ready) return;

        Clock.Restart();
        try
        {
            switch (_stage)
            {
                case 0:
                    if (!PrepareSome()) { StageMs[0] += Clock.Elapsed.TotalMilliseconds; _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds); return; }
                    break;
                case 1: DamageMap.Ready(); break;
                case 2:
                    DebrisArt.Chunk(0); DebrisArt.Plate(0); DebrisArt.Pipe(0); DebrisArt.Wire(0); DebrisArt.Core(0);
                    _ = DebrisArt.Nut; _ = DebrisArt.Stain; _ = DebrisArt.Pebble; _ = DebrisArt.Puff; _ = DebrisArt.Spark;
                    DamageMap.WarmArt();
                    TerrainFx.WarmFlash();
                    break;
                case 3: FractureSites.Warm(); break;
                case 4: RubbleBake.Warm(); ShadowPatch.Warm(); break;
                case 5: SolidMap.Ensure(); break;
                case 6: PropSim.Warm(); break; // 歩ける所の地図の次のフレーム (同じフレームに積むと 1 回の止まりが長い)
                case 7: BreakableProps.Warm(); break;
                case 8: DecompFx.Warm(); break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] stage {_stage}: {e.Message}"); }
        StageMs[_stage] += Clock.Elapsed.TotalMilliseconds;
        _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds);
        _stage++;
        if (_stage == 9) Plugin.Logger.LogInfo($"[TerrainWarm] {Report}");
    }

    // 地形のメソッドを先にコンパイルする (1 回に TickBudgetMs まで・メソッド単位)。全部終わったら true
    private static bool PrepareSome()
    {
        if (_list == null)
        {
            _list = new List<RuntimeMethodHandle>();
            foreach (var t in typeof(TerrainWarm).Assembly.GetTypes())
            {
                if (t.Namespace != typeof(TerrainWarm).Namespace || t.ContainsGenericParameters) continue;
                foreach (var m in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    if (!m.IsAbstract && !m.ContainsGenericParameters) _list.Add(m.MethodHandle);
            }
        }
        for (; _at < _list.Count; _at++)
        {
            if (Clock.Elapsed.TotalMilliseconds > TickBudgetMs) return false;
            try { RuntimeHelpers.PrepareMethod(_list[_at]); _methods++; }
            catch { /* 準備できないものは初回の呼び出しでコンパイルされる (従来どおり) */ }
        }
        return true;
    }
}
