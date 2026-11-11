using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職が体に付ける、地形との関わり方。役職を受け取った時に全員の端末で同じ物を登録する (試合の終わりで消える)。
// Trace  = 見えなくなっても体はそこにあり、水しぶき・波紋・濡れた足跡・粉の足跡を残す
// Skim   = 水面を走る (浅い水で足を取られず、深い水でも泳がず、溺れない)
// Steady = 水の流れと外壁の穴の吸い出しに押されない
// Fin    = 泳ぎが得意 (水の中でも歩きと同じ速さで動け、溺れない)
// Hardy  = 災害に強い (溺れる・焼ける までの秒が倍・吸い出しの力が半分・崩れた壁の下敷きにならず転ぶだけ)
// 掴まれる場所 (錨) = その近くの人は誰でも Steady と同じ
internal static class CrewTraits
{
    internal enum Trait : byte { Trace, Skim, Steady, Fin, Hardy }

    private static readonly Func<bool>[][] On = { new Func<bool>[256], new Func<bool>[256], new Func<bool>[256], new Func<bool>[256], new Func<bool>[256] };
    private static int _count;
    private static object _match;

    private const float HoldReach = 1.5f;
    private static readonly List<Vector2> Holds = new();

    private static void Bind()
    {
        if (ReferenceEquals(_match, RoleState.Match)) return;
        _match = RoleState.Match;
        RoleState.Match.OnRelease(Clear);
    }

    internal static void Set(byte id, Trait t, Func<bool> on)
    {
        Bind();
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

    // 掴まれる場所を足す (全員の端末で同じ点)。置く前へ時を戻すと消える
    internal static void AddHold(Vector2 at)
    {
        Bind();
        Holds.Add(at);
        Rewind.Undo(_ => Holds.Remove(at));
    }

    // 流れと吸い出しに押されないか (役職の Steady か、掴まれる場所の近く)
    internal static bool Steady(byte id, Vector2 pos)
    {
        if (Has(id, Trait.Steady)) return true;
        for (int i = 0; i < Holds.Count; i++)
        {
            float dx = Holds[i].x - pos.x, dy = Holds[i].y - pos.y;
            if (dx * dx + dy * dy < HoldReach * HoldReach) return true;
        }
        return false;
    }

    private static void Clear()
    {
        foreach (var a in On) Array.Clear(a, 0, a.Length);
        _count = 0;
        Holds.Clear();
        _match = null;
    }
}
