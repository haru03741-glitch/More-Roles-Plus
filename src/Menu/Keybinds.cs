// Registering mod actions into Rewired so they appear in the vanilla controls screen based on
// https://github.com/All-Of-Us-Mods/MiraAPI (KeybindsPatch / Extensions.RegisterModBind, LGPL-2.1)
using System;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// MRP の操作のキー。本編の設定の「操作」(Rewired の割り当て直しの画面) に並べ、そこで割り当て直せる。
// 本編はこの画面の行を Rewired の操作の一覧から作るので、入力の仕組みが起きる時 (InputManager_Base.Awake) に一覧へ足す。
// 毎フレームは Rewired を呼ばない (戻り値の箱詰めでゴミが出る): 割り当てられたキーを 2 秒ごとに控え、Input.GetKeyDown で読む。
// Rewired のキーボードのキーの番号は Unity の KeyCode と同じ
internal static class Keybinds
{
    internal enum Slot
    {
        Ability1, Ability2, Ability3, Emote, Dictionary, Dodge, PhotoMode, VoiceMute,
        // 右上の段・ロビーの板のボタン (押すとそのボタンを押したのと同じ)
        MatchInfo, BugReport, Chat, Settings, Friends, CopyCode, ShareLink, EditSettings, VoicePanel,
    }

    private static readonly (string Id, string Text, KeyboardKeyCode Key)[] Defs =
    {
        ("MRP.Ability1", "Keybinds.Ability1", KeyboardKeyCode.Z),
        ("MRP.Ability2", "Keybinds.Ability2", KeyboardKeyCode.X),
        ("MRP.Ability3", "Keybinds.Ability3", KeyboardKeyCode.C),
        ("MRP.Emote", "Keybinds.Emote", KeyboardKeyCode.B),
        ("MRP.Dictionary", "Keybinds.Dictionary", KeyboardKeyCode.H),
        ("MRP.Dodge", "Keybinds.Dodge", KeyboardKeyCode.LeftShift),
        ("MRP.PhotoMode", "Keybinds.PhotoMode", KeyboardKeyCode.F3),
        ("MRP.VoiceMute", "Keybinds.VoiceMute", KeyboardKeyCode.M),
        ("MRP.MatchInfo", "Keybinds.MatchInfo", KeyboardKeyCode.I),
        ("MRP.BugReport", "Keybinds.BugReport", KeyboardKeyCode.F4),
        ("MRP.Chat", "Keybinds.Chat", KeyboardKeyCode.T),
        ("MRP.Settings", "Keybinds.Settings", KeyboardKeyCode.Escape),
        ("MRP.Friends", "Keybinds.Friends", KeyboardKeyCode.L),
        ("MRP.CopyCode", "Keybinds.CopyCode", KeyboardKeyCode.J),
        ("MRP.ShareLink", "Keybinds.ShareLink", KeyboardKeyCode.K),
        ("MRP.EditSettings", "Keybinds.EditSettings", KeyboardKeyCode.O),
        ("MRP.VoicePanel", "Keybinds.VoicePanel", KeyboardKeyCode.N),
    };

    private const int RefreshFrames = 120;

    // コントローラー (XInput の型・Xbox のパッドなど) の既定: 能力の 1〜3 を B・左スティック押し込み・右スティック押し込みに。
    // どれも本編の試合中の操作に使われていないボタン (他の型のコントローラーは本編の設定の「操作」で割り当てる)
    private const string XInputGuid = "19002688-7406-4f4a-8340-8d25335406c8";
    private const int GameplayMapCategory = 1;
    private static readonly int[] PadDefaults = { 7, 14, 15 };

