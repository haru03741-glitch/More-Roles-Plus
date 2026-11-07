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
//   「ランクマッチ」(プレイの画面・オンラインの画面) を押すと、MRP のサーバーで空きのあるランクマッチの部屋を探してそのまま入る。
//   無ければ自分で部屋を作り (非公開・定員 15)、サーバーにランクマッチの部屋として届ける。
//   入った部屋がランクマッチの部屋かはサーバーに確かめ、違えばすぐ抜けて探し直す (普通の部屋には入らない)。
//   ランクマッチの部屋には「ホスト」がいない扱い: 開始のボタンと設定の画面は出さず、設定はサーバーが決めた物
//   (config_mrp.json の Ranked.SettingCode) を自動で当て、定員 (15 人) が揃ったら自動で始める (ボットでは埋めない)。
//   揃うまでの間はロビーでミニゲーム (RankedPastime) で遊べる。
//   試合が終わったら見た勝った人をサーバーへ知らせる (ランク・コインはサーバーが半分以上の知らせの一致で決める)。
//   試合中に抜けるとサーバーがランクを下げる (抜ける前に確かめの窓を出す)。
internal static class Ranked
{
    private enum Phase { Idle, Queue, Joining, Creating, Verify, Requeue }
    private static Phase _phase;
    private static float _phaseAt;
    private static int _attempt;
    private static bool _leaving; // 自分で抜けている (確かめの窓を出さない)

    // 部屋 (ホスト)
    private static MrpAccount.RankedSettings _settings;
    private static int _appliedFor = -1;     // 設定を当てた部屋 + ロビーの回
    private static int _lobbyGen;
    private static float _lobbySince = -1f, _fullAt = -1f, _cfgAt, _startAt = -1f;
    private static bool _registering, _registerWait;
    private static int _ratingBefore = -1;
    private static bool _reported;

    public static bool InRankedRoom => MrpAccount.RoomRanked && AmongUsClient.Instance && AmongUsClient.Instance.AmConnected;
    public static bool Busy => _phase != Phase.Idle;
    internal static bool Leaving => _leaving;

    // ── 探す ─────────────────────────────────

    // 今はランクマッチを開けない (遊ぶ人がまだ少なく、待っても揃わないため)。開ける時は true にする
    internal const bool Open = false;

    public static void Start()
    {
        if (!Open) return;
        if (_phase != Phase.Idle && _phase != Phase.Requeue) return;
        if (!MrpRegion.Select())
        {
            RankedToast.Show(new Text("Ranked.NoServer").ToString(), true);
            _phase = Phase.Idle;
            return;
        }
        Set(Phase.Queue);
        SearchWindow(); // 押したらすぐ「探しています」を出す
        MrpAccount.RankedConfig((s, e) => { if (s != null) _settings = s; });
        MrpAccount.Queue((code, err) =>
        {
            if (_phase != Phase.Queue) return;
            if (err != null)
            {
                Set(Phase.Idle);
                RankedToast.Show(new Text("Ranked.Error").With("error", err).ToString(), true);
                return;
            }
            if (!string.IsNullOrEmpty(code))
            {
                Set(Phase.Joining);
                SearchWindow();
                JoinLink.Request(code);
                return;
            }
            Create();
        });
    }

    private static void Set(Phase p)
    {
        _phase = p;
        _phaseAt = Time.realtimeSinceStartup;
    }

    // 探すのをやめる (探している画面の「やめる」・× ・Esc)。入りかけの部屋からも出る
    internal static void Cancel()
    {
        if (_phase == Phase.Idle) return;
        var was = _phase;
        Set(Phase.Idle);
        _attempt = 0;
        _registering = false;
        RankedToast.Hide();
        var c = AmongUsClient.Instance;
        if ((was == Phase.Joining || was == Phase.Creating || was == Phase.Verify) && c && (c.AmConnected || c.GameState != InnerNetClient.GameStates.NotJoined))
            Leave(false);
        Plugin.Logger.LogInfo($"ranked: canceled ({was})");
    }

    // 試験用: 探している画面だけを出す (サーバーには聞かない・やめるで閉じる)
    internal static void TestSearch()
    {
        Set(Phase.Queue);
        SearchWindow();
    }

    // ── 探している間の画面 (メニュー) ─────────────────
    // メニューの上を覆い、ほかの部屋を作る・入るボタンを押せなくする。閉じる = 探すのをやめる
    private static UiWindow _searchWin;
    private static Phase _searchShown = Phase.Idle;
    private static bool _closingSearch;

    private static bool Searching => _phase is Phase.Queue or Phase.Joining or Phase.Creating or Phase.Requeue;

