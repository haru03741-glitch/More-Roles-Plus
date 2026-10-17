using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 設定画面の「プリセット」のタブ。
//   上: 名前の欄と「保存」・知らせの文と「取り込む」(受け渡しのフォルダから)・「フォルダを開く」
//   中: 保存したプリセットの一覧 (1 ページ 5 件・行ごとに 読み込む・書き出す・消す) / 下: ページ送り
// 消すは 2 回押した時だけ消す。ボタンの処理は番号ごとに 1 つだけ作って使い回す (何も捕まえない)
internal static class PresetPage
{
    public static readonly Text TabName = new("プリセット", "Presets");
    public static readonly Color TabColor = new(0.45f, 0.85f, 0.6f);
    public static readonly Text Description = new("設定に名前を付けて保存・読み込み。書き出し・取り込みは受け渡しのフォルダ (デスクトップの MRP_Presets) を使います。",
        "Save and load settings by name. Export / import goes through the MRP_Presets folder on the desktop.");

    private const int PerPage = 5;
    private const float Top = 2f, RowStep = 0.45f, Z = -2f;
    private const float SX = 0.32f, SY = 0.55f; // ボタンの縮み (本編のタブのボタンを写して使う)

    private sealed class Row
    {
        public TextMeshPro Name;
        public PassiveButton Load, Export, Delete;
        public TextMeshPro DeleteLabel;
    }

    private static readonly Row[] Rows = new Row[PerPage];
    private static SearchBox _name;
    private static TextMeshPro _status, _pageLabel;
    private static PassiveButton _prev, _next;
    private static List<string> _names = new();
    private static int _page, _armed = -1;
    private static readonly Dictionary<int, UnityAction> Actions = new();

    public static void Forget()
    {
        if (_name != null) SearchBox.Forget(_name);
        _name = null;
        _status = _pageLabel = null;
        _prev = _next = null;
        for (int i = 0; i < PerPage; i++) Rows[i] = null;
        _armed = -1;
    }

    public static void Build(GameOptionsMenu m, GameSettingMenu menu)
    {
        Forget();
        var proto = menu.GameSettingsButton;
        var parent = m.settingsContainer;

        var h = Object.Instantiate(m.categoryHeaderOrigin, parent);
        h.SetHeader(StringNames.RolesCategory, 20);
        h.Title.text = TabName;
        h.transform.localScale = new Vector3(0.63f, 0.63f, 1f);
        h.transform.localPosition = new Vector3(-0.903f, Top, Z);

        _name = SearchBox.Create(parent, new Vector3(0f, Top - 0.6f, Z), 3.4f, 0.42f, new Text("新しいプリセットの名前", "Name for a new preset"), _ => { });
        Button(proto, parent, new Vector3(2.65f, Top - 0.6f, Z), new Text("今の設定を保存", "Save current"), 0, new Color(0.2f, 0.6f, 0.9f));

        _status = Label(menu.MenuDescriptionText, parent, new Vector3(-1.9f, Top - 1.12f, Z), 2.9f, 1.0f);
        _status.text = "";
        Button(proto, parent, new Vector3(1.8f, Top - 1.12f, Z), new Text("取り込む", "Import"), 1, new Color(0.2f, 0.6f, 0.9f));
        Button(proto, parent, new Vector3(3.05f, Top - 1.12f, Z), new Text("フォルダを開く", "Open folder"), 2, new Color(0.45f, 0.45f, 0.5f));

        for (int i = 0; i < PerPage; i++)
        {
            float y = Top - 1.7f - i * RowStep;
            var r = new Row { Name = Label(menu.MenuDescriptionText, parent, new Vector3(-1.9f, y, Z), 2.9f, 1.4f) };
            r.Load = Button(proto, parent, new Vector3(1.25f, y, Z), new Text("読み込む", "Load"), 100 + i * 4, new Color(0.2f, 0.7f, 0.4f));
            r.Export = Button(proto, parent, new Vector3(2.3f, y, Z), new Text("書き出す", "Export"), 101 + i * 4, new Color(0.45f, 0.45f, 0.5f));
            r.Delete = Button(proto, parent, new Vector3(3.35f, y, Z), new Text("消す", "Delete"), 102 + i * 4, new Color(0.7f, 0.25f, 0.25f));
            r.DeleteLabel = r.Delete ? r.Delete.GetComponentInChildren<TextMeshPro>() : null;
            Rows[i] = r;
        }

        float by = Top - 1.7f - PerPage * RowStep + 0.05f;
        _prev = Button(proto, parent, new Vector3(-1.4f, by, Z), new Text("◀", "◀"), 3, new Color(0.35f, 0.35f, 0.4f));
        _pageLabel = Label(menu.MenuDescriptionText, parent, new Vector3(-0.85f, by, Z), 0.8f, 1.3f);
        _next = Button(proto, parent, new Vector3(0.2f, by, Z), new Text("▶", "▶"), 4, new Color(0.35f, 0.35f, 0.4f));

        m.scrollBar.SetYBoundsMax(0f);
        Refresh();
    }

