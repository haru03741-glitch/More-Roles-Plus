// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/Help.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Commands;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 役職辞典の窓。上の札で 2 つの頁を切り替える。
// 役職 = 役職の一覧 (陣営の札・カード・右の要約) と、押した役職の説明の本文と今の設定。
//        ロビーのホストはここで設定も変えられる (閉じた時に保存して全員へ送る)。それ以外の人と試合中は読むだけ。
// 配役の見込み = 今の設定で人数を選んで配り方を何度も試し、役職ごとの出る割合と 1 回分の例を出す (ロビーでだけ)。
// 入口 = Esc の「MRP の設定」の板・設定画面の役職の頁の見出し。
internal static class RoleDictionary
{
    private const float WinW = 9.2f, WinH = 5.3f, InnerW = 8.6f, InnerH = 4.3f;
    private const int SimRuns = 1000;
    private static readonly Color TitleColor = new(1f, 0.85f, 0.35f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color TabColor = new(0.5f, 0.85f, 0.75f);

    // 開き直しても陣営の札などを残すので、作るのは 1 回だけ
    private static readonly RoleBrowser Browser = new(InnerW, InnerH, false, CanEdit, "dict")
    {
        Changed = () => _dirty = true,
    };

    private static bool _dirty;
    private static SearchBox _search;
    private static int _view;      // 0 = 役職・1 = 配役の見込み
    private static int _players;   // 配役の見込みの人数 (0 = まだ決めていない → 今の部屋の人数)
    private static Holder _holder;
    private static string _lastSim = "";

    private static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost && MatchInfoExtension.InLobby;

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_RoleDictionary";

    public static void Open() => Open(null);

    public static void Open(Registry.Section sec)
    {
        Roles.Builtin.VanillaRoles.EnsureSettings();
        var w = UiWindow.Open("RoleDictionary", new Vector2(WinW, WinH), OnClosed);
        if (w == null) return;
        _dirty = false;
        if (sec != null) _view = 0;
        // 検索欄は本編のチャットの入力欄を複製して作る (窓の右上・見出しの高さ)
        _search = SearchBox.Create(w.Root.transform, new Vector3(InnerW / 2f - 1.6f, WinH / 2f - 0.38f, -1f), 3.0f, 0.42f,
            new Text("RoleDictionary.SearchHint"), OnSearch);
        if (_search != null) OrderByDepth(_search.Root.transform, 30);
        BuildBody(sec);
    }

    private static void BuildBody(Registry.Section open = null)
    {
        var w = UiWindow.Current;
        if (w == null || !w.Root) return;
        Browser.Detach();
        var title = new Label(new Text("RoleDictionary.Title"), 2.4f) { MenuFont = true, Bold = true, Color = TitleColor };
        var head = new Stack(true, title, new Spacer(0.2f, 0.1f),
            Tab(new Text("RoleDictionary.TabRoles"), 0), Tab(new Text("RoleDictionary.TabOdds"), 1)) { Gap = 0.1f, Align = Align.Start };
        _holder = new Holder(new Vector2(InnerW, InnerH));
        w.Body.Set(new Stack(false, head, _holder) { Gap = 0.12f, Width = InnerW });
        if (_search != null) _search.Root.SetActive(_view == 0);
        if (_view == 0)
        {
            Browser.Attach(_holder.Screen, open);
            if (_search != null && _search.Query.Length > 0) Browser.SetQuery(_search.Query);
        }
        else _holder.Screen.Set(OddsView());
    }

    private static Widget Tab(string text, int view)
    {
        bool on = _view == view;
        var label = new Label(text, 1.5f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = on ? new Color(0.08f, 0.08f, 0.1f) : TabColor };
        var b = new Button(label, () => { if (_view == view) return; _view = view; BuildBody(); })
        { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.2f, 0.42f), Padding = new Vector2(0.18f, 0.04f), Color = TabColor };
        if (on) b.Fill = TabColor;
        return b;
    }

    private static void OnSearch(string q)
    {
        if (_view == 0) Browser.SetQuery(q);
    }

    // ---- 配役の見込み ----

