using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 本編の「ゲーム設定」タブのマップ選びの真下に、札を選ぶ行を並べる。
// - バリエーション (通常 / 左右反転): どのマップにも掛かる
// - モード (クラシック / かくれんぼ): 本編は部屋の作成時にしか選べないモードをロビーで替える。
//   本編のタブの中身はモードごとに違うので、替えたら設定画面を開き直す
// 札の行を足した分だけ、マップ選びより下の行を下へずらす。
// 札は行と一緒にスクロールするので、絵と字は本編の行の切り抜きのマテリアルを使い、押す所も本編の行と同じ範囲で切る
internal static class MapVariants
{
    public static readonly Text Title = new("MapVariants.Title");

    private const float RowH = 0.5f, ChipW = 1.25f, ChipH = 0.36f, ChipGap = 0.12f, Edge = 0.025f;
    private const int MaskLayer = 20;
    private static readonly Color Accent = new(0.5f, 0.85f, 0.75f);
    private static readonly Color Dark = new(0.11f, 0.12f, 0.15f, 1f);

    private sealed class ChipRow
    {
        public string Name;
        public Func<string> Label;
        public (Func<string> Name, int Index)[] Items;
        public Func<int> Current;
        public Action<int> Select;
    }

    private static readonly ChipRow[] Rows =
    {
        new()
        {
            Name = "MrpMapVariants",
            Label = () => new Text("MapVariants.Variant"),
            Items = new (Func<string>, int)[] { (() => new Text("MapVariants.Normal"), 0), (() => new Text("MapVariants.Mirrored"), 1) },
            Current = () => MapSettings.Mirror.Index,
            Select = i =>
            {
                MapSettings.Mirror.SetIndex(i);
                Refresh();
                SettingsMenu.RefreshAll();
            },
        },
        new()
        {
            Name = "MrpGameMode",
            Label = () => new Text("GameMode.Label"),
            Items = new (Func<string>, int)[] { (() => ModeName(GameModes.Normal), (int)GameModes.Normal), (() => ModeName(GameModes.HideNSeek), (int)GameModes.HideNSeek) },
            Current = () => (int)GameModeSwitch.Current,
            Select = i => SelectMode((GameModes)i),
        },
    };

    private sealed class Chip
    {
        public ChipRow Row;
        public int Index;
        public SpriteRenderer Fill, EdgeSr;
        public TextMeshPro Label;
    }

    private static readonly List<Chip> Chips = new();
    private static readonly Dictionary<(ChipRow, int), UnityAction> Clicks = new();

    // 本編がゲーム設定のタブの行を作った後
    public static void Attach(GameOptionsMenu m)
    {
        Chips.Clear();
        var picker = m.MapPicker;
        var container = m.settingsContainer;
        if (!picker || !container) return;
        foreach (var r in Rows)
        {
            var old = container.Find(r.Name);
            if (old) Object.DestroyImmediate(old.gameObject);
        }

        // 行の部品 (切り抜きのマテリアルを写す元)
        NumberOption rowProto = null;
        for (int i = 0; i < container.childCount && rowProto == null; i++)
            rowProto = container.GetChild(i).GetComponent<NumberOption>();
        if (rowProto == null) return;
        var bg = rowProto.transform.Find("LabelBackground");
        var mat = MenuArt.Masked(bg ? bg.GetComponent<SpriteRenderer>() : null);

        // マップ選びより下の物を札の行の分だけ下げる
        float pickerY = picker.transform.localPosition.y;
        float shift = RowH * Rows.Length;
        for (int i = 0; i < container.childCount; i++)
        {
            var c = container.GetChild(i);
            if (c == picker.transform) continue;
            var p = c.localPosition;
            if (p.y < pickerY + 0.3f) c.localPosition = new Vector3(p.x, p.y - shift, p.z);
        }
        var sb = m.scrollBar;
        if (sb) sb.SetYBoundsMax(sb.ContentYBounds.max + shift);

        var label = picker.transform.Find("Label");
        float y = pickerY + 0.12f;
        foreach (var row in Rows)
        {
            AttachRow(m, row, y, label, rowProto.TitleText, mat);
            y -= RowH;
        }
        Refresh();
    }

