// Based on https://github.com/tukasa0001/TownOfHost pull/1265 (ClientOptionItem), as carried by
// https://github.com/Gurge44/EndlessHostRoles and https://github.com/waffle-ful/Aeterna-End-K-not (GPL-3.0)
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 自分の端末だけに効く ON/OFF (見た目・操作の好み)。本編の設定画面 (Esc) に「MRP の設定」ボタンを足し、押すと設定画面の中に板が開く。
// 板は上に分類のタブ (表示・試合・幽霊と観戦・ボイスチャット・その他)、下にその分類のボタンを 2 列。ボタンには今の値 (ON / OFF など) も書く。
// 値は BepInEx の設定ファイル ([Client] 節) に残る。部屋の設定 (ホストが決めて全員に配る物) とは別
internal static class ClientOptions
{
    private enum Cat { Display, Match, Ghost, Voice, Record, Other }

    private sealed class Item
    {
        public string Key;
        public Cat Cat;
        public ConfigEntry<bool> Entry; // null = 押すと Click を呼ぶだけのボタン
        public Action Click;
        public Text Label;
        public Action Changed;
        public Func<string> Value; // 押すたびに値が替わるボタン (字の後ろに今の値を出す)
        public Func<bool> Visible; // null = いつも出す / 板を開くたびに見て、false なら隠す
        public GameObject Go;
        public TextMeshPro Tmp;
        public SpriteRenderer Back;
        public ButtonRolloverHandler Rollover;
    }

    private static readonly List<Item> Items = new();
    private static ConfigFile _cfg;

    public static ConfigEntry<bool> DarkTheme { get; private set; }
    public static ConfigEntry<bool> DarkMeeting { get; private set; }
    public static ConfigEntry<bool> EndSummary { get; private set; }
    public static ConfigEntry<bool> SummaryToChat { get; private set; }
    public static ConfigEntry<bool> TaskOnName { get; private set; }
    public static ConfigEntry<bool> CopyRoomCode { get; private set; }
    public static ConfigEntry<bool> HintsShown { get; private set; } // 初回のヒントを出したか (一覧には並べない)
    public static ConfigEntry<bool> AutoPlayAgain { get; private set; }
    public static ConfigEntry<bool> Reconnect { get; private set; }
    public static ConfigEntry<bool> Spectate { get; private set; }
    public static ConfigEntry<bool> WatchDuels { get; private set; }
    public static ConfigEntry<bool> SabotageFlash { get; private set; }
    public static ConfigEntry<bool> SabotageAlarm { get; private set; }
    public static ConfigEntry<bool> PreciseCooldown { get; private set; }
    public static ConfigEntry<bool> GhostSeeRoles { get; private set; }
    public static ConfigEntry<int> ButtonScale { get; private set; }
    public static ConfigEntry<bool> KeyHints { get; private set; }
    public static ConfigEntry<bool> VoiceMic { get; private set; }
    public static ConfigEntry<bool> VoiceHear { get; private set; }

