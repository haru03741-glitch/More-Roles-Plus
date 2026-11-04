using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Menu;

// ランクマッチ。
//   オンラインの画面の「ランクマッチ」を押すと、MRP のサーバーで空きのあるランクマッチの部屋を探して入る。
//   無ければ自分で部屋を作り (非公開・定員 15)、サーバーにランクマッチの部屋として届ける。
//   ランクマッチの部屋には「ホスト」がいない扱い: 開始のボタンと設定の画面は出さず、設定はサーバーが決めた物
//   (config_mrp.json の Ranked.SettingCode) を自動で当て、人が揃うか待つ時間が過ぎたら足りない分をボットで埋めて自動で始める。
//   試合が終わったら見た勝った人をサーバーへ知らせる (ランク・コインはサーバーが半分以上の知らせの一致で決める)。
//   試合中に抜けるとサーバーがランクを下げる。
//   ランクマッチのロビーと終了画面では、右上に自分のランクのバーを出す。ロビーでは皆の頭の上にランクを出す
internal static class Ranked
{
    private enum Phase { Idle, Queue, Joining, Creating }
    private static Phase _phase;
    private static float _phaseAt;
    private static string _error = "";

    // 部屋 (ホスト)
    private static MrpAccount.RankedSettings _settings;
    private static int _appliedFor = -1;     // 設定を当てた部屋 + ロビーの回
    private static int _lobbyGen;
    private static float _lobbySince = -1f, _fullAt = -1f, _botAt, _startAt = -1f;
    private static bool _registering;
    private static int _ratingBefore = -1;
    internal static int LastDelta;
    private static bool _reported;

    public static bool InRankedRoom => MrpAccount.RoomRanked && AmongUsClient.Instance && AmongUsClient.Instance.AmConnected;

    // ── 探す ─────────────────────────────────

    public static void Start()
    {
        if (_phase != Phase.Idle) return;
        if (!MrpRegion.Select())
        {
            ShowStatus(new Text("Ranked.NoServer").ToString());
            return;
        }
        _phase = Phase.Queue;
        _phaseAt = Time.realtimeSinceStartup;
        _error = "";
        ShowStatus(new Text("Ranked.Searching").ToString());
        MrpAccount.RankedConfig((s, e) => { if (s != null) _settings = s; });
        MrpAccount.Queue((code, err) =>
        {
            if (_phase != Phase.Queue) return;
            if (err != null)
            {
                _phase = Phase.Idle;
                ShowStatus(new Text("Ranked.Error").With("error", err).ToString());
                return;
            }
            if (!string.IsNullOrEmpty(code))
            {
                _phase = Phase.Joining;
                _phaseAt = Time.realtimeSinceStartup;
                ShowStatus(new Text("Ranked.Joining").With("code", code).ToString());
                JoinLink.Request(code);
                return;
            }
            Create();
        });
    }

