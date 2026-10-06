using System;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// ホストの PC のキー操作 (EndKnot と同じ押し方)。同じことはチャットのコマンドでもできる。
//   Shift+L+Enter 廃村 / Shift+M+Enter 会議を開く・閉じる / Shift+E+Enter 自分を倒す / 開始の数えの間に Shift 数えを飛ばす
//   ロビーで Enter だけ = 試合を始める (設定で入り切り)
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
        var hud = HudManager.InstanceExists ? HudManager.Instance : null;
        if (hud && hud.Chat && hud.Chat.IsOpenOrOpening) return;

        if (!enter)
        {
            HostActions.SkipCountdown();
            return;
        }
        if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
        {
            if (HostSettings.EnterToStart && !ShipStatus.Instance && GameStartManager.InstanceExists
                && GameStartManager.Instance.startState == GameStartManager.StartingStates.NotStarting
                && !GameSettingMenu.Instance)
                Report(hud, HostActions.Start());
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

[Settings(Tab.General, "ホストの操作", "Host controls", order: -80)]
public static class HostSettings
{
    public static readonly BoolOpt EnterToStart = new("ロビーで Enter を押すと試合を始める (PC)", "Press Enter in the lobby to start (PC)", false);
    public static readonly BoolOpt PlayersCanRename = new("参加者が /rename /color で名前と色を変えられる", "Players can change name and color with /rename /color", true);
}
