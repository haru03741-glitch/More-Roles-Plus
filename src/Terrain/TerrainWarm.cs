using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace MoreRolesPlus.Terrain;

// 試合 (船) が出たら、最初の破壊で払っていた準備 (PC 実測 365ms) を先に済ませる。
// この mod のメソッドを先にコンパイルし (初回の呼び出しのコンパイルが準備の大半だった)、損傷マスク・瓦礫/ひび/船体の中の絵・種点の板・焼くカメラを作っておく。
// コンパイルはプロセスに 1 回で済むので、メニューに着いた後 (起動の処理が終わってから) に始める。メニューで終わらなければ
// ロビーと試合でも続け、船ごとの準備 (段 1〜) はコンパイルが済んでから始める。
// コンパイルは 1 回の FixedUpdate に 4ms ずつ (メニューでは MenuBudgetMs ずつ) に分けて、1 フレームを長く止めない。
// 船ごとの準備は止まっても見えない瞬間にまとめる (LateTick / OnIntro)。
// 地形を壊せる人がいない試合 (壊せる役職の出現率が全部 0・練習でもフリープレイでもない) では、コンパイルも船ごとの準備もしない
// (メモリと読み込みを使わない。設定画面から外して壊れた時は、その場で作る従来の道に戻る)。コンパイルは要る設定を見てから (ロビー) 始める。
internal static class TerrainWarm
{
    private const double TickBudgetMs = 4.0;
    private const double MenuBudgetMs = 6.0; // メニューでは動く物が少なく、少し長く止まっても目立たない
    private const int Stages = 17;
    private const int GcStage = 16;
    private static ShipStatus _ship;
    private static int _stage;
    private static int _at;
    private static readonly Stopwatch Clock = new();
    private static readonly double[] StageMs = new double[Stages];
    private static int _methods;
    private static double _maxTick, _preMatchMs;
    private static bool _jitDone;
    private static bool _fxWarm; // 段 9 の火と音の準備を済ませた

    public static string Report =>
        $"stage={_stage} jit={StageMs[0]:F1}ms (before match {_preMatchMs:F1}ms, {_methods} methods{(_jitDone ? "" : " running")}, max {_maxTick:F1}ms/tick) map={StageMs[1]:F1} ({DamageMap.EnsureBreakdown}) art={StageMs[2]:F1} sites={StageMs[3]:F1} bake={StageMs[4]:F1} solid={StageMs[5]:F1}+{StageMs[6]:F1} props={StageMs[7]:F1} breakables+low={StageMs[8]:F1} decompfx={StageMs[9]:F1} water={StageMs[10]:F1} edges={StageMs[11]:F1} floor={StageMs[12]:F1} grid={StageMs[13]:F1}+{StageMs[14]:F1} front={StageMs[15]:F1} gc={StageMs[16]:F1}";

    // 地形を壊せる人が出る試合か (練習・フリープレイは誰でも壊せる)
    internal static bool Needed()
    {
        if (TerrainPermits.SandboxOverride ?? Roles.PracticeMatch.Sandbox) return true;
        if (!Roles.RoleSettings.Enabled) return false;
        foreach (var r in Options.Registry.Roles)
            if (r.Chance != null && r.Chance.Value > 0 && r.TerrainPermit.Any) return true;
        return false;
    }

    // Needed を数 tick ごとに見直す (役職の一覧を毎 tick 回さない)
    private static int _needAt;
    private static bool _need;
    private static bool NeedNow()
    {
        if (--_needAt <= 0) { _needAt = 25; _need = Needed(); }
        return _need;
    }

    public static void Tick()
    {
        if (!Fx.MrpBundle.Ready) return;
        var ship = ShipStatus.Instance;
        if (!_jitDone)
        {
            if (!ship && !Boot.BootClock.StartupOver) return; // 起動の処理 (ログイン等) とは重ねない
            if (!ship && !LobbyBehaviour.Instance && !(TerrainPermits.SandboxOverride ?? Roles.PracticeMatch.Sandbox)) return; // メニューでは要るか分からない
            if (!NeedNow())
            {
                // 要らない試合: GC の先回しだけ済ませる
                if (ship && _ship != ship) { Begin(ship); _stage = GcStage; }
                if (ship && _stage < Stages && Environment.TickCount64 >= _holdUntil) Step(TickBudgetMs);
                return;
            }
            Clock.Restart();
            _jitDone = JitSome(ship || PlayerControl.LocalPlayer ? TickBudgetMs : MenuBudgetMs);
            double ms = Clock.Elapsed.TotalMilliseconds;
            if (ship) { StageMs[0] += ms; _maxTick = Math.Max(_maxTick, ms); }
            else _preMatchMs += ms;
            if (_jitDone) Plugin.Logger.LogInfo($"[TerrainWarm] jit done methods={_methods} beforeMatch={_preMatchMs:F1}ms inMatch={StageMs[0]:F1}ms");
            return;
        }
        if (!ship) return;
        if (_ship != ship) { Begin(ship); if (!Needed()) _stage = GcStage; }
        if (_stage < Stages && Environment.TickCount64 >= _holdUntil) Step(TickBudgetMs);
    }