    public static void Bind(ConfigFile cfg)
    {
        _cfg = cfg;
        // 表示
        DarkTheme = Add(Cat.Display, "DarkTheme", false, new Text("ClientOptions.DarkChat"), ChatTheme.Refresh);
        DarkMeeting = Add(Cat.Display, "DarkMeeting", false, new Text("ClientOptions.DarkMeetingScreen"), null); // 次の会議から
        TaskOnName = Add(Cat.Display, "TaskOnName", true, new Text("ClientOptions.TaskOnName"), NameTaskTag.RefreshAll);
        PreciseCooldown = Add(Cat.Display, "PreciseCooldown", true, new Text("ClientOptions.PreciseCooldown"), null);
        KeyHints = Add(Cat.Display, "KeyHints", true, new Text("ClientOptions.KeyHints"), null);
        var scaleLabel = new Text("ClientOptions.ButtonScale");
        ButtonScale = cfg.Bind("Client", "ButtonScale", 100, scaleLabel.Builtin(false));
        Items.Add(new Item { Key = "ButtonScale", Cat = Cat.Display, Label = scaleLabel, Click = HudTweaks.NextScale, Value = HudTweaks.ScaleLabel });
        SabotageFlash = Add(Cat.Display, "SabotageFlash", true, new Text("ClientOptions.SabotageFlash"), null); // 次のサボタージュから
        SabotageAlarm = Add(Cat.Display, "SabotageAlarm", true, new Text("ClientOptions.SabotageAlarm"), null);
        // 試合
        EndSummary = Add(Cat.Match, "EndSummary", true, new Text("ClientOptions.ShowRolesAtEnd"), null);
        SummaryToChat = Add(Cat.Match, "SummaryToChat", true, new Text("ClientOptions.SummaryToChat"), null);
        AutoPlayAgain = Add(Cat.Match, "AutoPlayAgain", false, new Text("ClientOptions.AutoReturn"), null);
        Reconnect = Add(Cat.Match, "Reconnect", true, new Text("ClientOptions.AutoRejoin"), null);
        CopyRoomCode = Add(Cat.Match, "CopyRoomCode", true, new Text("ClientOptions.CopyRoomCode"), null);
        HintsShown = cfg.Bind("Client", "HintsShown", false, "初回の操作のヒントを出したか");
        // 幽霊・観戦
        Spectate = Add(Cat.Ghost, "Spectate", false, new Text("ClientOptions.Spectate"), Roles.Spectate.OnOptionChanged);
        WatchDuels = Add(Cat.Ghost, "WatchDuels", true, new Text("ClientOptions.WatchDuels"), null);
        GhostSeeRoles = Add(Cat.Ghost, "GhostSeeRoles", true, new Text("ClientOptions.GhostSeeRoles"), NameTaskTag.RefreshAll); // ホストが許した時だけ効く (会議の名札は次の会議から)
        // ボイスチャット
        VoiceMic = Add(Cat.Voice, "VoiceMic", false, new Text("ClientOptions.VoiceMic"), null); // マイクは自分で ON にした時だけ開ける
        VoiceHear = Add(Cat.Voice, "VoiceHear", true, new Text("ClientOptions.VoiceHear"), null);
        Voice.VoiceSettings.Bind(cfg);
        Items.Add(new Item { Key = "VoicePanel", Cat = Cat.Voice, Label = new Text("ClientOptions.VoicePanel"), Click = Voice.VoicePanel.Open });
        // 録画
        Recorder.Bind(cfg);
        Items.Add(new Item { Key = "Record", Cat = Cat.Record, Label = new Text("ClientOptions.Record"), Click = Recorder.Toggle, Value = Recorder.StateLabel });
        Items.Add(new Item { Key = "RecordVoice", Cat = Cat.Record, Entry = Recorder.RecordVoice, Label = new Text("ClientOptions.RecordVoice") });
        Items.Add(new Item { Key = "RecordQuality", Cat = Cat.Record, Label = new Text("ClientOptions.RecordQuality"), Click = Recorder.NextQuality, Value = Recorder.QualityLabel });
        Items.Add(new Item { Key = "RecordFolder", Cat = Cat.Record, Label = new Text("ClientOptions.RecordFolder"), Click = Recorder.OpenFolder });
        // その他
        Items.Add(new Item { Key = "RoleDictionary", Cat = Cat.Other, Label = new Text("RoleDictionary.Open"), Click = RoleDictionary.Open });
        Items.Add(new Item { Key = "JoinLists", Cat = Cat.Other, Label = new Text("JoinPanel.Open"), Click = JoinListPanel.Open, Visible = () => JoinListPanel.CanOpen });
        Items.Add(new Item { Key = "BugReport", Cat = Cat.Other, Label = new Text("Report.Open"), Click = Report.ReportWindow.Open, Visible = () => MatchInfoExtension.InLobby });
    }

