using HarmonyLib;

namespace MoreRolesPlus.Boot;

// Based on https://github.com/KYMario/TownOfHost-K Patches/ClientPatch.cs SplashLogoAnimatorPatch (GPL-3.0)
// 起動時のロゴ演出が終わるのを待たずに、メインメニューへ切り替えさせる。
// 読み込みは別に進んでいるので、ロゴの間は毎フレーム「終わったら切り替えてよい」を出し続ける
// (読み込みが始まる前に 1 回出すだけだと効かず、本編側も切り替えを待たなくなって止まる)
[HarmonyPatch(typeof(SplashManager), "Update")]
internal static class SplashSkipPatch
{
    public static void Prefix(SplashManager __instance)
    {
        __instance.sceneChanger.AllowFinishLoadingScene();
        __instance.startedSceneLoad = true;
    }
}
