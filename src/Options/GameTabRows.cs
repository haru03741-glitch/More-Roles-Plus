using System.Collections.Generic;
using MoreRolesPlus.Roles;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 本編の「ゲーム設定」タブに、陣営の数と配り方の MRP の項目を本編と同じ見出しと行で並べる。
// - インポスター: 本編のインポスター数の行を「最多人数」と呼び替え、その上に最少人数の行
// - インポスターの項目の下: 第三陣営 (数とキル役の設定)
// - タブの終わり: 配り方・人数の帯 (帯ごとに見出し 1 つ)
// 行は MRP の行 (SettingsMenu.MakeRow) なので、± は MRP の設定項目を書き換える。
// 本編の一覧 (Children) には入れない (本編の値の処理と行の作り直しを通さない)。足した分だけ下の物を下げる
internal static class GameTabRows
{
    internal readonly struct Group
    {
        public readonly Text Title;
        public readonly Opt[] Opts;

        public Group(Text title, params Opt[] opts)
        {
            Title = title;
            Opts = opts;
        }
    }

    public static readonly Group Neutral = new(new Text("NeutralKillSettings.Title"),
        NeutralKillSettings.MinNeutrals, NeutralKillSettings.MaxNeutrals, NeutralKillSettings.MaxKillers,
        NeutralKillSettings.KillCooldown, NeutralKillSettings.Vision, NeutralKillSettings.KillDistance);

