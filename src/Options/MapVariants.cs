using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 本編の「ゲーム設定」タブのマップ選びの真下に、マップのバリエーション (通常 / 左右反転) を選ぶ札を並べる。
// バリエーションはどのマップにも掛かる。札を足した分だけ、マップ選びより下の行を下へずらす。
// 札は行と一緒にスクロールするので、絵と字は本編の行の切り抜きのマテリアルを使い、押す所も本編の行と同じ範囲で切る
internal static class MapVariants
{
    public static readonly Text Title = new("マップのバリエーション", "Map variant");

    private const float RowH = 0.5f, ChipW = 1.25f, ChipH = 0.36f, ChipGap = 0.12f, Edge = 0.025f;
    private const int MaskLayer = 20;
    private static readonly Color Accent = new(0.5f, 0.85f, 0.75f);
    private static readonly Color Dark = new(0.11f, 0.12f, 0.15f, 1f);

    private static readonly (Text Name, int Index)[] Variants =
    {
        (new Text("通常", "Normal"), 0),
        (new Text("左右反転", "Mirrored"), 1),
    };

    private sealed class Chip
    {
        public int Index;
        public SpriteRenderer Fill, EdgeSr;
        public TextMeshPro Label;
    }

    private static readonly List<Chip> Chips = new();
    private static readonly Dictionary<int, UnityAction> Clicks = new();

    // 本編がゲーム設定のタブの行を作った後
    public static void Attach(GameOptionsMenu m)
    {
        Chips.Clear();
        var picker = m.MapPicker;
        var container = m.settingsContainer;
        if (!picker || !container) return;
        var old = container.Find("MrpMapVariants");
        if (old) Object.DestroyImmediate(old.gameObject);

        // 行の部品 (切り抜きのマテリアルを写す元)
        NumberOption rowProto = null;
        for (int i = 0; i < container.childCount && rowProto == null; i++)
            rowProto = container.GetChild(i).GetComponent<NumberOption>();
        if (rowProto == null) return;
        var bg = rowProto.transform.Find("LabelBackground");
        var mat = MenuArt.Masked(bg ? bg.GetComponent<SpriteRenderer>() : null);

        // マップ選びより下の物を札の分だけ下げる
        float pickerY = picker.transform.localPosition.y;
        for (int i = 0; i < container.childCount; i++)
        {
            var c = container.GetChild(i);
            if (c == picker.transform) continue;
            var p = c.localPosition;
            if (p.y < pickerY + 0.3f) c.localPosition = new Vector3(p.x, p.y - RowH, p.z);
        }
        var sb = m.scrollBar;
        if (sb) sb.SetYBoundsMax(sb.ContentYBounds.max + RowH);

        var root = new GameObject("MrpMapVariants") { layer = picker.gameObject.layer };
        root.transform.SetParent(container, false);
        float y = pickerY + 0.12f;
        root.transform.localPosition = new Vector3(0f, y, -2f);

        // 左の見出し (本編の「マップ」の札を写す)
        var label = picker.transform.Find("Label");
        if (label)
        {
            var l = Object.Instantiate(label.gameObject, root.transform);
            l.name = "MrpVariantLabel";
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
                tmp.text = new Text("バリエーション", "Variant");
            }
        }

        float x0 = 0.1f + ChipW / 2f;
        for (int i = 0; i < Variants.Length; i++)
        {
            var chip = MakeChip(m, root.transform, new Vector3(x0 + i * (ChipW + ChipGap), 0f, 0f), rowProto.TitleText, mat, Variants[i].Name);
            chip.Index = Variants[i].Index;
            var go = chip.Fill.transform.parent.gameObject;
            var col = go.AddComponent<BoxCollider2D>();
            col.size = new Vector2(ChipW, ChipH);
            var pb = go.AddComponent<PassiveButton>();
            pb.Colliders = new Collider2D[] { col };
            pb.ClickMask = m.ButtonClickMask;
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            int index = chip.Index;
            if (!Clicks.TryGetValue(index, out var a)) Clicks[index] = a = (UnityAction)(() => Select(index));
            pb.OnClick.AddListener(a);
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
            Chips.Add(chip);
        }
        Refresh();
    }

    private static Chip MakeChip(GameOptionsMenu m, Transform parent, Vector3 pos, TextMeshPro textProto, Material mat, Text text)
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

    private static void Select(int index)
    {
        MapSettings.Mirror.SetIndex(index);
        Refresh();
        SettingsMenu.RefreshAll();
    }

    // 札の見た目を今の設定に合わせる (選ばれた札 = 色の地 + 暗い字)
    public static void Refresh()
    {
        int cur = MapSettings.Mirror.Index;
        foreach (var c in Chips)
        {
            bool on = c.Index == cur;
            if (c.Fill) c.Fill.color = on ? Accent : Dark;
            if (c.EdgeSr) c.EdgeSr.color = on ? new Color(0.8f, 0.97f, 0.92f, 1f) : new Color(Accent.r * 0.75f, Accent.g * 0.75f, Accent.b * 0.75f, 1f);
            if (c.Label) c.Label.color = on ? new Color(0.08f, 0.08f, 0.1f, 1f) : new Color(0.75f, 0.93f, 0.88f, 1f);
        }
    }

    public static void Forget() => Chips.Clear();

    // 確かめ用: 札を押したのと同じことをする
    internal static string Press(int index)
    {
        if (index < 0 || index >= Chips.Count) return $"no chip (chips={Chips.Count})";
        Select(Chips[index].Index);
        return $"mirror={MapSettings.Mirror.Value}";
    }
}
