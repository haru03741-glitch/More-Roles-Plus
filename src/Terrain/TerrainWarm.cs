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
    private static readonly double[] StageMs = new double[14];
    private static int _methods;
    private static double _maxTick;
    private static bool _fxWarm; // 段 9 の火と音の準備を済ませた

    public static string Report =>
        $"stage={_stage} jit={StageMs[0]:F1}ms ({_methods} methods, max {_maxTick:F1}ms/tick) map={StageMs[1]:F1} ({DamageMap.EnsureBreakdown}) art={StageMs[2]:F1} sites={StageMs[3]:F1} bake={StageMs[4]:F1} solid={StageMs[5]:F1}+{StageMs[6]:F1} props={StageMs[7]:F1} breakables+low={StageMs[8]:F1} decompfx={StageMs[9]:F1} water={StageMs[10]:F1} edges={StageMs[11]:F1} floor={StageMs[12]:F1} gc={StageMs[13]:F1}";

    public static void Tick()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (_ship != ship) { _ship = ship; _stage = 0; _at = 0; _methods = 0; _maxTick = 0; _fxWarm = false; Array.Clear(StageMs, 0, StageMs.Length); }
        if (_stage >= 14 || !Fx.MrpBundle.Ready) return;

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
                case 5: SolidMap.Ensure(firstHalf: true); break; // 歩ける所の地図は 2 フレームに分ける (前半 = 壁の線と塗り・後半 = 船体と船の外)
                case 6: SolidMap.Ensure(); break;
                case 7: PropSim.Warm(); break; // 歩ける所の地図の次のフレーム (同じフレームに積むと 1 回の止まりが長い)
                case 8: BreakableProps.Warm(); LowFurniture.Ensure(); break; // 低い家具は壊れる家具を集めてから数える (PropSim と同じフレームに積むと PC でも 20ms を超える)
                case 9:
                    if (!_fxWarm) { FireFx.Warm(); BreakNoise.Warm(); _fxWarm = true; }
                    // 減圧の粒は 1 回に TickBudgetMs まで (残りは次の FixedUpdate)
                    if (!DecompFx.WarmSome(Clock, TickBudgetMs)) { StageMs[9] += Clock.Elapsed.TotalMilliseconds; _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds); return; }
                    break;
                case 10: WaterSim.Warm(); break;
                case 11: WaterSim.WarmEdges(); break;
                case 12: _ = FloorMask.Active; break; // 水の床の形 (最初の水のタイルを描く時に読むと端末で 50ms 級)
                // 準備で出たごみを今まとめて回収し、最初の破壊や水の作り直しの途中で全体の回収が走らないようにする
                case 13: GC.Collect(); break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] stage {_stage}: {e.Message}"); }
        StageMs[_stage] += Clock.Elapsed.TotalMilliseconds;
        _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds);
        _stage++;
        if (_stage == 14) Plugin.Logger.LogInfo($"[TerrainWarm] {Report}");
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
