using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 時戻し (地形の側): 水・火・減圧・物を数秒前の記録へ戻す。壁の形は TerrainUndo が件ごとに取り消す。
// 刻みは戻さない。戻す合図 (DamageKind.Rewind) は壊した結果と同じ連番の列で届き、刻み Tick に押されている。
// 各計算は自分の刻みの頭 (その刻みを進める前) で
//   - 刻みが Every の倍数なら今の中身を記録の輪へ写す
//   - 合図の刻みが来ていれば、Target 以前で一番新しい記録を読み戻す (中の刻みの値は 今の刻み − 記録の刻み だけ後へずらす)
// 全員の計算は同じ刻みで同じ中身なので、全員が同じ刻みに同じ記録を読み戻せば揃ったまま進む。
// 待っている出来事 (各計算の Queue) は読み戻さない (適用済みの出来事を二度効かせない)
internal static class TerrainRewind
{
    public const int Every = 15;          // 記録の間 (刻み) = 0.5 秒
    private const float SpareSeconds = 4f; // 戻す秒数に足して持つ分 (逆再生の最後の段は合図から最大 2 秒後に来る)
    private const int MaxSteps = 8;        // 逆再生の段の数の上限 (1 段 = 壁の取り消し + 各計算の読み戻し)
    private const int MinStepGap = 5;      // 段と段の間の刻み (前の段の作り直しの出来事を計算が先に済ませるだけ空ける)
    private const int Lead = 2;            // 段の刻みのこれだけ前に壁を取り消す (計算がその刻みを過ぎる前に出来事を積む)

    internal enum Sim { Decomp, Prop, Water, Fire, Count }

    private sealed class Order
    {
        public int Tick, Target;
        public readonly bool[] Done = new bool[(int)Sim.Count];
    }

    private static readonly List<Order> Orders = new();
    private static readonly List<(int Tick, int Target)> Plan = new(); // これから当てる逆再生の段 (刻みの順)
    private static bool _on;
    private static int _slots = 1, _shipGen = -1;

    // 確認用
    internal static int Rewinds { get; private set; }
    internal static int Late { get; private set; }
    internal static int Missing { get; private set; }
    internal static int Checked { get; private set; }
    internal static int Bad { get; private set; }
    // 写すのにかかった時間 (計算ごと・最後と最大 ms)
    private static readonly double[] SaveMs = new double[(int)Sim.Count], SaveMaxMs = new double[(int)Sim.Count];
    internal static string LastNote = "-";
    internal static int Walls; // 直前の合図で取り消した壁の件の数

    // 確認用: 記録の時の指紋を取っておき、読み戻した直後と比べる (自分の手元だけ・記録が重くなる)
    internal static bool SelfCheck;

    // 記録するか (各計算が刻みの頭で読む)
    internal static bool On => _on;
    // 逆再生の途中 (まだ当てていない段がある)。ホストはこの間の新しい破壊を受け付けない
    internal static bool Playing => Plan.Count > 0;

    // 逆再生の長さ (ms)。人の側 (TimeRewind) と同じ式で決める (電文に載せずに全員が揃う)
    internal static int PlayMs(int backMs) => Math.Clamp(800 + backMs / 10, 1200, 2000);
    internal static int Slots => _slots;

    // 時戻しがいる試合で、寿命が切れるまで記録する。seconds = 戻す秒数 (輪の長さを決める)
    internal static void Enable(Lifespan life, float seconds)
    {
        _slots = (int)Math.Ceiling((seconds + SpareSeconds) * GameClock.Hz / Every) + 1;
        if (_on) return;
        _on = true;
        Reset();
        TerrainUndo.Clear(); // 壁の記録は船ごとに自分で捨てる (ここの船の見張りは計算が動いてから初めて回るので、壊した後に記録を消してしまう)
        life.OnRelease(() => { _on = false; Reset(); TerrainUndo.Clear(); });
    }

    private static void Reset()
    {
        Orders.Clear();
        Plan.Clear();
        Rewinds = Late = Missing = Checked = Bad = 0;
        Array.Clear(SaveMs);
        Array.Clear(SaveMaxMs);
        LastNote = "-";
        WaterSim.RewindClear();
        FireSim.RewindClear();
        Decompression.RewindClear();
        PropSim.RewindClear();
        DamageMap.ClearScorchLog();
        FireFx.DropScorch();
    }

