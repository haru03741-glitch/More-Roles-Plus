using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Report;

// バグ報告の窓。3 つの頁:
//   一覧 … 送った報告 (スタッフの返事が来ていれば印)・「新しく報告する」
//   書く … 種類・題・内容・画像や動画。一緒に送られる物 (フレンドコード・名前・版・ログ) を見せてから送る
//   話す … その報告のスタッフとのやり取り (開いている間 5 秒ごとに読み直す)。「+」で画像・動画を選ぶとすぐ送る
internal static class ReportWindow
{
    private const float WinW = 9.2f, WinH = 5.4f, InnerW = 8.6f;
    private static readonly Color Accent = new(0f, 0.9f, 0.46f, 1f);
    private static readonly Color Sub = new(0.72f, 0.75f, 0.8f, 1f);
    private static readonly Color Warn = new(1f, 0.55f, 0.5f, 1f);
    private static readonly Color MeBubble = new(0.03f, 0.3f, 0.17f, 1f), StaffBubble = new(0.16f, 0.17f, 0.2f, 1f);
    private const float PollSeconds = 5f;

    private enum Page { List, Write, Chat }

    // 一覧の頁のタブ: 自分の報告 / 未修正のバグ (みんなの) / 修正済み (直ったバグの控え)
    private enum ListTabs { Mine, Open, Fixed }
    private static ListTabs _tab;
    private static List<ReportClient.KnownBug> _known;
    private static Task<List<ReportClient.KnownBug>> _knownFetch;
    private static string _knownError;

    private static Page _page;
    private static string _category = "bug";
    private static TextField _title, _body, _chat;
    private static string _titleText = "", _bodyText = "", _chatText = "";
    private static string _status;
    private static Color _statusColor;
    private static Task<ReportClient.Saved> _sending;
    private static Task<ReportClient.Thread> _fetching;
    private static Task _posting;
    private static ReportClient.Saved _open;
    private static ReportClient.Thread _thread;
    private static float _nextPoll;
    private static ScrollView _messages;
    private static ReportClient.Info _info;
    private static readonly List<string> _attach = new();  // 書く頁で付けた画像・動画の道筋
    private static Task<string[]> _picking;
    private static bool _pickForChat;
    // 添付の小さな絵 (添付の番号ごと・null = 読めなかった)
    private static readonly Dictionary<string, Sprite> _thumbs = new();
    private static readonly HashSet<string> _thumbLoading = new();
    private static readonly ConcurrentQueue<(string id, byte[] data)> _thumbDone = new();
    private static bool _thumbDirty;
    private const float ThumbW = 2.8f, ThumbH = 1.8f;

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_Report";

    // フレンドコードが無い (ゲストなど) 間は使えない。報告はフレンドコードで本人を確かめるため
    private static bool _testNoFriendCode; // 試験用 (ブリッジ: report nofc)
    public static bool HasFriendCode
    {
        get
        {
            if (_testNoFriendCode) return false;
            try { return EOSManager.Instance && !string.IsNullOrWhiteSpace(EOSManager.Instance.FriendCode); }
            catch { return false; }
        }
    }

    public static void Open()
    {
        var w = UiWindow.Open("Report", new Vector2(WinW, WinH), OnClosed);
        if (w == null) return;
        _page = Page.List;
        _status = null;
        if (!HasFriendCode)
        {
            w.Body.Set(new Stack(false, Title(new Text("Report.Title")), new Spacer(0.1f, 0.2f), Small(new Text("Report.NeedFriendCode"), Warn)) { Gap = 0.12f, Width = InnerW });
            return;
        }
        if (_tab != ListTabs.Mine) FetchKnown();
        Build();
    }

    private static void FetchKnown()
    {
        if (_knownFetch != null) return;
        _knownError = null;
        _knownFetch = Task.Run(ReportClient.OpenBugs);
    }

    private static void OnClosed()
    {
        TextField.Blur();
        _messages = null;
        _open = null;
        _thread = null;
    }

    private static void Go(Page p)
    {
        _page = p;
        _status = null;
        Build();
    }

    private static void Build()
    {
        var w = UiWindow.Current;
        if (w == null || !w.Root || !IsOpen || !HasFriendCode) return;
        TextField.Blur();
        Widget body = _page switch
        {
            Page.Write => WritePage(),
            Page.Chat => ChatPage(),
            _ => ListPage(),
        };
        w.Body.Set(body);
        if (_page == Page.Chat) _messages?.Scroller?.ScrollPercentY(1f);
    }

    private static Label Title(string s) => new(s, 2.3f) { MenuFont = true, Bold = true, Color = Accent };

