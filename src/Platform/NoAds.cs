// Ported from https://github.com/waffle-ful/Aeterna-End-K-not Modules/Android/ItchLogin.cs (GPL-3.0)
#if ANDROID
using System;
using HarmonyLib;

namespace MoreRolesPlus.Platform;

// ゲームはランチャーのプロセスの中で動くため、広告 SDK はランチャーのパッケージ名をゲームの app key と一緒に送ってしまう。
// app key に登録されていないアプリからの広告になるので、Android 版では SDK を初期化せず、報酬広告の入口も出さない。
internal static class NoAds
{
    [HarmonyPatch(typeof(AdsManager), nameof(AdsManager.InitLevelPlay))]
    private static class InitPatch
    {
        public static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(AdsMenu), nameof(AdsMenu.OnEnable))]
    private static class MenuPatch
    {
        public static void Postfix(AdsMenu __instance)
        {
            if (__instance.adButton != null) __instance.adButton.SetActive(false);
            // 初期化が来ないので、それを待つ読み込み表示も出したままにしない
            if (__instance.loadingObject != null) __instance.loadingObject.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(AdsMenu), nameof(AdsMenu.ClickAdButton))]
    private static class ClickPatch
    {
        public static bool Prefix() => false;
    }

    // 準備の通知がどんな結果で届いても広告ボタンは隠したままにする
    [HarmonyPatch(typeof(AdsMenu), nameof(AdsMenu.AdReadyCallback))]
    private static class ReadyPatch
    {
        public static void Postfix(AdsMenu __instance)
        {
            if (__instance.adButton != null) __instance.adButton.SetActive(false);
        }
    }

    // 「読み込んだが表示できない」(日次上限など) の時、本編は rewardedAd を null にした直後にもう一度参照して例外になり、
    // 広告の画面が応答待ちのまま固まる。表示できる時だけ本編に任せ、それ以外は失敗として知らせて本編を飛ばす。
    [HarmonyPatch(typeof(AdsManager), nameof(AdsManager.RewardedOnAdLoadedEvent))]
    private static class LoadedPatch
    {
        public static bool Prefix(AdsManager __instance)
        {
            try
            {
                var ad = __instance.rewardedAd;
                if (ad != null && ad.IsAdReady()) return true;
            }
            catch (Exception e) { Plugin.Logger.LogError("ads: IsAdReady threw: " + e.Message); return true; }

            try
            {
                __instance.rewardedAd = null;
                __instance.adMenuCallbak?.Invoke(false);
            }
            catch (Exception e) { Plugin.Logger.LogError("ads: failure callback threw: " + e.Message); }
            return false;
        }
    }
}
#endif
