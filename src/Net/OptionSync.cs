using Hazel;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Net;

// 設定値の同期。ホストが全項目の番号を Key の順に詰めて送る ([件数][番号...][プリセットの名前])。
// 送る時機 = 設定画面を閉じた時・同じ版の人が来た時・役職を配る直前。
internal static class OptionSync
{
    // 客の側: ホストの値で上書きされているか (自分がホストになったら保存値へ戻す)
    public static bool HoldsHostValues { get; private set; }

    private static readonly RemoteCall<(int[] Values, string Preset)> Values = new("Options.Values", Route.HostToAll,
        (w, m) =>
        {
            w.WritePacked(m.Values.Length);
            foreach (int v in m.Values) w.WritePacked(v);
            w.Write(m.Preset ?? "");
        },
        r =>
        {
            var values = new int[r.ReadPackedInt32()];
            for (int i = 0; i < values.Length; i++) values[i] = r.ReadPackedInt32();
            return (values, r.ReadString());
        },
        (_, m) => Receive(m.Values, m.Preset), Delivery.Queued);

    public static void SendAll(int target = -1)
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost) return;
        var values = new int[Registry.All.Count];
        for (int i = 0; i < values.Length; i++) values[i] = Registry.All[i].Index;
        Values.Send((values, PresetLabel.Shown), target);
    }

    private static void Receive(int[] values, string preset)
    {
        for (int i = 0; i < values.Length && i < Registry.All.Count; i++) Registry.All[i].SetIndex(values[i]);
        HoldsHostValues = true;
        LobbyView.OnOptionsReceived();
        PresetLabel.Receive(preset);
    }

    // 自分がホストとして設定を触る前に、受け取った値を捨てて保存値へ戻す
    public static void RestoreOwn()
    {
        if (!HoldsHostValues) return;
        Registry.Load();
        HoldsHostValues = false;
        PresetLabel.Recheck();
    }
}

// 開始の数えが始まった時にもう一度全員へ送る。途中で来た客にも、船ができる前 (マップの左右反転などを船の作り始めに読む) に届く
[HarmonyLib.HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ReallyBegin))]
internal static class OptionSyncOnBeginPatch
{
    public static void Postfix() => OptionSync.SendAll();
}