    private static void AttachRow(GameOptionsMenu m, ChipRow row, float y, Transform label, TextMeshPro textProto, Material mat)
    {
        var picker = m.MapPicker;
        var root = new GameObject(row.Name) { layer = picker.gameObject.layer };
        root.transform.SetParent(m.settingsContainer, false);
        root.transform.localPosition = new Vector3(0f, y, -2f);

        // 左の見出し (本編の「マップ」の札を写す)
        if (label)
        {
            var l = Object.Instantiate(label.gameObject, root.transform);
            l.name = "MrpChipRowLabel";
            var lp = label.localPosition;
            float px = picker.transform.localPosition.x;
            l.transform.localPosition = new Vector3(px + lp.x, 0f, lp.z);
            var tmp = l.GetComponentInChildren<TextMeshPro>(true);
            if (tmp)
            {
                var tr = tmp.GetComponent<TextTranslatorTMP>();
                if (tr) Object.Destroy(tr);
                tmp.enableAutoSizing = true;
                tmp.fontSizeMin = 1f;
                tmp.fontSizeMax = tmp.fontSize;
                tmp.text = row.Label();
            }
        }

        float x0 = 0.1f + ChipW / 2f;
        for (int i = 0; i < row.Items.Length; i++)
        {
            var chip = MakeChip(m, root.transform, new Vector3(x0 + i * (ChipW + ChipGap), 0f, 0f), textProto, mat, row.Items[i].Name());
            chip.Row = row;
            chip.Index = row.Items[i].Index;
            var go = chip.Fill.transform.parent.gameObject;
            var col = go.AddComponent<BoxCollider2D>();
            col.size = new Vector2(ChipW, ChipH);
            var pb = go.AddComponent<PassiveButton>();
            pb.Colliders = new Collider2D[] { col };
            pb.ClickMask = m.ButtonClickMask;
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            int index = chip.Index;
            if (!Clicks.TryGetValue((row, index), out var a)) Clicks[(row, index)] = a = (UnityAction)(() => row.Select(index));
            pb.OnClick.AddListener(a);
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
            Chips.Add(chip);
        }
    }

    private static Chip MakeChip(GameOptionsMenu m, Transform parent, Vector3 pos, TextMeshPro textProto, Material mat, string text)
    {
        var go = new GameObject("MrpVariantChip") { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var chip = new Chip
        {
            EdgeSr = MenuArt.Panel(go.transform, "Edge", ChipH / 2f, new Vector2(ChipW, ChipH), new Vector3(0f, 0f, 0.1f), Accent, mat),
            Fill = MenuArt.Panel(go.transform, "Fill", ChipH / 2f - Edge, new Vector2(ChipW - Edge * 2f, ChipH - Edge * 2f), Vector3.zero, Dark, mat),
        };
        if (textProto)
        {
            var t = Object.Instantiate(textProto, go.transform);
            t.name = "Label";
            t.transform.localScale = Vector3.one;
            t.transform.localPosition = new Vector3(0f, 0.005f, -0.1f);
            MenuFont.Apply(t, MaskLayer);
            var rt = t.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(ChipW - 0.12f, ChipH);
            t.enableWordWrapping = false;
            t.enableAutoSizing = true;
            t.fontSizeMin = 0.8f;
            t.fontSizeMax = 1.5f;
            t.alignment = TextAlignmentOptions.Center;
            t.text = text;
            chip.Label = t;
        }
        return chip;
    }

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
        if (GameModeSwitch.Current != mode) { Refresh(); return; }
        var menu = GameSettingMenu.Instance;
        if (!menu) return;
        menu.Close();
        GameModeSwitch.ReopenSettings();
    }

    // 札の見た目を今の設定に合わせる (選ばれた札 = 色の地 + 暗い字)
    public static void Refresh()
    {
        foreach (var c in Chips)
        {
            bool on = c.Index == c.Row.Current();
            if (c.Fill) c.Fill.color = on ? Accent : Dark;
            if (c.EdgeSr) c.EdgeSr.color = on ? new Color(0.8f, 0.97f, 0.92f, 1f) : new Color(Accent.r * 0.75f, Accent.g * 0.75f, Accent.b * 0.75f, 1f);
            if (c.Label) c.Label.color = on ? new Color(0.08f, 0.08f, 0.1f, 1f) : new Color(0.75f, 0.93f, 0.88f, 1f);
        }
    }

    public static void Forget() => Chips.Clear();

    // 確かめ用: 札を押したのと同じことをする (番号は上の行から順に 0, 1, 2…)
    internal static string Press(int index)
    {
        if (index < 0 || index >= Chips.Count) return $"no chip (chips={Chips.Count})";
        var c = Chips[index];
        c.Row.Select(c.Index);
        return $"mirror={MapSettings.Mirror.Value} mode={GameModeSwitch.Current}";
    }
}