    // 船ごとの準備は、止まっても見えない瞬間にまとめて済ませる:
    // - 船の読み込みで既に長く止まったフレーム (フリープレイは船を同期で読み、Airship は PC で 1.2 秒止まる) → そのフレームの LateUpdate。
    //   フリープレイはイントロが無いので、止まりが短くてもここで済ませる
    // - 船を裏で読む試合 (オンライン・ローカルの部屋) → 船は「シーッ！」の動きの途中に出るので、イントロの頭 (本編の画面が切り替わる所・
    //   GC の先回しと同じ瞬間) まで待つ。イントロが来なければ HoldMs で 1 段ずつに戻る
    // 次のフレームから 1 段ずつ回すと、短い止まりが十数回続いた。コンパイルがまだ終わっていなければ従来どおり 1 段ずつ
    private const double LongFrameSec = 0.25;
    private const long HoldMs = 4000;
    private static bool _shipStarted;
    private static long _holdUntil, _introAt = long.MinValue / 2;
    internal static void OnShipStart() => _shipStarted = true;

    public static void LateTick()
    {
        if (!_shipStarted) return;
        _shipStarted = false;
        try { LateTickCore(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] ship frame: {e.Message}"); }
    }

    private static void LateTickCore()
    {
        if (!Fx.MrpBundle.Ready) return;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        bool need = Needed();
        if (!_jitDone && need) return;
        GameClock.Tick(); // 船の世代を今の船にそろえてから作る (準備の作り置きは世代で見分ける)
        if (_ship != ship) { Begin(ship); if (!need) _stage = GcStage; }
        var client = AmongUsClient.Instance;
        bool freePlay = client && client.NetworkMode == NetworkModes.FreePlay; // フリープレイはイントロが無い
        if (freePlay || UnityEngine.Time.unscaledDeltaTime >= LongFrameSec) RunAll();
    }

    // イントロの頭 (IntroCutscene.CoBegin の前置き・GC の先回しの直前) から呼ぶ
    internal static void OnIntro()
    {
        _introAt = Environment.TickCount64;
        _holdUntil = 0;
        try { OnIntroCore(); } // 本編のイントロの入口なので、ここから例外を出さない
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] intro: {e.Message}"); }
    }

    private static void OnIntroCore()
    {
        if (!Fx.MrpBundle.Ready) return;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        bool need = Needed();
        if (!_jitDone && need) return;
        GameClock.Tick();
        if (_ship != ship) { Begin(ship); if (!need) _stage = GcStage; }
        RunAll();
    }

    private static void RunAll()
    {
        // 段が進まなくなっても止まらないように回数に上限 (減圧の粒の段は予算なしなら 1 回で終わる)
        for (int guard = 0; _stage < Stages && guard < Stages * 4; guard++) Step(double.MaxValue);
    }

    private static void Begin(ShipStatus ship)
    {
        long now = Environment.TickCount64;
        _holdUntil = now - _introAt > 10000 ? now + HoldMs : 0; // イントロを待つ (始まった後に船が出たなら待たない)
        _ship = ship;
        _stage = 1;
        _maxTick = 0;
        _fxWarm = false;
        Array.Clear(StageMs, 1, Stages - 1);
    }