    private static void Create()
    {
        var c = AmongUsClient.Instance;
        var gm = GameOptionsManager.Instance;
        if (!c || gm == null) { _phase = Phase.Idle; return; }
        _phase = Phase.Creating;
        _phaseAt = Time.realtimeSinceStartup;
        ShowStatus(new Text("Ranked.Creating").ToString());
        try
        {
            int max = Math.Clamp(_settings?.MaxPlayers ?? 15, 4, 15);
            gm.GameHostOptions.SetInt(Int32OptionNames.MaxPlayers, max);
            gm.CurrentGameOptions.SetInt(Int32OptionNames.MaxPlayers, max);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"ranked: max players {e.Message}"); }
        _registering = true;
        c.NetworkMode = NetworkModes.OnlineGame;
        c.StartCoroutine(c.CoCreateOnlineGame());
    }

    private static void ShowStatus(string text)
    {
        var w = UiWindow.Open("Ranked", new Vector2(6.4f, 2.4f), Cancel);
        if (w == null) return;
        var col = new Stack(false) { Gap = 0.15f, Width = 6f };
        col.Add(new Label(new Text("Ranked.Title"), 2.4f) { MenuFont = true, Bold = true, Color = new Color(1f, 0.85f, 0.35f) });
        col.Add(new Label(text, 1.6f) { Width = 5.8f });
        w.Body.Set(col);
    }

    private static void Cancel()
    {
        if (_phase is Phase.Queue) _phase = Phase.Idle;
    }

    // ── 毎フレーム ───────────────────────────────

    internal static void Tick()
    {
        var c = AmongUsClient.Instance;
        if (!c) return;
        float now = Time.realtimeSinceStartup;

        // 入った・作った
        if (_phase is Phase.Joining or Phase.Creating)
        {
            if (LobbyBehaviour.Instance && PlayerControl.LocalPlayer)
            {
                if (UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_Ranked") UiWindow.Current.Close();
                if (_phase == Phase.Creating && _registering && c.AmHost)
                {
                    _registering = false;
                    MrpAccount.Register(GameCode.IntToGameName(c.GameId), err =>
                    {
                        if (err != null) Plugin.Logger.LogWarning($"ranked: register {err}");
                    });
                }
                _phase = Phase.Idle;
            }
            else if (now - _phaseAt > 40f)
            {
                _phase = Phase.Idle;
                _registering = false;
                ShowStatus(new Text("Ranked.Error").With("error", "timeout").ToString());
            }
        }

        if (!InRankedRoom)
        {
            _lobbySince = -1f;
            RankBar.Hide();
            return;
        }
        bool lobby = LobbyBehaviour.Instance && !ShipStatus.Instance;
        if (lobby)
        {
            if (_lobbySince < 0f)
            {
                _lobbySince = now;
                _fullAt = _startAt = -1f;
                _lobbyGen++;
                _reported = false;
            }
            HideHostUi();
            if (c.AmHost) HostLobby(now);
            RankBar.Show(false);
            LobbyBanner.Show(Banner(now));
        }
        else
        {
            _lobbySince = -1f;
            LobbyBanner.Hide();
            RankBar.Hide();
        }
    }

    private static int Humans()
    {
        int n = 0;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.Data != null && !p.Data.Disconnected && !Dev.BotPlay.IsOurs(p.PlayerId)) n++;
        return n;
    }

    private static int MaxPlayers => Math.Clamp(_settings?.MaxPlayers ?? 15, 4, 15);
    private static float FillSeconds => Math.Max(10, _settings?.FillSeconds ?? 90);

    private static string Banner(float now)
    {
        int players = GameData.Instance ? GameData.Instance.PlayerCount : 0;
        float left = Math.Max(0f, FillSeconds - (now - _lobbySince));
        string head = $"<b><color=#FFD54F>{new Text("Ranked.Title")}</color></b>  {new Text("Ranked.Players").With("n", players).With("max", MaxPlayers)}";
        string sub = _startAt > 0f ? new Text("Ranked.Starting").ToString() : new Text("Ranked.Countdown").With("s", Mathf.CeilToInt(left)).ToString();
        return head + "\n<size=80%>" + sub + "</size>";
    }

    // ホスト (の端末) が裏で回す: 設定を当て、揃うか時間が来たらボットで埋めて始める
    private static void HostLobby(float now)
    {
        if (_settings == null)
        {
            if (now - _botAt > 5f)
            {
                _botAt = now;
                MrpAccount.RankedConfig((s, e) => { if (s != null) _settings = s; });
            }
            return;
        }
        int key = AmongUsClient.Instance.GameId * 31 + _lobbyGen;
        if (_appliedFor != key && now - _lobbySince > 1f)
        {
            _appliedFor = key;
            if (!string.IsNullOrEmpty(_settings.SettingCode) && !SettingCode.Apply(_settings.SettingCode))
                Plugin.Logger.LogWarning("ranked: setting code could not be read");
            try
            {
                var gm = GameOptionsManager.Instance;
                gm.CurrentGameOptions.SetInt(Int32OptionNames.MaxPlayers, MaxPlayers);
            }
            catch (Exception) { }
            OptionSync.SendAll();
        }
        if (_startAt > 0f)
        {
            if (now >= _startAt)
            {
                _startAt = -1f;
                Begin();
            }
            return;
        }
        int players = GameData.Instance ? GameData.Instance.PlayerCount : 0;
        bool timeUp = now - _lobbySince >= FillSeconds;
        if (players >= MaxPlayers)
        {
            if (_fullAt < 0f) _fullAt = now;
            if (now - _fullAt > 4f) _startAt = now + 1f;
            return;
        }
        _fullAt = -1f;
        if (timeUp && now - _botAt > 0.25f)
        {
            _botAt = now;
            string err = Dev.BotPlay.Add(null);
            if (err != null)
            {
                Plugin.Logger.LogWarning($"ranked: bot {err}");
                _startAt = now + 1f;
            }
        }
    }

    private static void Begin()
    {
        var gsm = Vanilla.StartManager;
        if (!gsm || !AmongUsClient.Instance.AmHost) return;
        Plugin.Logger.LogInfo($"ranked: auto start players={GameData.Instance.PlayerCount} humans={Humans()}");
        gsm.MinPlayers = 1;
        gsm.BeginGame();
    }

    // 「ホストがいない」: 開始のボタンと設定の画面を出さない
    private static void HideHostUi()
    {
        var gsm = Vanilla.StartManager;
        if (gsm && gsm.StartButton && gsm.StartButton.gameObject.activeSelf) gsm.StartButton.gameObject.SetActive(false);
        if (gsm && gsm.HostViewButton && gsm.HostViewButton.gameObject.activeSelf) gsm.HostViewButton.gameObject.SetActive(false);
        if (GameSettingMenu.Instance) GameSettingMenu.Instance.Close();
    }

    // ── 試合の終わり ─────────────────────────────

    // 試合が始まった時の自分のランク (終わった後の増減を出す)
    internal static void OnGameStart()
    {
        if (!InRankedRoom) return;
        _ratingBefore = MrpAccount.Ready ? MrpAccount.Rating : -1;
        LastDelta = 0;
    }

    internal static void OnGameEnd(EndGameResult result)
    {
        if (!InRankedRoom || _reported || !GameData.Instance) return;
        _reported = true;
        var winners = new List<int>();
        var last = Roles.GameEnd.Last;
        if (last != null && !last.Draw) foreach (var id in last.Winners) winners.Add(id);
        else if (last == null && result != null)
        {
            string r = result.GameOverReason.ToString();
            bool imp = r.StartsWith("Impostor", StringComparison.Ordinal);
            var all = GameData.Instance.AllPlayers;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Role == null) continue;
                if (p.Role.IsImpostor == imp) winners.Add(p.PlayerId);
            }
        }
        Plugin.Logger.LogInfo($"ranked: report winners=[{string.Join(",", winners)}]");
        MrpAccount.Report(winners);
    }

    internal static int Delta => _ratingBefore >= 0 && MrpAccount.Ready ? MrpAccount.Rating - _ratingBefore : 0;

    // ── オンラインの画面のボタン ──────────────────────

    private const string ButtonName = "MrpRankedButton";

    // 字体の写し元 (字体の入った字)
    internal static TextMeshPro FontFrom(Transform root)
    {
        if (root)
            foreach (var t in root.GetComponentsInChildren<TextMeshPro>(true))
                if (t && t.font && t.fontSharedMaterial) return t;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshPro>())
            if (t && t.font && t.fontSharedMaterial) return t;
        return null;
    }

    internal static void EnsureButton()
    {
        var menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
        if (!menu) return;
        var scaler = menu.transform.Find("MainUI/AspectScaler/RightPanel/MaskedBlackScreen/OnlineButtons/AspectSize");
        // 隠れている間に字を作ると字体が読めない (字の部品が起きていない) ので、画面が出てから作る
        if (!scaler || !scaler.gameObject.activeInHierarchy || scaler.Find(ButtonName)) return;
        var go = new GameObject(ButtonName) { layer = scaler.gameObject.layer };
        go.transform.SetParent(scaler, false);
        go.transform.localPosition = new Vector3(0.06f, -1.04f, -2f);
        var size = new Vector2(4.9f, 0.42f);
        // 本編のボタンと同じ板 (MenuSkin の絵) にして、指が乗ると少し明るくする
        var bg = new GameObject("Back") { layer = go.layer }.AddComponent<SpriteRenderer>();
        bg.transform.SetParent(go.transform, false);
        bg.sprite = MenuSkin.Button(false);
        bg.drawMode = SpriteDrawMode.Sliced;
        bg.size = size;
        var col = go.AddComponent<BoxCollider2D>();
        col.size = size;
        var text = new GameObject("Text") { layer = go.layer };
        text.transform.SetParent(go.transform, false);
        text.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        var tmp = text.AddComponent<TextMeshPro>();
        var font = FontFrom(scaler.parent);
        if (font) { tmp.font = font.font; tmp.fontSharedMaterial = font.fontSharedMaterial; }
        tmp.fontSize = 2.0f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = size;
        tmp.text = new Text("Ranked.Button").ToString();
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener((UnityAction)Start);
        pb.OnMouseOver.AddListener((UnityAction)(() => { if (bg) { bg.sprite = MenuSkin.Button(true); bg.size = size; } }));
        pb.OnMouseOut.AddListener((UnityAction)(() => { if (bg) { bg.sprite = MenuSkin.Button(false); bg.size = size; } }));
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        private static float _buttonAt;
        public static void Postfix()
        {
            try
            {
                Tick();
                RankTags.Tick();
                if (Time.unscaledTime > _buttonAt && MenuSkin.InMenu())
                {
                    _buttonAt = Time.unscaledTime + 0.5f;
                    EnsureButton();
                }
            }
            catch (Exception e) { Plugin.Logger.LogError($"ranked: {e}"); }
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), "OnGameEnd")]
    private static class GameEndPatch
    {
        public static void Postfix([HarmonyArgument(0)] EndGameResult endGameResult)
        {
            try { OnGameEnd(endGameResult); }
            catch (Exception e) { Plugin.Logger.LogError($"ranked end: {e}"); }
        }
    }

    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    private static class IntroPatch
    {
        public static void Prefix() => OnGameStart();
    }

    [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
    private static class EndScreenPatch
    {
        public static void Postfix(EndGameManager __instance)
        {
            try
            {
                if (MrpAccount.RoomRanked) RankBar.ShowEnd(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError($"ranked end screen: {e}"); }
        }
    }

    internal static string Describe() => $"phase={_phase} ranked={MrpAccount.RoomRanked} settings={(_settings != null)} lobbySince={_lobbySince:0.0} start={_startAt:0.0} humans={(GameData.Instance ? Humans() : 0)}";
}

// 右上の自分のランクのバー (段の名前・数・次の段までの進み)。ランクマッチのロビーと終了画面
internal static class RankBar
{
    private static GameObject _root;
    private static TextMeshPro _text;
    private static SpriteRenderer _fill;
    private const float W = 3.2f, H = 0.9f, BarW = 2.9f, BarH = 0.14f;
    private static int _seen = -1;
    private static bool _end;

    internal static void Hide()
    {
        if (_root && _root.activeSelf && !_end) _root.SetActive(false);
    }

    internal static void Show(bool end)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (!_root || _end) Build(hud.transform, hud.gameObject.layer, false);
        if (!_root) return;
        if (!_root.activeSelf) _root.SetActive(true);
        var cam = hud.UICamera;
        if (cam)
        {
            var p = cam.ViewportToWorldPoint(new Vector3(0.84f, 0.77f, 0f));
            _root.transform.position = new Vector3(p.x, p.y, hud.transform.position.z - 40f);
        }
        Refresh();
    }

    internal static void ShowEnd(EndGameManager egm)
    {
        Build(egm.transform, egm.gameObject.layer, true);
        var cam = Camera.main;
        if (cam && _root)
        {
            var p = cam.ViewportToWorldPoint(new Vector3(0.84f, 0.9f, 0f));
            _root.transform.position = new Vector3(p.x, p.y, egm.transform.position.z - 20f);
        }
        Refresh();
    }

    // 終了画面では、サーバーが結果を決めて数が変わったら描き直す
    internal static void Watch()
    {
        if (_root && _root.activeInHierarchy && _seen != MrpAccount.Version) Refresh();
    }

    private static void Build(Transform parent, int layer, bool end)
    {
        if (_root) UnityEngine.Object.Destroy(_root);
        _end = end;
        _root = new GameObject("MrpRankBar") { layer = layer };
        _root.transform.SetParent(parent, false);
        MenuArt.Panel(_root.transform, "Back", 0.14f, new Vector2(W, H), new Vector3(0f, 0f, 0.1f), new Color(0.08f, 0.08f, 0.2f, 0.88f), null).sortingOrder = 60;
        MenuArt.Panel(_root.transform, "BarBack", 0.06f, new Vector2(BarW, BarH), new Vector3(0f, -0.25f, 0f), new Color(0.25f, 0.25f, 0.35f, 1f), null).sortingOrder = 61;
        _fill = MenuArt.Panel(_root.transform, "Fill", 0.06f, new Vector2(BarW, BarH), new Vector3(0f, -0.25f, -0.01f), Color.white, null);
        _fill.sortingOrder = 62;
        var go = new GameObject("Text") { layer = layer };
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = new Vector3(0f, 0.12f, -0.02f);
        _text = go.AddComponent<TextMeshPro>();
        var font = Ranked.FontFrom(parent);
        if (font) { _text.font = font.font; _text.fontSharedMaterial = font.fontSharedMaterial; }
        _text.fontSize = 1.8f;
        _text.alignment = TextAlignmentOptions.Center;
        _text.enableWordWrapping = false;
        _text.rectTransform.sizeDelta = new Vector2(W, 0.5f);
        _text.sortingOrder = 63;
    }

    private static void Refresh()
    {
        if (!_text || !_fill) return;
        _seen = MrpAccount.Version;
        if (!MrpAccount.Ready)
        {
            _text.text = new Text("Shop.Loading").ToString();
            _fill.size = new Vector2(0.01f, BarH);
            return;
        }
        int tier = MrpAccount.Tier;
        string delta = "";
        int d = Ranked.Delta;
        if (_end && d != 0) delta = d > 0 ? $"  <color=#7CFF7C>+{d}</color>" : $"  <color=#FF7C7C>{d}</color>";
        _text.text = $"<b><color=#{MrpAccount.TierColorHex(tier)}>{MrpAccount.TierName(tier)}</color></b>  {MrpAccount.Rating}{delta}\n<size=70%>{new Text("Rank.Record").With("w", MrpAccount.Wins).With("l", MrpAccount.Losses)}</size>";
        float k = MrpAccount.Progress(MrpAccount.Rating);
        float w = Mathf.Max(0.02f, BarW * k);
        _fill.size = new Vector2(w, BarH);
        _fill.transform.localPosition = new Vector3(-BarW * 0.5f + w * 0.5f, -0.25f, -0.01f);
        _fill.color = MrpAccount.TierColors[Mathf.Clamp(tier, 0, 5)];
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try { Watch(); }
            catch (Exception) { }
        }
    }
}

