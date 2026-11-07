using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Boot;

// Based on https://github.com/KYMario/TownOfHost-K Patches/ClientPatch.cs SplashLogoAnimatorPatch (GPL-3.0)
// 起動時のロゴ演出が終わるのを待たずに、メインメニューへ切り替えさせる。
// 読み込みは別に進んでいるので、ロゴの間は毎フレーム「終わったら切り替えてよい」を出し続ける
// (読み込みが始まる前に 1 回出すだけだと効かず、本編側も切り替えを待たなくなって止まる)
[HarmonyPatch(typeof(SplashManager), "Update")]
internal static class SplashSkipPatch
{
    private static SplashManager _hidden;

    public static void Prefix(SplashManager __instance)
    {
        __instance.sceneChanger.AllowFinishLoadingScene();
        __instance.startedSceneLoad = true;
        HideLoading(__instance);
    }

    // 「AMONG US ロード中」とクルーの絵は出さない (進みは MRP の起動中の窓に出る)。
    // 絵と字を消すだけで、読み込みと切り替えは本編のまま
    private static void HideLoading(SplashManager m)
    {
        // 後から出てくる字 (「ロード中」) もあるので、ロゴの間は 5 フレームごとに見る
        if (_hidden == m && Time.frameCount % 5 != 0) return;
        _hidden = m;
        try
        {
            // 同じシーンの物だけ (シーンの一覧を尋ねる関数は使えないので、シーンの番号で見分ける)
            int scene = m.gameObject.scene.handle;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>())
                if (r && r.gameObject.scene.handle == scene) r.enabled = false;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<UnityEngine.Canvas>())
                if (c && c.gameObject.scene.handle == scene) c.enabled = false;
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"splash hide: {e.Message}"); }
    }
}
