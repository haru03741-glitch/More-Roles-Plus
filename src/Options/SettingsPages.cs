using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Builtin;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面の「ゲーム」(本編の設定と MRP のゲームのルール) と「全般」(MRP の部屋の管理など) の頁
internal static class SettingsPages
{
    public static readonly SectionPage General = new("generalpage", () => new Text("SettingsMenu.CatGeneral"),
        SettingsMenu.GeneralCatColor, SettingsMenu.GeneralPose, GeneralGroups);

    public static readonly SectionPage Game = new("gamepage", () => new Text("SettingsMenu.CatGame"),
        SettingsMenu.GameCatColor, SettingsMenu.GamePose, GameGroups, MapTop, VanillaGame.Rebuild, TopKey) { HeroSub = MapName };

    public static void Forget()
    {
        General.Forget();
        Game.Forget();
    }

    public static void RefreshMarks()
    {
        General.RefreshMarks();
        Game.RefreshMarks();
    }

    public static void RefreshFromOutside()
    {
        General.RefreshFromOutside();
        Game.RefreshFromOutside();
    }

    private static List<SectionPage.Group> GeneralGroups()
    {
        var list = new List<SectionPage.Group>();
        foreach (var sec in Registry.SectionsOf(Tab.General))
        {
            var g = new SectionPage.Group { Title = sec.Title.ToString() };
            if (sec.Color != null) g.Color = RoleDisplay.ParseColor(sec.Color);
            foreach (var opt in sec.Opts)
                if (!opt.OffTab) g.Opts.Add(opt);
            if (g.Opts.Count > 0) list.Add(g);
        }
        return list;
    }

    // 本編の設定の節の並びのまま、MRP の組をその前後と節の終わりに差し込む (かくれんぼでは本編の節だけ)
    private static List<SectionPage.Group> GameGroups()
    {
        var list = new List<SectionPage.Group>();
        bool normal = GameModeSwitch.IsNormal;
        if (normal)
            foreach (var t in GameTabRows.Top()) list.Add(Of(t));
        foreach (var sec in VanillaGame.All)
        {
            var g = new SectionPage.Group { Title = sec.Title.ToString() };
            bool impostors = false;
            foreach (var opt in sec.Opts)
            {
                if (normal && opt.IsImpostorCount)
                {
                    g.Opts.Add(ImpostorRange.Min);
                    impostors = true;
                }
                g.Opts.Add(opt);
            }
            if (normal)
                foreach (var (marker, extra) in GameTabRows.SectionRows)
                    if (sec.Opts.Exists(o => o.Key == "vanilla." + marker)) g.Opts.AddRange(extra);
            list.Add(g);
            if (impostors) list.Add(Of(GameTabRows.Neutral));
        }
        if (normal)
            foreach (var t in GameTabRows.Tail()) list.Add(Of(t));
        return list;
    }

    private static SectionPage.Group Of(GameTabRows.Group t)
    {
        var g = new SectionPage.Group { Title = t.Title.ToString() };
        g.Opts.AddRange(t.Opts);
        return g;
    }

    // ---- ゲームの頁の上の段: マップ・バリエーション・モード ----

    private static readonly Color Accent = new(0.5f, 0.85f, 0.75f, 1f);
    private static readonly Color ChipDark = new(0.08f, 0.08f, 0.1f, 1f);
    private const float LabelW = 1.0f, MapBtn = 0.62f, ChipH = 0.34f;

    private static IGameOptions Options => GameOptionsManager.Instance?.CurrentGameOptions;

    // 上の段の見た目が変わる値 (外で変えた時に組み直すかを決める)
    private static int TopKey()
    {
        var o = Options;
        int map = o != null ? o.MapId : -1;
        return map * 31 * 31 + MapSettings.Mirror.Index * 31 + (int)GameModeSwitch.Current;
    }

