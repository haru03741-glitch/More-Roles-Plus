using Hazel;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Net;

// 設定値の同期。ホストが全項目の番号を Key の順に詰めて送る ([指紋 u32][件数][番号...])。
// 送る時機 = 設定画面を閉じた時・同じ版の人が来た時・役職を配る直前。
internal static class OptionSync
{
    // 客の側: ホストの値で上書きされているか (自分がホストになったら保存値へ戻す)
    public static bool HoldsHostValues { get; private set; }

    public static void SendAll(int target = -1)
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || !PlayerControl.LocalPlayer) return;
        var w = Rpc.Start(Rpc.Options, target);
        w.Write(Registry.Fingerprint);
        w.WritePacked(Registry.All.Count);
        foreach (var o in Registry.All) w.WritePacked(o.Index);
        Rpc.Finish(w);
    }

    public static void Receive(PlayerControl sender, MessageReader r)
    {
        if (!Rpc.FromHost(sender) || AmongUsClient.Instance.AmHost) return;
        uint fp = r.ReadUInt32();
        if (fp != Registry.Fingerprint)
        {
            Plugin.Logger.LogWarning($"options from host ignored: fingerprint {fp:X8} != {Registry.Fingerprint:X8}");
            return;
        }
        int n = r.ReadPackedInt32();
        for (int i = 0; i < n && i < Registry.All.Count; i++) Registry.All[i].SetIndex(r.ReadPackedInt32());
        HoldsHostValues = true;
    }

    // 自分がホストとして設定を触る前に、受け取った値を捨てて保存値へ戻す
    public static void RestoreOwn()
    {
        if (!HoldsHostValues) return;
        Registry.Load();
        HoldsHostValues = false;
    }
}
