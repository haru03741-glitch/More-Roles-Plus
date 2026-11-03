using System;
using MoreRolesPlus.Commands;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// BAN・ホワイトリスト・VIP・モデレーターの一覧の窓 (MRP の設定・/lists)。一覧はホストの PC のファイルなので、部屋の外でも見て直せる。
// 上のタブで一覧を選び、行の × で消す。足すのは「部屋にいる人」から押すか、フレンドコードを打つ
internal static class JoinListPanel
{
    private const string Name = "JoinLists";
    private const float WinW = 8.4f, WinH = 5.4f, InnerW = 7.8f;
    private static readonly Color TitleColor = new(1f, 0.85f, 0.35f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color TabColor = new(0.5f, 0.85f, 0.75f);
    private static readonly Color RemoveColor = new(1f, 0.45f, 0.42f);
    private static readonly Color AddColor = new(0.45f, 0.8f, 1f);

    private static readonly (JoinLists.Kind Kind, string Key)[] Tabs =
    {
        (JoinLists.Kind.Ban, "JoinPanel.Ban"), (JoinLists.Kind.White, "JoinPanel.White"),
        (JoinLists.Kind.Vip, "JoinPanel.Vip"), (JoinLists.Kind.Mod, "JoinPanel.Mod"),
    };

    private static JoinLists.Kind _kind = JoinLists.Kind.Ban;
    private static string _code = "", _name = "", _message;

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_" + Name;

    internal static void Open()
    {
        var w = UiWindow.Open(Name, new Vector2(WinW, WinH));
        if (w == null) return;
        _message = null;
        Build();
    }

    private static void Build()
    {
        var w = UiWindow.Current;
        if (w == null || !IsOpen) return;
        var col = new Stack(false) { Gap = 0.1f, Width = InnerW };
        col.Add(new Label(new Text("JoinPanel.Title"), 2.3f) { MenuFont = true, Bold = true, Color = TitleColor });

        var tabs = new Stack(true) { Gap = 0.1f, Align = Align.Start };
        foreach (var (kind, key) in Tabs) tabs.Add(Tab(kind, key));
        col.Add(tabs);
        col.Add(new Label(new Text("JoinPanel.About" + _kind), 1.25f) { Color = SubText, Width = InnerW, Align = Align.Start });

        // 一覧
        var rows = new Stack(false) { Gap = 0.05f, Width = InnerW - 0.3f };
        int n = JoinLists.Count(_kind);
        if (n == 0) rows.Add(new Label(new Text("JoinPanel.Empty"), 1.35f) { Color = SubText });
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var (name, id) = JoinLists.At(_kind, i);
            rows.Add(new Plate(new Stack(true,
                new Label(string.IsNullOrEmpty(name) ? "?" : name, 1.4f) { Bold = true, Width = 2.6f, NoWrap = true, MinFontSize = 1f },
                new Label(id, 1.25f) { Color = SubText, Width = 3.9f, NoWrap = true, MinFontSize = 0.9f },
                new Button("×", () => { JoinLists.RemoveAt(_kind, idx); _message = null; Build(); }, 1.5f)
                    { Size = new Vector2(0.38f, 0.36f), Color = RemoveColor }) { Gap = 0.12f, Align = Align.Start }) { Width = InnerW - 0.3f });
        }
        col.Add(new ScrollView(new Vector2(InnerW, 1.55f), rows) { Tag = "joinlists:list" });

        // 部屋にいる人から足す
        var room = new Stack(true) { Gap = 0.08f, Align = Align.Start };
        room.Add(new Label(new Text("JoinPanel.FromRoom"), 1.3f) { Bold = true });
        int people = 0;
        var c = AmongUsClient.Instance;
        if (c && c.GameState != InnerNet.InnerNetClient.GameStates.NotJoined)
        {
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!p || p.AmOwner || p.notRealPlayer || p.Data == null || p.Data.Disconnected) continue;
                var pc = p;
                people++;
                room.Add(new Button(pc.Data.PlayerName, () =>
                {
                    var client = AmongUsClient.Instance ? AmongUsClient.Instance.GetClientFromCharacter(pc) : null;
                    _message = JoinLists.AddClient(_kind, client) ?? new Text("JoinPanel.Added").With("name", pc.Data?.PlayerName ?? "?");
                    Build();
                }, 1.25f) { Size = new Vector2(0f, 0.36f), MinSize = new Vector2(0.8f, 0.36f), Padding = new Vector2(0.1f, 0.03f), Color = AddColor });
            }
        }
        if (people == 0) room.Add(new Label(new Text("JoinPanel.NoOne"), 1.25f) { Color = SubText });
        col.Add(new ScrollView(new Vector2(InnerW, 0.5f), room) { Tag = "joinlists:room" });

        // フレンドコードを打って足す
        col.Add(new Stack(true,
            new Label(new Text("JoinPanel.Code"), 1.3f) { Bold = true },
            new TextField(new Vector2(2.6f, 0.4f), _code, "abcdef#1234") { MaxLength = 40, OnChange = v => _code = v, OnSubmit = AddByCode },
            new TextField(new Vector2(2.0f, 0.4f), _name, new Text("JoinPanel.NameHint")) { MaxLength = 30, OnChange = v => _name = v, OnSubmit = AddByCode },
            new Button(new Text("JoinPanel.Add"), AddByCode, 1.3f) { Size = new Vector2(0f, 0.4f), MinSize = new Vector2(0.9f, 0.4f), Color = AddColor })
            { Gap = 0.12f, Align = Align.Center });
        if (_message != null) col.Add(new Label(_message, 1.25f) { Color = new Color(1f, 0.85f, 0.4f), Width = InnerW, Align = Align.Start });
        w.Body.Set(col);
    }

    private static void AddByCode()
    {
        string why = JoinLists.AddCode(_kind, _code, _name);
        _message = why ?? new Text("JoinPanel.Added").With("name", _code.Trim());
        if (why == null) _code = _name = "";
        Build();
    }

    private static Widget Tab(JoinLists.Kind kind, string key)
    {
        bool on = _kind == kind;
        string text = $"{new Text(key)} ({JoinLists.Count(kind)})";
        var label = new Label(text, 1.35f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = on ? new Color(0.08f, 0.08f, 0.1f) : TabColor };
        var b = new Button(label, () => { _kind = kind; _message = null; Build(); })
            { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.7f, 0.42f), Padding = new Vector2(0.14f, 0.04f), Color = TabColor };
        if (on) b.Fill = TabColor;
        return b;
    }

    // 確かめ用 (bridge `joinlists [open|close|tab <0-3>|del <i>|code <fc> [名前]]`)
    internal static string Command(string[] a)
    {
        if (a.Length >= 1 && a[0] == "open") Open();
        else if (a.Length >= 1 && a[0] == "close") { if (IsOpen) UiWindow.Current.Close(); }
        else if (a.Length == 2 && a[0] == "tab" && int.TryParse(a[1], out int t) && t >= 0 && t < Tabs.Length) { _kind = Tabs[t].Kind; if (IsOpen) Build(); }
        else if (a.Length == 2 && a[0] == "del" && int.TryParse(a[1], out int d)) { JoinLists.RemoveAt(_kind, d); if (IsOpen) Build(); }
        else if (a.Length >= 2 && a[0] == "code")
        {
            _message = JoinLists.AddCode(_kind, a[1], a.Length > 2 ? a[2] : "");
            if (IsOpen) Build();
        }
        return $"open={IsOpen} tab={_kind} count={JoinLists.Count(_kind)} msg={_message ?? "-"}";
    }
}
