// Based on https://github.com/waffle-ful/Aeterna-End-K-not Patches/ControlPatch.cs and https://github.com/Gurge44/EndlessHostRoles Patches/ControlPatch.cs (GPL-3.0)
using System;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// ホストの PC のキー操作 (EndKnot と同じ押し方)。同じことはチャットのコマンドでもできる。
//   Shift+L+Enter 廃村 / Shift+M+Enter 会議を開く・閉じる / Shift+E+Enter 自分を倒す / 開始の数えの間に Shift 数えを飛ばす
//   ロビーで Enter だけ = 試合を始める・数えの間にもう一度 Enter で取りやめる (設定で入り切り)
//   F5+T = 文のファイルを読み直す (誰でも・どの画面でも)
// 毎フレームの問い合わせは「何かのキーが押されたか」と Enter の押し下げの 2 つだけで、押された時だけ先を見る。
// Enter と Shift+文字 は Windows のキーの状態 (GetAsyncKeyState) から読む。日本語入力がひらがなのままだと文字キーと Enter が
// IME に取られ、Unity の Input からは押されていないように見える (Shift+L+Enter が何も起こさない)
internal static class Shortcuts
{
    private static readonly bool Enabled = !OperatingSystem.IsAndroid();

    public static void Tick()
    {
        if (!Enabled) return;
        bool rawEnter = RawKeys.EnterPressed();
        if (!rawEnter && !Input.anyKeyDown) return;
        if (Input.GetKeyDown(KeyCode.F2))
        {
            try { Menu.HudTweaks.Screenshot(); }
            catch (System.Exception e) { Plugin.Logger.LogError($"screenshot: {e.Message}"); }
            return;
        }
        if (Input.GetKey(KeyCode.F5) && Input.GetKey(KeyCode.T))
        {
            string reloaded = LangFiles.Command("");
            Plugin.Logger.LogInfo($"shortcut: {reloaded}");
            if (Vanilla.Hud is { } h && h.Chat) Dev.DevChat.Notice(h.Chat, reloaded);
            return;
        }
        bool enter = rawEnter;
        bool shiftDown = !enter && Input.GetKeyDown(KeyCode.LeftShift);
        if (!enter && !shiftDown) return;
        bool shift = enter && RawKeys.Held(RawKeys.Shift);
        var client = AmongUsClient.Instance;
        var hud = Vanilla.Hud;
        // Shift+文字+Enter が弾かれた時だけ理由を 1 行残す (押した時だけなので毎フレームの費用は無い)
        string blocked =
            !client || !client.AmHost ? "not host" :
            Dev.DevConsole.IsOpen ? "dev console open" :
            hud && hud.Chat && hud.Chat.IsOpenOrOpening ? "chat open" :
            UI.TextField.Typing ? "typing" :
            null;
        if (blocked != null)
        {
            if (shift) Plugin.Logger.LogInfo($"shortcut ignored: {blocked}");
            return;
        }

        if (!enter)
        {
            HostActions.SkipCountdown();
            return;
        }
        if (!shift)
        {
            if (HostSettings.EnterToStart && !ShipStatus.Instance && Vanilla.StartManager is { } gsm && !GameSettingMenu.Instance)
            {
                if (gsm.startState == GameStartManager.StartingStates.NotStarting) Report(hud, HostActions.Start());
                else if (gsm.startState == GameStartManager.StartingStates.Countdown) Report(hud, HostActions.Cancel());
            }
            return;
        }
        string result =
            RawKeys.Held('L') ? HostActions.Abort() :
            RawKeys.Held('M') ? HostActions.ToggleMeeting() :
            RawKeys.Held('E') ? HostActions.Kill("") :
            null;
        if (result != null) Report(hud, result);
    }

    private static void Report(HudManager hud, string result)
    {
        Plugin.Logger.LogInfo($"shortcut: {result}");
        if (hud) Dev.DevChat.Notice(hud.Chat, result);
    }
}

// Windows のキーの状態を直接読む (IME に取られない)。全体のキーの状態なので、ゲームの窓が前面の時だけ使う。
// Enter は本体とテンキーが同じ仮想キー
internal static class RawKeys
{
    internal const int Shift = 0x10, Enter = 0x0D;

#if ANDROID
    internal static bool EnterPressed() => false;
    internal static bool Held(int vk) => false;
#else
    private static bool _enterWasDown;

    // 毎フレーム 1 回: Enter が今のフレームで押されたか
    internal static bool EnterPressed()
    {
        bool down = (GetAsyncKeyState(Enter) & 0x8000) != 0;
        bool pressed = down && !_enterWasDown;
        _enterWasDown = down;
        return pressed && Zoom.GameWindowInFront();
    }

    internal static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
#endif
}

[Settings(Tab.General, "HostSettings.Title", order: -80)]
public static class HostSettings
{
    public static readonly BoolOpt EnterToStart = new("HostSettings.EnterToStart", false);
    public static readonly BoolOpt PlayersCanRename = new("HostSettings.PlayersCanRename", true);
    public static readonly IntOpt StartSeconds = new("HostSettings.StartSeconds", 5, 0, 30, 1, "s");
    public static readonly BoolOpt NoGameEnd = new("HostSettings.NoGameEnd", false);
    public static readonly BoolOpt AbortWhenHostLeaves = new("HostSettings.AbortWhenHostLeaves", false);
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
