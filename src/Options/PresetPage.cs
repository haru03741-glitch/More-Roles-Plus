using System;
using System.Collections.Generic;
using MoreRolesPlus.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 設定画面の「プリセット」の頁。
//   上: 新しいプリセットの名前の欄と知らせの文
//   中: 保存したプリセットの枠 (3 列・クルーの絵・保存した日時・読み込む / 書き出す / 消す)。今の設定のままのプリセットは金の縁。
//       最後の枠は「今の設定を保存」(名前の欄の名前で)
//   下: 取り込む (受け渡しのフォルダから)・フォルダを開く・コードをコピー・コードを読む
// 消すは 2 回押した時だけ消す。押すたびに頁を作り直す (スクロールの位置は残す)
internal static class PresetPage
{
    public static readonly Text TabName = new("PresetPage.TabName");
    public static readonly Color TabColor = new(0.45f, 0.85f, 0.6f);
    public static readonly Text Description = new("PresetPage.Description");

    // 頁の置き場 (タブの GameOptionsMenu の座標で・役職の頁と同じ)
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    private const float NameW = 3.4f, NameH = 0.42f, Gap = 0.12f, SlotH = 1.3f, BarH = 0.4f;
    private const int Cols = 3;

    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color Gold = new(1f, 0.82f, 0.2f, 1f);
    private static readonly Color Dim = new(0.42f, 0.42f, 0.45f, 1f);
    private static readonly Color Blue = new(0.4f, 0.75f, 1f), Green = new(0.45f, 0.9f, 0.55f), Red = new(1f, 0.45f, 0.45f);
    // 枠のクルーの色 (並び順で回す)
    private static readonly Color[] CrewColors =
    {
        new(0.37f, 0.88f, 1f), new(1f, 0.25f, 0.25f), new(1f, 0.77f, 0.2f), new(0.7f, 0.53f, 1f), new(0.45f, 0.9f, 0.55f), new(1f, 0.55f, 0.75f),
    };

    private static GameObject _root;
    private static UiScreen _screen;
    private static SearchBox _name;
    private static string _status = "";
    private static List<string> _names = new();
    private static int _armed = -1;

    public static void Forget()
    {
        if (_name != null) SearchBox.Forget(_name);
        _name = null;
        _screen?.Clear();
        _screen = null;
        _root = null;
        _armed = -1;
        _status = "";
    }

    public static void Build(GameOptionsMenu m, GameSettingMenu menu)
    {
        Forget();
        if (!m || !UiAssets.Box) return;
        // タブの元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        _root = Widget.Node("MrpPresetPage", m.transform, m.gameObject.layer);
        _root.transform.localPosition = Center;
        _root.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        // 名前の欄は作り直さない (打った字を残す)。欄の中の字と地の前後は z で決まる作りなので、頁の SortingGroup の外に置く
        _name = SearchBox.Create(m.transform, Center + new Vector3(-W / 2f + NameW / 2f, H / 2f - NameH / 2f, -0.05f), NameW, NameH, new Text("PresetPage.NameForANewPreset"), _ => { });
        var body = Widget.Node("Body", _root.transform, m.gameObject.layer);
        _screen = new UiScreen(body.transform) { MaxWidth = W, BaseOrder = 1 };
        Refresh();
    }

    private static void Refresh()
    {
        if (_screen == null || !_screen.Alive) return;
        _names = PresetStore.Names();
        _screen.Set(Page());
    }

    private static Widget Page()
    {
        // 上の段: 名前の欄 (別に置いた物) の場所を空け、右に知らせの文
        string status = _status.Length > 0 ? _status : _names.Count == 0 ? new Text("PresetPage.NoSavedPresets") : "";
        var top = new Stack(true,
            new Spacer(NameW, NameH),
            new Label(status, 1.15f) { Color = SubText, Width = W - NameW - 0.15f, MinFontSize = 0.8f }) { Gap = 0.15f };

        float slotW = (W - 0.2f - Gap * (Cols - 1)) / Cols;
        var grid = new Stack(false) { Gap = Gap, Width = W - 0.2f, Align = Align.Start };
        Stack row = null;
        for (int i = 0; i <= _names.Count; i++)
        {
            if (i % Cols == 0) { row = new Stack(true) { Gap = Gap, Align = Align.Start }; grid.Add(row); }
            row.Add(i < _names.Count ? Slot(i, slotW) : SaveSlot(slotW));
        }
        float listH = H - NameH - BarH - 0.24f;
        var list = new ScrollView(new Vector2(W, listH), grid) { Tag = "presets" };

        float bw = (W - 0.1f * 3) / 4f;
        var bar = new Stack(true,
            new Button(new Text("PresetPage.Import"), () => Click(1), 1.3f) { Size = new Vector2(bw, BarH), Color = Blue },
            new Button(new Text("PresetPage.OpenFolder"), () => Click(2), 1.3f) { Size = new Vector2(bw, BarH), Color = Dim },
            new Button(new Text("PresetPage.CopyCode"), () => Click(5), 1.3f) { Size = new Vector2(bw, BarH), Color = Blue },
            new Button(new Text("PresetPage.PasteCode"), () => Click(6), 1.3f) { Size = new Vector2(bw, BarH), Color = Green }) { Gap = 0.1f };
        return new Stack(false, top, list, bar) { Gap = 0.12f, Width = W };
    }