    private static PassiveButton Button(PassiveButton proto, Transform parent, Vector3 pos, string label, int id, Color c)
    {
        var b = Object.Instantiate(proto, parent);
        b.name = "MrpPreset" + id;
        b.transform.localPosition = pos;
        b.transform.localScale = new Vector3(SX, SY, 1f);
        var t = b.GetComponentInChildren<TextMeshPro>();
        if (t)
        {
            var tr = t.GetComponent<TextTranslatorTMP>();
            if (tr) Object.Destroy(tr);
            t.text = label;
            t.enableAutoSizing = true;
            t.fontSizeMin = 1f;
            t.fontSizeMax = t.fontSize;
            t.alignment = TextAlignmentOptions.Center;
            // 横に潰れないように縦横の縮みを揃える
            var ls = t.transform.localScale;
            t.transform.localScale = new Vector3(ls.x * SY / SX, ls.y, ls.z);
            t.rectTransform.sizeDelta = new Vector2(t.rectTransform.sizeDelta.x * SX / SY, t.rectTransform.sizeDelta.y);
        }
        foreach (var go in new[] { b.inactiveSprites, b.activeSprites, b.selectedSprites })
        {
            var sr = go ? go.GetComponent<SpriteRenderer>() : null;
            if (sr) sr.color = go == b.inactiveSprites ? new Color(c.r * 0.7f, c.g * 0.7f, c.b * 0.7f, 1f) : c;
        }
        b.OnMouseOver = new UnityEvent();
        b.OnMouseOut = new UnityEvent();
        b.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        if (!Actions.TryGetValue(id, out var a)) Actions[id] = a = (UnityAction)(() => Click(id));
        b.OnClick.AddListener(a);
        b.SelectButton(false);
        return b;
    }

    private static TextMeshPro Label(TextMeshPro proto, Transform parent, Vector3 pos, float width, float size)
    {
        var t = Object.Instantiate(proto, parent);
        t.name = "MrpPresetLabel";
        var tr = t.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        t.transform.localPosition = pos;
        t.transform.localScale = Vector3.one;
        t.rectTransform.pivot = new Vector2(0f, 0.5f);
        t.rectTransform.sizeDelta = new Vector2(width, 0.45f);
        t.alignment = TextAlignmentOptions.Left;
        t.enableWordWrapping = true;
        t.enableAutoSizing = true;
        t.fontSizeMin = 0.8f;
        t.fontSizeMax = size * 1.4f;
        t.color = Color.white;
        t.fontStyle = FontStyles.Normal; // 見出しの文字の斜体・大文字を外す
        return t;
    }

    private static void Refresh()
    {
        _names = PresetStore.Names();
        int pages = Math.Max(1, (_names.Count + PerPage - 1) / PerPage);
        _page = Math.Clamp(_page, 0, pages - 1);
        // 1 ページに収まる時はページ送りを出さない
        if (_pageLabel) _pageLabel.text = pages > 1 ? $"{_page + 1}/{pages}" : "";
        if (_prev) _prev.gameObject.SetActive(pages > 1);
        if (_next) _next.gameObject.SetActive(pages > 1);
        for (int i = 0; i < PerPage; i++)
        {
            var r = Rows[i];
            if (r == null) continue;
            int n = _page * PerPage + i;
            bool has = n < _names.Count;
            if (r.Name) r.Name.text = has ? _names[n] : (i == 0 && _names.Count == 0 ? (string)new Text("保存したプリセットはありません", "No saved presets") : "");
            if (r.Load) r.Load.gameObject.SetActive(has);
            if (r.Export) r.Export.gameObject.SetActive(has);
            if (r.Delete) r.Delete.gameObject.SetActive(has);
            if (r.DeleteLabel) r.DeleteLabel.text = _armed == i ? new Text("本当に消す", "Really?") : new Text("消す", "Delete");
        }
    }

    private static void Say(string s)
    {
        if (_status) _status.text = s;
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
                Say(new Text($"受け渡しのフォルダ: {dir}", $"Share folder: {dir}"));
            }
                break;
            case 3: _page--; break;
            case 4: _page++; break;
            default:
            {
                int slot = (id - 100) / 4, kind = (id - 100) % 4;
                int n = _page * PerPage + slot;
                if (slot < 0 || slot >= PerPage || n >= _names.Count) break;
                string name = _names[n];
                if (kind == 0) Say(AmHost ? PresetStore.Load(name) : new Text("読み込めるのはホストだけです", "Only the host can load presets"));
                else if (kind == 1) Say(PresetStore.Export(name));
                else if (kind == 2)
                {
                    if (armed != slot) { _armed = slot; Say(new Text($"もう一度押すと「{name}」を消します", $"Press again to delete \"{name}\"")); }
                    else Say(PresetStore.Delete(name));
                }
                break;
            }
        }
        Refresh();
    }

    // 試験用: 名前を入れる / ボタンを押す (0 保存・1 取り込む・2 フォルダ・3 前・4 次・100+行*4+{0 読む,1 書き出す,2 消す})
    internal static string Simulate(string args)
    {
        if (args.StartsWith("name ")) { if (_name != null) _name.Type(args[5..]); }
        else if (int.TryParse(args, out int id)) Click(id);
        return $"OK presetui built={(_status != null)} page={_page} names=[{string.Join(",", _names)}] status=[{(_status ? _status.text : "")}]";
    }
}
