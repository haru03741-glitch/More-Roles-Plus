using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 役職辞典: 全部の役職とアドオンを画面いっぱいの板に並べ、押した物の説明と今の部屋の設定を出す。
// 左 = 名前の一覧 (● = 今の部屋で出る)・右 = 選んだ物のページ。試合中は自分の役職のページから開く。
// 開くたびと選ぶたびに作り直し、開いている間は何もしない (毎フレームの処理は無い)
internal static class RoleBook
{
    private const int Order = 1100, Rows = 11, PerPage = Rows * 2;
    private const float RowH = 0.34f, RowGap = 0.06f;
    private static readonly Color BackColor = new(0.05f, 0.05f, 0.07f, 0.96f);
    private static readonly Color RowColor = new(0.17f, 0.17f, 0.21f, 1f);
    private static readonly Color RowPicked = new(0.32f, 0.32f, 0.4f, 1f);

    private static GameObject _root;
    private static Assignable _shown;
    private static int _listPage;
    private static readonly List<Assignable> Entries = new();
    private static readonly Dictionary<int, UnityAction> Picks = new();
    private static UnityAction _close, _prev, _next, _none;

    public static bool IsOpen => _root;

    public static void Toggle()
    {
        if (IsOpen) Hide();
        else Open(null);
    }

