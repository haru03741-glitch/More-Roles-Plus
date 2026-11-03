using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;

namespace MoreRolesPlus.Net;

// 本編は DTLS を使わないオンラインのサーバーへつなぐ前に、別の口 (ポート + 2) の認証サーバーへつないで合言葉 (nonce) を受け取り、
// 届くまで最大 5 秒待つ。MRP のサーバー (Impostor) には認証サーバーが無いので、部屋を作る・入るたびに 5 秒待ってからつながっていた。
// MRP のサーバーは合言葉を読まないので、MRP のサーバーへつなぐ時だけどちらも飛ばす
// (公式のサーバーは合言葉が無いと切る。ほかの非公式のサーバーは持っているか分からないので本編のまま)
[HarmonyPatch]
internal static class NonceSkip
{
    private static bool Skip()
    {
        var client = AmongUsClient.Instance;
        return client && !client.useDtls && client.NetworkMode == NetworkModes.OnlineGame && MrpRegion.IsCurrent();
    }

    [HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoConnect)), HarmonyPrefix]
    public static bool Connect(ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!Skip()) return true;
        __result = Done().WrapToIl2Cpp();
        return false;
    }

    [HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoWaitForNonce)), HarmonyPrefix]
    public static bool Wait(ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!Skip()) return true;
        __result = Done().WrapToIl2Cpp();
        return false;
    }

    private static IEnumerator Done()
    {
        yield break;
    }
}
