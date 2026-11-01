using HarmonyLib;
using InnerNet;

namespace MoreRolesPlus.Net;

// 部屋の公開は MRP のサーバーでだけ。公式などで公開すると mod の無い人の一覧にも出て、入れない部屋が並ぶ。
// 公開ボタンを止め、作成画面で「公開」を選んで立てた部屋もロビーに入った所で非公開へ戻す
internal static class PublicGuard
{
    private static bool Allowed => MrpRegion.IsCurrent();

    private static void Tell()
    {
        var hud = Vanilla.Hud;
        if (hud && hud.Notifier) hud.Notifier.AddDisconnectMessage(new Text("PublicGuard.MrpOnly"));
    }

    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.MakePublic))]
    private static class MakePublicPatch
    {
        public static bool Prefix()
        {
            var client = AmongUsClient.Instance;
            if (!client || client.IsGamePublic || Allowed) return true;
            Tell();
            return false;
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.ChangeGamePublic))]
    private static class ChangePatch
    {
        public static void Prefix(ref bool isPublic)
        {
            if (!isPublic || Allowed) return;
            isPublic = false;
            Tell();
        }
    }

    // 立てた時から公開になっている部屋 (作成画面で選んだ物) を戻す
    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
    private static class StartPatch
    {
        public static void Postfix()
        {
            var client = AmongUsClient.Instance;
            if (!client || !client.AmHost || client.NetworkMode != NetworkModes.OnlineGame || !client.IsGamePublic || Allowed) return;
            client.ChangeGamePublic(false);
        }
    }
}
