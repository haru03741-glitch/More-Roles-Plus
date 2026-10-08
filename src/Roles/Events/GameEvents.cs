// Based on the event design of https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Game/Operation/GameOperatorManager.cs (GPL-3.0)
// and https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Modules/Events/Bases/EventTargetBase.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Reflection;

namespace MoreRolesPlus.Roles;

// 試合で起きた事。役職クラスに「引数がこの型 1 つのメソッド」を書くだけで、その役職が付いている間呼ばれる。
//   [OnlyMine] void OnExiled(PlayerExiledEvent e) => GameEnd.Win(this);
// 値を返す問い合わせ (視界・キルの待ち時間・相乗り) はイベントでなく RoleBase の override で書く。
public abstract class GameEvent { }

// 誰か 1 人についての出来事 ([OnlyMine] で自分の時だけ受けられる)
public interface IPlayerEvent
{
    byte PlayerId { get; }
}

// 自分 (役職の持ち主) についての出来事の時だけ呼ぶ
[AttributeUsage(AttributeTargets.Method)]
public sealed class OnlyMineAttribute : Attribute { }

// 役職の持ち主の端末でだけ呼ぶ (画面の表示など)
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalOnlyAttribute : Attribute { }

// ホストの端末でだけ呼ぶ (勝敗・判定など)。呼ばれる時のホストで決める (ホストが替わっても新しいホストで呼ばれる)
[AttributeUsage(AttributeTargets.Method)]
public sealed class HostOnlyAttribute : Attribute { }

// 同じイベントを受ける物の中で先に呼ぶ (大きいほど先・既定 0)
[AttributeUsage(AttributeTargets.Method)]
public sealed class PriorityAttribute : Attribute
{
    public readonly int Value;
    public PriorityAttribute(int value) => Value = value;
}

internal interface IEventStats
{
    string Name { get; }
    int Live { get; }
    long Fired { get; }
}

// イベント 1 種類の受け手の一覧。受け手の追加・削除は配列を作り直す (配信は今の配列をそのまま回すだけで割り当て無し)。
// 寿命の切れた受け手は配信の時に飛ばし、外側の配信が終わってから取り除く。
public static class Events<T> where T : GameEvent
{
    private struct Entry
    {
        public Action<T> Fn;
        public Lifespan Life;
        public int Priority;
        public string Owner;
    }

    private static Entry[] _list = Array.Empty<Entry>();
    private static int _depth;
    private static bool _dirty;
    private static long _fired;

    static Events() => EventStats.Add(new Stats());

    public static void Subscribe(Action<T> fn, Lifespan life, int priority = 0, string owner = null)
    {
        if (life.IsDead) return;
        // 作り直すついでに寿命の切れた受け手を除く (起きないまま試合が終わるイベントにも溜めない)。配信中は除かない
        var next = new List<Entry>(_list.Length + 1);
        var add = new Entry { Fn = fn, Life = life, Priority = priority, Owner = owner ?? fn.Method.Name };
        bool added = false;
        foreach (var e in _list)
        {
            if (!added && e.Priority < priority) { next.Add(add); added = true; }
            if (_depth == 0 && e.Life.IsDead) continue;
            next.Add(e);
        }
        if (!added) next.Add(add);
        _list = next.ToArray();
    }

    public static T Run(T ev)
    {
        _fired++;
        var list = _list;
        if (list.Length == 0) return ev;
        _depth++;
        try
        {
            for (int i = 0; i < list.Length; i++)
            {
                ref var e = ref list[i];
                if (e.Life.IsDead) { _dirty = true; continue; }
                try { e.Fn(ev); }
                catch (Exception ex) { Plugin.Logger.LogError($"{e.Owner}({typeof(T).Name}): {ex}"); }
            }
        }
        finally
        {
            if (--_depth == 0 && _dirty) Compact();
        }
        return ev;
    }

    private static void Compact()
    {
        _dirty = false;
        var live = new List<Entry>(_list.Length);
        foreach (var e in _list) if (!e.Life.IsDead) live.Add(e);
        _list = live.ToArray();
    }

    private sealed class Stats : IEventStats
    {
        public string Name => typeof(T).Name;
        public int Live { get { int n = 0; foreach (var e in _list) if (!e.Life.IsDead) n++; return n; } }
        public long Fired => _fired;
    }
}

internal static class EventStats
{
    public static readonly List<IEventStats> All = new();
    public static void Add(IEventStats s) => All.Add(s);
}

// 役職クラスの「引数がイベント 1 つのメソッド」を集めて、役職の寿命で購読する。反射は型ごとに 1 回 (Registry.Init で済ませる)
internal static class EventBinder
{
    private sealed class Binding
    {
        public MethodInfo Method;
        public Action<Assignable, MethodInfo, Binding> Add;
        public bool Mine, Local, Host;
        public int Priority;
    }

    private static readonly Dictionary<Type, Binding[]> ByRole = new();
    private static readonly Dictionary<Type, Action<Assignable, MethodInfo, Binding>> Adders = new();
    private static readonly MethodInfo AddGeneric = typeof(EventBinder).GetMethod(nameof(Add), BindingFlags.Static | BindingFlags.NonPublic);

    public static void Prepare(Type roleType)
    {
        if (ByRole.ContainsKey(roleType)) return;
        var list = new List<Binding>();
        for (var t = roleType; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if (ps.Length != 1 || m.ReturnType != typeof(void) || m.Name.StartsWith('<')) continue;
                var ev = ps[0].ParameterType;
                if (!typeof(GameEvent).IsAssignableFrom(ev) || ev.IsAbstract) continue;
                var b = new Binding
                {
                    Method = m,
                    Mine = m.GetCustomAttribute<OnlyMineAttribute>() != null,
                    Local = m.GetCustomAttribute<LocalOnlyAttribute>() != null,
                    Host = m.GetCustomAttribute<HostOnlyAttribute>() != null,
                    Priority = m.GetCustomAttribute<PriorityAttribute>()?.Value ?? 0,
                };
                if (b.Mine && !typeof(IPlayerEvent).IsAssignableFrom(ev))
                {
                    Plugin.Logger.LogError($"{roleType.Name}.{m.Name}: [OnlyMine] needs an event about one player ({ev.Name}) — ignored");
                    continue;
                }
                if (!Adders.TryGetValue(ev, out var add))
                    Adders[ev] = add = AddGeneric.MakeGenericMethod(ev).CreateDelegate<Action<Assignable, MethodInfo, Binding>>();
                b.Add = add;
                list.Add(b);
            }
        }
        ByRole[roleType] = list.ToArray();
    }

    // 割り当てた直後に呼ぶ (role.Lifespan が付いていること)
    public static void Bind(Assignable role)
    {
        var t = role.GetType();
        Prepare(t);
        foreach (var b in ByRole[t])
        {
            if (b.Local && !role.IsLocal) continue;
            b.Add(role, b.Method, b);
        }
    }

    private static void Add<E>(Assignable role, MethodInfo m, Binding b) where E : GameEvent
    {
        var fn = m.CreateDelegate<Action<E>>(role);
        if (b.Mine)
        {
            var inner = fn;
            byte pid = role.PlayerId;
            fn = e => { if (((IPlayerEvent)e).PlayerId == pid) inner(e); };
        }
        if (b.Host)
        {
            var inner = fn;
            fn = e => { if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) inner(e); };
        }
        Events<E>.Subscribe(fn, role.Lifespan, b.Priority, $"{role.Id}.{m.Name}");
    }
}
