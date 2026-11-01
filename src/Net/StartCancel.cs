using HarmonyLib;
using MoreRolesPlus.UI;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Net;

// 開始の数えの間、本編は「開始」のボタンを消して「開始まであと〇」の板 (GameStarting) を出すので、もう一度押せない。
// 板に当たり判定とボタンを付け、ホストが押したら数えを取りやめる (本編の取りやめ。客の数えの表示も消える)
internal static class StartCancel
{
    private const string Marker = "MrpStartCancel";

    public static void Ensure(GameStartManager gsm)
    {
        var board = gsm ? gsm.transform.Find("StartGameArea/GameStarting") : null;
        if (!board || board.Find(Marker)) return;
        var sr = board.GetComponent<SpriteRenderer>();
        new GameObject(Marker) { layer = board.gameObject.layer }.transform.SetParent(board, false);
        var col = board.gameObject.AddComponent<BoxCollider2D>();
        col.size = sr ? sr.size : new Vector2(5.16f, 1.23f);
        var pb = board.gameObject.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.ClickSound = UiAssets.ClickSound;
        var target = gsm;
        pb.OnClick.AddListener((UnityAction)(() => Cancel(target)));
    }

    private static void Cancel(GameStartManager gsm)
    {
        if (!gsm || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        if (gsm.startState != GameStartManager.StartingStates.Countdown) return;
        gsm.ResetStartState();
    }

    // 確かめ用 (ブリッジ): 板を押したのと同じ
    internal static string Press()
    {
        var gsm = Vanilla.StartManager;
        var board = gsm ? gsm.transform.Find("StartGameArea/GameStarting") : null;
        if (!board || !board.gameObject.activeInHierarchy) return "board not shown";
        var pb = board.GetComponent<PassiveButton>();
        var col = board.GetComponent<BoxCollider2D>();
        if (!pb || !col || !col.enabled) return "board not clickable";
        pb.OnClick.Invoke();
        return $"pressed state={gsm.startState}";
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
internal static class StartCancelPatch
{
    public static void Postfix(GameStartManager __instance)
    {
        try { StartCancel.Ensure(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogError($"start cancel: {e}"); }
    }
}
