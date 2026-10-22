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
    internal enum Slot { Ability1, Ability2, Ability3, Emote }

    private static readonly (string Id, string Text, KeyboardKeyCode Key)[] Defs =
    {
        ("MRP.Ability1", "Keybinds.Ability1", KeyboardKeyCode.Z),
        ("MRP.Ability2", "Keybinds.Ability2", KeyboardKeyCode.X),
        ("MRP.Ability3", "Keybinds.Ability3", KeyboardKeyCode.C),
        ("MRP.Emote", "Keybinds.Emote", KeyboardKeyCode.B),
    };

    private const int RefreshFrames = 120;

    private static readonly int[] ActionIds = { -1, -1, -1, -1 };
    private static readonly KeyCode[] Keys = { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.B };
    private static readonly string[] Names = new string[4]; // 画面に出している名前 (言語が替わった時だけ書き直す)
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
        }
        Plugin.Logger.LogInfo($"keybinds registered: {string.Join(",", ActionIds)}");
    }

    // 押された瞬間か (割り当てが無ければ false)
    internal static bool Down(Slot slot)
    {
        var k = Keys[(int)slot];
        return k != KeyCode.None && Input.GetKeyDown(k);
    }

    internal static KeyCode Key(Slot slot) => Keys[(int)slot];

    // 毎フレーム。割り当ての控えを 2 秒ごとに読み直し、能力のキーでボタンを押す。
    // ここで投げると同じ Update の後ろの処理まで止まるので囲む
    internal static void Tick()
    {
        try { Step(); }
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
        int n = Math.Min(3, Roles.Abilities.Local.Count); // 能力の数だけ読む
        if (n == 0 || !Roles.ModButton.HudShown) return;
        for (int i = 0; i < n; i++)
        {
            if (!Down((Slot)i)) continue;
            // 上に画面が出ている時 (チャットに打っている・作業・会議など) は押さない。周りの画面を見るのは押された時だけ
            if (!Emotes.Covered()) Roles.Abilities.PressSlot(i);
            return;
        }
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
