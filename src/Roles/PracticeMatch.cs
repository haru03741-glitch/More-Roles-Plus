using HarmonyLib;

namespace MoreRolesPlus.Roles;

// 一人でも試合を始められるようにする。本編の下限より少ない人数で始めた試合は「練習」として自動では終わらない
// (一人だと始まった瞬間に勝敗が決まってしまうため)。終えるときはメニューから抜ける
internal static class PracticeMatch
{
    private const int VanillaMinPlayers = 4;

    // ホスト: 今の試合が練習か (始めた時の人数で決める)
    public static bool Active { get; private set; }

    internal static void OnBegin()
    {
        var data = GameData.Instance;
        Active = data && data.PlayerCount < VanillaMinPlayers;
        if (Active) Plugin.Logger.LogInfo($"practice match: {data.PlayerCount} player(s), automatic game end off");
    }
}

// 本編はロビーが開いた時の下限 (4 人) で開始ボタンを押せなくする。下限は Update で書き戻されないので、開いた時に 1 回だけ下げる
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
internal static class MinPlayersPatch
{
    public static void Postfix(GameStartManager __instance) => __instance.MinPlayers = 1;
}