    // 各計算が刻みの頭で呼ぶ: 船が替わったら前の船の記録を捨てる
    internal static void CheckShip()
    {
        if (_shipGen == GameClock.ShipGen) return;
        _shipGen = GameClock.ShipGen;
        Reset();
    }

    // 戻す合図 (全員の手元・連番の順)。一度に戻さず、逆再生として段に分ける: 合図の刻み T から長さ P の間に N 段、
    // 段 k (刻み T + P·k/N) で「T − back·k/N より後」を取り消す。壁は新しい件から組み戻り、水・火などは記録を過去へ順に読み戻す。
    // 段の刻みと戻す所は合図の刻みと秒数だけから決まるので、全員が同じ刻みで同じ段を当てる
    internal static void OnApplied(in ResolvedDamage r)
    {
        if (!_on) return;
        CheckShip();
        FlushPlan(); // 前の逆再生の残り (続けて戻した時)
        int tick = GameClock.Expand(r.Tick), back = r.Seed;
        int play = PlayMs(back * 1000 / GameClock.Hz) * GameClock.Hz / 1000;
        int n = Math.Max(1, Math.Min(Math.Min((back + Every - 1) / Every, MaxSteps), play / MinStepGap));
        for (int k = 1; k <= n; k++)
            Plan.Add((tick + play * k / n, FloorDiv(tick - back * k / n, Every) * Every));
        // 戻す刻み以後のまだ処理していない出来事は、合図の時に 1 回だけ捨てる。段ごとに捨てると、計算は時計より遅れて進むので
        // 前の段の取り消しが積んだ作り直し (漏れの口を外すなど) を処理する前に捨ててしまう
        int last = Plan[Plan.Count - 1].Target;
        WaterSim.RewindPurge(last);
        FireSim.RewindPurge(last);
        Decompression.RewindPurge(last);
        PropSim.RewindPurge(last);
        Walls = 0;
        Rewinds++;
        RunDue();
    }

    // 毎フレーム (計算が刻みを進める前): 刻みが来た段を当てる
    internal static void RunDue()
    {
        if (Plan.Count == 0) return;
        try
        {
            int now = GameClock.Now;
            while (Plan.Count > 0 && Plan[0].Tick - Lead <= now) StepNext();
        }
        catch (Exception e) { Plugin.Logger.LogError($"[TerrainRewind] step: {e}"); }
    }

    // 残りの段を今すぐ全部当てる。逆再生の途中で別の破壊が届いた時は、その前に当て切る (取り消す件の順を端末ごとに割らない)
    internal static void FlushPlan()
    {
        if (Plan.Count == 0) return;
        try { while (Plan.Count > 0) StepNext(); }
        catch (Exception e) { Plugin.Logger.LogError($"[TerrainRewind] flush: {e}"); }
    }

    // 段 1 つ: その後に壊した壁を取り消し (取り消しは各計算へ段の刻みの作り直しを入れる)、各計算に記録の読み戻しを頼む
    private static void StepNext()
    {
        var (tick, target) = Plan[0];
        Plan.RemoveAt(0);
        var o = new Order { Tick = tick, Target = target };
        FireFx.DropScorch();
        DustCloud.RewindNewer((GameClock.Now - target) / (float)GameClock.Hz); // まだ地図へ書いていない焦げ (ほぼ戻す刻みより後に燃えた分)
        Walls += TerrainUndo.Rewind(target, tick);
        o.Done[(int)Sim.Decomp] = !Decompression.Running;
        o.Done[(int)Sim.Prop] = !PropSim.Drives;
        o.Done[(int)Sim.Water] = !WaterSim.Running;
        o.Done[(int)Sim.Fire] = !FireSim.Running;
        Orders.Add(o);
        Prune();
        if (Plan.Count > 0) return;
        FireFx.HidePuffs();
        WaterLeak.HideSpray();
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // 刻み step の頭で、この計算が戻すべき所 (無ければ false)。届くのが遅れて刻みを過ぎていたら、着いた刻みで戻して数える
    internal static bool Due(Sim sim, int step, out int target, out int lateBy)
    {
        target = lateBy = 0;
        if (Orders.Count == 0) return false;
        CheckShip();
        bool any = false;
        foreach (var o in Orders)
        {
            if (o.Done[(int)sim] || o.Tick > step) continue;
            o.Done[(int)sim] = true;
            target = o.Target;
            lateBy = step - o.Tick;
            any = true;
        }
        if (!any) return false;
        if (lateBy > 0) Late++;
        Prune();
        return true;
    }

    // 記録の輪に戻す所が無かった (記録を始める前・輪の外)
    internal static void NoteMissing(Sim sim, int target)
    {
        Missing++;
        LastNote = $"{sim} missing {target}";
    }

    internal static long SaveBegin() => System.Diagnostics.Stopwatch.GetTimestamp();

    internal static void SaveEnd(Sim sim, long t0)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        SaveMs[(int)sim] = ms;
        if (ms > SaveMaxMs[(int)sim]) SaveMaxMs[(int)sim] = ms;
    }