    private static Widget OddsView()
    {
        if (_players <= 0) _players = Math.Max(4, TeamSizes.PlayerCount());
        int players = _players, runs = SimRuns;
        var res = InfoCommands.RunSim(ref players, ref runs, out int impMin, out int impMax, out var error);
        _players = players;
        float w = InnerW - 0.3f;
        var col = new Stack(false) { Gap = 0.08f, Width = w };

        var stepper = new Stack(true,
            new Button("<", () => Step(-1), 1.7f) { Size = new Vector2(0.5f, 0.42f) },
            new Label(new Text("RoleDictionary.Players").With("n", _players), 1.8f) { Bold = true, Width = 1.2f, TextAlign = TextAlignmentOptions.Center },
            new Button(">", () => Step(1), 1.7f) { Size = new Vector2(0.5f, 0.42f) }) { Gap = 0.1f, Align = Align.Start };
        col.Add(stepper);
        if (res == null)
        {
            _lastSim = "error";
            col.Add(new Label(error, 1.5f) { Color = SubText, Width = w, Align = Align.Start });
            return new ScrollView(new Vector2(InnerW, InnerH), col);
        }

        string imps = impMin == impMax ? impMin.ToString() : new Text("InfoCommands.SimRange").With("min", impMin).With("max", impMax).ToString();
        col.Add(new Label(new Text("RoleDictionary.OddsHead").With("runs", runs).With("impostors", imps)
            .With("min", res.NeutralMin).With("max", res.NeutralMax).With("roles", ((double)res.WithRole / runs).ToString("0.0")), 1.3f)
            { Color = SubText, Width = w, Align = Align.Start });

        // 1 回分の例 (席の番号は試しの中だけの物で、部屋の人とは関係ない)
        col.Add(new Label(new Text("RoleDictionary.Example"), 1.5f) { Bold = true, Color = TabColor, Align = Align.Start });
        const int perRow = 5;
        float chipW = (w - 0.08f * (perRow - 1)) / perRow;
        Stack row = null;
        for (int i = 0; i < res.Players; i++)
        {
            if (i % perRow == 0) { row = new Stack(true) { Gap = 0.08f, Align = Align.Start }; col.Add(row); }
            row.Add(Seat(res, i, chipW));
        }

        // 役職ごとの出る割合 (1 人以上出た試合の割合・多い順)
        col.Add(new Spacer(0.1f, 0.06f));
        col.Add(new Label(new Text("RoleDictionary.OddsRoles"), 1.5f) { Bold = true, Color = TabColor, Align = Align.Start });
        var order = new int[res.RoleRuns.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) => res.RoleRuns[y].CompareTo(res.RoleRuns[x]));
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        foreach (int i in order)
        {
            if (res.RoleRuns[i] == 0) break;
            col.Add(OddsRow(Registry.Roles[i], res.RoleRuns[i], runs, res.RoleHits[i], w));
            sb.Append(Registry.Roles[i].Id).Append('=').Append(res.RoleRuns[i]).Append(' ');
            shown++;
        }
        if (shown == 0) col.Add(new Label(new Text("RoleMenu.NoMatchingRoles"), 1.4f) { Color = SubText, Align = Align.Start });

