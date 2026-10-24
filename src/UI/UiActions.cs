using System;
using System.Collections.Generic;
using UnityEngine.Events;

namespace MoreRolesPlus.UI;

// ボタンに渡す処理の受け口。il2cpp 側へ渡す UnityAction は番号ごとに 1 つだけ作って使い回し、
// 押された時は番号から今の処理を引く (渡すたびに作ると解放されない参照が増え、
// 処理が画面の部品を捕まえていると閉じた後も残るため)。番号は画面を作り直す時にまとめて返す。
internal static class UiActions
{
    private static readonly List<Action> Handlers = new();
    private static readonly List<UnityAction> Actions = new();
    private static readonly Stack<int> Free = new();

    // handler を番号に結び、その番号の UnityAction を返す。番号は owner に控える
    public static UnityAction Take(Action handler, List<int> owner)
    {
        int i;
        if (Free.Count > 0) i = Free.Pop();
        else
        {
            i = Handlers.Count;
            Handlers.Add(null);
            int k = i;
            Actions.Add((UnityAction)(() => Run(k)));
        }
        Handlers[i] = handler;
        owner.Add(i);
        return Actions[i];
    }

    public static void Release(List<int> owner)
    {
        foreach (int i in owner)
        {
            if (Handlers[i] == null)
            {
                Plugin.Logger.LogWarning($"ui action: slot {i} released twice");
                continue;
            }
            Handlers[i] = null;
            Free.Push(i);
        }
        owner.Clear();
    }

    private static void Run(int i)
    {
        var h = i < Handlers.Count ? Handlers[i] : null;
        if (h == null) return;
        try { h(); }
        catch (Exception e) { Plugin.Logger.LogError($"ui action: {e}"); }
    }

    // 確かめ用: 使っている番号の数 / 作った UnityAction の数
    public static string Census() => $"{Handlers.Count - Free.Count}/{Actions.Count}";
}
