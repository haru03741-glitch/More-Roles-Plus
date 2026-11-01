using HarmonyLib;

namespace MoreRolesPlus.Roles;

// フリープレイ (誰でも地形の破壊などを試してよい場)。オンラインの試合は人数が少なくても普通の役職のまま
internal static class SandboxMode
{
    public static bool On => AmongUsClient.Instance && AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay;
}

// 本編はロビーが開いた時の下限 (4 人) で開始ボタンを押せなくする。下限は Update で書き戻されないので、開いた時に 1 回だけ下げる
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
internal static class MinPlayersPatch
{
    public static void Postfix(GameStartManager __instance) => __instance.MinPlayers = Options.GameModeSwitch.MinPlayers;
}

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
internal static class MatchStartIntroPatch
{
    public static void Prefix()
    {
        MatchLog.Reset(); // 「もう一度プレイ」でロビーを通らずに次の試合へ入っても前の死因を残さない
        Commands.JoinLists.OnMatchStart();
    }
}

// フリープレイでは試合中もチャット欄と破壊のボタンを出す。陣営チャットをタスク中に使える設定なら、仲間のいる人にも出す。
// 本編は HUD を出し直す時 (会議の後・死んだ時・フリープレイの始め) と、オンラインの試合ではイントロの後にチャット欄を隠すので、その両方の後に出す
[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class SandboxChatOnStartPatch
{
    public static void Postfix(HudManager __instance)
    {
        Commands.TeamChat.OnGameStart();
        SandboxChatPatch.Show(__instance);
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class SandboxChatPatch
{
    public static void Postfix(HudManager __instance, bool isActive)
    {
        if (isActive) Show(__instance);
    }

    public static void Show(HudManager hud)
    {
        bool sandbox = SandboxMode.On;
        if (!sandbox && !Commands.TeamChat.ShowsInTasks) { Unshift(); return; }
        if (!hud.Chat) return;
        hud.Chat.SetVisible(true);
        Shift();
        if (sandbox)
        {
            Terrain.SandboxCommands.ShowHint(hud.Chat);
            Terrain.SandboxCommands.EnsureButtons();
        }
        Commands.TeamChat.Refresh(hud.Chat);
    }

    // 試合の情報 (?) のボタンは毎フレーム「普段の位置」か「チャットをよけた位置」(会議中) のどちらかに自分を置く。
    // チャットの後ろの板は右隣のボタンへつながる形なので、チャットを動かさず、試合中にチャット欄を出している間だけ
    // 普段の位置をよけた位置にする (静的な値を 1 回書くだけで、毎フレームの処理は足さない)
    private static bool _shifted;

    private static void Shift()
    {
        if (_shifted) return;
        _ = Menu.MatchInfoExtension.VanillaButtonDefault; // ロビーでも同じ値を書き換えるので、元の値は共有の控えから
        MatchInfoHudButton.defaultDistanceFromEdge = MatchInfoHudButton.adjustedDistanceFromEdge;
        _shifted = true;
    }

    private static void Unshift()
    {
        if (!_shifted) return;
        MatchInfoHudButton.defaultDistanceFromEdge = Menu.MatchInfoExtension.VanillaButtonDefault;
        _shifted = false;
    }
}

