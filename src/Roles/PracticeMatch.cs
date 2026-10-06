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
    public static void Prefix() => PracticeMatch.OnIntro();
}

// 練習とフリープレイでは試合中もチャット欄を出す (/hammer などをそこから打てるように)。
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
        if (!hud.Chat || !PracticeMatch.Sandbox) return;
        hud.Chat.SetVisible(true);
        LayOut(hud);
        Terrain.SandboxCommands.ShowHint(hud.Chat);
    }

    // 試合中の右上は「地図・設定・試合の情報」の並びで、チャットのボタンは試合の情報のボタンに半分重なる位置にある。
    // チャットを試合の情報の 1 つ左へ置く (試合の情報のボタンは本編が置き直すので動かさない。
    // 間隔は地図と設定のボタンの間から測るので何度呼んでも同じ)
    private static void LayOut(HudManager hud)
    {
        var map = Aspect(hud.transform, "Buttons/TopRight/MapButton");
        var menu = Aspect(hud.transform, "Buttons/TopRight/MenuButton");
        var chat = Aspect(hud.Chat.transform, "ChatButton");
        if (!map || !menu || !chat) return;
        float x = menu.DistanceFromEdge.x;
        float slot = x - map.DistanceFromEdge.x;
        // 縦は本編のまま (チャットのボタンは絵の基準が他と違い、本編の値で同じ高さに見える)
        var c = chat.DistanceFromEdge;
        chat.DistanceFromEdge = new UnityEngine.Vector3(x + slot * 2f, c.y, c.z);
        chat.AdjustPosition();
    }

    private static AspectPosition Aspect(UnityEngine.Transform root, string path)
    {
        var t = root.Find(path);
        return t ? t.GetComponent<AspectPosition>() : null;
    }
}