    private static Label Small(string s, Color c, float width = InnerW) => new(s, 1.25f) { Color = c, Width = width, Align = Align.Start };

    private static Button Action(string text, Action click, float w, bool primary = false)
    {
        var b = new Button(new Label(text, 1.5f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = primary ? new Color(0.02f, 0.16f, 0.09f) : Color.white }, click)
        {
            Size = new Vector2(w, 0.46f),
            Color = primary ? Accent : new Color(0.6f, 0.63f, 0.68f),
        };
        if (primary) b.Fill = Accent;
        return b;
    }

    private static Widget StatusLine(float width = InnerW) =>
        _status == null ? new Spacer(width, 0.3f) : Small(_status, _statusColor, width);

    // ---- 一覧 ----

    private static Widget ListPage()
    {
        var head = new Stack(true, Title(new Text("Report.Title")), new Spacer(0.3f, 0.1f), Action(new Text("Report.New"), () => Go(Page.Write), 2.6f, true)) { Gap = 0.1f, Align = Align.Start };
        var tabs = new Stack(true, ListTab(ListTabs.Mine, new Text("Report.TabMine")), ListTab(ListTabs.Open, new Text("Report.TabKnown")), ListTab(ListTabs.Fixed, new Text("Report.TabFixed")))
        { Gap = 0.12f, Align = Align.Start };
        if (_tab != ListTabs.Mine)
        {
            bool fixedTab = _tab == ListTabs.Fixed;
            return new Stack(false, head, tabs, Small(new Text(fixedTab ? "Report.FixedIntro" : "Report.KnownIntro"), Sub), KnownList(fixedTab), StatusLine()) { Gap = 0.12f, Width = InnerW };
        }
        var col = new Stack(false) { Gap = 0.08f, Width = InnerW - 0.3f };
        var all = ReportClient.All;
        if (all.Count == 0) col.Add(Small(new Text("Report.None"), Sub, InnerW - 0.3f));
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var s = all[i];
            bool unread = s.LastStaff.Length > 0 && s.LastStaff != s.LastRead && !ReportClient.Before(s.LastStaff, s.LastRead);
            string when = DateTimeOffset.FromUnixTimeSeconds(s.Created).ToLocalTime().ToString("MM/dd HH:mm");
            string state = s.Closed ? new Text("Report.StateClosed") : unread ? new Text("Report.StateReply") : new Text("Report.StateOpen");
            var row = new Stack(true,
                new Label($"{Icon(s.Category)}  {Esc(s.Title)}", 1.5f) { Width = 5.6f, NoWrap = true, Bold = unread },
                new Label($"<color=#{(unread ? "00E676" : "9AA3AD")}>{state}</color>", 1.3f) { Width = 1.4f, TextAlign = TextAlignmentOptions.Right },
                new Label(when, 1.2f) { Width = 1.0f, Color = Sub, TextAlign = TextAlignmentOptions.Right }) { Gap = 0.1f };
            var saved = s;
            col.Add(new Button(row, () => OpenChat(saved)) { Size = new Vector2(InnerW - 0.3f, 0.5f), Badge = true, BadgeOn = unread, Color = new Color(0.35f, 0.38f, 0.44f), Fill = new Color(0.09f, 0.1f, 0.12f) });
        }
        var list = new ScrollView(new Vector2(InnerW, 3.0f), col) { Tag = "report-list" };
        return new Stack(false, head, tabs, Small(new Text("Report.Intro"), Sub), list, StatusLine()) { Gap = 0.12f, Width = InnerW };
    }

    private static Widget ListTab(ListTabs tab, string text)
    {
        bool on = _tab == tab;
        var label = new Label(text, 1.4f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = on ? new Color(0.02f, 0.16f, 0.09f) : Color.white };
        var b = new Button(label, () =>
        {
            if (_tab == tab) return;
            // 未修正と修正済みは同じ一覧から分けるので、行き来では読み直さない
            if (_tab == ListTabs.Mine && tab != ListTabs.Mine) { _known = null; FetchKnown(); }
            _tab = tab;
            Build();
        })
        { Size = new Vector2(2.3f, 0.42f), Color = on ? Accent : new Color(0.45f, 0.48f, 0.54f) };
        if (on) b.Fill = Accent;
        return b;
    }

    // みんなのバグ (題と、その時の MRP と Among Us の版)。fixedOnly = 直った物 (控え) / でなければ直っていない物
    private static Widget KnownList(bool fixedOnly)
    {
        const float w = InnerW - 0.3f;
        var col = new Stack(false) { Gap = 0.08f, Width = w };
        var shown = _known?.FindAll(k => k.Closed == fixedOnly);
        if (shown == null) col.Add(Small(_knownError ?? new Text("Report.KnownLoading"), _knownError != null ? Warn : Sub, w));
        else if (shown.Count == 0) col.Add(Small(new Text(fixedOnly ? "Report.FixedNone" : "Report.KnownNone"), Sub, w));
        else
            foreach (var k in shown)
            {
                // 修正済みは直った日・未修正は届いた日
                long at = fixedOnly && k.ClosedAt > 0 ? k.ClosedAt : k.Created;
                string when = DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("MM/dd");
                string tag = fixedOnly ? $"<color=#00E676>[{new Text("Report.TagFixed")}]</color>" : Icon("bug");
                string title = fixedOnly ? $"<color=#9AA3AD>{Esc(k.Title)}</color>" : Esc(k.Title);
                string ver = k.ModVersion.Length > 0 ? $"MRP {Esc(k.ModVersion)}" : "MRP ?";
                if (k.GameVersion.Length > 0) ver += $"  /  AU {Esc(k.GameVersion)}";
                var mine = ReportClient.All.Find(s => s.Id == k.Id);
                var row = new Stack(true,
                    new Label($"{tag}  {title}", 1.5f) { Width = 4.6f, NoWrap = true, Bold = mine != null },
                    new Label(ver, 1.2f) { Width = 2.4f, Color = Sub, NoWrap = true, TextAlign = TextAlignmentOptions.Right },
                    new Label(when, 1.2f) { Width = 0.9f, Color = Sub, TextAlign = TextAlignmentOptions.Right }) { Gap = 0.1f };
                var size = new Vector2(w, 0.5f);
                // 自分が送った物は押すとやり取りを開く
                col.Add(mine != null
                    ? new Button(row, () => OpenChat(mine)) { Size = size, Color = new Color(0.35f, 0.38f, 0.44f), Fill = new Color(0.09f, 0.1f, 0.12f) }
                    : new Button(row, () => { }) { Size = size, Color = new Color(0.25f, 0.27f, 0.31f), Fill = new Color(0.07f, 0.08f, 0.09f) });
            }
        return new ScrollView(new Vector2(InnerW, 3.0f), col) { Tag = "report-known" };
    }

    // 種類の印 (本編の字に絵文字が無いので色の付いた字で)
    private static string Icon(string c) => c switch
    {
        "idea" => $"<color=#FFD54F>[{new Text("Report.TagIdea")}]</color>",
        "other" => $"<color=#90CAF9>[{new Text("Report.TagOther")}]</color>",
        _ => $"<color=#FF8A80>[{new Text("Report.TagBug")}]</color>",
    };

    private static string Esc(string s) => (s ?? "").Replace("<", "<​");

    // ---- 書く ----

    private static Widget WritePage()
    {
        _info ??= ReportClient.Collect();
        var i = _info;
        var head = new Stack(true, Action("◀", () => Go(Page.List), 0.6f), Title(new Text("Report.WriteTitle"))) { Gap = 0.15f, Align = Align.Start };
        var tabs = new Stack(true, Tab("bug", new Text("Report.CatBug")), Tab("idea", new Text("Report.CatIdea")), Tab("other", new Text("Report.CatOther"))) { Gap = 0.1f, Align = Align.Start };
        _title = new TextField(new Vector2(InnerW, 0.48f), _titleText, new Text("Report.TitleHint")) { MaxLength = 80, OnChange = v => _titleText = v };
        _body = new TextField(new Vector2(InnerW, 1.12f), _bodyText, new Text("Report.BodyHint")) { MaxLength = 3000, Multiline = true, FontSize = 1.3f, OnChange = v => _bodyText = v };

        string fc = i.FriendCode.Length > 0 ? i.FriendCode : new Text("Report.NoFriendCode");
        string sent = new Text("Report.SentWith").With("fc", fc).With("name", Esc(i.Name)).With("ver", i.ModVersion);
        var info = new Plate(Small(sent, Sub, InnerW - 0.2f)) { Width = InnerW };
        bool busy = _sending != null;
        var send = Action(busy ? new Text("Report.Sending") : new Text("Report.Send"), Send, 2.4f, !busy);
        var foot = new Stack(true, StatusLine(InnerW - 2.6f), send) { Gap = 0.2f };
        return new Stack(false, head, tabs, _title, _body, AttachRow(), info, foot) { Gap = 0.1f, Width = InnerW };
    }

    // 「+ 画像・動画」と付けた物の札 (押すと外す)
    private static Widget AttachRow()
    {
        var row = new Stack(true) { Gap = 0.1f, Align = Align.Start };
        if (_attach.Count >= ReportClient.MaxFiles) row.Add(new Spacer(0.01f, 0.42f));
        else row.Add(Action(_picking != null ? "…" : new Text("Report.Attach"), () => Pick(false), 2.0f, false));
        if (_attach.Count == 0) row.Add(Small(new Text("Report.AttachHint").With("n", ReportClient.MaxFiles.ToString()), Sub, InnerW - 2.2f));
        foreach (var path in _attach)
        {
            var p = path;
            string name = Path.GetFileName(p);
            if (name.Length > 9) name = name.Substring(0, 8) + "…";
            var label = new Label($"{Kind(FilePicker.IsVideo(p) ? "video" : "image")} {Esc(name)}  <color=#FF8A80>×</color>", 1.15f) { NoWrap = true, Width = 1.25f };
            row.Add(new Button(label, () => { _attach.Remove(p); Build(); }) { Size = new Vector2(1.55f, 0.42f), Color = new Color(0.45f, 0.48f, 0.54f) });
        }
        return row;
    }

    private static string Kind(string kind) => kind switch
    {
        "video" => $"<color=#CE93D8>[{new Text("Report.TagVideo")}]</color>",
        "image" => $"<color=#80CBC4>[{new Text("Report.TagImage")}]</color>",
        _ => $"<color=#9AA3AD>[{new Text("Report.TagFile")}]</color>",
    };

    private static void Pick(bool forChat)
    {
        if (_picking != null) return;
        _pickForChat = forChat;
        _picking = FilePicker.Pick(new Text("Report.PickTitle"), new Text("Report.PickFilter"), true);
        Build();
    }

    // 選び終わった (Tick から)
    private static void Picked(string[] files)
    {
        var ok = new List<string>();
        string err = null;
        foreach (var f in files)
        {
            string e = ReportClient.CheckFile(f);
            if (e != null) { err ??= e; continue; }
            ok.Add(f);
        }
        if (_pickForChat)
        {
            if (ok.Count > ReportClient.MaxFiles) { ok.RemoveRange(ReportClient.MaxFiles, ok.Count - ReportClient.MaxFiles); err ??= "Report.ErrTooManyFiles"; }
            if (ok.Count > 0 && _page == Page.Chat) PostChat(ok);
        }
        else
        {
            foreach (var f in ok)
            {
                if (_attach.Contains(f)) continue;
                if (_attach.Count >= ReportClient.MaxFiles) { err ??= "Report.ErrTooManyFiles"; break; }
                _attach.Add(f);
            }
        }
        if (err != null) SetStatus(new Text(err).With("n", ReportClient.MaxFiles.ToString()), Warn);
        else Build();
    }

    private static Widget Tab(string key, string text)
    {
        bool on = _category == key;
        var label = new Label(text, 1.4f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = on ? new Color(0.02f, 0.16f, 0.09f) : Color.white };
        var b = new Button(label, () => { if (_category == key) return; _category = key; Build(); })
        { Size = new Vector2(1.7f, 0.42f), Color = on ? Accent : new Color(0.45f, 0.48f, 0.54f) };
        if (on) b.Fill = Accent;
        return b;
    }

    private static void Send()
    {
        if (_sending != null) return;
        var i = _info ?? ReportClient.Collect();
        if (i.FriendCode.Length == 0) { SetStatus(new Text("Report.ErrNoFriendCode"), Warn); return; }
        string title = _titleText.Trim(), body = _bodyText.Trim();
        if (title.Length == 0) { SetStatus(new Text("Report.ErrNoTitle"), Warn); return; }
        string category = _category;
        // ログのパスと本編の情報はここ (メインスレッド) で集め、zip と送信は裏で
        var files = ReportClient.LogFiles();
        string infoText = ReportClient.InfoText(i);
        var media = new List<string>(_attach);
        _sending = Task.Run(async () =>
        {
            byte[] zip = ReportClient.ZipLogs(files, infoText);
            return await ReportClient.Send(i, category, title, body, zip, media).ConfigureAwait(false);
        });
        SetStatus(new Text("Report.Sending"), Sub);
    }

    // ---- 話す ----

    private static void OpenChat(ReportClient.Saved s)
    {
        _open = s;
        _thread = null;
        _nextPoll = 0f;
        Go(Page.Chat);
    }

    private static Widget ChatPage()
    {
        var s = _open;
        var head = new Stack(true, Action("◀", () => Go(Page.List), 0.6f),
            new Label($"{Icon(s.Category)}  {Esc(s.Title)}", 1.9f) { Bold = true, Width = InnerW - 1.0f, NoWrap = true }) { Gap = 0.15f, Align = Align.Start };
        var col = new Stack(false) { Gap = 0.1f, Width = InnerW - 0.4f };
        col.Add(Small(new Text("Report.ChatIntro").With("id", s.Id), Sub, InnerW - 0.4f));
        if (_thread == null) col.Add(Small(new Text("Report.Loading"), Sub, InnerW - 0.4f));
        else
        {
            if (_thread.Messages.Count == 0) col.Add(Small(new Text("Report.NoReply"), Sub, InnerW - 0.4f));
            foreach (var m in _thread.Messages) col.Add(Bubble(m));
            if (_thread.Closed) col.Add(Small(new Text("Report.ClosedNote"), Sub, InnerW - 0.4f));
        }
        _messages = new ScrollView(new Vector2(InnerW, 3.45f), col);
        Widget input;
        if (_thread != null && _thread.Closed) input = new Spacer(0.1f, 0.48f);
        else
        {
            _chat = new TextField(new Vector2(InnerW - 2.45f, 0.48f), _chatText, new Text("Report.ChatHint")) { MaxLength = 500, OnChange = v => _chatText = v, OnSubmit = () => PostChat() };
            var plus = new Button(new Label(_picking != null ? "…" : "+", 2.2f) { Bold = true, TextAlign = TextAlignmentOptions.Center }, () => Pick(true))
            { Size = new Vector2(0.55f, 0.46f), Color = new Color(0.6f, 0.63f, 0.68f) };
            input = new Stack(true, plus, _chat, Action(_posting != null ? "…" : new Text("Report.ChatSend"), () => PostChat(), 1.5f, _posting == null)) { Gap = 0.2f };
        }
        return new Stack(false, head, _messages, input, StatusLine()) { Gap = 0.08f, Width = InnerW };
    }

    private static Widget Bubble(ReportClient.Message m)
    {
        float maxW = InnerW * 0.72f;
        string time = m.Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(m.Time).ToLocalTime().ToString("MM/dd HH:mm") : "";
        if (m.From == "system")
            return new Label($"<color=#9AA3AD>— {Esc(m.Text)} —</color>", 1.25f) { Width = InnerW - 0.4f, TextAlign = TextAlignmentOptions.Center };
        bool me = m.From == "me";
        string who = me ? new Text("Report.You") : $"{Esc(m.Name)} <color=#00E676>STAFF</color>";
        var text = new Stack(false, new Label($"<size=80%><color=#9AA3AD>{who}  {time}</color></size>", 1.3f) { Align = Align.Start }) { Gap = 0.05f };
        if (!string.IsNullOrEmpty(m.Text))
            text.Add(new Label(Esc(m.Text), 1.4f) { Width = Math.Min(maxW, Label.Measure(Esc(m.Text), 1.4f, false) + 0.1f), Align = Align.Start });
        foreach (var a in m.Attachments) text.Add(AttachmentView(a));
        var plate = new Plate(text) { Color = me ? MeBubble : StaffBubble, Padding = new Vector2(0.14f, 0.08f), Radius = 0.12f, Align = me ? Align.End : Align.Start };
        return plate;
    }

    // 添付 1 つ: 画像は小さな絵 (押すとブラウザーで元の大きさ)・動画などは名前の札 (押すとブラウザーで開く)
    private static Widget AttachmentView(ReportClient.Attachment a)
    {
        string name = Esc(a.Name.Length > 28 ? a.Name.Substring(0, 27) + "…" : a.Name);
        bool canOpen = a.Url.StartsWith("https://");
        if (a.Kind == "image" && a.Thumb.Length > 0 && Thumb(a) is Sprite sp && sp)
        {
            var b = sp.bounds.size;
            float k = Math.Min(ThumbW / Math.Max(b.x, 0.01f), ThumbH / Math.Max(b.y, 0.01f));
            var pic = new Picture(sp, b.x * k, b.y * k) { Align = Align.Start };
            if (!canOpen) return pic;
            return new Button(pic, () => OpenUrl(a.Url)) { Padding = new Vector2(0.05f, 0.05f), Color = new Color(0.35f, 0.38f, 0.44f), Align = Align.Start };
        }
        string size = a.Size > 0 ? $"  <color=#9AA3AD>{ReportClient.SizeText(a.Size)}</color>" : "";
        string loading = a.Kind == "image" && a.Thumb.Length > 0 && !_thumbs.ContainsKey(a.Id) ? $"  <color=#9AA3AD>{new Text("Report.Loading")}</color>" : "";
        var label = new Label($"{Kind(a.Kind)} {name}{size}{loading}", 1.25f) { NoWrap = true, Align = Align.Start };
        if (!canOpen) return label;
        return new Button(new Stack(true, label, new Label($"<color=#00E676>{new Text("Report.OpenFile")}</color>", 1.25f)) { Gap = 0.2f }, () => OpenUrl(a.Url))
        { Color = new Color(0.35f, 0.38f, 0.44f), Fill = new Color(0.09f, 0.1f, 0.12f), Align = Align.Start };
    }

    private static void OpenUrl(string url)
    {
        if (url.StartsWith("https://")) Application.OpenURL(url);
    }

    // 読めた絵 / まだなら読みに行って null
    private static Sprite Thumb(ReportClient.Attachment a)
    {
        if (_thumbs.TryGetValue(a.Id, out var sp)) return sp;
        if (a.Id.Length == 0 || !a.Thumb.StartsWith("https://") || !_thumbLoading.Add(a.Id)) return null;
        string id = a.Id, url = a.Thumb;
        Task.Run(async () =>
        {
            byte[] data = null;
            try { data = await ReportClient.Download(url).ConfigureAwait(false); }
            catch (Exception e) { Plugin.Logger.LogWarning($"report: thumb {id}: {e.Message}"); }
            _thumbDone.Enqueue((id, data));
        });
        return null;
    }

    private static void PostChat(List<string> media = null)
    {
        string text = _chatText.Trim();
        bool hasMedia = media is { Count: > 0 };
        if (_posting != null || _open == null || (text.Length == 0 && !hasMedia)) return;
        var s = _open;
        _posting = Task.Run(() => ReportClient.Post(s, text, media));
        _chatText = "";
        // 送った物はすぐ自分の吹き出しで出す (次に読み直した時にサーバーの物と入れ替わる)
        _thread ??= new ReportClient.Thread();
        var mine = new ReportClient.Message { From = "me", Text = text, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        if (hasMedia)
            foreach (var f in media)
                mine.Attachments.Add(new ReportClient.Attachment { Name = Path.GetFileName(f), Kind = FilePicker.IsVideo(f) ? "video" : "image", Size = SafeSize(f) });
        _thread.Messages.Add(mine);
        if (hasMedia) SetStatus(new Text("Report.Uploading"), Sub);
        else Build();
        _chat?.Focus();
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static void SetStatus(string text, Color c)
    {
        _status = text;
        _statusColor = c;
        Build();
    }

    private static string Error(Exception e)
    {
        string code = (e as ReportClient.ReportException ?? e.GetBaseException() as ReportClient.ReportException)?.Code ?? "network";
        return code switch
        {
            "not_seen" => new Text("Report.ErrNotSeen"),
            "too_many" => new Text("Report.ErrTooMany"),
            "banned" => new Text("Report.ErrBanned"),
            "closed" => new Text("Report.ClosedNote"),
            "disabled" => new Text("Report.ErrDisabled"),
            "no_friend_code" => new Text("Report.ErrNoFriendCode"),
            "too_large" => new Text("Report.ErrFileTooLarge"),
            "too_many_files" => new Text("Report.ErrTooManyFiles").With("n", ReportClient.MaxFiles.ToString()),
            "bad_media" => new Text("Report.ErrBadMedia"),
            "file_unreadable" => new Text("Report.ErrFileUnreadable"),
            "network" => new Text("Report.ErrNetwork"),
            "http404" or "http502" or "http503" => new Text("Report.ErrNotReady"),
            _ => new Text("Report.ErrOther").With("code", code),
        };
    }

    // 毎フレーム (Plugin.Update)。裏の送受信の終わりを本編の側で受け取る
    public static void Tick()
    {
        if (_sending != null && _sending.IsCompleted)
        {
            var t = _sending;
            _sending = null;
            if (t.IsFaulted) { Plugin.Logger.LogWarning($"report: send failed: {t.Exception?.GetBaseException().Message}"); if (IsOpen && _page == Page.Write) SetStatus(Error(t.Exception), Warn); }
            else
            {
                ReportClient.All.Add(t.Result);
                ReportClient.Save();
                _titleText = _bodyText = "";
                _attach.Clear();
                _info = null;
                Plugin.Logger.LogInfo($"report: sent #{t.Result.Id}");
                if (IsOpen) OpenChat(t.Result);
            }
        }
        if (_knownFetch != null && _knownFetch.IsCompleted)
        {
            var t = _knownFetch;
            _knownFetch = null;
            if (t.IsFaulted)
            {
                _known = null;
                // 一覧の口が無い = サーバーがまだ古い
                string code = (t.Exception?.GetBaseException() as ReportClient.ReportException)?.Code;
                _knownError = code is "not_found" or "http404" ? new Text("Report.KnownNotReady") : Error(t.Exception);
            }
            else { _known = t.Result; _knownError = null; }
            if (IsOpen && _page == Page.List && _tab != ListTabs.Mine) Build();
        }
        ReportClient.TickRoom();
        if (_posting != null && _posting.IsCompleted)
        {
            var t = _posting;
            _posting = null;
            if (t.IsFaulted) { if (IsOpen && _page == Page.Chat) SetStatus(Error(t.Exception), Warn); }
            else _nextPoll = 0f;
            if (IsOpen && _page == Page.Chat && !t.IsFaulted) { _status = null; Build(); }
        }
        if (_fetching != null && _fetching.IsCompleted)
        {
            var t = _fetching;
            _fetching = null;
            if (t.IsFaulted) { if (IsOpen && _page == Page.Chat && _thread == null) SetStatus(Error(t.Exception), Warn); }
            else if (_open != null)
            {
                bool changed = _thread == null || _thread.Closed != t.Result.Closed || Count(_thread) != t.Result.Messages.Count || LastId(_thread) != LastId(t.Result);
                _thread = t.Result;
                MarkRead(_open, t.Result);
                if (changed && IsOpen && _page == Page.Chat && _posting == null && !TextField.Typing) Build();
                else if (changed && IsOpen && _page == Page.Chat) _nextPoll = Time.realtimeSinceStartup + 1f; // 打っている間は作り直さず、少し後に読み直す
            }
        }
        if (_picking != null && _picking.IsCompleted)
        {
            var t = _picking;
            _picking = null;
            if (t.IsFaulted) Plugin.Logger.LogWarning($"report: picker: {t.Exception?.GetBaseException().Message}");
            if (IsOpen) { if (!t.IsFaulted && t.Result.Length > 0) Picked(t.Result); else Build(); }
        }
        while (_thumbDone.TryDequeue(out var th))
        {
            // 増えすぎたら全部捨てる (開いている報告の物はまた読む)
            if (_thumbs.Count >= 60)
            {
                foreach (var old in _thumbs.Values) if (old) { UnityEngine.Object.Destroy(old.texture); UnityEngine.Object.Destroy(old); }
                _thumbs.Clear();
                _thumbLoading.Clear();
            }
            _thumbs[th.id] = th.data != null ? Roles.ButtonIcons.FromBytes(th.data, "ReportThumb", 100f, new Vector2(0.5f, 0.5f)) : null;
            _thumbDirty = true;
        }
        if (_thumbDirty && IsOpen && _page == Page.Chat && _posting == null && !TextField.Typing) { _thumbDirty = false; Build(); }
        // 試合が始まったら閉じる (ロビーだけで使う)
        if (IsOpen && ShipStatus.Instance) UiWindow.Current.Close();
        if (IsOpen && _page == Page.Chat && _open != null && _fetching == null && Time.realtimeSinceStartup >= _nextPoll)
        {
            _nextPoll = Time.realtimeSinceStartup + PollSeconds;
            var s = _open;
            _fetching = Task.Run(() => ReportClient.Fetch(s));
        }
        ReportNotifier.Tick();
    }

    private static int Count(ReportClient.Thread t) => t.Messages.Count;

    private static string LastId(ReportClient.Thread t) => t.Messages.Count > 0 ? t.Messages[^1].Id : "";

    internal static void MarkRead(ReportClient.Saved s, ReportClient.Thread t)
    {
        string lastStaff = "";
        foreach (var m in t.Messages) if (m.From == "staff" && m.Id.Length > 0) lastStaff = m.Id;
        bool dirty = s.Closed != t.Closed;
        s.Closed = t.Closed;
        if (lastStaff.Length > 0 && s.LastStaff != lastStaff) { s.LastStaff = lastStaff; dirty = true; }
        if (IsOpen && _open == s && s.LastRead != s.LastStaff) { s.LastRead = s.LastStaff; dirty = true; }
        if (dirty) ReportClient.Save();
    }

    // 試験用 (ブリッジ): report open | new | title <字> | body <字> | chat <字> | send | info
    internal static void RegisterBridge()
    {
        Bridge.TestBridge.Register("report", "open|new|title <字>|body <字>|chat <字>|attach <道筋>|pick|send|demo [絵の道筋]|button <info|click|ensure> バグ報告の窓", (args, reply) =>
        {
            string a = args.Trim();
            string op = a.Split(' ', 2)[0];
            string rest = a.Contains(' ') ? a.Substring(a.IndexOf(' ') + 1) : "";
            switch (op)
            {
                case "open": Open(); break;
                case "nofc": _testNoFriendCode = !_testNoFriendCode; if (IsOpen) UiWindow.Current.Close(); Open(); break;
                case "tab":
                    if (!IsOpen) Open();
                    _tab = (ListTabs)Math.Clamp(int.TryParse(rest, out int ti) ? ti : 0, 0, 2);
                    if (_tab != ListTabs.Mine) FetchKnown();
                    Go(Page.List);
                    break;
                case "knowndemo":
                {
                    // 見た目の確認用 (サーバーへは読みに行かない)
                    long t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _known = new List<ReportClient.KnownBug>
                    {
                        new() { Id = "demo1", Title = "会議の後に名前が消える", ModVersion = "v0.0.1", GameVersion = "2025.9.9", Created = t0 - 3600 },
                        new() { Id = "demo2", Title = "ドッペルゲンガーの分身が壁を抜ける", ModVersion = "v0.0.1", GameVersion = "2025.9.9", Created = t0 - 86400 },
                        new() { Id = "demo3", Title = "サムライの斬り合いで画面が止まる", ModVersion = "v0.0.0", GameVersion = "2025.9.9", Created = t0 - 86400 * 5, Closed = true, ClosedAt = t0 - 86400 * 2 },
                    };
                    _knownError = null;
                    Build();
                    break;
                }
                case "new": if (!IsOpen) Open(); Go(Page.Write); break;
                case "title": _titleText = rest; Build(); break;
                case "body": _bodyText = rest.Replace("\\n", "\n"); Build(); break;
                case "chat": _chatText = rest; Build(); break;
                case "send": if (_page == Page.Chat) PostChat(); else Send(); break;
                case "focus": (_page == Page.Chat ? _chat : _title)?.Focus(); break;
                case "attach": _pickForChat = _page == Page.Chat; Picked(new[] { rest.Trim('"') }); break;
                case "pick": Pick(_page == Page.Chat); break;
                case "button": reply("OK report " + ReportNotifier.Command(rest.Trim())); return;
                case "demo":
                    // 見た目の確認用 (サーバーへは読みに行かない)
                    if (!IsOpen) Open();
                    _open = new ReportClient.Saved { Id = "demo000000", Title = "会議の後に名前が消える", Category = "bug", Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    _thread = new ReportClient.Thread();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _thread.Messages.Add(new ReportClient.Message { Id = "1", From = "staff", Name = "haru", Text = "報告ありがとうございます！どのマップで起きましたか？", Time = now - 300 });
                    _thread.Messages.Add(new ReportClient.Message { Id = "2", From = "me", Text = "Polus です。3 回に 1 回くらい起きます。", Time = now - 200 });
                    _thread.Messages.Add(new ReportClient.Message { Id = "3", From = "staff", Name = "haru", Text = "再現できました。次の版で直します！", Time = now - 60 });
                    // 添付の見た目 (絵は手元の画像を読む)
                    var shot = new ReportClient.Message { Id = "4", From = "staff", Name = "haru", Text = "この画面ですか？", Time = now - 30 };
                    string img = rest.Trim('"');
                    if (img.Length > 0 && File.Exists(img))
                    {
                        _thumbs["demo-img"] = Roles.ButtonIcons.FromBytes(File.ReadAllBytes(img), "ReportThumb", 100f, new Vector2(0.5f, 0.5f));
                        shot.Attachments.Add(new ReportClient.Attachment { Id = "demo-img", Name = "screenshot.png", Kind = "image", Url = "https://example.com/", Thumb = "https://example.com/t" });
                    }
                    _thread.Messages.Add(shot);
                    var clip = new ReportClient.Message { Id = "5", From = "me", Time = now - 10 };
                    clip.Attachments.Add(new ReportClient.Attachment { Id = "v", Name = "clip.mp4", Kind = "video", Url = "https://example.com/v", Size = 4_200_000 });
                    _thread.Messages.Add(clip);
                    _thread.Messages.Add(new ReportClient.Message { Id = "6", From = "system", Text = "スタッフが報告を読みました。", Time = now });
                    _nextPoll = float.MaxValue;
                    _status = null;
                    _page = Page.Chat;
                    Build();
                    break;
            }
            reply($"OK report open={IsOpen} page={_page} status={_status} saved={ReportClient.All.Count} unread={ReportClient.Unread} url={ReportClient.BaseUrl}");
        });
    }
}
