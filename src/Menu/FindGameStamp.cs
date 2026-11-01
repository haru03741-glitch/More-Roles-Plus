using HarmonyLib;

namespace MoreRolesPlus.Menu;

// 「ゲームを探す」画面では右上の MOD の印がタイトルに重なるので隠す
internal static class FindGameStamp
{
    private static FindAGameManager _screen;

    [HarmonyPatch(typeof(FindAGameManager), nameof(FindAGameManager.Start))]
    private static class StartPatch { public static void Postfix(FindAGameManager __instance) => _screen = __instance; }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class StampPatch
    {
        public static void Postfix(ModManager __instance)
        {
            if (_screen is null) return;
            if (!_screen) { _screen = null; return; }
            if (__instance.ModStamp && __instance.ModStamp.gameObject.activeSelf) __instance.ModStamp.gameObject.SetActive(false);
        }
    }
}
