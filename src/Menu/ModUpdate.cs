using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// メインメニューの更新ボタン (PC だけ。Android の DLL はランチャーが入れる)。
// 起動ごとに 1 回、GitHub のリリースの一番新しい物 (プレリリースも含む) を裏のスレッドで読み、
// 今より新しい版に MoreRolesPlus.dll が付いていれば、右の窓の左下に本編の「ニュース」ボタンの写しを出す。
// 見つけた版ごとに 1 回だけ本編の確認窓でも知らせる。押すと DLL を落として今の DLL と入れ替える
// (読み込み中の DLL は消せないが名前は変えられるので .bak へ移し、次の起動で消す)。
internal static class ModUpdate
{
    private const string Repo = "haru03741-glitch/More-Roles-Plus";
    private const string AssetName = "MoreRolesPlus.dll";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=1";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    private const string PageUrl = "https://github.com/" + Repo + "/releases";

    private enum State { Idle, Checking, None, Available, Downloading, Done, Failed }

    private sealed class Release
    {
        public Version Ver;
        public string Tag, Url;
    }

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<string> _notified; // 確認窓で知らせた版のタグ (同じ版では 2 回出さない)

    private static State _state;
    private static Task<Release> _check;
    private static Task<string> _download; // 結果 = 失敗の理由 (null = 入れ替えた)
    private static Release _latest;
    private static string _error;

    private static MainMenuManager _menu;
    private static bool _made, _popupChecked;
    private static GameObject _button;
    private static TextMeshPro _buttonText;
    private static Transform _screens, _credits; // 右の窓の中身 (遊び方の選択など)。開いている間はボタンを隠す
    private static int _tick;

    private static GenericPopup _popup;
    private static PassiveButton _popupA, _popupB;
    private static TextMeshPro _popupAText, _popupBText;

    // 押した時の処理は 1 つずつ作って使い回す (il2cpp 側へ渡すたびに解放されない参照が増えるため)
    private static UnityAction _onButton, _onUpdate, _onClose, _onQuit, _onPage;

