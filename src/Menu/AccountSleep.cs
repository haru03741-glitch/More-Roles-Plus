using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// Based on https://github.com/waffle-ful/Aeterna-End-K-not (Patches/CalamityMenu/VanillaSuppressor.cs・AccountDialogWake.cs, GPL-3.0)
// メニューのアカウント表示 (AccountManager) は、ログインが終わるまで黒い幕でメニュー全体を押せなくし、
// 右下に読み込み中のクルーを走らせる。起動直後のメニューでだけ止めておき、メニューをすぐ使えるようにする。
// 起こすのは: ログインが終わった時 (アカウント欄を元に戻す)・マイアカウントを押した時・
// 本編がログイン失敗や年齢確認などの画面を出す時 (止めたままだと見えない画面がボタンを待って起動が進まない)。
internal static class AccountSleep
{
    private static bool _asleep;
    private static MainMenuManager _menu;

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    private static class SleepOnMenu
    {
        public static void Postfix(MainMenuManager __instance)
        {
            if (_asleep || !Plugin.SleepAccountManager.Value) return;
            var eos = EOSManager.Instance;
            if (!eos || eos.loginFlowFinished) return; // ログイン済みでメニューに戻った時は本編のまま
            if (!AccountManager.InstanceExists) return;
            var am = AccountManager.Instance;
            if (!am || !am.gameObject.activeSelf) return;
            am.gameObject.SetActive(false);
            _menu = __instance;
            _asleep = true;
            Plugin.Logger.LogInfo("account: asleep until login finishes");
        }
    }

    // Ticker.Update から呼ぶ。止めている間だけ、ログインの終わり (メニューの起動処理の完了) を見る
    internal static void Tick()
    {
        if (!_asleep) return;
        if (!_menu) { Wake("menu gone"); return; }
        if (_menu.finishStartup) Wake("startup finished");
    }

    private static void Wake(string why)
    {
        if (!_asleep) return;
        _asleep = false;
        _menu = null;
        try
        {
            if (!AccountManager.InstanceExists) return;
            var am = AccountManager.Instance;
            if (am && !am.gameObject.activeSelf) am.gameObject.SetActive(true);
            Plugin.Logger.LogInfo($"account: woken ({why})");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"account: wake failed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.OpenAccountMenu))]
    private static class OpenAccountMenuPatch
    {
        public static void Prefix() => Wake("my account");
    }

    // 本編のアカウント画面を出す入口。_asleep の bool だけ見て抜けるので、メニュー以外で呼ばれても軽い
    [HarmonyPatch]
    private static class DialogPatches
    {
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.SignInFail))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.SignInSuccess))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.PlatformSignInFail))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.ShowAgeGate))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.ShowPermissionsRequestForm))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.ShowGuardianEmailSentConfirm))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.EditGuardianEmail))]
        [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.UpdateMissingGuardianEmail))]
        [HarmonyPatch(typeof(SignInScreen), nameof(SignInScreen.Open))]
        [HarmonyPatch(typeof(PrivacyPolicyScreen), nameof(PrivacyPolicyScreen.Show))]
        [HarmonyPrefix]
        public static void Prefix()
        {
            if (_asleep) Wake("account dialog");
        }
    }
}