    // 保存したプリセット 1 つの枠
    private static Widget Slot(int n, float w)
    {
        string name = _names[n];
        bool current = name == PresetLabel.Shown;
        var slot = new Layers(w, SlotH);
        slot.Add(new Box(null) { Size = new Vector2(w, SlotH), Color = current ? Gold : Color.white, Fill = current ? new Color(0.16f, 0.13f, 0.04f) : Color.black }, 0f, 0f);
        var sp = CrewArt.Pose(n % CrewArt.Count);
        var mat = sp ? CrewArt.Material(CrewColors[n % CrewColors.Length]) : null;
        if (mat) slot.Add(new Picture(sp, 0.75f, 0.68f) { Fit = true, Material = mat, Color = new Color(1f, 1f, 1f, 0.9f) }, 0.06f, 0.06f, true);

        float textW = w - 0.2f;
        var at = PresetStore.SavedAt(name);
        var text = new Stack(false, new Label(name, 1.8f) { MenuFont = true, Outline = 0.15f, Width = textW - 0.6f, MinFontSize = 1.1f, Align = Align.Start }) { Gap = 0f, Width = textW };
        if (at is DateTime t) text.Add(new Label(new Text("PresetPage.SavedAt").With("time", t.ToString("M/d HH:mm")), 1.0f) { Color = SubText, Width = textW, Align = Align.Start });
        slot.Add(text, 0.1f, 0.1f);

        int id = 100 + n * 4;
        float bw = (w - 0.2f - 0.06f * 2) / 3f;
        string del = _armed == n ? new Text("PresetPage.Really") : new Text("PresetPage.Delete");
        var buttons = new Stack(true,
            SmallButton(new Text("PresetPage.Load"), id, bw, Green),
            SmallButton(new Text("PresetPage.Export"), id + 1, bw, Dim),
            SmallButton(del, id + 2, bw, Red)) { Gap = 0.06f };
        slot.Add(buttons, 0.1f, 0.1f, false, true);
        return slot;
    }

    private static Button SmallButton(string text, int id, float w, Color c) =>
        new(new Label(text, 1.1f) { Width = w - 0.08f, MinFontSize = 0.7f, TextAlign = TMPro.TextAlignmentOptions.Center }, () => Click(id))
        {
            Size = new Vector2(w, 0.32f),
            Padding = new Vector2(0.04f, 0.02f),
            Color = c,
        };

    // 最後の枠: 今の設定を名前の欄の名前で保存する
    private static Widget SaveSlot(float w) =>
        new Button(new Label("＋ " + new Text("PresetPage.SaveCurrent"), 1.5f) { Width = w - 0.3f, MinFontSize = 1f, Color = SubText, TextAlign = TMPro.TextAlignmentOptions.Center }, () => Click(0))
        {
            Size = new Vector2(w, SlotH),
            Color = Dim,
        };

    private static void Say(string s)
    {
        _status = s ?? "";
        Plugin.Logger.LogInfo($"presets: {s}");
    }

    private static bool AmHost => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    private static void Click(int id)
    {
        try { DoClick(id); }
        catch (Exception e) { Plugin.Logger.LogError($"presets: {e}"); }
    }

    private static void DoClick(int id)
    {
        int armed = _armed;
        _armed = -1;
        switch (id)
        {
            case 0:
                Say(PresetStore.Save(_name != null ? _name.Query.Trim() : ""));
                break;
            case 1:
                Say(PresetStore.Import());
                break;
            case 2:
            {
                string dir = PresetStore.OpenShare();
                Say(new Text("PresetPage.ShareFolder").With("dir", dir));
            }
                break;
            case 5:
                Say(SettingCode.Copy());
                break;
            case 6:
                Say(SettingCode.Paste());
                break;
            default:
            {
                int n = (id - 100) / 4, kind = (id - 100) % 4;
                if (n < 0 || n >= _names.Count) break;
                string name = _names[n];
                if (kind == 0) Say(AmHost ? PresetStore.Load(name) : new Text("PresetPage.OnlyTheHostCanLoad"));
                else if (kind == 1) Say(PresetStore.Export(name));
                else if (kind == 2)
                {
                    if (armed != n) { _armed = n; Say(new Text("PresetPage.PressAgainToDelete").With("name", name)); }
                    else Say(PresetStore.Delete(name));
                }
                break;
            }
        }
        Refresh();
    }

    // 試験用: 名前を入れる / ボタンを押す (0 保存・1 取り込む・2 フォルダ・5 コードをコピー・6 コードを読む・100+番号*4+{0 読む,1 書き出す,2 消す})
    internal static string Simulate(string args)
    {
        if (args.StartsWith("name ")) { if (_name != null) _name.Type(args[5..]); }
        else if (int.TryParse(args, out int id)) Click(id);
        return $"OK presetui built={(_screen != null)} names=[{string.Join(",", _names)}] current={PresetLabel.Shown} status=[{_status}]";
    }
}
