using System.Collections.Generic;
using Hazel;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Net;

// 設定値の同期。ホストが全項目の番号を Key の順に詰めて送る ([件数][番号...][プリセットの名前])。
// 送る時機 = 設定画面を閉じた時・同じ版の人が来た時・役職を配る直前。
// 同じ部屋で値が変わっていたら、ホストは送る時・客は受け取った時に SettingNotice で知らせる。
internal static class OptionSync
{
    // 客の側: ホストの値で上書きされているか (自分がホストになったら保存値へ戻す)
    public static bool HoldsHostValues { get; private set; }

    // 前の値がどの部屋の物か (別の部屋へ移った最初の同期は、自分の保存値との違いなので知らせない)
    private static int[] _sent;     // ホスト: 前に送った番号
    private static int _sentGame;
    private static string _sentPreset;
    private static int _heldGame;   // 客: 前に受け取った部屋
    private static readonly List<Opt> Changes = new();

    private static readonly RemoteCall<(int[] Values, string Preset)> Values = new("Options.Values", Route.HostToAll,
        (w, m) =>
        {
            w.WritePacked(m.Values.Length);
            foreach (int v in m.Values) w.WritePacked(v);
            w.Write(m.Preset ?? "");
        },
        r =>
        {
            // 同じ版なら件数は項目の数と同じ。違う数は確保する前に捨てる (大きな数で受け手のメモリを食わせない)
            int n = r.ReadPackedInt32();
            if (n != Registry.All.Count) throw new System.IO.InvalidDataException($"options count {n} != {Registry.All.Count}");
            var values = new int[n];
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
        if (target != -1) return; // 来た人 1 人への送り直しは変更ではない (控えも進めない)

        Changes.Clear();
        if (_sent != null && _sent.Length == values.Length && _sentGame == client.GameId)
            for (int i = 0; i < values.Length; i++)
                if (values[i] != _sent[i]) Changes.Add(Registry.All[i]);
        string preset = PresetLabel.Shown;
        string loaded = preset != null && preset != _sentPreset ? preset : null;
        _sent = values;
        _sentGame = client.GameId;
        _sentPreset = preset;
        SettingNotice.Show(Changes, loaded, false); // 画面を触っていたのは自分なので音は鳴らさない
    }

    private static void Receive(int[] values, string preset)
    {
        var client = AmongUsClient.Instance;
        int game = client ? client.GameId : 0;
        bool notify = HoldsHostValues && _heldGame == game;
        string presetBefore = PresetLabel.Shown;
        Changes.Clear();
        for (int i = 0; i < values.Length && i < Registry.All.Count; i++)
        {
            var opt = Registry.All[i];
            int before = opt.Index;
            opt.SetIndex(values[i]);
            if (notify && opt.Index != before) Changes.Add(opt);
        }
        // ホストの値に置き換わったので、自分が前に変えた値の取り消しは残さない
        OptHistory.Clear();
        // 同じ値の送り直し (人が来た時など) では前の印を残す
        if (!notify) SettingNotice.ClearFresh();
        else if (Changes.Count > 0) SettingNotice.MarkFresh(Changes);
        HoldsHostValues = true;
        _heldGame = game;
        LobbyView.OnOptionsReceived();
        PresetLabel.Receive(preset);
        string loaded = PresetLabel.Shown != null && PresetLabel.Shown != presetBefore ? PresetLabel.Shown : null;
        SettingNotice.Show(Changes, loaded, true);
    }

    // ホストの部屋が始まった時の値を「前に送った値」として控える (最初に設定画面を閉じた時から知らせる)
    public static void RememberSent()
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost) return;
        PresetLabel.Recheck();
        _sent = new int[Registry.All.Count];
        for (int i = 0; i < _sent.Length; i++) _sent[i] = Registry.All[i].Index;
        _sentGame = client.GameId;
        _sentPreset = PresetLabel.Shown;
    }

    // 自分がホストとして設定を触る前に、受け取った値を捨てて保存値へ戻す
    public static void RestoreOwn()
    {
        if (!HoldsHostValues) return;
        var held = new int[Registry.All.Count];
        for (int i = 0; i < held.Length; i++) held[i] = Registry.All[i].Index;
        string heldPreset = PresetLabel.Shown;
        Registry.Load();
        HoldsHostValues = false;
        SettingNotice.ClearFresh();
        PresetLabel.Recheck();
        _sent = null;
        RememberSent();
        // ホストを引き継いだ時は、部屋で使っていた値から戻して変わった所も次に送る時に知らせる
        if (_sent == null) return;
        _sent = held;
        _sentPreset = heldPreset;
    }
}

[HarmonyLib.HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class OptionSyncLobbyStartPatch
{
    public static void Postfix()
    {
        try { OptionSync.RememberSent(); }
        catch (System.Exception e) { Plugin.Logger.LogError($"option sync: {e.Message}"); }
    }
}

// 開始の数えが始まった時にもう一度全員へ送る。途中で来た客にも、船ができる前 (マップの左右反転などを船の作り始めに読む) に届く
[HarmonyLib.HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ReallyBegin))]
internal static class OptionSyncOnBeginPatch
{
    public static void Postfix() => OptionSync.SendAll();
}