    // focus = 開くページ (null なら自分の役職か一覧の先頭)
    public static void Open(Assignable focus)
    {
        try
        {
            Collect();
            if (Entries.Count == 0) return;
            _shown = focus ?? Mine() ?? Entries[0];
            int i = Entries.IndexOf(_shown);
            _listPage = i < 0 ? 0 : i / PerPage;
            Build();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"role book: {e}");
            Hide();
        }
    }

    public static void Hide()
    {
        if (_root) Object.Destroy(_root);
        _root = null;
    }

    // /book [役職]
    public static string Command(string args)
    {
        if (args.Length == 0) { Open(null); return ""; }
        Assignable a = Commands.HostActions.FindRole(args);
        a ??= Commands.InfoCommands.FindAddon(args);
        if (a == null) return new Text("RoleBook.NotFound").With("arg", args);
        Open(a);
        return "";
    }

    // 設定画面と同じ並び (クルー → インポスター → 第三陣営 → アドオン)
    private static void Collect()
    {
        Entries.Clear();
        foreach (var tab in new[] { Tab.Crew, Tab.Impostor, Tab.Neutral, Tab.Addon })
            foreach (var sec in Registry.SectionsOf(tab))
                if (sec.Role != null) Entries.Add(sec.Role);
    }

    // 自分の役職の雛形 (出現率と設定は雛形が持つ)。本編の役職のままの人は本編の役職の雛形
    private static Assignable Mine()
    {
        var me = PlayerControl.LocalPlayer;
        if (!ShipStatus.Instance || !me || me.Data == null) return null;
        string id = RoleState.Local?.Id ?? (me.Data.Role ? me.Data.Role.Role.ToString() : null);
        if (id == null) return null;
        foreach (var a in Entries)
            if (a.Id == id) return a;
        return null;
    }

    private static bool Appears(Assignable a) => a.Chance != null && a.Chance.Value > 0;

    private static string TeamLabel(Assignable a) => a is RoleBase r
        ? r.Team switch
        {
            Team.Crew => new Text("InfoCommands.Crew"),
            Team.Impostor => new Text("InfoCommands.Impostor"),
            _ => new Text("InfoCommands.Neutral"),
        }
        : new Text("InfoCommands.Addon");

    // 右のページの文
    internal static string Page(Assignable a)
    {
        var sb = new StringBuilder();
        sb.Append("<size=150%>").Append(a.ColoredName).Append("</size>");
        if (a == Mine()) sb.Append("   <size=80%><color=#FFD84D>").Append(new Text("RoleBook.Mine").ToString()).Append("</color></size>");
        sb.Append("\n<color=#A8A8B4>").Append(TeamLabel(a)).Append("   ");
        if (Appears(a))
            sb.Append(new Text("RoleBook.InRoom").With("chance", a.Chance.Value).With("count", a.Count != null ? a.Count.Value : 1).ToString());
        else sb.Append(new Text("RoleBook.NotInRoom").ToString());
        sb.Append("</color>\n\n").Append(a.Help.ToString()).Append('\n');

        Registry.Section sec = null;
        foreach (var s in Registry.SectionsOf(a.Tab))
            if (s.Role == a) { sec = s; break; }
        bool head = false;
        if (sec != null)
            foreach (var o in sec.Opts)
            {
                if (o == a.Chance || o == a.Count) continue;
                if (!head)
                {
                    sb.Append("\n<color=#A8A8B4>― ").Append(new Text("RoleBook.Settings").ToString()).Append(" ―</color>\n");
                    head = true;
                }
                o.Sync();
                sb.Append(o.Label.ToString()).Append(": ").Append(o.Display()).Append('\n');
            }
        return sb.ToString();
    }

    private static TextMeshPro Proto()
    {
        var hud = Vanilla.Hud;
        if (hud && hud.TaskPanel && hud.TaskPanel.taskText) return hud.TaskPanel.taskText;
        var chat = Vanilla.Chat;
        return chat && chat.freeChatField ? chat.freeChatField.charCountText : null;
    }

    private static void Build()
    {
        var hud = Vanilla.Hud;
        var cam = Camera.main;
        var proto = Proto();
        if (!hud || !cam || !proto) return;
        Hide();
        float h = cam.orthographicSize, w = h * cam.aspect;

        _root = new GameObject("MrpRoleBook") { layer = hud.gameObject.layer };
        _root.transform.SetParent(hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, -150f);
        var back = MenuArt.Panel(_root.transform, "Back", 0.3f, new Vector2(w * 2f - 0.4f, h * 2f - 0.4f), Vector3.zero, BackColor, null);
        back.sortingOrder = Order;
        // 板の上を押しても下の本編のボタン (キルなど) に届かないように、板全体で受けて何もしない
        Clickable(back.gameObject, new Vector2(w * 2f - 0.4f, h * 2f - 0.4f), _none ??= (UnityAction)(() => { }));

        float left = -w + 0.45f, top = h - 0.45f;
        Label(proto, _root.transform, new Vector3(left + w / 2f, top, -1f), new Vector2(w, 0.5f), TextAlignmentOptions.Left, 1.8f,
            $"<b>{new Text("RoleBook.Title")}</b>   <size=60%><color=#A8A8B4>{new Text("RoleBook.Hint")}</color></size>");

        // 閉じる
        var close = MenuArt.Panel(_root.transform, "Close", 0.08f, new Vector2(0.5f, 0.5f), new Vector3(w - 0.55f, h - 0.5f, -1f), RowColor, null);
        close.sortingOrder = Order + 1;
        Clickable(close.gameObject, new Vector2(0.5f, 0.5f), _close ??= (UnityAction)Hide);
        Label(proto, close.transform, new Vector3(0f, 0f, -0.1f), new Vector2(0.5f, 0.5f), TextAlignmentOptions.Center, 2f, "×");

        // 左の一覧 (2 列)
        float listW = (w * 2f - 0.9f) * 0.36f, colW = listW / 2f - 0.05f;
        float rowTop = top - 0.6f;
        int pages = (Entries.Count + PerPage - 1) / PerPage;
        if (_listPage >= pages) _listPage = Math.Max(0, pages - 1);
        for (int n = 0; n < PerPage; n++)
        {
            int k = _listPage * PerPage + n;
            if (k >= Entries.Count) break;
            var a = Entries[k];
            float x = left + colW / 2f + (n / Rows) * (colW + 0.1f);
            float y = rowTop - (n % Rows) * (RowH + RowGap);
            var row = MenuArt.Panel(_root.transform, "Row" + n, 0.06f, new Vector2(colW, RowH), new Vector3(x, y, -1f), a == _shown ? RowPicked : RowColor, null);
            row.sortingOrder = Order + 1;
            int slot = n;
            if (!Picks.TryGetValue(slot, out var act)) Picks[slot] = act = (UnityAction)(() => Pick(slot));
            Clickable(row.gameObject, new Vector2(colW, RowH), act);
            Label(proto, row.transform, new Vector3(0f, 0f, -0.1f), new Vector2(colW - 0.16f, RowH - 0.04f), TextAlignmentOptions.Left, 1.4f,
                (Appears(a) ? "<color=#7CFF7C>●</color> " : "<color=#55555F>●</color> ") + a.ColoredName);
        }
        if (pages > 1)
        {
            float y = rowTop - Rows * (RowH + RowGap);
            Pager(proto, left + 0.3f, y, "<", _prev ??= (UnityAction)(() => Turn(-1)));
            Pager(proto, left + listW - 0.3f, y, ">", _next ??= (UnityAction)(() => Turn(1)));
            Label(proto, _root.transform, new Vector3(left + listW / 2f, y, -1f), new Vector2(1f, RowH), TextAlignmentOptions.Center, 1.3f, $"{_listPage + 1}/{pages}");
        }

        // 右のページ
        float pageX = left + listW + 0.3f, pageW = w - 0.45f - pageX;
        var page = Label(proto, _root.transform, new Vector3(pageX + pageW / 2f, rowTop + RowH / 2f - (h * 2f - 1.6f) / 2f, -1f),
            new Vector2(pageW, h * 2f - 1.6f), TextAlignmentOptions.TopLeft, 1.5f, Page(_shown));
        page.enableWordWrapping = true;
        page.fontSizeMin = 0.9f;
    }

    private static void Pick(int slot)
    {
        int k = _listPage * PerPage + slot;
        if (k < 0 || k >= Entries.Count) return;
        _shown = Entries[k];
        Build();
    }

    internal static void Turn(int d)
    {
        int pages = (Entries.Count + PerPage - 1) / PerPage;
        if (pages <= 1) return;
        _listPage = ((_listPage + d) % pages + pages) % pages;
        Build();
    }

    private static void Pager(TextMeshPro proto, float x, float y, string glyph, UnityAction act)
    {
        var b = MenuArt.Panel(_root.transform, "Page" + glyph, 0.06f, new Vector2(0.5f, RowH), new Vector3(x, y, -1f), RowColor, null);
        b.sortingOrder = Order + 1;
        Clickable(b.gameObject, new Vector2(0.5f, RowH), act);
        Label(proto, b.transform, new Vector3(0f, 0f, -0.1f), new Vector2(0.5f, RowH), TextAlignmentOptions.Center, 1.4f, glyph);
    }

    private static void Clickable(GameObject go, Vector2 size, UnityAction act)
    {
        var col = go.AddComponent<BoxCollider2D>();
        col.size = size;
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener(act);
    }

    // 文の欄。pos は欄の真ん中 (TopLeft の時も同じ)
    private static TextMeshPro Label(TextMeshPro proto, Transform parent, Vector3 pos, Vector2 size, TextAlignmentOptions align, float maxSize, string text)
    {
        var t = Object.Instantiate(proto, parent);
        t.gameObject.layer = parent.gameObject.layer;
        if (t.font) t.fontSharedMaterial = t.font.material; // 作業一覧の切り抜きを外す
        t.transform.localPosition = pos;
        t.transform.localScale = Vector3.one;
        t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        t.rectTransform.sizeDelta = size;
        t.alignment = align;
        t.enableWordWrapping = false;
        t.enableAutoSizing = true;
        t.fontSizeMin = 0.6f;
        t.fontSizeMax = maxSize;
        t.color = Color.white;
        t.richText = true;
        t.text = text;
        t.sortingOrder = Order + 2;
        return t;
    }

    // dev: 状態 / 名前のページを開く / 一覧の slot 番目を押す / 閉じる
    internal static string Simulate(string args)
    {
        if (args == "close") Hide();
        else if (args.StartsWith("pick ") && int.TryParse(args[5..], out int s)) Pick(s);
        else if (args.StartsWith("page ") && int.TryParse(args[5..], out int d)) Turn(d);
        else if (args.Length > 0) { string r = Command(args == "open" ? "" : args); if (r.Length > 0) return "ERR book " + r; }
        return $"OK book open={IsOpen} shown={(_shown?.Id ?? "-")} entries={Entries.Count} listPage={_listPage}";
    }
}