    // 読み戻した直後の指紋が記録の時と同じか (写し漏れの見張り)
    internal static void Check(Sim sim, uint want, uint got)
    {
        Checked++;
        if (want == got) return;
        Bad++;
        LastNote = $"{sim} digest {want:x8} != {got:x8}";
        Plugin.Logger.LogWarning($"[TerrainRewind] {LastNote}");
    }

    private static void Prune()
    {
        for (int i = Orders.Count - 1; i >= 0; i--)
        {
            var d = Orders[i].Done;
            if (d[0] && d[1] && d[2] && d[3]) Orders.RemoveAt(i);
        }
    }

    internal static string Probe() => $"on={_on} slots={_slots} every={Every} orders={Orders.Count} plan={Plan.Count} rewinds={Rewinds} late={Late} missing={Missing} check={(SelfCheck ? 1 : 0)} checked={Checked} bad={Bad} " +
        $"saved=w{WaterSim.RewindCount}/f{FireSim.RewindCount}/d{Decompression.RewindCount}/p{PropSim.RewindCount} saveMs=w{SaveMs[2]:0.000}/{SaveMaxMs[2]:0.000} f{SaveMs[3]:0.000}/{SaveMaxMs[3]:0.000} d{SaveMs[0]:0.000}/{SaveMaxMs[0]:0.000} p{SaveMs[1]:0.000}/{SaveMaxMs[1]:0.000} note={LastNote}";
}

// 刻みごとの記録の輪 (枠の物は使い回す)。T は自分の中身を写す入れ物
internal sealed class RewindRing<T> where T : class, new()
{
    private T[] _slot = Array.Empty<T>();
    private int[] _step = Array.Empty<int>();
    private int _head, _count;

    internal int Count => _count;

    // 刻みの頭で毎回: 記録が途切れていたら (計算が止まっていた間がある) 前の記録を捨てる。
    // 止まる前の記録は止まっていた間の中身ではない (止まっていた間は何も無かった)
    internal void Gap(int step)
    {
        int last = _lastHead;
        _lastHead = step;
        if (_count > 0 && (step - last > TerrainRewind.Every || step < last)) Clear();
    }
    private int _lastHead;

    // step の記録を書く枠 (一番古い枠を使い回す)。同じ刻みを 2 回写す時は同じ枠
    internal T Take(int step)
    {
        int cap = TerrainRewind.Slots;
        if (_slot.Length != cap) Resize(cap);
        if (_count > 0)
        {
            int last = (_head - 1 + cap) % cap;
            if (_step[last] == step) return _slot[last];
        }
        int s = _head;
        _slot[s] ??= new T();
        _step[s] = step;
        _head = (_head + 1) % cap;
        if (_count < cap) _count++;
        return _slot[s];
    }

    // target 以前で一番新しい記録と、その刻み (無ければ null)
    internal T Find(int target, out int step)
    {
        int cap = _slot.Length;
        for (int k = 1; k <= _count; k++)
        {
            int s = (_head - k + cap) % cap;
            if (_step[s] > target) continue;
            step = _step[s];
            return _slot[s];
        }
        step = 0;
        return null;
    }

    // step より後の記録を捨てる (戻した後は、戻した所より後の記録は無かったことになる)
    internal void DropAfter(int step)
    {
        int cap = _slot.Length;
        while (_count > 0 && _step[(_head - 1 + cap) % cap] > step)
        {
            _head = (_head - 1 + cap) % cap;
            _count--;
        }
    }

    internal void Clear() { _head = _count = 0; }

    private void Resize(int cap)
    {
        _slot = new T[cap];
        _step = new int[cap];
        _head = _count = 0;
    }
}