    // タブの終わりに並べる組 (検索の組にも使う)
    public static IEnumerable<Group> Tail()
    {
        yield return new Group(new Text("AssignSettings.Title"), AssignSettings.MaxRoled, AssignSettings.MaxAddonsPerPlayer, AssignSettings.Fill);
        yield return new Group(new Text("TeamSizes.Title"), TeamSizes.Enabled);
        var bands = TeamSizes.Bands;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            yield return new Group(new Text("TeamSizes.Band" + (i + 1)), b.Players, b.MinImpostors, b.MaxImpostors, b.MinNeutrals, b.MaxNeutrals, b.MaxNeutralKillers);
        }
    }

    private const string Prefix = "MrpGameTab";
    private const float RowStep = 0.45f;

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

        // インポスターの項目のいちばん下の行と、インポスター数の行
        OptionBehaviour last = null;
        NumberOption count = null;
        foreach (var row in m.Children)
        {
            if (!row || System.Array.IndexOf(ImpostorRows, row.Title) < 0) continue;
            if (last == null || row.transform.localPosition.y < last.transform.localPosition.y) last = row;
            if (row.Title == StringNames.GameNumImpostors) count = row.TryCast<NumberOption>();
        }
        if (last == null || count == null) return;
        float lastY = last.transform.localPosition.y;

        // 見出しの位置と見た目は本編のインポスターの見出しを写す。見出しから最初の行までと、行から次の見出しまでの間も本編から測る
        CategoryHeaderMasked proto = null, next = null;
        float headerToRow = 0.63f, rowToHeader = RowStep, bottom = float.MaxValue;
        for (int i = 0; i < container.childCount; i++)
        {
            var t = container.GetChild(i);
            if (t.gameObject.activeSelf) bottom = System.Math.Min(bottom, t.localPosition.y);
            var h = t.GetComponent<CategoryHeaderMasked>();
            if (h == null) continue;
            float y = t.localPosition.y;
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
        var layout = new Layout(m, proto, count.transform.localPosition, headerToRow, rowToHeader);

        // 下から順に足す (上で足した分の下げが、先に足した物にも掛かるように)
        float y0 = bottom - rowToHeader, end = y0;
        foreach (var g in Tail()) end = layout.Add(g, end);
        Grow(m, y0 - end);

        float h0 = rowToHeader + headerToRow + RowStep * (Neutral.Opts.Length - 1);
        Open(m, lastY - 0.01f, h0);
        layout.Add(Neutral, lastY - rowToHeader);

        float countY = count.transform.localPosition.y;
        Open(m, countY + 0.01f, RowStep);
        layout.Row(ImpostorRange.Min, countY);
        Relabel(count);
    }

    // 見出しと行の置き方 (本編から測った間隔)
    private sealed class Layout
    {
        private readonly GameOptionsMenu _m;
        private readonly CategoryHeaderMasked _proto;
        private readonly Vector3 _row;
        private readonly float _headerToRow, _rowToHeader;

        public Layout(GameOptionsMenu m, CategoryHeaderMasked proto, Vector3 row, float headerToRow, float rowToHeader)
        {
            _m = m;
            _proto = proto;
            _row = row;
            _headerToRow = headerToRow;
            _rowToHeader = rowToHeader;
        }

        // 見出しを y に置き、その下に行を並べる。次の見出しの y を返す
        public float Add(Group g, float y)
        {
            Header(g.Title, y);
            float ry = y - _headerToRow;
            foreach (var opt in g.Opts)
            {
                Row(opt, ry);
                ry -= RowStep;
            }
            return ry + RowStep - _rowToHeader;
        }

        // 見出しは本編の見出しと同じ作り方・同じ色と大きさ (字だけ替える)
        private void Header(Text title, float y)
        {
            var header = Object.Instantiate(_m.categoryHeaderOrigin, _m.settingsContainer);
            header.name = Prefix + "Header";
            header.SetHeader(StringNames.ImpostorsCategory, GameOptionsMenu.MASK_LAYER);
            header.Title.text = title;
            if (header.Background && _proto.Background) header.Background.color = _proto.Background.color;
            if (header.Divider && _proto.Divider) header.Divider.color = _proto.Divider.color;
            header.Title.color = _proto.Title.color;
            header.transform.localScale = _proto.transform.localScale;
            var pp = _proto.transform.localPosition;
            header.transform.localPosition = new Vector3(pp.x, y, pp.z);
        }

        public void Row(Opt opt, float y)
        {
            var row = SettingsMenu.MakeRow(_m, opt);
            row.name = Prefix + "Row";
            row.transform.localPosition = new Vector3(_row.x, y, _row.z);
        }
    }

    // y より下の物を h だけ下げる
    private static void Open(GameOptionsMenu m, float belowY, float h)
    {
        var container = m.settingsContainer;
        for (int i = 0; i < container.childCount; i++)
        {
            var c = container.GetChild(i);
            var p = c.localPosition;
            if (p.y < belowY) c.localPosition = new Vector3(p.x, p.y - h, p.z);
        }
        Grow(m, h);
    }

    private static void Grow(GameOptionsMenu m, float h)
    {
        var sb = m.scrollBar;
        if (sb) sb.SetYBoundsMax(sb.ContentYBounds.max + h);
    }

    // 本編のインポスター数の行は、最少人数の行と組で「最多人数」として使う
    internal static void Relabel(NumberOption row)
    {
        if (row && row.TitleText) row.TitleText.text = ImpostorRange.MaxLabel;
    }

    // 本編のインポスター数の行を本編の設定から読み直す (帯の値を写した後)
    internal static void RefreshVanillaImpostors(GameOptionsMenu m)
    {
        var children = m ? m.Children : null;
        if (children == null) return;
        foreach (var c in children)
        {
            if (!c || c.Title != StringNames.GameNumImpostors) continue;
            var num = c.TryCast<NumberOption>();
            if (num == null) continue;
            num.Initialize();
            Relabel(num);
        }
    }

    // 確かめ用: 足した見出しと行 (と本編の見出し) の位置と値 / タブを縦に送る / 行の ± を押す
    internal static string Probe(string[] a)
    {
        var menu = GameSettingMenu.Instance;
        var m = menu ? menu.GameSettingsTab : null;
        if (!m || !m.settingsContainer) return "ERR gamerows: game tab not open";
        if (a.Length >= 2 && a[0] == "scroll" && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sp))
            m.scrollBar.ScrollPercentY(sp);
        var rows = new List<OptionBehaviour>();
        var sb = new System.Text.StringBuilder("OK gamerows");
        var c = m.settingsContainer;
        for (int i = 0; i < c.childCount; i++)
        {
            var t = c.GetChild(i);
            var h = t.GetComponent<CategoryHeaderMasked>();
            if (h) sb.Append($"\n header{i} '{h.Title.text}' y={t.localPosition.y:0.###}");
            var row = t.GetComponent<OptionBehaviour>();
            if (row == null) continue;
            bool ours = t.name.StartsWith(Prefix);
            if (ours || row.Title == StringNames.GameNumImpostors) rows.Add(row);
        }
        // 「q」を付けると行の一覧を省いて最後の 1 行だけ返す
        bool quiet = System.Array.IndexOf(a, "q") >= 0;
        if (quiet) sb.Length = "OK gamerows".Length;
        if (a.Length >= 3 && a[0] == "press" && int.TryParse(a[1], out int n) && n >= 0 && n < rows.Count)
        {
            var num = rows[n].TryCast<NumberOption>();
            if (num != null)
            {
                if (a[2].StartsWith("-")) num.Decrease();
                else num.Increase();
            }
            else rows[n].TryCast<ToggleOption>()?.Toggle();
        }
        for (int i = 0; i < rows.Count && !quiet; i++)
        {
            var num = rows[i].TryCast<NumberOption>();
            var tog = rows[i].TryCast<ToggleOption>();
            string title = num != null ? num.TitleText.text : tog != null ? tog.TitleText.text : "?";
            string value = num != null ? num.ValueText.text : tog != null ? (tog.CheckMark.enabled ? "ON" : "OFF") : "?";
            sb.Append($"\n row{i} y={rows[i].transform.localPosition.y:0.###} '{title}' = '{value}'");
        }
        var o = GameOptionsManager.Instance?.CurrentGameOptions;
        sb.Append($"\n scroll max={m.scrollBar.ContentYBounds.max:0.##} impostors={ImpostorRange.Min.Value}-{(o != null ? o.NumImpostors : -1)} neutrals={NeutralKillSettings.MinNeutrals.Value}-{NeutralKillSettings.MaxNeutrals.Value} killers={NeutralKillSettings.MaxKillers.Value} bands={TeamSizes.Enabled.Value} players={TeamSizes.PlayerCount()}");
        return sb.ToString();
    }
}