    private static void SearchWindow()
    {
        bool inMenu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
        bool want = Searching && inMenu;
        bool open = _searchWin != null && _searchWin.Root && UiWindow.Current == _searchWin;
        if (!want)
        {
            if (open) { _closingSearch = true; try { _searchWin.Close(); } finally { _closingSearch = false; } }
            _searchWin = null;
            _searchShown = Phase.Idle;
            return;
        }
        // 段 (探す・入る・作る) が変わったら書き直す
        var shown = _phase == Phase.Requeue ? Phase.Queue : _phase;
        if (open && _searchShown == shown) return;
        if (open) { _closingSearch = true; try { _searchWin.Close(); } finally { _closingSearch = false; } }
        _searchShown = shown;
        // 場面が変わって (部屋に入って) 窓が消えた時はやめない。人が閉じた時 (窓がまだある) だけやめる
        UiWindow w = null;
        w = UiWindow.Open("RankedSearch", new Vector2(6.4f, 3.0f), () => { if (!_closingSearch && w != null && w.Root) Cancel(); });
        _searchWin = w;
        if (_searchWin == null) return;
        RankedToast.Hide();
        string key = shown switch { Phase.Joining => "Ranked.Joining", Phase.Creating => "Ranked.Creating", _ => "Ranked.Searching" };
        var col = new Stack(false) { Gap = 0.2f, Width = 6.0f };
        col.Add(new Label(new Text("Ranked.Button"), 2.4f) { MenuFont = true, Bold = true, Color = RankStyle.Accent });
        col.Add(new Label(new Text(key), 1.7f) { Width = 5.8f });
        col.Add(new Label(new Text("Ranked.SearchHint"), 1.3f) { Width = 5.8f, Color = RankStyle.Sub });
        col.Add(new Button(new Text("Ranked.Cancel"), () => UiWindow.Current?.Close(), 1.6f) { Size = new Vector2(2.5f, 0.5f), Color = RankStyle.Warn });
        _searchWin.Body.Set(col);
    }

