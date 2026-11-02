using HarmonyLib;
using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// ホーム画面 (メインメニュー) にもバグ報告を開くボタンを置く。右の窓の左下 (更新のボタンが出ている時はその上)。
// 作りは更新のボタンと同じ (本編のニュースのボタンを写す)
[HarmonyPatch]
internal static class HomeReport
{
    private const float ButtonX = -2.4f, ButtonY = -2.0f, ButtonZ = -1f;
    private const float Step = 0.62f; // 更新のボタンと重ねる時の間 (ボタンの高さほど)
    private const int VisibilityEvery = 10;

    private static MainMenuManager _menu;
    private static GameObject _button;
    private static Transform _screens, _credits;
    private static bool _made;
    private static int _tick;
    private static UnityAction _onClick;

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start)), HarmonyPostfix]
    public static void MenuStart(MainMenuManager __instance)
    {
        _menu = __instance;
        _made = false;
        _button = null;
    }

    // FixedUpdate から
    public static void Tick()
    {
        if (_menu is null || ++_tick % VisibilityEvery != 0) return;
        if (!_menu)
        {
            _menu = null;
            _button = null;
            return;
        }
        if (!_made) Make();
        if (!_button) return;
        // 遊び方の選択・クレジットなど、右の窓に別の物が出ている間は隠す
        bool busy = _credits && _credits.gameObject.activeSelf;
        if (!busy && _screens)
        {
            for (int i = 0, n = _screens.childCount; i < n && !busy; i++)
                busy = _screens.GetChild(i).gameObject.activeSelf;
        }
        if (_button.activeSelf == busy) _button.SetActive(!busy);
        float y = ModUpdate.ButtonShown ? ButtonY + Step : ButtonY;
        var p = _button.transform.localPosition;
        if (!Mathf.Approximately(p.y, y)) _button.transform.localPosition = FxMath.V3(ButtonX, y, ButtonZ);
    }

    private static void Make()
    {
        _made = true;
        var proto = _menu.newsButton;
        var panel = _menu.transform.Find("MainUI/AspectScaler/RightPanel");
        if (!proto || !panel) { Plugin.Logger.LogWarning("home report: menu parts not found"); return; }
        _screens = panel.Find("MaskedBlackScreen");
        _credits = panel.Find("CreditsSizer/CreditsScreen");

        _button = Object.Instantiate(proto.gameObject, panel);
        _button.name = "MrpReportButton";
        var t = _button.transform;
        t.localPosition = FxMath.V3(ButtonX, ButtonY, ButtonZ);
        t.localScale = proto.transform.localScale;
        var news = _button.GetComponent<NewsCountButton>();
        if (news) Object.Destroy(news);
        var badge = t.Find("NewItem");
        if (badge) badge.gameObject.SetActive(false);

        var label = t.Find("FontPlacer/Text_TMP");
        var tr = label ? label.GetComponent<TextTranslatorTMP>() : null;
        if (tr) Object.Destroy(tr);
        var tmp = label ? label.GetComponent<TextMeshPro>() : null;
        if (tmp) tmp.text = new Text("Report.Open").ToString();

        _onClick ??= (UnityAction)(() => { if (!Report.ReportWindow.IsOpen) Report.ReportWindow.Open(); });
        ClientOptions.SetClick(_button.GetComponent<PassiveButton>(), _onClick);
    }
}