    private static Widget MapTop(bool edit)
    {
        float rowW = SectionPage.RowW;
        var col = new Stack(false) { Gap = 0.1f, Width = rowW, Align = Align.Start };
        var picker = GameSettingMenu.Instance && GameSettingMenu.Instance.GameSettingsTab ? GameSettingMenu.Instance.GameSettingsTab.MapPicker : null;
        var icons = picker ? picker.AllMapIcons : null;
        if (icons != null && icons.Count > 0)
        {
            int cur = Options != null ? Options.MapId : -1;
            // マップは名前の札を付けず、行の幅いっぱいに並べる (選んだマップの名前は右の見出しに出す)
            var line = new Stack(true) { Gap = 0.1f };
            foreach (var icon in icons)
            {
                if (icon == null || !icon.MapIcon) continue;
                int id = (int)icon.Name;
                bool on = id == cur;
                line.Add(new Button(new Picture(icon.MapIcon, MapBtn - 0.1f, MapBtn - 0.1f) { Fit = true }, () => SelectMap(id))
                {
                    Size = new Vector2(MapBtn, MapBtn),
                    Padding = Vector2.zero,
                    Color = on ? Accent : OptEditor.Dim,
                    Fill = on ? Color.Lerp(Color.black, Accent, 0.35f) : null,
                });
            }
            col.Add(new Stack(true, line) { Width = rowW });
        }
        foreach (var row in MapVariants.Rows)
        {
            var line = new Stack(true) { Gap = 0.08f, Align = Align.Start };
            line.Add(new Label(row.Label(), 1.35f) { Bold = true, Width = LabelW, MinFontSize = 0.9f });
            int cur = row.Current();
            float w = (rowW - LabelW - 0.08f * row.Items.Length) / row.Items.Length;
            foreach (var (name, index) in row.Items)
            {
                bool on = index == cur;
                var r = row;
                int i = index;
                line.Add(new Button(new Label(name(), 1.3f) { Bold = true, Color = on ? ChipDark : Color.white, Width = w - 0.12f, MinFontSize = 0.9f, TextAlign = TextAlignmentOptions.Center },
                    () => { if (CanEdit()) r.Select(i); })
                {
                    Size = new Vector2(w, ChipH),
                    Color = on ? Accent : Color.Lerp(Accent, Color.black, 0.3f),
                    Fill = on ? Accent : null,
                });
            }
            col.Add(line);
        }
        return col;
    }

    // 選んでいるマップの名前 (左右反転は名前の後ろに添える)
    private static string MapName()
    {
        var o = Options;
        if (o == null || !TranslationController.Instance) return "";
        StringNames? n = o.MapId switch
        {
            0 => StringNames.MapNameSkeld,
            1 => StringNames.MapNameMira,
            2 => StringNames.MapNamePolus,
            4 => StringNames.MapNameAirship,
            5 => StringNames.MapNameFungle,
            _ => null,
        };
        string name = n.HasValue ? TranslationController.Instance.GetString(n.Value) : "";
        return MapSettings.Mirror.Value && name.Length > 0 ? $"{name}・{new Text("MapVariants.Mirrored")}" : name;
    }

    private static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    // 本編のマップ選びと同じ設定の欄へ書く (同期と保存は画面を閉じた時に本編の仕組みで)
    private static void SelectMap(int id)
    {
        var o = Options;
        if (o == null || !CanEdit() || o.MapId == id) return;
        o.SetByte(ByteOptionNames.MapId, (byte)id);
        VanillaRoles.Dirty = true;
        SettingsMenu.RefreshAll();
    }

    // 試験用
    internal static string Command(SectionPage page, string[] a)
    {
        if (page == Game && a.Length > 1 && a[0] == "map" && int.TryParse(a[1], out int id))
        {
            SelectMap(id);
            return $"map={(Options != null ? Options.MapId : -1)}";
        }
        if (page == Game && a.Length > 2 && a[0] == "chip" && int.TryParse(a[1], out int r) && int.TryParse(a[2], out int i) && r >= 0 && r < MapVariants.Rows.Length && i >= 0 && i < MapVariants.Rows[r].Items.Length)
        {
            MapVariants.Rows[r].Select(MapVariants.Rows[r].Items[i].Index);
            return $"mirror={MapSettings.Mirror.Value} mode={GameModeSwitch.Current}";
        }
        if (page == Game && a.Length > 0 && a[0] == "imp")
        {
            var o = Options;
            return $"impostors={ImpostorRange.Min.Value}-{(o != null ? o.NumImpostors : -1)} neutrals={NeutralKillSettings.MinNeutrals.Value}-{NeutralKillSettings.MaxNeutrals.Value}";
        }
        return page.Command(a);
    }
}
