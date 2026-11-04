#if ANDROID
using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MoreRolesPlus.Platform;

// 本編の着せ替えには「スマホ版だけ有料」の印 (paidOnMobile) がある。PC では無料の物が、スマホの本編では持っていない扱いになり使えない。
// Android 版の MRP はランチャーから PC と同じ itch.io のアカウントでログインするので、印を外して PC と同じ物を使えるようにする
// (印の無い有料の物・豆や星で買う物はそのまま)。
internal static class MobileCosmetics
{
    private static int Clear<T>(Il2CppReferenceArray<T> all) where T : CosmeticData
    {
        if (all == null) return 0;
        int n = 0;
        foreach (var c in all)
        {
            if (c == null || !c.paidOnMobile) continue;
            c.paidOnMobile = false;
            n++;
        }
        return n;
    }

    internal static void Apply(HatManager hm)
    {
        if (!hm) return;
        int n = Clear(hm.allHats) + Clear(hm.allSkins) + Clear(hm.allVisors) + Clear(hm.allPets) + Clear(hm.allNamePlates);
        if (n > 0) Plugin.Logger.LogInfo($"cosmetics: {n} items free on PC made usable on Android");
    }

    [HarmonyPatch(typeof(HatManager), nameof(HatManager.Initialize))]
    private static class InitPatch
    {
        public static void Postfix(HatManager __instance)
        {
            try { Apply(__instance); }
            catch (Exception e) { Plugin.Logger.LogError($"cosmetics: {e}"); }
        }
    }

    // 本編は持ち物の判定をこの値で決める (中で印を読む前に外しておく)
    [HarmonyPatch(typeof(CosmeticData), nameof(CosmeticData.PaidOnMobile), MethodType.Getter)]
    private static class PaidPatch
    {
        public static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }
}
#endif
