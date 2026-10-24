using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 役職辞典の窓。役職の一覧 (陣営の札・カード・右の要約) と、押した役職の説明の本文と今の設定を読める。
// ロビーのホストはここで設定も変えられる (閉じた時に保存して全員へ送る)。それ以外の人と試合中は読むだけ。
// 入口 = Esc の「MRP の設定」の板・設定画面の役職の頁の見出し。
internal static class RoleDictionary
{
    private const float WinW = 9.2f, WinH = 5.3f, InnerW = 8.6f, InnerH = 4.3f, TopH = 0.5f;

    // 開き直しても陣営の札などを残すので、作るのは 1 回だけ
    private static readonly RoleBrowser Browser = new(InnerW, InnerH, false, CanEdit, "dict")
    {
        Changed = () => _dirty = true,
    };

    private static bool _dirty;
    private static SearchBox _search;

    private static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost && MatchInfoExtension.InLobby;

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_RoleDictionary";

    public static void Open() => Open(null);

    public static void Open(Registry.Section sec)
    {
        Roles.Builtin.VanillaRoles.EnsureSettings();
        var w = UiWindow.Open("RoleDictionary", new Vector2(WinW, WinH), OnClosed);
        if (w == null) return;
        _dirty = false;
        var title = new Label(new Text("RoleDictionary.Title"), 2.4f) { MenuFont = true, Bold = true, Color = new Color(1f, 0.85f, 0.35f), Align = Align.Start };
        var holder = new Holder(new Vector2(InnerW, InnerH));
        w.Body.Set(new Stack(false, new Stack(true, title) { Width = InnerW, Align = Align.Start }, holder) { Gap = 0.12f, Width = InnerW });
        Browser.Attach(holder.Screen, sec);
        // 検索欄は本編のチャットの入力欄を複製して作る (窓の右上・見出しの高さ)
        _search = SearchBox.Create(w.Root.transform, new Vector3(InnerW / 2f - 1.6f, WinH / 2f - 0.38f, -1f), 3.0f, 0.42f,
            new Text("RoleDictionary.SearchHint"), q => Browser.SetQuery(q));
        if (_search != null) OrderByDepth(_search.Root.transform, 30);
    }

    // 窓は SortingGroup の中なので z では前後が決まらない。複製した入力欄の部品を、手前 (z が小さい) ほど後に描く
    private static void OrderByDepth(Transform root, int baseOrder)
    {
        var rs = root.GetComponentsInChildren<Renderer>(true);
        var list = new System.Collections.Generic.List<(float z, Renderer r)>(rs.Length);
        foreach (var r in rs) list.Add((r.transform.position.z, r));
        list.Sort((a, b) => b.z.CompareTo(a.z));
        for (int i = 0; i < list.Count; i++) list[i].r.sortingOrder = baseOrder + i;
    }

    private static void OnClosed()
    {
        Browser.Detach();
        if (_search != null) SearchBox.Forget(_search);
        _search = null;
        if (!_dirty) return;
        _dirty = false;
        SettingsMenu.CommitHostEdits();
        SettingsMenu.RefreshAll();
    }

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        string op = a.Length > 0 ? a[0] : "dump";
        if (op == "open")
        {
            Open(a.Length > 1 ? RoleBrowser.Find(a[1]) : null);
            return IsOpen ? Browser.Command(new[] { "dump" }) : "not opened";
        }
        if (op == "close")
        {
            UiWindow.Current?.Close();
            return $"closed slots={UiActions.Census()}";
        }
        if (!IsOpen) return "not open";
        if (op == "q")
        {
            string q = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "";
            if (_search != null) _search.Type(q);
            else Browser.SetQuery(q);
            return Browser.Command(new[] { "dump" });
        }
        return Browser.Command(a);
    }
}
