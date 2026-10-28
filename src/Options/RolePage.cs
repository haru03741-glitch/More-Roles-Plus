using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面の「役職」タブ。中身は RoleBrowser (陣営の札・役職のカード・細かい頁・検索の結果)。
// 左の検索欄の語もここへ流す (本編のゲーム設定・MRP 全般・陣営の設定の行も出す)。
internal static class RolePage
{
    // タブの中の置き場 (タブの GameOptionsMenu の座標で)
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;

    // 開き直しても陣営の札などを残すので、作るのは 1 回だけ
    private static readonly RoleBrowser Browser = new(W, H, true, CanEdit, "rolepage")
    {
        Changed = SettingsMenu.RefreshCatCounts,
        HeadExtra = DictionaryButton,
        TeamFromOutside = true,
    };

    private static GameObject _root;
    private static bool _built;  // 毎フレームの確かめを Unity の生存確認より先に済ませるため
    private static string _query = "";

    // 設定画面はホストしか開けないが、試合の情報などから開いた時に備えて確かめる
    private static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;

    public static void Build(GameOptionsMenu m)
    {
        Forget();
        if (!m || !UiAssets.Box) return;
        Roles.Builtin.VanillaRoles.EnsureSettings();
        // タブの元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        _root = Widget.Node("MrpRolePage", m.transform, m.gameObject.layer);
        _root.transform.localPosition = Center;
        // 頁ごと本編の設定画面の上に描く (中のスクロール欄は自分の切り抜きを持つ)
        var group = _root.AddComponent<UnityEngine.Rendering.SortingGroup>();
        group.sortingOrder = 5;
        _built = true;
        Browser.ChipScreen = ChipScreen(m);
        Browser.Attach(new UiScreen(_root.transform) { MaxWidth = W, BaseOrder = 1 });
        if (_query.Length > 0) Browser.SetQuery(_query);
    }

    // 絞り込みの札の置き場 = 左の列の下 (左の列の位置が分からなければ一覧の上に出す)
    private static UiScreen ChipScreen(GameOptionsMenu m)
    {
        if (!SettingsMenu.CatFoot(out var topLeft, out float width)) return null;
        var t = m.transform;
        var go = Widget.Node("MrpRoleChips", t, m.gameObject.layer);
        var local = t.InverseTransformPoint(topLeft);
        go.transform.localPosition = new Vector3(local.x, local.y, Center.z);
        go.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        float w = t.InverseTransformVector(new Vector3(width, 0f, 0f)).x;
        return new UiScreen(go.transform) { Anchor = Align.Start, MaxWidth = w, BaseOrder = 1 };
    }

    public static void Forget()
    {
        Browser.Detach();
        _root = null;
        _built = false;
        _query = "";
    }

    // 検索欄の語が変わった (空 = 検索をやめた)。タブがまだ作られていなければ、作る時に使う
    public static void SetQuery(string q)
    {
        _query = q ?? "";
        if (_built) Browser.SetQuery(_query);
    }

    // 一覧に出す陣営 (設定画面の左の列で選ぶ)
    public static int Team => Browser.Team;
    public static void SetTeam(int kind) => Browser.Team = kind;

    // 細かい頁の見出しの右に「役職辞典で見る」(説明の本文はそちらに)
    private static Widget DictionaryButton(Registry.Section sec) =>
        sec.Role == null ? null
            : new Button(new Text("RoleDictionary.ShowInDictionary"), () => Menu.RoleDictionary.Open(sec), 1.35f) { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.6f, 0.42f) };

    // 設定画面の外 (役職辞典の窓) で値を変えた後に、開いたままのタブのカードの値と縁の色を今の値へ
    public static void RefreshCards()
    {
        if (!_built || !_root) return;
        Browser.RefreshFromOutside();
        SettingsMenu.RefreshCatCounts();
    }

    public static void Tick()
    {
        if (!_built || !_root) return;
        Browser.Tick();
    }

    // ---- 試験用 ----

    internal static string Command(string[] a)
    {
        // 検索は役職のタブが未だ作られていなくても打てる (打つと役職のタブが開いて作られる)
        if (a.Length > 0 && a[0] == "q")
        {
            if (!SettingsMenu.TypeSearch(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "")) return "no search box";
            a = new[] { "dump" };
        }
        if (!_root) return "role page not built";
        return Browser.Command(a);
    }
}
