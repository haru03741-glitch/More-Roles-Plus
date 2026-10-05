// Ported from https://github.com/waffle-ful/Aeterna-End-K-not Patches/ServerVersionPatch.cs (GPL-3.0),
// which follows https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using HarmonyLib;
using InnerNet;

namespace MoreRolesPlus.Net;

// 公式サーバーに「mod の部屋」として名乗る。版番号の下 2 桁を 50 で割った余りが 25〜49 なら
// サーバーは部屋をホスト権限で動かし、本編の不正検知 (インポスター以外のキル等) で蹴らなくなる。
// 同じ呼び出しが何度も来るので、すでに 25〜49 の時は足さない (足すと次の版の帯へはみ出して蹴られる)。
[HarmonyPatch(typeof(Constants), nameof(Constants.GetBroadcastVersion))]
internal static class BroadcastVersionPatch
{
    // テスト用 (ブリッジの modver 0)。印を付けない時の動きと比べる
    public static bool Off;

    public static void Postfix(ref int __result)
    {
        var client = AmongUsClient.Instance;
        if (Off || !client || client.NetworkMode != NetworkModes.OnlineGame) return;
        if (__result % 50 < 25) __result += 25;
    }
}

[HarmonyPatch(typeof(Constants), nameof(Constants.IsVersionModded))]
internal static class IsVersionModdedPatch
{
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