// ランクマッチのロビーの上の真ん中の帯 (人数・開始までの秒)
internal static class LobbyBanner
{
    private static GameObject _root;
    private static TextMeshPro _text;

    internal static void Hide()
    {
        if (_root && _root.activeSelf) _root.SetActive(false);
    }

    internal static void Show(string body)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (!_root)
        {
            _root = new GameObject("MrpRankedBanner") { layer = hud.gameObject.layer };
            _root.transform.SetParent(hud.transform, false);
            MenuArt.Panel(_root.transform, "Back", 0.14f, new Vector2(5.2f, 0.8f), new Vector3(0f, 0f, 0.1f), new Color(0.22f, 0.12f, 0.38f, 0.88f), null).sortingOrder = 60;
            var go = new GameObject("Text") { layer = hud.gameObject.layer };
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            _text = go.AddComponent<TextMeshPro>();
            var font = hud.UseButton ? hud.UseButton.buttonLabelText : null;
            if (font) { _text.font = font.font; _text.fontSharedMaterial = font.fontSharedMaterial; }
            _text.fontSize = 2f;
            _text.alignment = TextAlignmentOptions.Center;
            _text.enableWordWrapping = false;
            _text.rectTransform.sizeDelta = new Vector2(5.2f, 0.8f);
            _text.sortingOrder = 61;
        }
        if (!_root.activeSelf) _root.SetActive(true);
        var cam = hud.UICamera;
        if (cam)
        {
            var p = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.86f, 0f));
            _root.transform.position = new Vector3(p.x, p.y, hud.transform.position.z - 40f);
        }
        if (_text.text != body) _text.text = body;
    }
}