    // 右の窓 (RightPanel) の中の置き場所。窓の内側の左下から 0.2 内側 (右側は横長の画面で画面の外へ出る・本編の押すなボタンもある)
    private const float ButtonX = -2.4f, ButtonY = -2.0f, ButtonZ = -1f;
    private const int VisibilityEvery = 10; // FixedUpdate 10 回ごと (0.2 秒) に右の窓の中身を見る

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Update", "CheckForUpdates", true, "起動時に GitHub で新しい版を確かめ、メインメニューに更新ボタンを出す");
        _notified = cfg.Bind("Update", "NotifiedVersion", "", "確認窓で知らせた版 (同じ版では 2 回出さない)");
#if !ANDROID
        RemoveLeftovers();
#endif
    }

    private static string DllPath => typeof(ModUpdate).Assembly.Location;

    // 前回の更新で残した古い DLL と書きかけのファイル (今は読み込まれていないので消せる)
    private static void RemoveLeftovers()
    {
        try
        {
            string dll = DllPath;
            if (string.IsNullOrEmpty(dll)) return;
            foreach (string f in new[] { dll + ".bak", dll + ".tmp" })
            {
                if (!File.Exists(f)) continue;
                File.Delete(f);
                Plugin.Logger.LogInfo($"update: removed {Path.GetFileName(f)}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"update: leftovers not removed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    private static class MenuStart
    {
        public static void Postfix(MainMenuManager __instance)
        {
#if !ANDROID
            _menu = __instance;
            _made = false;
            _button = null;
            _popup = null;
            if (_state == State.Idle && _enabled.Value) StartCheck();
#endif
        }
    }

    private static void StartCheck()
    {
        _state = State.Checking;
        _check = Task.Run(FetchLatest);
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.Add("User-Agent", "MoreRolesPlus-Updater"); // 無いと GitHub の API は 403 を返す
        return http;
    }

    // 裏のスレッド。ゲーム側の物には触らない
    private static async Task<Release> FetchLatest()
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        using var res = await http.GetAsync(ApiUrl).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"status {(int)res.StatusCode}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var r = root[0];
        if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        string tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseVersion(tag, out var ver) || !r.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n) || n.GetString() != AssetName) continue;
            string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url == null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            return new Release { Ver = ver, Tag = tag, Url = url };
        }
        return null;
    }

    // "v0.2.0" / "0.2.0-beta" → 0.2.0
    private static bool TryParseVersion(string s, out Version v)
    {
        v = null;
        if (string.IsNullOrEmpty(s)) return false;
        s = s.TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        return Version.TryParse(s, out v);
    }

    // FixedUpdate から。確かめ終わって新しい版が無ければ何もしない
    public static void Tick()
    {
        switch (_state)
        {
            case State.Idle:
            case State.None:
                return;
            case State.Checking:
                if (_check.IsCompleted) FinishCheck();
                return;
            case State.Downloading:
                if (_download.IsCompleted) FinishDownload();
                break;
        }
        if (_menu is null || ++_tick % VisibilityEvery != 0) return;
        if (!_menu)
        {
            _menu = null;
            _button = null;
            _popup = null;
            return;
        }
        if (!_made) MakeButton();
        UpdateVisibility();
        if (!_popupChecked && _menu.finishStartup)
        {
            _popupChecked = true;
            // 確認窓を出せた時だけ「知らせた」と記録する (出せなければ次の起動でまた試す)
            if (_state == State.Available && _notified.Value != _latest.Tag && ShowFound()) _notified.Value = _latest.Tag;
        }
    }

    private static void FinishCheck()
    {
        Release r = null;
        try { r = _check.Result; }
        catch (Exception e) { Plugin.Logger.LogWarning($"update: check failed: {e.GetBaseException().Message}"); }
        _check = null;
        if (r == null || !TryParseVersion(Plugin.Version, out var cur) || r.Ver <= cur)
        {
            _state = State.None;
            Plugin.Logger.LogInfo($"update: none (latest {r?.Tag ?? "-"}, current {Plugin.Version})");
            return;
        }
        _latest = r;
        _state = State.Available;
        Plugin.Logger.LogInfo($"update: {r.Tag} available ({r.Url})");
    }

    private static void MakeButton()
    {
        _made = true;
        var proto = _menu.newsButton;
        var panel = _menu.transform.Find("MainUI/AspectScaler/RightPanel");
        if (!proto || !panel) { Plugin.Logger.LogWarning("update: menu parts not found"); return; }
        _screens = panel.Find("MaskedBlackScreen");
        _credits = panel.Find("CreditsSizer/CreditsScreen");

        _button = Object.Instantiate(proto.gameObject, panel);
        _button.name = "MrpUpdateButton";
        var t = _button.transform;
        t.localPosition = FxMath.V3(ButtonX, ButtonY, ButtonZ);
        t.localScale = proto.transform.localScale;
        var news = _button.GetComponent<NewsCountButton>();
        if (news) Object.Destroy(news);
        var badge = t.Find("NewItem"); // 本編の「新着」の印を「!」にして出す (写した時はニュースの数が入っている)
        if (badge)
        {
            badge.gameObject.SetActive(true);
            var count = badge.GetComponentInChildren<TextMeshPro>(true);
            if (count) count.text = "!";
        }

        var label = t.Find("FontPlacer/Text_TMP");
        _buttonText = label ? label.GetComponent<TextMeshPro>() : null;
        var tr = label ? label.GetComponent<TextTranslatorTMP>() : null;
        if (tr) Object.Destroy(tr);
        RefreshButtonText();

        _onButton ??= (UnityAction)OnButton;
        ClientOptions.SetClick(_button.GetComponent<PassiveButton>(), _onButton);
        UpdateVisibility();
    }

    private static void RefreshButtonText()
    {
        if (!_buttonText || _latest == null) return;
        _buttonText.text = (_state switch
        {
            State.Downloading => new Text("Update.ButtonDownloading"),
            State.Done => new Text("Update.ButtonRestart"),
            _ => new Text("Update.Button").With("ver", _latest.Tag),
        }).ToString();
    }

    // 遊び方の選択・オンライン・コード入力・クレジットなど、右の窓に別の物が出ている間は隠す
    private static void UpdateVisibility()
    {
        if (!_button) return;
        bool busy = _credits && _credits.gameObject.activeSelf;
        if (!busy && _screens)
        {
            for (int i = 0, n = _screens.childCount; i < n && !busy; i++)
                busy = _screens.GetChild(i).gameObject.activeSelf;
        }
        if (_button.activeSelf == busy) _button.SetActive(!busy);
    }

    private static void OnButton()
    {
        switch (_state)
        {
            case State.Available:
            case State.Failed:
                ShowFound();
                break;
            case State.Downloading:
                ShowPopup(new Text("Update.Downloading"), null, null, null, null);
                break;
            case State.Done:
                ShowDone();
                break;
        }
    }

    private static bool ShowFound()
    {
        _onUpdate ??= (UnityAction)StartDownload;
        _onClose ??= (UnityAction)ClosePopup;
        return ShowPopup(new Text("Update.Found").With("ver", _latest.Tag).With("cur", "v" + Plugin.Version),
            new Text("Update.Now"), _onUpdate, new Text("Update.Later"), _onClose);
    }

    private static void ShowDone()
    {
        _onQuit ??= (UnityAction)Application.Quit;
        _onClose ??= (UnityAction)ClosePopup;
        ShowPopup(new Text("Update.Done").With("ver", _latest.Tag), new Text("Update.Quit"), _onQuit, new Text("Update.Later"), _onClose);
    }

    private static void StartDownload()
    {
        if (_state != State.Available && _state != State.Failed) return;
        _state = State.Downloading;
        RefreshButtonText();
        ShowPopup(new Text("Update.Downloading"), null, null, null, null);
        string url = _latest.Url, dll = DllPath;
        Plugin.Logger.LogInfo($"update: downloading {_latest.Tag}");
        _download = Task.Run(() => Replace(url, dll));
    }

    // 裏のスレッド。落とした物を書いてから今の DLL を .bak へ移し、新しい物をその名前へ移す
    private static async Task<string> Replace(string url, string dll)
    {
        try
        {
            if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) return "dll not found";
            byte[] data;
            if (url.StartsWith("file:", StringComparison.Ordinal)) data = File.ReadAllBytes(url.Substring(5)); // dev の update fake だけ
            else
            {
                using var http = NewClient(TimeSpan.FromMinutes(3));
                data = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            if (data.Length < 64 * 1024 || data[0] != (byte)'M' || data[1] != (byte)'Z') return $"not a dll ({data.Length} B)";
            string tmp = dll + ".tmp", bak = dll + ".bak";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(dll, bak);
            try { File.Move(tmp, dll); }
            catch (Exception e)
            {
                try { File.Move(bak, dll); }
                catch { return $"{e.Message} (and {Path.GetFileName(bak)} could not be renamed back; rename it to {Path.GetFileName(dll)} by hand)"; }
                throw;
            }
            return null;
        }
        catch (Exception e) { return e.GetBaseException().Message; }
    }

    private static void FinishDownload()
    {
        _error = _download.Result;
        _download = null;
        if (_error == null)
        {
            _state = State.Done;
            Plugin.Logger.LogInfo($"update: replaced with {_latest.Tag} (restart to load)");
            RefreshButtonText();
            ShowDone();
            return;
        }
        _state = State.Failed;
        Plugin.Logger.LogWarning($"update: failed: {_error}");
        RefreshButtonText();
        _onPage ??= (UnityAction)OpenPage;
        _onClose ??= (UnityAction)ClosePopup;
        ShowPopup(new Text("Update.Failed"), new Text("Update.OpenPage"), _onPage, new Text("Update.Close"), _onClose);
    }

    private static void OpenPage()
    {
        Application.OpenURL(PageUrl);
        ClosePopup();
    }

    private static void ClosePopup()
    {
        if (_popup) _popup.Close();
    }

    // 本編の確認窓 (Twitch の知らせに使う GenericPopup) の写しに、ボタンを 2 つ並べる
    private static bool MakePopup()
    {
        if (_popup) return true;
        if (!Twitch.TwitchManager.InstanceExists) return false;
        var proto = Twitch.TwitchManager.Instance.TwitchPopup;
        if (!proto) return false;
        _popup = Object.Instantiate(proto);
        _popup.name = "MrpUpdatePopup";
        _popup.destroyOnClose = false;
        var exit = _popup.transform.Find("ExitGame");
        if (!exit) { Object.Destroy(_popup.gameObject); _popup = null; return false; }
        _popupA = exit.GetComponent<PassiveButton>();
        _popupB = Object.Instantiate(exit.gameObject, _popup.transform).GetComponent<PassiveButton>();
        _popupAText = ButtonLabel(_popupA);
        _popupBText = ButtonLabel(_popupB);
        var bg = _popup.transform.Find("Background");
        var sr = bg ? bg.GetComponent<SpriteRenderer>() : null;
        if (sr) sr.size = FxMath.V2(PopupWidth, PopupHeight);
        // 本編の字は箱に合わせて自動で縮むので、2 行の文が小さくなりすぎないよう大きさを決めて折り返す
        var text = _popup.TextAreaTMP;
        text.enableAutoSizing = false;
        text.fontSize = PopupFontSize;
        text.enableWordWrapping = true;
        text.rectTransform.sizeDelta = FxMath.V2(PopupWidth - 0.4f, 1.3f);
        var tp = text.transform.localPosition;
        text.transform.localPosition = FxMath.V3(tp.x, PopupTextY, tp.z);
        return true;
    }

    private const float PopupWidth = 4.6f, PopupHeight = 2.3f, PopupButtonX = 0.9f, PopupFontSize = 1.6f, PopupTextY = 0.35f;

    private static TextMeshPro ButtonLabel(PassiveButton b)
    {
        var label = b ? b.transform.Find("Text_TMP") : null;
        if (!label) return null;
        var tr = label.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        return label.GetComponent<TextMeshPro>();
    }

    private static bool ShowPopup(Text message, Text? a, UnityAction actA, Text? b, UnityAction actB)
    {
        if (!MakePopup()) return false;
        SetPopupButton(_popupA, _popupAText, a, actA, b == null ? 0f : -PopupButtonX);
        SetPopupButton(_popupB, _popupBText, b, actB, PopupButtonX);
        _popup.Show(message.ToString());
        return true;
    }

    private static void SetPopupButton(PassiveButton button, TextMeshPro label, Text? text, UnityAction act, float x)
    {
        if (!button) return;
        bool on = text != null;
        button.gameObject.SetActive(on);
        if (!on) return;
        var p = button.transform.localPosition;
        button.transform.localPosition = FxMath.V3(x, p.y, p.z);
        if (label) label.text = text.Value.ToString();
        ClientOptions.SetClick(button, act);
    }

    // dev: update [state | check | fake <版> [url|file:<パス>] | press | popup | reset]
    internal static string Command(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = a.Length > 0 ? a[0] : "state";
        switch (verb)
        {
            case "check":
                _state = State.Idle;
                _latest = null;
                StartCheck();
                return "checking";
            case "fake":
                if (a.Length < 2 || !TryParseVersion(a[1], out var v)) return "usage: update fake <版> [url|file:<パス>]";
                _latest = new Release { Ver = v, Tag = "v" + v, Url = a.Length > 2 ? a[2] : DownloadPrefix + "v" + v + "/" + AssetName };
                _state = State.Available;
                _popupChecked = false;
                if (_button) Object.Destroy(_button);
                _button = null;
                _made = false;
                return $"fake {_latest.Tag} {_latest.Url}";
            case "press":
                if (!_button) return "no button";
                _button.GetComponent<PassiveButton>().OnClick.Invoke();
                return "pressed";
            case "popup":
                if (_latest == null) return "no release";
                ShowFound();
                return "shown";
            case "reset":
                _notified.Value = "";
                return "notified cleared";
        }
        string btn = _button ? $"button={(_button.activeSelf ? "shown" : "hidden")} pos={_button.transform.localPosition}" : "button=none";
        return $"state={_state} latest={_latest?.Tag ?? "-"} current={Plugin.Version} notified={_notified.Value} {btn} popup={(_popup && _popup.gameObject.activeSelf ? "open" : "closed")} error={_error ?? "-"} dll={DllPath}";
    }
}
