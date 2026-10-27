using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Options;

// ロビーで MRP の設定が変わった時に、本編の設定の通知と同じ枠 (画面の左下) へ「何が何に変わったか」を出す。
// 差分は同期の前後で OptionSync が取る (ホスト = 前に送った値と・客 = 受け取った値を書き込む前と)。
// 本編の設定 (VanillaOpt) は MRP の同期に入らず、本編が自分で知らせる。
internal static class SettingNotice
{
    private static readonly Text Changed = new("SettingNotice.Changed");
    private static readonly Text Others = new("SettingNotice.Others");
    private static readonly Text PresetLoaded = new("SettingNotice.Preset");

    private const int MaxShown = 5;

    // 客の側で、ホストが直前に変えた項目の数 (設定の画面と辞典とロビーの一覧で印を付ける)
    public static int FreshCount { get; private set; }

    // 続けて届いた変更は 1 回分にまとめる (設定画面を閉じた時と、プリセットや人数の調整が続けて送られる時)
    private const float MergeSeconds = 5f;
    private static float _lastFresh = -100f;

    // 受け取った 1 回分の変更を印にする (前の印は外す)。別の部屋へ移った・自分がホストになった時は印を全部外す
    public static void MarkFresh(List<Opt> changed)
    {
        if (changed.Count == 0 && FreshCount == 0) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        bool merge = changed.Count > 0 && now - _lastFresh < MergeSeconds;
        _lastFresh = changed.Count > 0 ? now : -100f;
        if (!merge) foreach (var o in Registry.All) o.Fresh = false;
        foreach (var o in changed) o.Fresh = true;
        int n = 0;
        foreach (var o in Registry.All) if (o.Fresh) n++;
        FreshCount = n;
        RolePage.RefreshCards();
        Menu.RoleDictionary.Refresh();
    }

    public static void ClearFresh() => MarkFresh(new List<Opt>());

    // preset = 読み込んだプリセットの名前 (プリセットへ切り替わった時だけ・個別の項目は並べない)
    public static void Show(List<Opt> changed, string preset, bool sound)
    {
        if (changed.Count == 0) return;
        var client = AmongUsClient.Instance;
        // 開始の直前の送り直し (役職を配る前) でも通るので、ロビーにいる間だけ
        if (!client || client.GameState != InnerNet.InnerNetClient.GameStates.Joined || !LobbyBehaviour.Instance) return;
        var hud = Vanilla.Hud;
        if (!hud || !hud.Notifier) return;
        var notifier = hud.Notifier;
        try
        {
            if (preset != null)
            {
                Post(notifier, PresetLoaded.With("name", preset).With("count", changed.Count), sound);
                return;
            }
            // 本編は通知を一定の間隔で積むだけなので、並べすぎると左側を覆う。超えた分は「ほか N 件」の 1 通にまとめる
            int room = Math.Clamp(notifier.maxMessages, 2, MaxShown);
            int shown = changed.Count <= room ? changed.Count : room - 1;
            for (int i = 0; i < shown; i++) Post(notifier, Line(changed[i]), sound && i == 0);
            if (shown < changed.Count) Post(notifier, Others.With("count", changed.Count - shown), false);
            Plugin.Logger.LogInfo($"[settingnotice] changed={changed.Count} shown={shown}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"setting notice: {e.Message}"); }
    }

    private static string Line(Opt opt)
    {
        var sec = opt.Section;
        string where = sec == null ? null : sec.Color != null ? $"<color={sec.Color}>{sec.Title}</color>" : sec.Title;
        string name = string.IsNullOrEmpty(where) ? opt.Label : $"{where} / {opt.Label}";
        return Changed.With("name", name).With("value", opt.Display());
    }

    private static void Post(NotificationPopper notifier, string text, bool sound)
    {
        // 本編は直前と同じ鍵なら前の通知の文を書き換えるので、毎回新しい通知にする。
        // 鍵は本物の文の番号を渡す (偽の番号は本編が文を引くと落ちうる)
        notifier.lastMessageKey = -1;
        notifier.SettingsChangeMessageLogic(StringNames.LobbyChangeSettingNotification, text, sound);
    }
}