    private static readonly int[] ActionIds = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(-1, Defs.Length));
    private static readonly KeyCode[] Keys = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Defs, d => (KeyCode)(int)d.Key));
    private static readonly string[] Names = new string[Defs.Length]; // 画面に出している名前 (言語が替わった時だけ書き直す)
    private static InputManager_Base _manager;
    private static int _frame;

    // 入力の仕組みが起きる前 (一覧に足した操作は、保存された割り当て直しと一緒に読み込まれる)
    internal static void Register(InputManager_Base manager)
    {
        var data = manager ? manager.userData : null;
        if (data == null) return;
        _manager = manager;
        for (int i = 0; i < Defs.Length; i++)
        {
            var (id, _, key) = Defs[i];
            int existing = data.GetActionId(id);
            if (existing >= 0)
            {
                ActionIds[i] = existing;
                continue;
            }
            data.AddAction(0);
            var action = data.GetAction(data.actions.Count - 1);
            action.name = id;
            action.descriptiveName = id;
            action.categoryId = 0;
            action.type = InputActionType.Button;
            action.userAssignable = true;
            var map = new ActionElementMap
            {
                _elementIdentifierId = -1,
                _actionId = action.id,
                _elementType = ControllerElementType.Button,
                _axisContribution = Pole.Positive,
                _keyboardKeyCode = key,
            };
            data.keyboardMaps[0].actionElementMaps.Add(map);
            ActionIds[i] = action.id;
            if (i < PadDefaults.Length) AddPadDefault(data, action.id, PadDefaults[i]);
        }
        Plugin.Logger.LogInfo($"keybinds registered: {string.Join(",", ActionIds)}");
    }

    private static void AddPadDefault(Rewired.Data.UserData data, int actionId, int element)
    {
        foreach (var m in data.joystickMaps)
        {
            if (m == null || m.categoryId != GameplayMapCategory || m.hardwareGuidString != XInputGuid) continue;
            m.actionElementMaps.Add(new ActionElementMap
            {
                _actionId = actionId,
                _elementType = ControllerElementType.Button,
                _elementIdentifierId = element,
                _axisContribution = Pole.Positive,
            });
        }
    }

    private static bool _pad; // コントローラーがつながっている (2 秒ごとに見る)

    // 押された瞬間か (割り当てが無ければ false)。コントローラーがつながっている時はそのボタンも見る
    internal static bool Down(Slot slot)
    {
        var k = Keys[(int)slot];
        if (k != KeyCode.None && Input.GetKeyDown(k)) return true;
        int id = ActionIds[(int)slot];
        return _pad && id >= 0 && ReInput.isReady && ReInput.players.GetPlayer(0).GetButtonDown(id);
    }

    internal static int ActionId(Slot slot) => ActionIds[(int)slot];

    internal static KeyCode Key(Slot slot) => Keys[(int)slot];

    // 毎フレーム。割り当ての控えを 2 秒ごとに読み直し、能力のキーでボタンを押す。
    // ここで投げると同じ Update の後ろの処理まで止まるので囲む
    internal static void Tick()
    {
        try
        {
            Step();
            NoteBusy();
        }
        catch (Exception e) { Plugin.Logger.LogError($"keybinds: {e}"); }
    }

    private static void Step()
    {
        if (++_frame >= RefreshFrames)
        {
            // 読み直しに失敗したら次は 30 秒後に (同じ失敗のログを 2 秒ごとに出さない)
            _frame = 0;
            try { Refresh(); }
            catch (Exception e) { Plugin.Logger.LogError($"keybinds: {e}"); _frame = -RefreshFrames * 14; }
        }
        // ここから先は押された瞬間の読みだけなので、何も押されていないフレームは 1 回の問い合わせで帰す
        if (!Input.anyKeyDown && !_pad) return;
        if (Down(Slot.Dictionary))
        {
            ToggleDictionary();
            return;
        }
        if (Down(Slot.PhotoMode))
        {
            if (!Typing()) PhotoMode.Toggle();
            return;
        }
        if (Down(Slot.VoiceMute))
        {
            if (!Typing() && Voice.VoiceChat.Allowed) Voice.VoiceChat.ToggleMute();
            return;
        }
        for (var s = Slot.MatchInfo; s <= Slot.VoicePanel; s++)
        {
            if (!Down(s)) continue;
            if (s == Slot.Settings) PressSettings();
            else if (!Typing() && !UI.TextField.Typing) PressButton(s);
            return;
        }
        int n = Math.Min(3, Roles.Abilities.Local.Count); // 能力の数だけ読む
        if (n == 0 || !Roles.ModButton.HudShown) return;
        for (int i = 0; i < n; i++)
        {
            if (!Down((Slot)i)) continue;
            // 上に画面が出ている時 (チャットに打っている・作業・会議など) は押さない。周りの画面を見るのは押された時だけ
            if (!Emotes.Covered()) Roles.Abilities.PressSlot(i, Keys[i]);
            return;
        }
    }

    // 右上の段・ロビーの板のボタンを押す (出ていない時・上に画面が出ている時は何もしない)
    private static void PressButton(Slot s)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (s == Slot.VoicePanel)
        {
            Voice.VoicePanel.Toggle();
            return;
        }
        if (Blocked(hud)) return;
        var pane = Vanilla.LobbyInfo;
        var target = s switch
        {
            Slot.MatchInfo => hud.MatchInfoButton ? hud.MatchInfoButton.transform : null,
            Slot.BugReport => hud.MatchInfoButton ? hud.MatchInfoButton.transform.Find("MrpReportButton") : null,
            Slot.Chat => hud.Chat && hud.Chat.chatButton ? hud.Chat.chatButton.transform : null,
            Slot.Friends => hud.transform.Find("Friends List Button"),
            Slot.CopyCode => pane ? FindDeep(pane.transform, "CopyGameCodeButton") : null,
            Slot.ShareLink => pane ? FindDeep(pane.transform, "MrpShareLinkButton") : null,
            Slot.EditSettings => pane ? FindDeep(FindDeep(pane.transform, "Host Buttons"), "Edit") : null,
            _ => null,
        };
        Click(target);
    }

    // 上に画面が出ている (この間は右上のボタンを押さない)
    private static bool Blocked(HudManager hud) =>
        IntroCutscene.Instance || ExileController.Instance || Minigame.Instance || GameSettingMenu.Instance || PlayerCustomizationMenu.Instance
        || hud.GameMenu && hud.GameMenu.IsOpen || UI.UiWindow.IsOpen || MapBehaviour.Instance && MapBehaviour.Instance.IsOpen
        || Vanilla.LobbyInfo is { } info && info.LobbyViewSettingsPane && info.LobbyViewSettingsPane.isActiveAndEnabled;

    internal static bool Click(Transform t)
    {
        if (!t || !t.gameObject.activeInHierarchy) return false;
        var pb = t.GetComponent<PassiveButton>();
        if (!pb) pb = t.GetComponentInChildren<PassiveButton>();
        if (!pb || !pb.enabled) return false;
        pb.OnClick.Invoke();
        return true;
    }

    internal static Transform FindDeep(Transform t, string name)
    {
        if (!t) return null;
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var hit = FindDeep(t.GetChild(i), name);
            if (hit) return hit;
        }
        return null;
    }

    // 設定 (Esc): 何も開いていない時に右上の歯車を押す。開いている時・同じ Esc で他の画面が閉じた時は本編に任せる
    private static int _busyFrame = -10;

    private static void PressSettings()
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.GameMenu || UI.UiWindow.EscapeTaken()) return;
        if (hud.GameMenu.IsOpen)
        {
            hud.GameMenu.Close();
            return;
        }
        if (Time.frameCount - _busyFrame <= 1 || Typing() || UI.TextField.Typing) return;
        if (Blocked(hud) || Emotes.Covered()) return;
        Click(hud.SettingsButton ? hud.SettingsButton.transform : null);
    }

    // 毎フレームの終わり: 何か画面が出ていたフレームを控える (次のフレームの Esc はその画面を閉じるための物)
    private static void NoteBusy()
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (Blocked(hud) || Emotes.Covered()) _busyFrame = Time.frameCount;
    }

    // 役職辞典を開く / 閉じる。字を打っている間 (チャット・検索欄)・演出や作業の画面の間は何もしない
    private static void ToggleDictionary()
    {
        // 開けるのは右上の辞典のボタンと同じく HUD のある場面 (ロビー・試合中) だけ
        if (!Vanilla.Hud) return;
        if (IntroCutscene.Instance || ExileController.Instance || Minigame.Instance) return;
        if (Typing() || UI.TextField.Typing) return;
        if (RoleDictionary.IsOpen) UI.UiWindow.Current?.Close();
        else RoleDictionary.Open();
    }

    // 字を打っている間 (チャット・検索欄)。押された時だけ見る
    private static bool Typing()
    {
        var chat = Vanilla.Chat;
        if (chat && chat.IsOpenOrOpening) return true;
        foreach (var box in UnityEngine.Object.FindObjectsOfType<TextBoxTMP>())
            if (box && box.hasFocus) return true;
        return false;
    }

    private static bool _warned;

    private static void Refresh()
    {
        if (!_manager)
        {
            // 入力の仕組みがプラグインより先に起きていた (本編の割り当ての画面に出ない・キーは既定のまま効く)
            if (!_warned) { _warned = true; Plugin.Logger.LogWarning("keybinds: not registered (input manager woke before the plugin)"); }
            return;
        }
        if (!ReInput.isReady) return;
        _pad = ReInput.controllers.joystickCount > 0;
        var player = ReInput.players.GetPlayer(0);
        for (int i = 0; i < Defs.Length; i++)
        {
            if (ActionIds[i] < 0) continue;
            var map = player.controllers.maps.GetFirstElementMapWithAction(ControllerType.Keyboard, ActionIds[i], false);
            // 前に保存した割り当て (MRP の行が無い) を本編が読み込んだ時は既定のキーで
            Keys[i] = map != null ? (KeyCode)(int)map.keyboardKeyCode : (KeyCode)(int)Defs[i].Key;
            // 画面の名前は言語の文から (起きた時はまだ言語の文が読めないので後から書く)
            string name = new Text(Defs[i].Text).ToString(); // 本編の行と同じく 1 行
            if (name == Names[i]) continue;
            var action = _manager.userData.GetActionById(ActionIds[i]);
            if (action != null) action.descriptiveName = name;
            Names[i] = name;
        }
    }

    // 確かめ用: i 番目の能力のキーを押したのと同じ
    internal static void PressForTest(int i) => Roles.Abilities.PressSlot(i);

    internal static string DumpMaps()
    {
        var sb = new System.Text.StringBuilder();
        var data = _manager ? _manager.userData : null;
        if (data == null) return "no data";
        foreach (var a in data.actions) sb.Append($"\n A {a.id} {a.name} cat={a.categoryId}");
        foreach (var m in data.joystickMaps)
        {
            sb.Append($"\n J {m.id} {m.name} cat={m.categoryId} layout={m.layoutId} hw={m.hardwareGuidString}");
            foreach (var e in m.actionElementMaps) sb.Append($" [{e._actionId}:{e._elementType}:{e._elementIdentifierId}]");
        }
        foreach (var m in data.keyboardMaps)
        {
            sb.Append($"\n K {m.id} {m.name} cat={m.categoryId}");
            foreach (var e in m.actionElementMaps) sb.Append($" [{e._actionId}:{e._keyboardKeyCode}]");
        }
        sb.Append($"\n joysticks={ReInput.controllers.joystickCount}");
        return sb.ToString();
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder("keys");
        for (int i = 0; i < Defs.Length; i++) sb.Append($" {Defs[i].Id}#{ActionIds[i]}={Keys[i]}");
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
internal static class KeybindsRegisterPatch
{
    private static bool _done;

    public static void Prefix(InputManager_Base __instance)
    {
        if (_done) return;
        _done = true;
        try { Keybinds.Register(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"keybinds: {e}"); }
    }
}
