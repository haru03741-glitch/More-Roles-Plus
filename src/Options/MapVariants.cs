using System;
using AmongUs.GameOptions;

namespace MoreRolesPlus.Options;

// ゲームの頁のマップ選びの下に並べる、札を選ぶ行の中身。
// - バリエーション (通常 / 左右反転): どのマップにも掛かる
// - モード (クラシック / かくれんぼ / サドンデス): 本編は部屋の作成時にしか選べないモードをロビーで替える。
//   本編の設定の項目はモードごとに違うので、替えたら設定画面を開き直す。サドンデスは本編のクラシックの上の MRP のモード
internal static class MapVariants
{
    public static readonly Text Title = new("MapVariants.Title");

    internal sealed class ChipRow
    {
        public Func<string> Label;
        public (Func<string> Name, int Index)[] Items;
        public Func<int> Current;
        public Action<int> Select;
    }

    internal static readonly ChipRow[] Rows =
    {
        new()
        {
            Label = () => new Text("MapVariants.Variant"),
            Items = new (Func<string>, int)[] { (() => new Text("MapVariants.Normal"), 0), (() => new Text("MapVariants.Mirrored"), 1) },
            Current = () => MapSettings.Mirror.Index,
            Select = i =>
            {
                MapSettings.Mirror.SetIndex(i);
                SettingsMenu.RefreshAll();
            },
        },
        new()
        {
            Label = () => new Text("GameMode.Label"),
            Items = new (Func<string>, int)[]
            {
                (() => ModeName(GameModes.Normal), (int)GameModes.Normal),
                (() => ModeName(GameModes.HideNSeek), (int)GameModes.HideNSeek),
                (() => new Text("SuddenDeath.Chip"), Rules.SuddenDeath.ChipIndex),
            },
            Current = () => Rules.SuddenDeath.Active ? Rules.SuddenDeath.ChipIndex : (int)GameModeSwitch.Current,
            Select = i =>
            {
                bool sudden = i == Rules.SuddenDeath.ChipIndex;
                var mode = sudden ? GameModes.Normal : (GameModes)i;
                if (Rules.SuddenDeath.Enabled != sudden)
                {
                    Rules.SuddenDeath.Enabled.SetIndex(sudden ? 1 : 0);
                    if (GameModeSwitch.Current == mode) SettingsMenu.RefreshAll();
                }
                SelectMode(mode);
            },
        },
    };

    // 本編の部屋の作成画面と同じ名前 (クラシック / かくれんぼ)
    private static string ModeName(GameModes mode)
    {
        var names = GameModesHelpers.ModeToName;
        return names != null && names.ContainsKey(mode) ? TranslationController.Instance.GetString(names[mode]) : mode.ToString();
    }

    private static void SelectMode(GameModes mode)
    {
        if (GameModeSwitch.Current == mode) return;
        string r = GameModeSwitch.Switch(mode);
        Plugin.Logger.LogInfo($"game mode: {r}");
        if (GameModeSwitch.Current != mode) { SettingsMenu.RefreshAll(); return; }
        var menu = GameSettingMenu.Instance;
        if (!menu) return;
        menu.Close();
        GameModeSwitch.ReopenSettings();
    }
}
