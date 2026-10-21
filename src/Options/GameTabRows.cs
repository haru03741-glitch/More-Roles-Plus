using System.Collections.Generic;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Rules;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Options;

// 本編の「ゲーム設定」タブに、MRP の項目を本編と同じ見出しと行で並べる。
// - マップ選びの下 (インポスターの見出しの前): マップのルール
// - インポスター: 本編のインポスター数の行を「最多人数」と呼び替え、その上に最少人数の行
// - インポスターの項目の下: 第三陣営 (数とキル役の設定)
// - クルー・会議・タスクの項目の終わり: 全員ベント・会議を無くす・タスクで勝てなくする の行
// - タブの終わり: タスクの無効化・機器の無効化・サボタージュ・配り方・人数の帯 (帯ごとに見出し 1 つ)
// 行は MRP の行 (SettingsMenu.MakeRow) なので、± は MRP の設定項目を書き換える。
// 本編の一覧 (Children) には入れない (本編の値の処理と行の作り直しを通さない)。
// 親の項目 (Opt.Parent) が OFF の行は隠し、ON/OFF を押すたびに本編の物ごと並べ直す (足した分だけ下の物を下げる)
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

    // マップ選びの下に並べる組
    public static IEnumerable<Group> Top()
    {
        yield return new Group(new Text("MapRules.Title"), MapRules.Rows);
    }

    // タブの終わりに並べる組
    public static IEnumerable<Group> Tail()
    {
        yield return new Group(new Text("TaskFilter.Title"), TaskFilter.Rows);
        yield return new Group(new Text("Devices.Title"), Devices.Rows);
        yield return new Group(new Text("SabotageRules.Title"), SabotageRules.Rows);
        yield return new Group(new Text("AssignSettings.Title"), AssignSettings.MaxRoled, AssignSettings.MaxAddonsPerPlayer, AssignSettings.Fill);
        yield return new Group(new Text("TeamSizes.Title"), TeamSizes.Enabled);
        var bands = TeamSizes.Bands;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            yield return new Group(new Text("TeamSizes.Band" + (i + 1)), b.Players, b.MinImpostors, b.MaxImpostors, b.MinNeutrals, b.MaxNeutrals, b.MaxNeutralKillers);
        }
    }

    // 本編の項目の終わりに足す行 (その項目の目印の行・足す行)
    private static readonly (StringNames Marker, Opt Opt)[] SectionRows =
    {
        (StringNames.GameCrewLight, GameRules.EveryoneVents),
        (StringNames.GameNumMeetings, GameRules.NoMeetings),
        (StringNames.GameCommonTasks, GameRules.TaskWin),
    };

    // 検索の組 (タブに並べる物全部)
    public static IEnumerable<Group> All()
    {
        foreach (var g in Top()) yield return g;
        yield return Neutral;
        yield return new Group(new Text("GameRules.Title"), GameRules.EveryoneVents, GameRules.NoMeetings, GameRules.TaskWin);
        foreach (var g in Tail()) yield return g;
    }

    private const string Prefix = "MrpGameTab";
    private const float RowStep = 0.45f;

    private static readonly StringNames[] ImpostorRows =
    {
        StringNames.GameNumImpostors, StringNames.GameKillCooldown, StringNames.GameImpostorLight, StringNames.GameKillDistance,
    };

    // 足した物の塊 1 つ。元の座標で At より下の本編の物を、この塊の高さだけ下げる
    private sealed class Block
    {
        public float At, Top;
        public readonly List<Part> Parts = new();
        public float Height;
    }

    // 見出し (Opt が null) か行。行は初めて見える時に作る (親が OFF の行を開くたびに作らない)
    private sealed class Part
    {
        public GameObject Go;
        public Opt Opt;
        public Part[] Rows; // 見出しの時: その組の行
    }

    private static Maker _maker;

    private static GameOptionsMenu _menu;
    private static readonly List<Block> Blocks = new();
    private static readonly List<(Transform T, float Y)> Base = new(); // 本編の物と元の y
    private static float _scrollBase, _headerToRow, _rowToHeader;

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
        _menu = null;
        Blocks.Clear();
        Base.Clear();

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
        var headers = new List<CategoryHeaderMasked>();
        CategoryHeaderMasked proto = null, next = null;
        float headerToRow = 0.63f, rowToHeader = RowStep, bottom = float.MaxValue;
        for (int i = 0; i < container.childCount; i++)
        {
            var t = container.GetChild(i);
            if (t.gameObject.activeSelf) bottom = System.Math.Min(bottom, t.localPosition.y);
            var h = t.GetComponent<CategoryHeaderMasked>();
            if (h == null) continue;
            headers.Add(h);
            float y = t.localPosition.y;
            if (y > lastY)
            {
                if (proto == null || y < proto.transform.localPosition.y) proto = h; // インポスターの見出し
            }
            else if (next == null || y > next.transform.localPosition.y) next = h;
        }
        if (proto == null) return;
        if (next != null) rowToHeader = lastY - next.transform.localPosition.y;
        float protoY = proto.transform.localPosition.y;
        float firstBelow = float.MinValue;
        foreach (var row in m.Children)
        {
            if (!row) continue;
            float y = row.transform.localPosition.y;
            if (y < protoY && y > firstBelow) firstBelow = y;
        }
        if (firstBelow > float.MinValue) headerToRow = protoY - firstBelow;
        _headerToRow = headerToRow;
        _rowToHeader = rowToHeader;

        // 本編の物と元の位置 (この後の並べ直しはいつもここから出す)
        for (int i = 0; i < container.childCount; i++)
        {
            var t = container.GetChild(i);
            Base.Add((t, t.localPosition.y));
        }
        _scrollBase = m.scrollBar ? m.scrollBar.ContentYBounds.max : 0f;
        var rowPos = count.transform.localPosition;
        var make = _maker = new Maker(m, proto, rowPos);

        // マップのルール: インポスターの見出しの前
        Blocks.Add(make.Groups(Top(), protoY + 0.01f, protoY));
        // インポスターの最少人数: インポスター数の行の上
        float countY = count.transform.localPosition.y;
        Blocks.Add(make.Rows(countY + 0.01f, countY, ImpostorRange.Min));
        // 第三陣営: インポスターの項目の下
        Blocks.Add(make.Groups(new[] { Neutral }, lastY - 0.01f, lastY - rowToHeader));
        // 本編の項目の終わりの行
        foreach (var (marker, opt) in SectionRows)
        {
            float end = SectionEnd(m, headers, marker);
            if (!float.IsNaN(end)) Blocks.Add(make.Rows(end - 0.01f, end - RowStep, opt));
        }
        // タブの終わり (タスクの項目の終わりの行より後ろ)
        Blocks.Add(make.Groups(Tail(), bottom - 0.02f, bottom - rowToHeader));

        _menu = m;
        Relayout();
        Relabel(count);
    }

    // 目印の行がある本編の項目のいちばん下の行の y (見つからなければ NaN)
    private static float SectionEnd(GameOptionsMenu m, List<CategoryHeaderMasked> headers, StringNames marker)
    {
        float markY = float.NaN;
        foreach (var row in m.Children)
            if (row && row.Title == marker) { markY = row.transform.localPosition.y; break; }
        if (float.IsNaN(markY)) return float.NaN;
        float nextHeader = float.MinValue; // 目印より下で最も近い見出し
        foreach (var h in headers)
        {
            float y = h.transform.localPosition.y;
            if (y < markY && y > nextHeader) nextHeader = y;
        }
        float end = markY;
        foreach (var row in m.Children)
        {
            if (!row) continue;
            float y = row.transform.localPosition.y;
            if (y > nextHeader && y < end) end = y;
        }
        return end;
    }

    private sealed class Maker
    {
        private readonly GameOptionsMenu _m;
        private readonly CategoryHeaderMasked _proto;
        private readonly Vector3 _row;

        public Maker(GameOptionsMenu m, CategoryHeaderMasked proto, Vector3 row)
        {
            _m = m;
            _proto = proto;
            _row = row;
        }

        // 見出し付きの組を並べる塊
        public Block Groups(IEnumerable<Group> groups, float at, float top)
        {
            var b = new Block { At = at, Top = top };
            foreach (var g in groups)
            {
                var rows = new Part[g.Opts.Length];
                for (int i = 0; i < rows.Length; i++) rows[i] = Row(g.Opts[i]);
                b.Parts.Add(new Part { Go = Header(g.Title), Rows = rows });
            }
            return b;
        }

        // 見出しの無い行だけの塊
        public Block Rows(float at, float top, params Opt[] opts)
        {
            var b = new Block { At = at, Top = top };
            foreach (var o in opts) b.Parts.Add(Row(o));
            return b;
        }

        // 見出しは本編の見出しと同じ作り方・同じ色と大きさ (字だけ替える)
        private GameObject Header(Text title)
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
            header.transform.localPosition = new Vector3(pp.x, 0f, pp.z);
            return header.gameObject;
        }

        private Part Row(Opt opt) => new() { Go = opt.Shown ? RowGo(opt) : null, Opt = opt };

        public GameObject RowGo(Opt opt)
        {
            var row = SettingsMenu.MakeRow(_m, opt);
            row.name = Prefix + "Row";
            row.transform.localPosition = _row;
            return row.gameObject;
        }
    }

    // 親の ON/OFF を押した・値をまとめて変えた後: 隠す行を選び直して全部並べ直す
    internal static void Relayout()
    {
        var m = _menu;
        if (!m || !m.settingsContainer) { _menu = null; return; }

        // 塊ごとの高さ (見えている行だけ)
        foreach (var b in Blocks)
        {
            float h = 0f;
            foreach (var p in b.Parts)
            {
                if (p.Rows == null) { h += p.Opt.Shown ? RowStep : 0f; continue; }
                int n = Visible(p.Rows);
                if (n > 0) h += _rowToHeader + _headerToRow + RowStep * (n - 1);
            }
            b.Height = h;
        }

        // 本編の物: 元の位置より上にある塊の高さだけ下げる
        foreach (var (t, y0) in Base)
        {
            if (!t) continue;
            var p = t.localPosition;
            t.localPosition = new Vector3(p.x, y0 - Above(y0), p.z);
        }

        float total = 0f;
        foreach (var b in Blocks)
        {
            total += b.Height;
            float y = b.Top - Above(b.At);
            foreach (var p in b.Parts)
            {
                if (p.Rows == null)
                {
                    bool show = p.Opt.Shown;
                    PlaceRow(p, show, y);
                    if (show) y -= RowStep;
                    continue;
                }
                bool any = Visible(p.Rows) > 0;
                Place(p.Go, any, y);
                if (!any)
                {
                    foreach (var r in p.Rows) PlaceRow(r, false, y);
                    continue;
                }
                float ry = y - _headerToRow;
                foreach (var r in p.Rows)
                {
                    bool show = r.Opt.Shown;
                    PlaceRow(r, show, ry);
                    if (show) ry -= RowStep;
                }
                y = ry + RowStep - _rowToHeader;
            }
        }
        if (m.scrollBar) m.scrollBar.SetYBoundsMax(_scrollBase + total);
    }

    private static int Visible(Part[] rows)
    {
        int n = 0;
        foreach (var r in rows) if (r.Opt.Shown) n++;
        return n;
    }

    // 元の座標で y より上に足した塊の高さの合計
    private static float Above(float y)
    {
        float s = 0f;
        foreach (var b in Blocks) if (b.At > y) s += b.Height;
        return s;
    }

    private static void PlaceRow(Part p, bool show, float y)
    {
        if (show && !p.Go && _maker != null) p.Go = _maker.RowGo(p.Opt);
        Place(p.Go, show, y);
    }

    private static void Place(GameObject go, bool show, float y)
    {
        if (!go) return;
        if (go.activeSelf != show) go.SetActive(show);
        var p = go.transform.localPosition;
        go.transform.localPosition = new Vector3(p.x, y, p.z);
    }

    // 行を押した後。親の項目なら並べ直す
    internal static void OnEdited(Opt opt)
    {
        if (_menu && opt is BoolOpt && IsParent(opt)) Relayout();
    }

    private static readonly HashSet<Opt> Parents = new();

    private static bool IsParent(Opt opt)
    {
        if (Parents.Count == 0)
            foreach (var o in Registry.All)
                if (o.Parent != null) Parents.Add(o.Parent);
        return Parents.Contains(opt);
    }

    internal static void Forget()
    {
        _menu = null;
        _maker = null;
        Blocks.Clear();
        Base.Clear();
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
            if (h && t.gameObject.activeSelf) sb.Append($"\n header{i} '{h.Title.text}' y={t.localPosition.y:0.###}");
            var row = t.GetComponent<OptionBehaviour>();
            if (row == null || !t.gameObject.activeSelf) continue;
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