    // 今の段を 1 つ進める (減圧の粒だけは budgetMs ごとに分けて、終わるまで同じ段に留まる)
    private static void Step(double budgetMs)
    {
        Clock.Restart();
        try
        {
            switch (_stage)
            {
                case 1: DamageMap.Ready(); break;
                case 2:
                    DebrisArt.Chunk(0); DebrisArt.Plate(0); DebrisArt.Pipe(0); DebrisArt.Wire(0); DebrisArt.Core(0);
                    _ = DebrisArt.Nut; _ = DebrisArt.Stain; _ = DebrisArt.Pebble; _ = DebrisArt.Puff; _ = DebrisArt.Spark;
                    DamageMap.WarmArt();
                    TerrainFx.WarmFlash();
                    TerrainFx.WarmPool(48); // 爆発 2 回分の火花・閃光・煙
                    break;
                case 3: FractureSites.Warm(); break;
                case 4: RubbleBake.Warm(); ShadowPatch.Warm(); break;
                case 5: SolidMap.Ensure(firstHalf: true); break; // 歩ける所の地図は 2 段に分ける (前半 = 壁の線と塗り・後半 = 船体と船の外)
                case 6: SolidMap.Ensure(); break;
                case 7: PropSim.Warm(); break; // 歩ける所の地図の次の段
                case 8: BreakableProps.Warm(); LowFurniture.Ensure(); break; // 低い家具は壊れる家具を集めてから数える
                case 9:
                    if (!_fxWarm) { FireFx.Warm(); BreakNoise.Warm(); _fxWarm = true; }
                    if (!DecompFx.WarmSome(Clock, budgetMs)) { StageMs[9] += Clock.Elapsed.TotalMilliseconds; _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds); return; }
                    break;
                case 10: WaterSim.Warm(); break;
                case 11: WaterSim.WarmEdges(); break;
                case 12: _ = FloorMask.Active; break; // 水の床の形 (最初の水のタイルを描く時に読むと端末で 50ms 級)
                case 13: WaterSim.WarmBase(firstHalf: true); break; // 水の升の土台 (最初の爆破の時に作ると 1 発目だけ重い)。升と高さ
                case 14: WaterSim.WarmBase(); break; // 落ちる縁
                // 準備で出たごみを今まとめて回収し、最初の破壊や水の作り直しの途中で全体の回収が走らないようにする。
                // 本編の側 (il2cpp) も回す: 船の読み込みのごみで、直後に本編の GC が 60ms 級で走っていた
                case 15: PropSim.WarmFront(); break; // 手前の絵の水面の板の対象 (最初に水が来た時に集めると Airship で 11ms)
                case 16: if (Plugin.PreemptiveGc.Value) Boot.GcPrepass.Collect("ship", force: true); else Boot.MemTrim.Collect(); break;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[TerrainWarm] stage {_stage}: {e.Message}"); }
        StageMs[_stage] += Clock.Elapsed.TotalMilliseconds;
        _maxTick = Math.Max(_maxTick, Clock.Elapsed.TotalMilliseconds);
        _stage++;
        if (_stage == Stages) Plugin.Logger.LogInfo($"[TerrainWarm] {Report}");
    }

    // 試合中に走るこの mod のメソッドを先にコンパイルする (1 回に budgetMs まで・メソッド単位)。全部終わったら true。
    // 呼ぶ先 (値型を入れる List/Dictionary・本編と Unity の包み・Fx などの別の名前空間) も、
    // メソッドの中身 (IL) から拾って足す。値型のジェネリックとこの mod のメソッドはさらに奥 (MaxDepth 段) まで辿る
    // (拾わないと、試合で最初の爆破の時に 200 件以上をその場でコンパイルしていた)
    private const int MaxDepth = 4;
    private const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static List<(MethodBase M, int Depth)> _list;
    private static HashSet<MethodBase> _seen;
    private static bool _scanGarbage, _gcNext;
    private static bool JitSome(double budgetMs)
    {
        // 中身を読み終えた回は、読んだごみの回収を 1 tick 使って済ませる (次の重い段に重ねない)
        if (_scanGarbage)
        {
            if (_gcNext) { Boot.MemTrim.Compact("jit"); _scanGarbage = false; _list = null; return true; }
            _gcNext = true;
            return false;
        }
        if (_list == null)
        {
            _list = new List<(MethodBase, int)>();
            _seen = new HashSet<MethodBase>();
            foreach (var t in typeof(TerrainWarm).Assembly.GetTypes())
            {
                if (!Root(t.Namespace) || t.ContainsGenericParameters) continue;
                foreach (var m in t.GetMethods(AllDeclared)) Add(m, 0);
                foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) Add(c, 0);
            }
        }
        for (; _at < _list.Count; _at++)
        {
            if (Clock.Elapsed.TotalMilliseconds > budgetMs) return false;
            var (m, depth) = _list[_at];
            try { Prepare(m); _methods++; }
            catch { /* 準備できないものは初回の呼び出しでコンパイルされる (従来どおり) */ }
            if (depth < MaxDepth && (depth == 0 || Deeper(m)))
            {
                try { AddCallees(m, depth + 1); }
                catch { /* 読めない中身は辿らない */ }
            }
        }
        _seen = null;
        _scanGarbage = true; // 次の tick から回収して終わる
        return false;
    }

    // 試合中に走る名前空間 (地形・役職・通信・演出・設定・チャット)。起動の処理と開発用の道具は除く
    private static bool Root(string ns) =>
        ns != null && ns.StartsWith("MoreRolesPlus", StringComparison.Ordinal)
        && !ns.StartsWith("MoreRolesPlus.Bridge", StringComparison.Ordinal) && !ns.StartsWith("MoreRolesPlus.Dev", StringComparison.Ordinal)
        && !ns.StartsWith("MoreRolesPlus.Boot", StringComparison.Ordinal);

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

[HarmonyLib.HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class TerrainWarmShipStartPatch
{
    public static void Postfix() => TerrainWarm.OnShipStart();
}