// ロビーで皆の頭の上 (名前の上) にランクを出す (MRP のサーバーで結ばれた人だけ)
internal static class RankTags
{
    private const string TagName = "MrpRankTag";
    private static float _at;
    private static readonly List<GameObject> Shown = new();

    internal static void Tick()
    {
        if (Time.unscaledTime < _at) return;
        _at = Time.unscaledTime + 0.3f;
        foreach (var go in Shown) if (go) go.SetActive(false);
        Shown.Clear();
        if (!LobbyBehaviour.Instance || ShipStatus.Instance) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || !p.cosmetics || !p.cosmetics.nameText) continue;
            var r = MrpAccount.RankOf(p);
            if (r == null) continue;
            var tag = Tag(p);
            if (!tag) continue;
            var tmp = tag.GetComponent<TextMeshPro>();
            string text = $"<color=#{MrpAccount.TierColorHex(r.Tier)}>{MrpAccount.TierName(r.Tier)}</color> <size=80%>{r.Rating}</size>";
            if (tmp.text != text) tmp.text = text;
            tag.SetActive(true);
            Shown.Add(tag);
        }
    }

    private static GameObject Tag(PlayerControl p)
    {
        var name = p.cosmetics.nameText;
        var parent = name.transform.parent ? name.transform.parent : name.transform;
        var t = parent.Find(TagName);
        if (t) return t.gameObject;
        var go = new GameObject(TagName) { layer = name.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = name.transform.localPosition + new Vector3(0f, 0.27f, -0.01f);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.font = name.font;
        tmp.fontSharedMaterial = name.fontSharedMaterial;
        tmp.fontSize = name.fontSize * 0.75f;
        tmp.enableAutoSizing = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(2f, 0.3f);
        var mr = go.GetComponent<MeshRenderer>();
        var nr = name.GetComponent<MeshRenderer>();
        if (mr && nr) { mr.sortingLayerID = nr.sortingLayerID; mr.sortingOrder = nr.sortingOrder + 1; }
        go.SetActive(false);
        return go;
    }
}
