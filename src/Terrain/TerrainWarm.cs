using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MoreRolesPlus.Terrain;

// 試合 (船) が出たら、最初の破壊で払っていた準備 (PC 実測 365ms) を先に済ませる。
// 損傷マスク・瓦礫/ひび/船体の中の絵・種点の板・焼くカメラを作り、地形のメソッドを先にコンパイルしておく。
// 1 回の FixedUpdate に 1 段ずつ (コンパイルは型 8 個ずつ) に分けて、1 フレームを長く止めない。
internal static class TerrainWarm
{
    private const int TypesPerTick = 8;
    private static ShipStatus _ship;
    private static int _stage;
    private static List<Type> _types;
    private static int _typeAt;
    private static readonly Stopwatch Clock = new();
    private static readonly double[] StageMs = new double[5];
    private static int _methods;

    public static string Report =>
        $"stage={_stage} map={StageMs[0]:F1} art={StageMs[1]:F1} sites={StageMs[2]:F1} bake={StageMs[3]:F1} jit={StageMs[4]:F1}ms ({_methods} methods)";

    public static void Tick()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (_ship != ship) { _ship = ship; _stage = 0; _typeAt = 0; _methods = 0; Array.Clear(StageMs, 0, StageMs.Length); }
        if (_stage >= 5 || !Fx.MrpBundle.Ready) return;

        Clock.Restart();
        try
        {
            switch (_stage)
            {
                case 0: DamageMap.Ready(); break;
                case 1:
                    DebrisArt.Chunk(0); DebrisArt.Plate(0); DebrisArt.Pipe(0); DebrisArt.Wire(0); DebrisArt.Core(0);
                    _ = DebrisArt.Nut; _ = DebrisArt.Stain; _ = DebrisArt.Pebble; _ = DebrisArt.Puff; _ = DebrisArt.Spark;
                    DamageMap.WarmArt();
                    break;
                case 2: FractureSites.Warm(); break;
                case 3: RubbleBake.Warm(); break;
                case 4:
                    if (!PrepareSome()) { StageMs[4] += Clock.Elapsed.TotalMilliseconds; return; }
                    break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] stage {_stage}: {e.Message}"); }
        StageMs[_stage] += Clock.Elapsed.TotalMilliseconds;
        _stage++;
        if (_stage == 5) Plugin.Logger.LogInfo($"[TerrainWarm] {Report}");
    }

    // 地形の型のメソッドを先にコンパイルする。全部終わったら true
    private static bool PrepareSome()
    {
        if (_types == null)
        {
            _types = new List<Type>();
            foreach (var t in typeof(TerrainWarm).Assembly.GetTypes())
                if (t.Namespace == typeof(TerrainWarm).Namespace && !t.ContainsGenericParameters) _types.Add(t);
        }
        int end = Math.Min(_types.Count, _typeAt + TypesPerTick);
        for (; _typeAt < end; _typeAt++)
            foreach (var m in _types[_typeAt].GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); _methods++; }
                catch { /* 準備できないものは初回の呼び出しでコンパイルされる (従来どおり) */ }
            }
        return _typeAt >= _types.Count;
    }
}
