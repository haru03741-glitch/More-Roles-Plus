using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面に足した頁 (SortingGroup の順番 5)・左の列の変えた印・会議の名札のページ送りは描く順番を上げて本編の画面の手前に描くが、
// 本編の ESC の窓とチャットは順番 0 のままなので、それらの手前にも出てしまう。どちらかが開いている間は隠し、閉じたら隠した物だけ戻す。
// 頁は SetActive で、出し入れを自分で SetActive する物 (変えた印) は描画の enabled で隠す。
// 開く・閉じるの後置で即座に見て、後置が呼ばれない道の分は 0.1 秒ごとにも見る (頁が 1 つも無い間は何もしない)
internal static class SettingsCover
{
    private const int Every = 3; // FixedUpdate 3 回ごと (0.1 秒)

    private static readonly List<GameObject> Roots = new();
    private static readonly List<GameObject> Hidden = new();
    private static readonly List<Renderer> Marks = new();
    private static readonly List<Renderer> HiddenMarks = new();
    private static bool _covered;
    private static int _tick;

    internal static void Add(GameObject root)
    {
        if (!root) return;
        Roots.Add(root);
        if (_covered && root.activeSelf)
        {
            root.SetActive(false);
            Hidden.Add(root);
        }
    }

    // 出し入れを持ち主が SetActive で決める物は、描画だけを止める
    internal static void Add(Renderer r)
    {
        if (!r) return;
        Marks.Add(r);
        if (_covered && r.enabled)
        {
            r.enabled = false;
            HiddenMarks.Add(r);
        }
    }

    // FixedUpdate から
    internal static void Tick()
    {
        if (Roots.Count + Marks.Count == 0 || ++_tick % Every != 0) return;
        Refresh();
    }

    internal static void Refresh()
    {
        if (Roots.Count + Marks.Count == 0) return;
        for (int i = Roots.Count - 1; i >= 0; i--)
            if (!Roots[i]) Roots.RemoveAt(i);
        for (int i = Marks.Count - 1; i >= 0; i--)
            if (!Marks[i]) Marks.RemoveAt(i);
        bool covered = Roots.Count + Marks.Count > 0 && Covered();
        if (covered == _covered) return;
        _covered = covered;
        if (covered)
        {
            foreach (var r in Roots)
            {
                if (!r.activeSelf) continue;
                r.SetActive(false);
                Hidden.Add(r);
            }
            foreach (var m in Marks)
            {
                if (!m.enabled) continue;
                m.enabled = false;
                HiddenMarks.Add(m);
            }
        }
        else
        {
            foreach (var h in Hidden)
                if (h) h.SetActive(true);
            Hidden.Clear();
            foreach (var m in HiddenMarks)
                if (m) m.enabled = true;
            HiddenMarks.Clear();
        }
    }

    private static bool Covered()
    {
        var hud = Vanilla.Hud;
        if (!hud) return false;
        return hud.GameMenu && hud.GameMenu.IsOpen || hud.Chat && hud.Chat.IsOpenOrOpening;
    }
}

[HarmonyPatch]
internal static class SettingsCoverPatches
{
    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Open)), HarmonyPostfix]
    public static void Open() => SettingsCover.Refresh();

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close)), HarmonyPostfix]
    public static void Close() => SettingsCover.Refresh();

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void Toggle() => SettingsCover.Refresh();
}
