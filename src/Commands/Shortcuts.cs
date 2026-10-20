// Based on https://github.com/waffle-ful/Aeterna-End-K-not Patches/ControlPatch.cs and https://github.com/Gurge44/EndlessHostRoles Patches/ControlPatch.cs (GPL-3.0)
using System;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// ホストの PC のキー操作 (EndKnot と同じ押し方)。同じことはチャットのコマンドでもできる。
//   Shift+L+Enter 廃村 / Shift+M+Enter 会議を開く・閉じる / Shift+E+Enter 自分を倒す / 開始の数えの間に Shift 数えを飛ばす
//   ロビーで Enter だけ = 試合を始める・数えの間にもう一度 Enter で取りやめる (設定で入り切り)
// 毎フレームの問い合わせは Enter と Shift の押し下げの 2 つだけで、どちらかが押された時だけ先を見る
internal static class Shortcuts
{
    private static readonly bool Enabled = !OperatingSystem.IsAndroid();

    public static void Tick()
    {
        if (!Enabled) return;
        bool enter = Input.GetKeyDown(KeyCode.Return);
        bool shiftDown = Input.GetKeyDown(KeyCode.LeftShift);
        if (!enter && !shiftDown) return;
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || Dev.DevConsole.IsOpen) return;
        var hud = Vanilla.Hud;
        if (hud && hud.Chat && hud.Chat.IsOpenOrOpening) return;

        if (!enter)
        {
            HostActions.SkipCountdown();
            return;
        }
        if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
        {
            if (HostSettings.EnterToStart && !ShipStatus.Instance && Vanilla.StartManager is { } gsm && !GameSettingMenu.Instance)
            {
                if (gsm.startState == GameStartManager.StartingStates.NotStarting) Report(hud, HostActions.Start());
                else if (gsm.startState == GameStartManager.StartingStates.Countdown) Report(hud, HostActions.Cancel());
            }
            return;
        }
        string result =
            Input.GetKey(KeyCode.L) ? HostActions.Abort() :
            Input.GetKey(KeyCode.M) ? HostActions.ToggleMeeting() :
            Input.GetKey(KeyCode.E) ? HostActions.Kill("") :
            null;
        if (result != null) Report(hud, result);
    }

    private static void Report(HudManager hud, string result)
    {
        Plugin.Logger.LogInfo($"shortcut: {result}");
        if (hud) Dev.DevChat.Notice(hud.Chat, result);
    }
}

[Settings(Tab.General, "HostSettings.Title", order: -80)]
public static class HostSettings
{
    public static readonly BoolOpt EnterToStart = new("HostSettings.EnterToStart", false);
    public static readonly BoolOpt PlayersCanRename = new("HostSettings.PlayersCanRename", true);
    public static readonly IntOpt StartSeconds = new("HostSettings.StartSeconds", 5, 0, 30, 1, "s");
    public static readonly BoolOpt NoGameEnd = new("HostSettings.NoGameEnd", false);
}

// 開始の数えの長さを設定の秒数にする (本編は 5 秒)。数えはホストの端末が進めて客へ残りの秒を送るので、ホストだけで足りる
[HarmonyLib.HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ReallyBegin))]
internal static class StartSecondsPatch
{
    public static void Postfix(GameStartManager __instance)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        if (__instance.startState != GameStartManager.StartingStates.Countdown) return;
        __instance.countDownTimer = HostSettings.StartSeconds + 0.0001f;
    }
}
