using HarmonyLib;

namespace MoreRolesPlus.Roles;

// 一人でも試合を始められるようにする。本編の下限より少ない人数で始めた試合は「練習」として自動では終わらない
// (一人だと始まった瞬間に勝敗が決まってしまうため)。終えるときはメニューから抜ける
internal static class PracticeMatch
{
    private const int VanillaMinPlayers = 4;

    // 今の試合が練習か。試合の始めの人数で全員がそれぞれ決める (同じ人数を見るので通信は要らない)
    public static bool Active { get; private set; }

    // 練習かフリープレイ (誰でも地形の破壊などを試してよい場)
    public static bool Sandbox => Active || AmongUsClient.Instance && AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay;

    internal static void OnIntro()
    {
        var data = GameData.Instance;
        Active = data && data.PlayerCount < VanillaMinPlayers;
        if (Active) Plugin.Logger.LogInfo($"practice match: {data.PlayerCount} player(s), automatic game end off");
    }

    internal static void OnJoined() => Active = false;
}

// 本編はロビーが開いた時の下限 (4 人) で開始ボタンを押せなくする。下限は Update で書き戻されないので、開いた時に 1 回だけ下げる
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
internal static class MinPlayersPatch
{
    public static void Postfix(GameStartManager __instance) => __instance.MinPlayers = 1;
}

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
internal static class PracticeIntroPatch
{
    public static void Prefix()
    {
        PracticeMatch.OnIntro();
        MatchLog.Reset(); // 「もう一度プレイ」でロビーを通らずに次の試合へ入っても前の死因を残さない
    }
}

// 練習とフリープレイでは試合中もチャット欄と破壊のボタンを出す。
// 本編は HUD を出し直す時 (会議の後・死んだ時・フリープレイの始め) と、オンラインの試合ではイントロの後にチャット欄を隠すので、その両方の後に出す
[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class SandboxChatOnStartPatch
{
    public static void Postfix(HudManager __instance) => SandboxChatPatch.Show(__instance);
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
        if (!PracticeMatch.Sandbox) { Unshift(); return; }
        if (!hud.Chat) return;
        hud.Chat.SetVisible(true);
        Shift();
        Terrain.SandboxCommands.ShowHint(hud.Chat);
        Terrain.SandboxCommands.EnsureButtons();
    }

    // 試合の情報 (?) のボタンは毎フレーム「普段の位置」か「チャットをよけた位置」(会議中) のどちらかに自分を置く。
    // チャットの後ろの板は右隣のボタンへつながる形なので、チャットを動かさず、練習とフリープレイの間だけ
    // 普段の位置をよけた位置にする (静的な値を 1 回書くだけで、毎フレームの処理は足さない)
    private static bool _shifted;
    private static UnityEngine.Vector3 _default;

    private static void Shift()
    {
        if (_shifted) return;
        _default = MatchInfoHudButton.defaultDistanceFromEdge;
        MatchInfoHudButton.defaultDistanceFromEdge = MatchInfoHudButton.adjustedDistanceFromEdge;
        _shifted = true;
    }

    private static void Unshift()
    {
        if (!_shifted) return;
        MatchInfoHudButton.defaultDistanceFromEdge = _default;
        _shifted = false;
    }
}