        var addons = new System.Text.StringBuilder();
        for (int i = 0; i < res.AddonHits.Length; i++)
        {
            if (res.AddonHits[i] == 0) continue;
            if (addons.Length > 0) addons.Append(", ");
            addons.Append(Registry.Addons[i].ColoredName).Append(' ').Append(((double)res.AddonHits[i] / runs).ToString("0.0"));
        }
        if (addons.Length > 0)
            col.Add(new Label($"{new Text("InfoCommands.SimAddons")}: {addons}", 1.3f) { Width = w, Align = Align.Start });
        _lastSim = $"players={res.Players} runs={runs} imps={imps} neutral={res.NeutralMin}-{res.NeutralMax} roles: {sb}";
        return new ScrollView(new Vector2(InnerW, InnerH), col) { Tag = "dict:odds" };
    }

    private static void Step(int d)
    {
        _players = Math.Clamp(_players + d, 4, Crowded.MaxPlayers);
        if (_holder?.Screen != null && _holder.Screen.Alive) _holder.Screen.Set(OddsView());
    }

    private static Widget Seat(AssignPlan.SimResult res, int seat, float width)
    {
        var pick = res.Example?.Find(x => x.PlayerId == seat);
        string name;
        Color edge;
        if (pick != null && pick.RoleIndex >= 0)
        {
            var r = Registry.Roles[pick.RoleIndex];
            name = r.Name;
            edge = RoleDisplay.ParseColor(r.Color);
        }
        else
        {
            bool imp = res.ExampleImpostor[seat];
            name = InfoCommands.TeamName(imp ? Team.Impostor : Team.Crew);
            edge = imp ? new Color(1f, 0.35f, 0.35f) : new Color(0.55f, 0.85f, 1f);
        }
        var label = new Label($"{seat + 1}. {name}", 1.3f) { Bold = true, Color = edge, Width = width - 0.2f, TextAlign = TextAlignmentOptions.Center };
        var col = new Stack(false, label) { Gap = 0.01f };
        if (pick != null && pick.Addons.Count > 0)
        {
            var ads = new System.Text.StringBuilder();
            foreach (int ad in pick.Addons) ads.Append(ads.Length > 0 ? " " : "").Append('+').Append(Registry.Addons[ad].ColoredName);
            col.Add(new Label(ads.ToString(), 1.05f) { Width = width - 0.2f, TextAlign = TextAlignmentOptions.Center });
        }
        return new Box(col) { Size = new Vector2(width, 0f), MinSize = new Vector2(width, 0.42f), Padding = new Vector2(0.08f, 0.05f), Color = edge };
    }

    private static Widget OddsRow(RoleBase role, int runsWith, int runs, int hits, float width)
    {
        const float nameW = 2.4f, pctW = 0.9f, barH = 0.2f;
        float barMax = width - nameW - pctW - 0.3f;
        float frac = (float)runsWith / runs;
        var c = RoleDisplay.ParseColor(role.Color);
        var name = new Label(role.Name, 1.55f) { MenuFont = true, Color = c, Width = nameW };
        var bar = new Stack(true,
            new Picture(UiAssets.Blank, Math.Max(barMax * frac, 0.02f), barH) { Color = c },
            new Picture(UiAssets.Blank, Math.Max(barMax * (1f - frac), 0.001f), barH) { Color = new Color(1f, 1f, 1f, 0.12f) }) { Gap = 0f };
        string pct = InfoCommands.Percent(runsWith, runs);
        // 1 試合に 2 人以上出ることがある役職は、出た試合での平均の人数も
        if (hits > runsWith) pct += "\n" + new Text("RoleDictionary.Avg").With("n", ((double)hits / runsWith).ToString("0.0"));
        var label = new Label(pct, 1.4f) { Bold = true, Width = pctW, TextAlign = TextAlignmentOptions.Right };
        return new Stack(true, name, bar, label) { Gap = 0.1f, Align = Align.Start };
    }

    // ---- 片付け ----

    // 窓は SortingGroup の中なので z では前後が決まらない。複製した入力欄の部品を、手前 (z が小さい) ほど後に描く
    private static void OrderByDepth(Transform root, int baseOrder)
    {
        var rs = root.GetComponentsInChildren<Renderer>(true);
        var list = new List<(float z, Renderer r)>(rs.Length);
        foreach (var r in rs) list.Add((r.transform.position.z, r));
        list.Sort((a, b) => b.z.CompareTo(a.z));
        for (int i = 0; i < list.Count; i++) list[i].r.sortingOrder = baseOrder + i;
    }

    private static void OnClosed()
    {
        Browser.Detach();
        _holder = null;
        if (_search != null) SearchBox.Forget(_search);
        _search = null;
        if (!_dirty) return;
        _dirty = false;
        SettingsMenu.CommitHostEdits();
        SettingsMenu.RefreshAll();
        RolePage.RefreshCards();
    }

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        string op = a.Length > 0 ? a[0] : "dump";
        if (op == "open")
        {
            Open(a.Length > 1 ? RoleBrowser.Find(a[1]) : null);
            return IsOpen ? Dump() : "not opened";
        }
        if (op == "button") return DictionaryButton.Command(a);
        if (op == "close")
        {
            UiWindow.Current?.Close();
            return $"closed slots={UiActions.Census()}";
        }
        if (!IsOpen) return "not open";
        switch (op)
        {
            case "q":
            {
                string q = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "";
                if (_search != null) _search.Type(q);
                else Browser.SetQuery(q);
                return Dump();
            }
            case "view":
                _view = a.Length > 1 && a[1] == "1" ? 1 : 0;
                BuildBody();
                return Dump();
            case "players":
                if (a.Length > 1 && int.TryParse(a[1], out int n)) { _players = n; if (_view == 1) BuildBody(); }
                return Dump();
        }
        return Browser.Command(a);
    }

    private static string Dump() => _view == 1 ? $"view=odds {_lastSim} slots={UiActions.Census()}" : Browser.Command(new[] { "dump" });
}
