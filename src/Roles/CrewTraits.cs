using System;

namespace MoreRolesPlus.Roles;

// 役職が体に付ける、地形との関わり方。役職を受け取った時に全員の端末で同じ物を登録する (試合の終わりで消える)。
// Trace  = 見えなくなっても体はそこにあり、水しぶき・波紋・濡れた足跡・粉の足跡を残す
// Skim   = 水面を走る (浅い水で足を取られず、深い水でも泳がず、溺れない)
// Steady = 水の流れと外壁の穴の吸い出しに押されない
internal static class CrewTraits
{
    internal enum Trait : byte { Trace, Skim, Steady }

    private static readonly Func<bool>[][] On = { new Func<bool>[256], new Func<bool>[256], new Func<bool>[256] };
    private static int _count;
    private static object _match;

    internal static void Set(byte id, Trait t, Func<bool> on)
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(Clear);
        }
        if (On[(int)t][id] == null) _count++;
        On[(int)t][id] = on;
    }

    internal static bool Has(byte id, Trait t)
    {
        if (_count == 0) return false;
        var f = On[(int)t][id];
        if (f == null) return false;
        try { return f(); } catch { return false; }
    }

    // この端末で見えていない人も、体が地形に跡を残すか (見えている人はいつも残す)
    internal static bool LeavesTrace(PlayerControl pc) => pc.Visible || Has(pc.PlayerId, Trait.Trace);

    private static void Clear()
    {
        foreach (var a in On) Array.Clear(a, 0, a.Length);
        _count = 0;
        _match = null;
    }
}
