using System;
using BepInEx.Configuration;
using HarmonyLib;
using MoreRolesPlus.Roles.Builtin;
using TMPro;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// ロビーの右の板の「プリセット」の欄に、MRP のプリセットの名前を出す。
// - 読み込んだ / 保存したプリセットと今の設定が同じ間だけ名前を出す。違えば「カスタム」。
//   本編の表記 (本編のプリセットの名前) は使わない: 本編のプリセットの画面は無く、部屋の作成画面が本編の設定を
//   「コア設定」にするので、MRP の設定と関係なく「コア設定」と出てしまう。
// - 同じかどうかは設定の指紋 (MRP の全項目・本編のゲーム設定・本編の役職の細かい設定・マップ) で見る。
//   値を一度変えて元に戻した時も、同じ設定なら名前に戻る。
// - 名前はホストが決めて、設定と一緒に全員へ送る (OptionSync)。客は届いた名前を出すだけ
internal static class PresetLabel
{
    private static ConfigEntry<string> _last; // "名前|指紋" (最後に読み込んだ / 保存したプリセット)

    private static string _written; // 写した字に最後に書いた名前 (同じなら書き直さない)
    private static int _writtenTo;

    // 出している名前 (null = 合うプリセットなし)
    public static string Shown { get; private set; }

    private static readonly Text CustomName = new("カスタム", "Custom");

    public static void Bind(ConfigFile cfg) =>
        _last = cfg.Bind("Client", "LastPreset", "", "The preset last loaded or saved, and a fingerprint of its settings");

    private static bool IsGuest => AmongUsClient.Instance && AmongUsClient.Instance.AmConnected && !AmongUsClient.Instance.AmHost;

    // プリセットを読み込んだ / 保存した直後
    public static void Remember(string name)
    {
        if (_last == null) return;
        _last.Value = name + "|" + Fingerprint().ToString("x8");
        Recheck();
    }

    // ホスト (か部屋の外) で、今の設定が覚えたプリセットのままかを見直す
    public static void Recheck()
    {
        if (IsGuest) return;
        Set(Compute());
    }

    private static string Compute()
    {
        string v = _last?.Value;
        if (string.IsNullOrEmpty(v)) return null;
        int bar = v.LastIndexOf('|');
        if (bar <= 0) return null;
        string name = v[..bar];
        return PresetStore.Exists(name) && v[(bar + 1)..] == Fingerprint().ToString("x8") ? name : null;
    }

    // 客: ホストから届いた名前 ("" = 名前なし)
    public static void Receive(string name) => Set(string.IsNullOrEmpty(name) ? null : name);

    private static void Set(string name)
    {
        Shown = name;
        Apply();
    }

    // 板の欄に出す。本編は欄の字を毎フレーム書き直すので、本編の字は隠して、同じ所に写した字を重ねる
    public static void Apply()
    {
        var pane = Vanilla.LobbyInfo;
        if (!pane) return;
        var t = pane.transform.Find("AspectSize/ModeValue/GameModeText");
        var tmp = t ? t.GetComponent<TextMeshPro>() : null;
        if (!tmp) return;
        var mine = t.parent.Find("MrpPresetText");
        string label = Shown ?? CustomName;
        if (!mine)
        {
            var copy = Object.Instantiate(tmp, t.parent);
            copy.name = "MrpPresetText";
            copy.transform.localPosition = t.localPosition;
            copy.enableAutoSizing = true;
            copy.fontSizeMin = 1f;
            copy.fontSizeMax = 3.1f; // 本編の欄の字の大きさ (本編は書き直す時に字の大きさも変える)
            mine = copy.transform;
        }
        if (!mine.gameObject.activeSelf) mine.gameObject.SetActive(true);
        if (!ReferenceEquals(_written, label) || mine.GetInstanceID() != _writtenTo)
        {
            mine.GetComponent<TextMeshPro>().text = label;
            _written = label;
            _writtenTo = mine.GetInstanceID();
        }
        if (tmp.enabled) tmp.enabled = false;
    }

    // 設定の指紋 (FNV-1a)。本編の設定は画面の行と同じ番号で数える
    private static uint Fingerprint()
    {
        uint h = 2166136261;
        void Add(int v)
        {
            unchecked
            {
                for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (i * 8)); h *= 16777619; }
            }
        }
        foreach (var o in Registry.All) Add(o.Index);
        if (VanillaGame.All.Count == 0) VanillaGame.Rebuild();
        foreach (var sec in VanillaGame.All)
            foreach (var o in sec.Opts) { o.Pull(); Add(o.Index); }
        VanillaRoles.EnsureSettings();
        foreach (var tab in new[] { Tab.Crew, Tab.Impostor })
            foreach (var sec in Registry.SectionsOf(tab))
                foreach (var o in sec.Opts)
                    if (o is VanillaOpt v) { v.Pull(); Add(v.Index); }
        var opts = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opts != null) Add(opts.MapId);
        return h;
    }
}

[HarmonyPatch]
internal static class PresetLabelPatches
{
    // 本編が板を書き直した後に名前を書き戻す
    [HarmonyPatch(typeof(LobbyInfoPane), nameof(LobbyInfoPane.RefreshPane)), HarmonyPostfix]
    public static void Refresh()
    {
        try { PresetLabel.Apply(); }
        catch (Exception e) { Plugin.Logger.LogError($"preset label: {e.Message}"); }
    }

    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start)), HarmonyPostfix]
    public static void LobbyStart()
    {
        try { PresetLabel.Recheck(); }
        catch (Exception e) { Plugin.Logger.LogError($"preset label: {e.Message}"); }
    }
}