// 右上の本のボタン (ロビーでも試合中でも押せる)。本編の設定のボタンと同じ端からの距離の決め方で、同じ高さの左に置く。
// 右上の並びは場面で伸び縮みする (試合中は 試合の情報・チャット・設定・地図) ので、いちばん伸びた時の左端よりさらに左
//   (設定のボタンから 2.2 = ボタン 3 つ分強)
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
internal static class RoleBookButton
{
    private const float Left = 2.2f;
    private static UnityAction _click;

    public static void Postfix(HudManager __instance)
    {
        try { Create(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"role book button: {e.Message}"); }
    }

    private static void Create(HudManager hud)
    {
        var settings = hud.SettingsButton;
        var anchor = settings ? settings.GetComponent<AspectPosition>() : null;
        if (!anchor) { Plugin.Logger.LogWarning("role book button: no settings button to line up with"); return; }
        var go = new GameObject("MrpRoleBookButton") { layer = settings.layer };
        go.transform.SetParent(settings.transform.parent, false);
        go.transform.localPosition = settings.transform.localPosition + new Vector3(-Left, 0f, 0f);
        const float s = 0.62f;
        var edge = MenuArt.Panel(go.transform, "Edge", 0.12f, new Vector2(s, s), new Vector3(0f, 0f, 0f), new Color(0.06f, 0.06f, 0.07f, 1f), null);
        var bg = MenuArt.Panel(go.transform, "Bg", 0.1f, new Vector2(s - 0.08f, s - 0.08f), new Vector3(0f, 0f, -0.01f), new Color(0.27f, 0.28f, 0.31f, 1f), null);
        var icon = new GameObject("Icon") { layer = go.layer };
        icon.transform.SetParent(go.transform, false);
        icon.transform.localPosition = new Vector3(0f, 0f, -0.02f);
        icon.transform.localScale = new Vector3(1.4f, 1.4f, 1f);
        var isr = icon.AddComponent<SpriteRenderer>();
        isr.sprite = MenuArt.Book();
        isr.color = new Color(0.95f, 0.85f, 0.6f, 1f);
        var ssr = settings.GetComponent<SpriteRenderer>();
        int order = ssr ? ssr.sortingOrder : 0;
        edge.sortingOrder = order;
        bg.sortingOrder = order + 1;
        isr.sortingOrder = order + 2;

        var pos = go.AddComponent<AspectPosition>();
        pos.Alignment = anchor.Alignment;
        pos.DistanceFromEdge = anchor.DistanceFromEdge + new Vector3(Left, 0f, 0f);
        pos.AdjustPosition();

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(s, s);
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener(_click ??= (UnityAction)RoleBook.Toggle);
    }
}
