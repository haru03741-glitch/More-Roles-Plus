using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MoreRolesPlus.Roles;

// 時戻しで戻る値と出来事。役職を書く時は、この 2 つを使えば時戻しへの対応を個別に書かなくてよい:
//   値: 役職のフィールドを Tracked にする。時戻しのいる試合では 0.25 秒ごとに記録され、戻すと記録の時の値になる (各端末が自分の手元の値を戻す)
//     private readonly Tracked<float> _gauge = new(0f);
//     _gauge.Value += Time.fixedDeltaTime;
//   出来事: 起きた時に取り消し方を渡す。戻す区間の中で起きた物は、逆再生がその時刻を越えた時に新しい順に呼ばれる
//     Rewind.Undo(shift => RemoveMark(id));
//     shift = 戻したことで消える実時間 (ms)。「この時刻まで続く」物を戻す時は、その時刻に足すと残りが起きた時のままになる
//   戻し終わった後は Assignable.OnRewound が呼ばれる (戻った値から見た目を作り直す)。
// 能力の回数・ボタンの待ち時間・位置・生き死に・タスク・妨害・地形は時戻しが自分で戻すので、ここに入れなくてよい。
// 本編の電文で起きる事 (変身・透明など) は逆の操作が本編に無いので、その入口で Rewind.Undo を呼ぶ
public static class Rewind
{
    // 時戻しのいる試合で、記録している間か (逆再生の間と、取り消しを当てている間は記録しない)
    public static bool Recording => TimeRewind.On && !TimeRewind.Playing && !RewindJournal.Undoing;

    public static void Undo(Action<long> undo)
    {
        if (undo == null || !Recording) return;
        RewindJournal.Push(undo);
    }
}

// 時戻しで記録の時の値へ戻る値 (8 バイトまでの値の型: float・int・bool・long・Vector2 など)。
// 時戻しで戻したくない値は keep: true
public sealed class Tracked<T> : ITracked where T : unmanaged
{
    public T Value;
    private readonly bool _keep;

    public Tracked(T value = default, bool keep = false)
    {
        if (Unsafe.SizeOf<T>() > sizeof(long)) throw new ArgumentException($"Tracked<{typeof(T).Name}> は 8 バイトまで");
        Value = value;
        _keep = keep;
    }

    bool ITracked.Keep => _keep;

    long ITracked.Save()
    {
        long bits = 0;
        Unsafe.As<long, T>(ref bits) = Value;
        return bits;
    }

    void ITracked.Load(long bits) => Value = Unsafe.As<long, T>(ref bits);

    public static implicit operator T(Tracked<T> t) => t.Value;

    public override string ToString() => Value.ToString();
}

public interface ITracked
{
    bool Keep { get; }
    long Save();
    void Load(long bits);
}

// 役職のインスタンスが持つ Tracked のフィールドを集める (型ごとに 1 回だけリフレクションで探す)
internal static class TrackedFields
{
    private static readonly Dictionary<Type, FieldInfo[]> ByType = new();

    internal static ITracked[] Of(Assignable a)
    {
        var type = a.GetType();
        if (!ByType.TryGetValue(type, out var fields))
        {
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (typeof(ITracked).IsAssignableFrom(f.FieldType)) list.Add(f);
            fields = list.ToArray();
            ByType[type] = fields;
        }
        if (fields.Length == 0) return Array.Empty<ITracked>();
        var r = new List<ITracked>(fields.Length);
        foreach (var f in fields)
            if (f.GetValue(a) is ITracked v) r.Add(v);
        return r.ToArray();
    }
}

// 起きた事の取り消しの記録 (各端末が自分の手元で起きた事を、起きた時刻と一緒に積む)
internal static class RewindJournal
{
    private const long KeepMs = 26000; // 戻せる長さの上限 (秒数 15 + 押してからの 8) より長く持つ

    private readonly struct Entry
    {
        public readonly long Ms;
        public readonly Action<long> Undo;
        public Entry(long ms, Action<long> undo) { Ms = ms; Undo = undo; }
    }

    private static readonly List<Entry> List = new();
    private static long _shift;

    internal static bool Undoing { get; private set; }
    internal static int Count => List.Count;
    internal static int Undone;

    internal static void Push(Action<long> undo)
    {
        long now = Environment.TickCount64;
        int old = 0;
        while (old < List.Count && now - List[old].Ms > KeepMs) old++;
        if (old > 0) List.RemoveRange(0, old);
        List.Add(new Entry(now, undo));
    }

    internal static void Clear() => List.Clear();

    // 逆再生の始め: 戻し終わった時に消える実時間 (逆再生の終わり − 戻し先の時刻)
    internal static void Begin(long shiftMs) => _shift = shiftMs;

    // 世界の時刻 worldMs より後に起きた事を新しい順に取り消す
    internal static void UndoAfter(long worldMs)
    {
        while (List.Count > 0 && List[List.Count - 1].Ms > worldMs)
        {
            var e = List[List.Count - 1];
            List.RemoveAt(List.Count - 1);
            Undoing = true;
            try { e.Undo(_shift); }
            catch (Exception ex) { Plugin.Logger.LogError($"Rewind.Undo: {ex}"); }
            finally { Undoing = false; }
            Undone++;
        }
    }
}

// 逆再生の緩急 (実時間の進み → 世界の進み)。姿と音は小数で、地形の段は整数の表で同じ形をたどる
internal static class RewindClock
{
    private const int Steps = 64, One = 4096;
    private static readonly int[] Ease = BuildEase();

    // smoothstep(i/64) × 4096 (整数だけで作るので全員の端末で同じ)
    private static int[] BuildEase()
    {
        var a = new int[Steps + 1];
        for (long i = 0; i <= Steps; i++) a[i] = (int)((3 * i * i * Steps - 2 * i * i * i) * One / ((long)Steps * Steps * Steps));
        return a;
    }

    internal static float World(float r)
    {
        if (r <= 0f) return 0f;
        if (r >= 1f) return 1f;
        return r * r * (3f - 2f * r);
    }

    // 世界の進みが k/n に届く実時間の進み (64 分のいくつ)
    internal static int RealSteps(int k, int n)
    {
        for (int i = 0; i <= Steps; i++)
            if ((long)Ease[i] * n >= (long)k * One) return i;
        return Steps;
    }

    internal const int StepsTotal = Steps;
}
