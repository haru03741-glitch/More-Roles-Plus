using System;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// ホストの PC のキー操作 (EndKnot と同じ押し方)。同じことはチャットのコマンドでもできる。
//   Shift+L+Enter 廃村 / Shift+M+Enter 会議を開く・閉じる / Shift+E+Enter 自分を倒す / 開始の数えの間に Shift 数えを飛ばす
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
        if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) return;
        string result =
            Input.GetKey(KeyCode.L) ? HostActions.Abort() :
            Input.GetKey(KeyCode.M) ? HostActions.ToggleMeeting() :
            Input.GetKey(KeyCode.E) ? HostActions.Kill("") :
            null;
        if (result == null) return;
        Plugin.Logger.LogInfo($"shortcut: {result}");
        if (hud) Dev.DevChat.Notice(hud.Chat, result);
    }
}