    private static void Create()
    {
        var c = AmongUsClient.Instance;
        var gm = GameOptionsManager.Instance;
        if (!c || gm == null) { Set(Phase.Idle); return; }
        Set(Phase.Creating);
        SearchWindow();
        try
        {
            gm.GameHostOptions.SetInt(Int32OptionNames.MaxPlayers, MaxPlayers);
            gm.CurrentGameOptions.SetInt(Int32OptionNames.MaxPlayers, MaxPlayers);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"ranked: max players {e.Message}"); }
        _registering = true;
        c.NetworkMode = NetworkModes.OnlineGame;
        c.StartCoroutine(c.CoCreateOnlineGame());
    }

    // 部屋を自分で抜ける (確かめの窓を出さない)。again = メニューへ戻ったら探し直す
    private static void Leave(bool again)
    {
        _leaving = true;
        Set(again ? Phase.Requeue : Phase.Idle);
        try { AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame); }
        catch (Exception e) { Plugin.Logger.LogWarning($"ranked: leave {e.Message}"); }
    }

    private static void Fail(string error)
    {
        Plugin.Logger.LogWarning($"ranked: {error}");
        RankedToast.Show(new Text("Ranked.Error").With("error", error).ToString(), true);
    }

    // ── 毎フレーム ───────────────────────────────

    private static bool _wasRanked;

    internal static void Tick()
    {
        // 休止中で何も進めていなければ何も見ない (待ち画面の確認でシーンを毎フレーム探さない)。
        // サーバーがランクマッチの部屋と言っている間と、その後の片付けの 1 回はいつも通り回す
        bool ranked = MrpAccount.RoomRanked;
        if (!Open && _phase == Phase.Idle && _searchWin == null && !ranked && !_wasRanked && !_registerWait && !_leaving) return;
        _wasRanked = ranked;
        var c = AmongUsClient.Instance;
        if (!c) return;
        float now = Time.realtimeSinceStartup;
        bool inLobby = LobbyBehaviour.Instance && PlayerControl.LocalPlayer;
        bool inMenu = !c.AmConnected && c.GameState == InnerNetClient.GameStates.NotJoined;
        if (inMenu) _leaving = false;
        SearchWindow();

        switch (_phase)
        {
            case Phase.Joining:
                if (inLobby)
                {
                    // 入った部屋がランクマッチの部屋かをサーバーに確かめる
                    Set(Phase.Verify);
                    RankedToast.Hide();
                }
                else if (now - _phaseAt > 12f)
                {
                    // 入れなかった (満員・始まった)。何度か探し直し、だめなら自分で作る
                    if (++_attempt < 3) { Set(Phase.Requeue); _phaseAt -= 10f; }
                    else { _attempt = 0; Create(); }
                }
                break;
            case Phase.Creating:
                if (inLobby && c.AmHost && _registering)
                {
                    _registering = false;
                    _registerWait = true;
                    try { c.ChangeGamePublic(false); } catch (Exception) { } // 部屋の一覧に出さない (普通の人が入らない)
                    MrpAccount.Register(GameCode.IntToGameName(c.GameId), err =>
                    {
                        _registerWait = false;
                        if (err == null) return;
                        // サーバーにランクマッチの部屋として届けられない部屋には居ない (普通の部屋として遊ばれないように)
                        Fail(err);
                        Leave(false);
                    });
                    Set(Phase.Idle);
                    RankedToast.Hide();
                    _attempt = 0;
                }
                else if (now - _phaseAt > 40f) { Set(Phase.Idle); Fail("timeout"); }
                break;
            case Phase.Verify:
                if (!inLobby) { Set(Phase.Idle); break; }
                if (MrpAccount.RoomRanked) { Set(Phase.Idle); _attempt = 0; break; }
                if (MrpAccount.RoomReads > 0 || now - _phaseAt > 12f)
                {
                    // ランクマッチの部屋ではなかった (終わって普通の部屋に戻った等)。抜けて探し直す
                    Plugin.Logger.LogWarning("ranked: joined room is not ranked, leaving");
                    if (++_attempt < 3) Leave(true);
                    else { _attempt = 0; Leave(false); Fail("not_ranked"); }
                }
                break;
            case Phase.Requeue:
                if (inMenu && now - _phaseAt > 1.5f && UnityEngine.Object.FindObjectOfType<MainMenuManager>()) Start();
                else if (now - _phaseAt > 30f) Set(Phase.Idle);
                break;
        }

        // 作った部屋をサーバーへ届けている間も、開始のボタンと設定の画面は出さない
        if ((_registerWait || _phase == Phase.Creating) && inLobby) HideHostUi();
        if (!InRankedRoom)
        {
            RestoreHostUi();
            _lobbySince = -1f;
            RankHud.Hide();
            LobbyBanner.Hide();
            RankedPastime.Stop();
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
            bool covered = Covered();
            RankedPastime.Paused = covered;
            if (covered)
            {
                RankHud.Hide();
                LobbyBanner.Hide();
            }
            else
            {
                RankHud.Show();
                LobbyBanner.Show(Players(), MaxPlayers, _startAt > 0f || _fullAt > 0f);
            }
        }
        else
        {
            _lobbySince = -1f;
            LobbyBanner.Hide();
            RankHud.Hide();
            RankedPastime.Stop();
        }
    }

    private static int Players() => GameData.Instance ? GameData.Instance.PlayerCount : 0;

    internal static int MaxPlayers => Math.Clamp(_settings?.MaxPlayers ?? 15, 4, 15);

    // ホスト (の端末) が裏で回す: 設定を当て、定員が揃ったら始める
    private static void HostLobby(float now)
    {
        if (_settings == null)
        {
            if (now - _cfgAt > 5f)
            {
                _cfgAt = now;
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
            try { GameOptionsManager.Instance.CurrentGameOptions.SetInt(Int32OptionNames.MaxPlayers, MaxPlayers); }
            catch (Exception) { }
            OptionSync.SendAll();
        }
        if (_startAt > 0f)
        {
            if (Players() < MaxPlayers) { _startAt = _fullAt = -1f; return; } // 始まる前に抜けた人がいた
            if (now >= _startAt) { _startAt = -1f; Begin(); }
            return;
        }
        if (Players() >= MaxPlayers)
        {
            if (_fullAt < 0f) _fullAt = now;
            if (now - _fullAt > 3f) _startAt = now + 2f;
            return;
        }
        _fullAt = -1f;
    }

    private static void Begin()
    {
        var gsm = Vanilla.StartManager;
        if (!gsm || !AmongUsClient.Instance.AmHost) return;
        Plugin.Logger.LogInfo($"ranked: auto start players={Players()}");
        RankedPastime.Stop();
        gsm.MinPlayers = 1;
        gsm.BeginGame();
    }

    // 「ホストがいない」: 開始のボタン・「ホスト: 〇〇」・設定を開くボタンを出さない
    // (設定の画面は開く前にボタンごと隠す。開いてから閉じると一瞬見えてしまう)
    private static readonly List<GameObject> Hidden = new();

    private static void HideHostUi()
    {
        var gsm = Vanilla.StartManager;
        if (gsm)
        {
            Off(gsm.StartButton ? gsm.StartButton.gameObject : null, false);
            Off(gsm.HostViewButton ? gsm.HostViewButton.gameObject : null, false);
            Off(Child(gsm.transform, "StartGameArea/Host Info"), true);
            Off(Child(gsm.transform, "StartGameArea/StartButton (Client)"), false);
        }
        var hud = Vanilla.Hud;
        if (hud)
        {
            Off(Child(hud.transform, "LobbyInfoPane/AspectSize/GameSettingsButtons"), true);
            Off(Child(hud.transform, "LobbyInfoPane/AspectSize/PrivacyLabel"), true); // 公開・非公開はランクマッチでは変えない
        }
        if (GameSettingMenu.Instance) GameSettingMenu.Instance.Close();
    }

    private static GameObject Child(Transform t, string path)
    {
        var c = t ? t.Find(path) : null;
        return c ? c.gameObject : null;
    }

    // restore = ランクマッチの部屋を出たら出し直す物
    private static void Off(GameObject go, bool restore)
    {
        if (!go || !go.activeSelf) return;
        go.SetActive(false);
        if (restore && !Hidden.Contains(go)) Hidden.Add(go);
    }

    private static void RestoreHostUi()
    {
        if (Hidden.Count == 0) return;
        foreach (var go in Hidden) if (go) go.SetActive(true);
        Hidden.Clear();
    }

    // ほかの画面 (着せ替え・チャット・Esc の画面・試合の情報・窓 など) が開いている間はランクの表示を隠す
    private static bool Covered()
    {
        var hud = Vanilla.Hud;
        if (PlayerCustomizationMenu.Instance && PlayerCustomizationMenu.Instance.gameObject.activeInHierarchy) return true;
        if (Minigame.Instance || GameSettingMenu.Instance || UiWindow.IsOpen) return true;
        if (!hud) return false;
        if (hud.Chat && hud.Chat.IsOpenOrOpening) return true;
        foreach (var path in CoverPaths)
        {
            var go = Child(hud.transform, path);
            if (go && go.activeInHierarchy) return true;
        }
        return false;
    }

    private static readonly string[] CoverPaths = { "Menu", "MatchInfoGuide/MatchInfoParent", "FriendsList", "LobbyInfoPane/AspectSize/RulesPopOutWindow" };

    // ── 試合の終わり ─────────────────────────────

    internal static void OnGameStart()
    {
        if (!InRankedRoom) return;
        _ratingBefore = MrpAccount.Ready ? MrpAccount.Rating : -1;
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

    // ── 抜ける前の確かめ ───────────────────────────

    private static bool _confirmed;

    // 本編の「ゲームをやめる」を押した。ランクマッチの部屋なら確かめの窓を出して止める
    internal static bool ConfirmLeave(ExitGameButton button)
    {
        if (_confirmed || _leaving || !InRankedRoom) return true;
        if (UnityEngine.Object.FindObjectOfType<EndGameManager>()) return true; // 終わった後は下がらない
        bool playing = ShipStatus.Instance && AmongUsClient.Instance.IsGameStarted;
        var w = UiWindow.Open("RankedLeave", new Vector2(6.6f, 3.0f));
        if (w == null) return true;
        var col = new Stack(false) { Gap = 0.18f, Width = 6.2f };
        col.Add(new Label(new Text("Ranked.LeaveTitle"), 2.3f) { MenuFont = true, Bold = true, Color = RankStyle.Accent });
        col.Add(new Label(new Text(playing ? "Ranked.LeavePlaying" : "Ranked.LeaveLobby").With("n", _settings?.LeaveRating ?? 40), 1.5f) { Width = 6f, Color = playing ? RankStyle.Warn : RankStyle.Sub });
        var stay = new Button(new Text("Ranked.LeaveStay"), () => UiWindow.Current?.Close(), 1.6f) { Size = new Vector2(2.5f, 0.5f), Color = RankStyle.Accent };
        var go = new Button(new Text("Ranked.LeaveGo"), () =>
        {
            UiWindow.Current?.Close();
            _confirmed = true;
            try { if (button) button.OnClick(); }
            finally { _confirmed = false; }
        }, 1.6f) { Size = new Vector2(2.5f, 0.5f), Color = RankStyle.Warn };
        col.Add(new Stack(true, stay, go) { Gap = 0.3f });
        w.Body.Set(col);
        return false;
    }

    // ── ボタン (プレイの画面・オンラインの画面) ──────────────

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

    private const string ButtonName = "MrpRankedButton";

    internal static void EnsureButtons()
    {
        if (!Open) return;
        var menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
        if (!menu) return;
        // オンラインの画面: カードの下
        AddButton(0, menu.transform.Find("MainUI/AspectScaler/RightPanel/MaskedBlackScreen/OnlineButtons/AspectSize"), new Vector3(0.06f, -1.04f, -2f), new Vector2(4.9f, 0.42f));
        // プレイの画面: 見出しの左の空き (オンラインの画面を通らずに入れる)
        AddButton(1, menu.transform.Find("MainUI/AspectScaler/RightPanel/MaskedBlackScreen/GameModeButtons/AspectSize"), new Vector3(-0.91f, 2.18f, -2f), new Vector2(3.6f, 0.5f));
    }

    private static void AddButton(int slot, Transform scaler, Vector3 pos, Vector2 size)
    {
        // 隠れている間に字を作ると字体が読めない (字の部品が起きていない) ので、画面が出てから作る
        if (!scaler || !scaler.gameObject.activeInHierarchy || scaler.Find(ButtonName)) return;
        var go = new GameObject(ButtonName) { layer = scaler.gameObject.layer };
        go.transform.SetParent(scaler, false);
        go.transform.localPosition = pos;
        var bg = new GameObject("Back") { layer = go.layer }.AddComponent<SpriteRenderer>();
        bg.transform.SetParent(go.transform, false);
        bg.sprite = MenuSkin.Button(false);
        bg.drawMode = SpriteDrawMode.Sliced;
        bg.size = size;
        var col = go.AddComponent<BoxCollider2D>();
        col.size = size;
        // 左に自分のランクの印
        var icon = new GameObject("Emblem") { layer = go.layer }.AddComponent<SpriteRenderer>();
        icon.transform.SetParent(go.transform, false);
        icon.transform.localPosition = new Vector3(-size.x * 0.5f + 0.3f, 0f, -0.05f);
        icon.transform.localScale = Vector3.one * 0.38f;
        icon.sprite = RankEmblem.Get(MrpAccount.Ready ? MrpAccount.Tier : 0);
        var text = new GameObject("Text") { layer = go.layer };
        text.transform.SetParent(go.transform, false);
        text.transform.localPosition = new Vector3(0.1f, 0f, -0.1f);
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
        // 押した時/乗せた時の処理は置き場ごとに 1 回だけ作って使い回す (画面ごとに管理ラムダを渡すと手放されずに残る)
        BtnBg[slot] = bg;
        BtnSize[slot] = size;
        _onClick ??= (UnityAction)(() => { _attempt = 0; Start(); });
        OnOver[slot] ??= (UnityAction)(() => Hover(slot, true));
        OnOut[slot] ??= (UnityAction)(() => Hover(slot, false));
        pb.OnClick.AddListener(_onClick);
        pb.OnMouseOver.AddListener(OnOver[slot]);
        pb.OnMouseOut.AddListener(OnOut[slot]);
    }

    private static readonly SpriteRenderer[] BtnBg = new SpriteRenderer[2];
    private static readonly Vector2[] BtnSize = new Vector2[2];
    private static readonly UnityAction[] OnOver = new UnityAction[2], OnOut = new UnityAction[2];
    private static UnityAction _onClick;

    private static void Hover(int slot, bool on)
    {
        var bg = BtnBg[slot];
        if (!bg) return;
        bg.sprite = MenuSkin.Button(on);
        bg.size = BtnSize[slot];
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
                RankedToast.Tick();
                if (Time.unscaledTime > _buttonAt && MenuSkin.InMenu())
                {
                    _buttonAt = Time.unscaledTime + 0.5f;
                    EnsureButtons();
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
                if (MrpAccount.RoomRanked) RankHud.ShowEnd(__instance);
            }
            catch (Exception e) { Plugin.Logger.LogError($"ranked end screen: {e}"); }
        }
    }

    [HarmonyPatch(typeof(ExitGameButton), nameof(ExitGameButton.OnClick))]
    private static class LeavePatch
    {
        public static bool Prefix(ExitGameButton __instance)
        {
            try { return ConfirmLeave(__instance); }
            catch (Exception e) { Plugin.Logger.LogError($"ranked leave: {e}"); return true; }
        }
    }

    internal static string Describe() => $"phase={_phase} attempt={_attempt} ranked={MrpAccount.RoomRanked} reads={MrpAccount.RoomReads} settings={(_settings != null)} lobbySince={_lobbySince:0.0} full={_fullAt:0.0} start={_startAt:0.0} players={Players()}/{MaxPlayers}";
}
