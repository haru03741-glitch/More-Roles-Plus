using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
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
    private static int _at;
    private static readonly Stopwatch Clock = new();
    private static readonly double[] StageMs = new double[16];
    private static int _methods;
    private static double _maxTick;
    private static bool _fxWarm; // 段 9 の火と音の準備を済ませた

    public static string Report =>
        $"stage={_stage} jit={StageMs[0]:F1}ms ({_methods} methods, max {_maxTick:F1}ms/tick) map={StageMs[1]:F1} ({DamageMap.EnsureBreakdown}) art={StageMs[2]:F1} sites={StageMs[3]:F1} bake={StageMs[4]:F1} solid={StageMs[5]:F1}+{StageMs[6]:F1} props={StageMs[7]:F1} breakables+low={StageMs[8]:F1} decompfx={StageMs[9]:F1} water={StageMs[10]:F1} edges={StageMs[11]:F1} floor={StageMs[12]:F1} grid={StageMs[13]:F1}+{StageMs[14]:F1} gc={StageMs[15]:F1}";

    public static void Tick()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (_ship != ship) { _ship = ship; _stage = 0; _at = 0; _methods = 0; _maxTick = 0; _fxWarm = false; Array.Clear(StageMs, 0, StageMs.Length); }
        if (_stage >= 16 || !Fx.MrpBundle.Ready) return;

        Clock.Restart();
        try
        {
            switch (_stage)
            {
                case 0:
                    if (!PrepareSome()) { StageMs[0] += Clock.Elapsed.TotalMilliseconds; _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds); return; }
                    // 中身を読んだ回は、読んだごみの回収を次の段 (絵を作る重い段) に重ねないよう、ここで 1 tick 使って済ませる
                    if (_scanGarbage)
                    {
                        if (_gcNext) { GC.Collect(); _scanGarbage = false; }
                        _gcNext = !_gcNext;
                        StageMs[0] += Clock.Elapsed.TotalMilliseconds;
                        _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds);
                        return;
                    }
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
                case 13: WaterSim.WarmBase(firstHalf: true); break; // 水の升の土台 (最初の爆破の時に作ると 1 発目だけ重い)。升と高さ
                case 14: WaterSim.WarmBase(); break; // 落ちる縁
                case 15: GC.Collect(); break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] stage {_stage}: {e.Message}"); }
        StageMs[_stage] += Clock.Elapsed.TotalMilliseconds;
        _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds);
        _stage++;
        if (_stage == 16) Plugin.Logger.LogInfo($"[TerrainWarm] {Report}");
    }

    // 地形のメソッドを先にコンパイルする (1 回に TickBudgetMs まで・メソッド単位)。全部終わったら true。
    // 地形のメソッドが呼ぶ先 (値型を入れる List/Dictionary・本編と Unity の包み・Fx などの別の名前空間) も、
    // メソッドの中身 (IL) から拾って足す。値型のジェネリックとこの mod のメソッドはさらに奥 (MaxDepth 段) まで辿る
    // (拾わないと、試合で最初の爆破の時に 200 件以上をその場でコンパイルしていた)
    private const int MaxDepth = 4;
    private const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static List<(MethodBase M, int Depth)> _list;
    private static HashSet<MethodBase> _seen;
    private static int _scanned; // 中身を読み終えた所まで (2 試合目からは読まない)
    private static bool _scanGarbage, _gcNext;
    private static bool PrepareSome()
    {
        if (_list == null)
        {
            _list = new List<(MethodBase, int)>();
            _seen = new HashSet<MethodBase>();
            foreach (var t in typeof(TerrainWarm).Assembly.GetTypes())
            {
                if (t.Namespace != typeof(TerrainWarm).Namespace || t.ContainsGenericParameters) continue;
                foreach (var m in t.GetMethods(AllDeclared)) Add(m, 0);
                foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) Add(c, 0);
            }
        }
        for (; _at < _list.Count; _at++)
        {
            if (Clock.Elapsed.TotalMilliseconds > TickBudgetMs) return false;
            var (m, depth) = _list[_at];
            try { Prepare(m); _methods++; }
            catch { /* 準備できないものは初回の呼び出しでコンパイルされる (従来どおり) */ }
            if (_at < _scanned) continue;
            _scanned = _at + 1;
            if (depth < MaxDepth && (depth == 0 || Deeper(m)))
            {
                _scanGarbage = true;
                try { AddCallees(m, depth + 1); }
                catch { /* 読めない中身は辿らない */ }
            }
        }
        _seen = null; // 読み終えた (2 試合目からは足さない)
        return true;
    }

    private static void Add(MethodBase m, int depth)
    {
        if (m == null || m.IsAbstract || m.ContainsGenericParameters || (m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0
            || (m.Attributes & MethodAttributes.PinvokeImpl) != 0 || m.DeclaringType == null) return;
        if (_seen != null && _seen.Add(m)) _list.Add((m, depth));
    }

    // 奥まで辿る: この mod のメソッドと、値型を型引数に持つジェネリック (型ごとに別にコンパイルされる)
    private static bool Deeper(MethodBase m)
    {
        var t = m.DeclaringType;
        if (t.Assembly == typeof(TerrainWarm).Assembly) return true;
        if (t.IsGenericType) foreach (var g in t.GetGenericArguments()) if (g.IsValueType) return true;
        if (m.IsGenericMethod) foreach (var g in m.GetGenericArguments()) if (g.IsValueType) return true;
        return false;
    }

    private static void Prepare(MethodBase m)
    {
        var t = m.DeclaringType;
        if (!t.IsGenericType && !m.IsGenericMethod) { RuntimeHelpers.PrepareMethod(m.MethodHandle); return; }
        var inst = new List<RuntimeTypeHandle>();
        if (t.IsGenericType) foreach (var g in t.GetGenericArguments()) inst.Add(g.TypeHandle);
        if (m.IsGenericMethod) foreach (var g in m.GetGenericArguments()) inst.Add(g.TypeHandle);
        RuntimeHelpers.PrepareMethod(m.MethodHandle, inst.ToArray());
    }

    // 中身の call / callvirt / newobj / ldftn の行き先
    private static void AddCallees(MethodBase m, int depth)
    {
        var il = m.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return;
        var t = m.DeclaringType;
        var targs = t.IsGenericType ? t.GetGenericArguments() : null;
        var margs = m.IsGenericMethod ? m.GetGenericArguments() : null;
        for (int i = 0; i < il.Length;)
        {
            int op = il[i++];
            if (op == 0xFE) op = 0x100 | il[i++];
            var kind = OperandOf(op);
            if (kind == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, i);
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(token, targs, margs); }
                catch { }
                Add(callee, depth);
            }
            i += kind switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
    }

    // 命令の番号 → 後ろに付く値の種類 (1 バイト命令は 0..255・0xFE xx は 0x100 | xx)
    private static OperandType[] _operands;
    private static OperandType OperandOf(int op)
    {
        if (_operands == null)
        {
            _operands = new OperandType[0x200];
            Array.Fill(_operands, OperandType.InlineNone);
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode c) continue;
                int v = (ushort)c.Value;
                _operands[c.Size == 1 ? v & 0xFF : 0x100 | (v & 0xFF)] = c.OperandType;
            }
        }
        return op < _operands.Length ? _operands[op] : OperandType.InlineNone;
    }
}