    private static ConfigEntry<bool> Add(Cat cat, string key, bool def, Text label, Action changed)
    {
        var e = _cfg.Bind("Client", key, def, label.Builtin(false));
        Items.Add(new Item { Key = key, Cat = cat, Entry = e, Label = label, Changed = changed });
        return e;
    }

    private static readonly Color On = new Color32(0, 165, 255, 255);
    private static readonly Color Off = new Color32(77, 77, 77, 255);
    private static readonly Color Action = new Color32(40, 170, 120, 255);
    private static readonly Color Choice = new Color32(120, 95, 220, 255);
    private static readonly Color TabOn = new Color32(255, 165, 30, 255);
    private static readonly Color TabOff = new Color32(55, 58, 70, 255);

    // 板の中の置き場所 (板の大きさ基準)
    private const float TabY = 1.95f, TabStep = 1.3f, TabW = 1.22f;
    private const float RowY = 1.2f, RowStep = 0.56f, ColX = 1.95f, ItemW = 3.75f;

    private static SpriteRenderer _panel; // 開いた板 (本編の設定画面の子なので画面と一緒に消える)
    private static Transform _tab;        // 本編の設定の並び。板を開いている間は隠す (板には当たり判定が無く、押した所が下の本編のボタンに届くため)
    private static readonly List<(Cat cat, SpriteRenderer back, ButtonRolloverHandler roll)> Tabs = new();
    private static Cat _cat = Cat.Display;
    internal static ToggleButtonBehaviour Proto { get; private set; }

    // 押した時の処理は番号ごとに 1 つだけ作って使い回す (il2cpp 側へ渡すたびに解放されない参照が増えるため)。何も捕まえない
    private static readonly Dictionary<int, UnityAction> Clicks = new();
    private static readonly Dictionary<Cat, UnityAction> TabClicks = new();
    private static UnityAction _open, _close;

    public static bool IsOpen => _panel && _panel.gameObject.activeSelf;

