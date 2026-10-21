using MoreRolesPlus.Roles;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 本編の「ゲーム設定」タブのインポスターの項目の真下に、第三陣営のキル役の項目 (NeutralKillSettings) を
// 見出し付きで並べる。行は MRP の行 (SettingsMenu.MakeRow) なので、± は MRP の設定項目を書き換える。
// 本編の一覧 (Children) には入れない (本編の値の処理と行の作り直しを通さない)。足した分だけ下の物を下げる
internal static class NeutralKillRows
{
    public static readonly Text Title = new("NeutralKillSettings.Title");
    public static readonly Opt[] Opts = { NeutralKillSettings.KillCooldown, NeutralKillSettings.Vision, NeutralKillSettings.KillDistance };

    private const string Prefix = "MrpNeutralKill";
    private const float RowStep = 0.45f;
    private static readonly Color Accent = new(1f, 0.7f, 0.25f);

    private static readonly StringNames[] ImpostorRows =
    {
        StringNames.GameNumImpostors, StringNames.GameKillCooldown, StringNames.GameImpostorLight, StringNames.GameKillDistance,
    };

    // 本編がゲーム設定のタブの行を作った後 (マップのバリエーションの後)
    public static void Attach(GameOptionsMenu m)
    {
        var container = m.settingsContainer;
        if (!container || m.Children == null) return;
        for (int i = container.childCount - 1; i >= 0; i--)
        {
            var c = container.GetChild(i);
            if (c.name.StartsWith(Prefix))
            {
                c.SetParent(null);
                Object.Destroy(c.gameObject);
            }
        }

        // インポスターの項目の最後の行 (いちばん下)
        OptionBehaviour last = null;
        foreach (var row in m.Children)
        {
            if (!row || System.Array.IndexOf(ImpostorRows, row.Title) < 0) continue;
            if (last == null || row.transform.localPosition.y < last.transform.localPosition.y) last = row;
        }
        if (last == null) return;
        float lastY = last.transform.localPosition.y;

        // 見出しの位置と大きさは本編の見出しを写す。見出しから最初の行までと、行から次の見出しまでの間も本編から測る
        CategoryHeaderMasked proto = null, next = null;
        float headerToRow = 0.63f, rowToHeader = RowStep;
        for (int i = 0; i < container.childCount; i++)
        {
            var h = container.GetChild(i).GetComponent<CategoryHeaderMasked>();
            if (h == null) continue;
            float y = h.transform.localPosition.y;
            if (y > lastY)
            {
                if (proto == null || y < proto.transform.localPosition.y) proto = h; // インポスターの見出し
            }
            else if (next == null || y > next.transform.localPosition.y) next = h;
        }
        if (proto == null) return;
        if (next != null) rowToHeader = lastY - next.transform.localPosition.y;
        float firstBelow = float.MinValue;
        foreach (var row in m.Children)
        {
            if (!row) continue;
            float y = row.transform.localPosition.y;
            if (y < proto.transform.localPosition.y && y > firstBelow) firstBelow = y;
        }
        if (firstBelow > float.MinValue) headerToRow = proto.transform.localPosition.y - firstBelow;

        float height = rowToHeader + headerToRow + RowStep * (Opts.Length - 1);
        for (int i = 0; i < container.childCount; i++)
        {
            var c = container.GetChild(i);
            var p = c.localPosition;
            if (p.y < lastY - 0.01f) c.localPosition = new Vector3(p.x, p.y - height, p.z);
        }
        var sb = m.scrollBar;
        if (sb) sb.SetYBoundsMax(sb.ContentYBounds.max + height);

        var pp = proto.transform.localPosition;
        var header = Object.Instantiate(m.categoryHeaderOrigin, container);
        header.name = Prefix + "Header";
        header.SetHeader(StringNames.ImpostorsCategory, GameOptionsMenu.MASK_LAYER);
        header.Title.text = Title;
        header.Background.color = new Color(Accent.r * 0.35f, Accent.g * 0.35f, Accent.b * 0.35f, 1f);
        header.Divider.color = Accent;
        header.transform.localScale = proto.transform.localScale;
        float y0 = lastY - rowToHeader;
        header.transform.localPosition = new Vector3(pp.x, y0, pp.z);

        var lp = last.transform.localPosition;
        float ry = y0 - headerToRow;
        foreach (var opt in Opts)
        {
            var row = SettingsMenu.MakeRow(m, opt);
            row.name = Prefix + "Row";
            row.transform.localPosition = new Vector3(lp.x, ry, lp.z);
            ry -= RowStep;
        }
    }

    // 確かめ用: 足した見出しと行 (と本編のインポスター・次の見出し) の位置と値 / タブを縦に送る / 行の ± を押す
    internal static string Probe(string[] a)
    {
        var menu = GameSettingMenu.Instance;
        var m = menu ? menu.GameSettingsTab : null;
        if (!m || !m.settingsContainer) return "ERR nkrows: game tab not open";
        if (a.Length >= 2 && a[0] == "scroll" && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sp))
            m.scrollBar.ScrollPercentY(sp);
        var rows = new System.Collections.Generic.List<OptionBehaviour>();
        var sb = new System.Text.StringBuilder("OK nkrows");
        var c = m.settingsContainer;
        for (int i = 0; i < c.childCount; i++)
        {
            var t = c.GetChild(i);
            var h = t.GetComponent<CategoryHeaderMasked>();
            if (h) sb.Append($"\n header '{h.Title.text}' y={t.localPosition.y:0.###}");
            if (!t.name.StartsWith(Prefix + "Row")) continue;
            var row = t.GetComponent<OptionBehaviour>();
            rows.Add(row);
        }
        if (a.Length >= 3 && a[0] == "press" && int.TryParse(a[1], out int n) && n >= 0 && n < rows.Count)
        {
            var num = rows[n].TryCast<NumberOption>();
            if (num != null)
            {
                if (a[2].StartsWith("-")) num.Decrease();
                else num.Increase();
            }
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var num = rows[i].TryCast<NumberOption>();
            sb.Append($"\n row{i} y={rows[i].transform.localPosition.y:0.###} '{(num != null ? num.TitleText.text : "?")}' = '{(num != null ? num.ValueText.text : "?")}'");
        }
        sb.Append($"\n scroll max={m.scrollBar.ContentYBounds.max:0.##} kill={NeutralKillSettings.KillCooldown.Value} vision={NeutralKillSettings.Vision.Value} dist={NeutralKillSettings.KillDistance.Value}({NeutralKillSettings.Distance():0.##})");
        return sb.ToString();
    }
}