    public static void Build(OptionsMenuBehaviour menu)
    {
        var proto = menu.DisableMouseMovement;
        if (!proto || _panel) return;
        Lang.Refresh();
        Proto = proto;

        _panel = NewPanel(menu, "MrpClientOptions");

        // 見出し: 「MRP の設定」と、この端末だけに効くこと
        var title = MakeText(proto, _panel.transform, "MrpClientTitle",
            $"<color=#FFD95A><b>{new Text("ClientOptions.MRPOptions")}</b></color>  <size=65%><color=#C8CCD8>{new Text("ClientOptions.OnlyYou")}</color></size>");
        title.transform.localPosition = new Vector3(0f, 2.6f, -6f);

        // 分類のタブ
        Tabs.Clear();
        var cats = (Cat[])Enum.GetValues(typeof(Cat));
        for (int i = 0; i < cats.Length; i++)
        {
            var c = cats[i];
            var b = MakeButton(proto, _panel.transform, "MrpClientTab" + c, $"<b>{new Text("ClientOptions.Cat" + c)}</b>", out var back, out var roll);
            Fit(b, back, TabW);
            b.transform.localPosition = new Vector3((i - (cats.Length - 1) / 2f) * TabStep, TabY, -6f);
            if (!TabClicks.TryGetValue(c, out var a)) TabClicks[c] = a = (UnityAction)(() => Select(c));
            SetClick(b, a);
            Tabs.Add((c, back, roll));
        }

        var close = MakeButton(proto, _panel.transform, "Back", new Text("ClientOptions.Back"), out var closeBack, out var closeRoll);
        close.transform.localPosition = new Vector3(0f, -2.3f, -6f);
        closeBack.color = Palette.DisabledGrey;
        closeRoll?.ChangeOutColor(Palette.DisabledGrey);
        SetClick(close, _close ??= (UnityAction)(() => ShowPanel(false)));

        // 開くボタンは「ゲームを抜ける」の所に置き、抜ける・戻るのボタンは左右へ寄せる
        PassiveButton leave = null, ret = null;
        foreach (var ui in menu.ControllerSelectable)
        {
            if (!ui) continue;
            if (ui.name == "LeaveGameButton") leave = ui.GetComponent<PassiveButton>();
            else if (ui.name == "ReturnToGameButton") ret = ui.GetComponent<PassiveButton>();
        }
        var tab = proto.transform.parent.parent.parent;
        _tab = tab;
        var open = MakeButton(proto, tab, "MrpClientOptionsButton", new Text("ClientOptions.MRPOptions"), out var openBack, out _);
        open.transform.localPosition = leave ? leave.transform.localPosition : new Vector3(0f, -2.4f, 1f);
        openBack.color = On;
        SetClick(open, _open ??= (UnityAction)(() => ShowPanel(true)));
        if (leave) leave.transform.localPosition = new Vector3(-1.35f, -2.411f, -1f);
        if (ret) ret.transform.localPosition = new Vector3(1.35f, -2.411f, -1f);

        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            var b = MakeButton(proto, _panel.transform, "MrpClient" + it.Key, it.Label, out it.Back, out it.Rollover);
            Fit(b, it.Back, ItemW);
            if (!Clicks.TryGetValue(i, out var a))
            {
                int idx = i;
                Clicks[i] = a = (UnityAction)(() => Toggle(idx));
            }
            SetClick(b, a);
            it.Go = b.gameObject;
            it.Tmp = b.GetComponentInChildren<TextMeshPro>(true);
            Paint(it);
        }
        Layout();
    }

    // 選んだ分類のボタンだけを上から 2 列で並べ、タブの色を塗り直す
    private static void Layout()
    {
        int n = 0;
        foreach (var it in Items)
        {
            if (!it.Go) continue;
            bool show = it.Cat == _cat && (it.Visible == null || it.Visible());
            it.Go.SetActive(show);
            if (!show) continue;
            it.Go.transform.localPosition = new Vector3(n % 2 == 0 ? -ColX : ColX, RowY - RowStep * (n / 2), -6f);
            Paint(it);
            n++;
        }
        foreach (var (cat, back, roll) in Tabs)
        {
            var c = cat == _cat ? TabOn : TabOff;
            if (back) back.color = c;
            roll?.ChangeOutColor(c);
        }
    }

    private static void Select(Cat c)
    {
        _cat = c;
        Layout();
    }

    // 本編の設定画面の背景を写した板 (閉じた状態で作る)
    internal static SpriteRenderer NewPanel(OptionsMenuBehaviour menu, string name)
    {
        var p = Object.Instantiate(menu.Background, menu.transform);
        p.name = name;
        p.transform.localScale = new Vector3(0.9f, 0.9f, 1f);
        p.transform.localPosition += Vector3.back * 8f;
        p.size += new Vector2(3f, 0f);
        p.gameObject.SetActive(false);
        return p;
    }

    internal static void ShowPanel(bool on)
    {
        if (on) Layout();
        if (_panel) _panel.gameObject.SetActive(on);
        HideTab(on);
    }

    internal static void HideTab(bool hide)
    {
        if (_tab) _tab.gameObject.SetActive(!hide);
    }

    // 本編の設定画面を閉じた: 次に開いた時に本編の並びが出るように戻す
    internal static void OnMenuClosed()
    {
        ShowPanel(false);
    }

    // 本編の ON/OFF ボタンを写して、見た目の部品だけ残す (本編のボタンの処理は自分の文字を言語の切り替えで書き直すので外す)
    internal static PassiveButton MakeButton(ToggleButtonBehaviour proto, Transform parent, string name, string label, out SpriteRenderer back, out ButtonRolloverHandler rollover)
    {
        var t = Object.Instantiate(proto, parent);
        t.name = name;
        back = t.Background;
        rollover = t.Rollover;
        TextMeshPro text = t.Text;
        var pb = t.GetComponent<PassiveButton>();
        Object.Destroy(t);
        if (text)
        {
            var tr = text.GetComponent<TextTranslatorTMP>();
            if (tr) Object.Destroy(tr);
            text.text = label;
        }
        return pb;
    }

    // 本編のボタンの字だけを写した見出し (当たり判定や背景は捨てる)
    private static TextMeshPro MakeText(ToggleButtonBehaviour proto, Transform parent, string name, string text)
    {
        var src = proto.Text;
        var t = Object.Instantiate(src, parent);
        t.name = name;
        var tr = t.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
        t.transform.localScale = Vector3.one;
        t.enableAutoSizing = false;
        t.fontSize = src.fontSize * 1.25f;
        t.enableWordWrapping = false;
        t.alignment = TextAlignmentOptions.Center;
        t.rectTransform.sizeDelta = new Vector2(9f, 0.6f);
        t.text = text;
        return t;
    }

    // ほかの画面で写した本編のボタンの横幅を変える (背景・光る枠・当たり判定。字と絵はそのまま)
    internal static void FitBack(PassiveButton b, float width)
    {
        if (!b) return;
        var text = b.GetComponentInChildren<TextMeshPro>(true);
        var size = text ? text.rectTransform.sizeDelta : Vector2.zero;
        bool auto = text && text.enableAutoSizing;
        Fit(b, b.GetComponentInChildren<SpriteRenderer>(true), width);
        if (!text) return;
        text.rectTransform.sizeDelta = size;
        text.enableAutoSizing = auto;
    }

    // ボタンの横幅を変える (背景・当たり判定・字の欄)。字は欄に収まるまで小さくする
    private static void Fit(PassiveButton b, SpriteRenderer back, float width)
    {
        if (!b) return;
        float w0 = 0f;
        if (back)
        {
            if (back.drawMode != SpriteDrawMode.Simple)
            {
                w0 = back.size.x;
                back.size = new Vector2(width, back.size.y);
            }
            else
            {
                w0 = back.sprite ? back.sprite.bounds.size.x * back.transform.localScale.x : width;
                var s = back.transform.localScale;
                back.transform.localScale = new Vector3(s.x * width / Math.Max(w0, 0.01f), s.y, s.z);
            }
        }
        // マウスを乗せた時に光る枠など、ほかの絵も同じだけ幅を変える (本編の幅のままだと隣のボタンまで光る)
        if (back && w0 > 0f)
            foreach (var sr in b.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == back || sr.drawMode == SpriteDrawMode.Simple) continue;
                sr.size = new Vector2(Math.Max(sr.size.x + width - w0, 0.1f), sr.size.y);
            }
        // 当たり判定は子に付いていることがある (本編のボタンの幅のままだと隣のボタンに重なり、押した所と違うボタンが反応する)。
        // ボタンからの大きさの比を見て、見た目と同じ幅にそろえる
        float bs = Math.Max(Math.Abs(b.transform.lossyScale.x), 1e-4f);
        foreach (var col in b.GetComponentsInChildren<BoxCollider2D>(true))
        {
            float cs = Math.Max(Math.Abs(col.transform.lossyScale.x), 1e-4f);
            col.size = new Vector2(width * bs / cs, col.size.y);
            col.offset = new Vector2(0f, col.offset.y);
        }
        var text = b.GetComponentInChildren<TextMeshPro>(true);
        if (text)
        {
            float maxSize = text.fontSize;
            text.enableAutoSizing = true;
            text.fontSizeMax = maxSize;
            text.fontSizeMin = maxSize * 0.45f;
            text.enableWordWrapping = false;
            text.rectTransform.sizeDelta = new Vector2((width - 0.25f) / Math.Max(text.transform.localScale.x, 0.01f), text.rectTransform.sizeDelta.y);
        }
    }

    internal static void SetClick(PassiveButton b, UnityAction a)
    {
        if (!b) return;
        b.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        b.OnClick.AddListener(a);
    }

    private static void Toggle(int idx)
    {
        var it = Items[idx];
        if (it.Entry == null)
        {
            try { it.Click?.Invoke(); }
            catch (Exception e) { Plugin.Logger.LogError($"client option {it.Key}: {e}"); }
            Paint(it);
            return;
        }
        it.Entry.Value = !it.Entry.Value;
        Paint(it);
        try { it.Changed?.Invoke(); }
        catch (Exception e) { Plugin.Logger.LogError($"client option {it.Key}: {e}"); }
    }

    // 色と字: ON は青・OFF は灰色、値を選ぶボタンは紫、開くボタンは緑。字の後ろに今の値を書く
    private static void Paint(Item it)
    {
        Color c;
        string tail;
        if (it.Entry != null)
        {
            c = it.Entry.Value ? On : Off;
            tail = it.Entry.Value ? "<color=#BFF0FF><b>ON</b></color>" : "<color=#B0B0B0><b>OFF</b></color>";
        }
        else if (it.Value != null)
        {
            c = Choice;
            tail = $"<color=#E6DCFF><b>{it.Value()}</b></color>";
        }
        else
        {
            c = Action;
            tail = "<color=#D4FFE6><b>▶</b></color>";
        }
        if (it.Back) it.Back.color = c;
        if (it.Rollover) it.Rollover.ChangeOutColor(c);
        if (it.Tmp) it.Tmp.text = $"{it.Label}  {tail}";
    }

    // 確認用: 一覧 / 名前で切り替え / cat <番号> で分類を選ぶ
    internal static string Describe(string key)
    {
        var hud = Vanilla.Hud;
        if (key == "menu")
        {
            if (hud && hud.GameMenu) hud.GameMenu.Open();
            else if (Object.FindObjectOfType<MainMenuManager>() is { } mm && mm.settingsButton) mm.settingsButton.OnClick.Invoke();
        }
        else if (key == "open") _open?.Invoke();
        else if (key == "close") _close?.Invoke();
        else if (key.StartsWith("cat ") && int.TryParse(key[4..], out int c) && c >= 0 && c <= (int)Cat.Other) Select((Cat)c);
        else if (key.Length > 0)
        {
            int i = Items.FindIndex(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return $"ERR client no option {key}";
            Toggle(i);
        }
        var sb = new System.Text.StringBuilder("OK client");
        foreach (var it in Items)
            if (it.Entry != null) sb.Append(' ').Append(it.Key).Append('=').Append(it.Entry.Value ? "on" : "off");
            else if (it.Value != null) sb.Append(' ').Append(it.Key).Append('=').Append(it.Value());
        sb.Append(" panel=").Append(_panel ? (_panel.gameObject.activeSelf ? "open" : "closed") : "none").Append(" cat=").Append(_cat);
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
internal static class ClientOptionsPatch
{
    public static void Postfix(OptionsMenuBehaviour __instance)
    {
        try { ClientOptions.Build(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"client options: {e}"); }
    }
}

// 開く時にも戻す (閉じる処理が埋め込まれて後置が呼ばれない道があっても、次に開いた時は本編の並びから)
[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Open))]
internal static class ClientOptionsOpenPatch
{
    public static void Postfix()
    {
        try { ClientOptions.OnMenuClosed(); }
        catch (Exception e) { Plugin.Logger.LogError($"client options: {e}"); }
    }
}

[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
internal static class ClientOptionsClosePatch
{
    public static void Postfix()
    {
        try { ClientOptions.OnMenuClosed(); }
        catch (Exception e) { Plugin.Logger.LogError($"client options: {e}"); }
    }
}
