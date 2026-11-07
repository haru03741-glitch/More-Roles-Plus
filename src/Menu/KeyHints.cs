using System;
using System.Collections.Generic;
using Rewired;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 右下の操作のボタン (本編の キル・使う・通報・ベント・能力 と MRP の能力) の右上に、押すキーを小さく出す。
// 右上の段 (設定・チャット・地図・役職辞典) は右下の角に、エモートの扇は選ぶ数字 (1〜4) を出す。
// 最後に触った物に合わせて出し分ける: コントローラーを触ればコントローラーのボタンの名前、キーボードかマウスならキーの名前。
// 割り当ては Rewired から読む (本編の設定の「操作」で割り当て直した物もそのまま出る)。読むのは 0.5 秒ごと
internal static class KeyHints
{
    private const string HintName = "MrpKeyHint";
    private const int Every = 5;         // FixedUpdate 5 回ごと (0.1 秒): ボタンが出る・隠れるに付いていく
    private const int RefreshEvery = 25; // 割り当ての読み直しは 0.5 秒ごと

    private static int _tick;
    private static readonly Dictionary<int, string> Cache = new();
    private static readonly Dictionary<IntPtr, int> ModIds = new();

    // FixedUpdate から
    internal static void Tick()
    {
        if (++_tick % Every != 0) return;
        try { Step(); }
        catch (Exception e) { Plugin.Logger.LogError($"key hints: {e.Message}"); }
    }

    private static void Step()
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (_tick % RefreshEvery == 0) Cache.Clear();
        bool pad = UsingPad();
        if (pad != _pad) { _pad = pad; Cache.Clear(); }
        // スマホはキーボードで押さないので出さない (前に出した物を消すため Put は回す)
        bool on = !OperatingSystem.IsAndroid() && (ClientOptions.KeyHints == null || ClientOptions.KeyHints.Value);
        Put(hud.KillButton, (int)RewiredConstsEnum.Action.ActionSecondary, on);
        Put(hud.UseButton, (int)RewiredConstsEnum.Action.ActionPrimary, on);
        Put(hud.PetButton, (int)RewiredConstsEnum.Action.ActionPrimary, on);
        Put(hud.ReportButton, (int)RewiredConstsEnum.Action.ActionTertiary, on);
        Put(hud.ImpostorVentButton, (int)RewiredConstsEnum.Action.UseVent, on);
        Put(hud.AbilityButton, (int)RewiredConstsEnum.Action.ActionQuaternary, on);
        Put(hud.SabotageButton, (int)RewiredConstsEnum.Action.ToggleMap, on);

        // MRP のボタン: 能力は並びの順にキーの 1〜3 番・エモートはエモートのキー・他 (エモートの輪など) は出さない。
        // どれも本編の能力のボタンを写した物なので、写した時の字は消し、コントローラーの印も MRP の操作の物にする
        ModIds.Clear();
        int slot = 0;
        foreach (var a in Roles.Abilities.Local)
        {
            if (a.Button == null) continue;
            var t = a.Button.Transform;
            if (t) ModIds[t.gameObject.Pointer] = slot < 3 ? Keybinds.ActionId((Keybinds.Slot)slot) : -1;
            slot++;
        }
        var emote = Emotes.MainButton?.Transform;
        if (emote) ModIds[emote.gameObject.Pointer] = Keybinds.ActionId(Keybinds.Slot.Emote);
        var ring = Emotes.Ring;
        foreach (var m in Roles.ModButton.Buttons)
        {
            var t = m.Transform;
            var b = t ? t.GetComponent<ActionButton>() : null;
            if (!b) continue;
            // エモートの扇: 開いている間は 1〜4 で選べる
            if (ring && t.parent == ring)
            {
                Put(b, on ? (t.GetSiblingIndex() + 1).ToString() : null);
                continue;
            }
            int id = ModIds.TryGetValue(t.gameObject.Pointer, out var v) ? v : -1;
            if (b.glyph && id >= 0) b.glyph.actionToDisplayMappedGlyphFor = (RewiredConstsEnum.Action)id;
            Put(b, id, on);
        }

        // 右上の段
        // 字のひな形は「使う」の字 (キルの字は赤い縁取り)
        var tmpl = hud.UseButton ? hud.UseButton.buttonLabelText : null;
        float scale = hud.KillButton ? hud.KillButton.transform.lossyScale.x * TopScale : 0f;
        PutTop(hud.SettingsButton ? hud.SettingsButton.transform : null, Keybinds.ActionId(Keybinds.Slot.Settings), on, tmpl, scale);
        PutTop(hud.MapButton ? hud.MapButton.transform : null, (int)RewiredConstsEnum.Action.ToggleMap, on, tmpl, scale);
        var chat = hud.Chat ? hud.Chat.chatButton : null;
        PutTop(chat ? chat.transform : null, Keybinds.ActionId(Keybinds.Slot.Chat), on, tmpl, scale);
        var info = hud.MatchInfoButton ? hud.MatchInfoButton.transform : null;
        // 「?」の子には役職辞典・バグ報告のボタンがあるので、「?」自身の絵だけを見る (ロビーでは絵が消えているので出さない)
        PutTop(info, Keybinds.ActionId(Keybinds.Slot.MatchInfo), on, tmpl, scale, ownArt: true);
        PutTop(info ? info.Find("MrpDictionaryButton") : null, Keybinds.ActionId(Keybinds.Slot.Dictionary), on, tmpl, scale);
        PutTop(info ? info.Find("MrpReportButton") : null, Keybinds.ActionId(Keybinds.Slot.BugReport), on, tmpl, scale);
        PutTop(info ? info.Find("MrpBotButton") : null, Keybinds.ActionId(Keybinds.Slot.Bots), on, tmpl, scale);
        // 友達のボタン (L キー) には字を出さない (本編の MOD の印と通知の数が重なって見づらい)。キーで開くのはそのまま

        // ロビーの板 (コピー・シェア・編集)
        var pane = Vanilla.LobbyInfo;
        if (pane)
        {
            PutTop(Keybinds.FindDeep(pane.transform, "CopyGameCodeButton"), Keybinds.ActionId(Keybinds.Slot.CopyCode), on, tmpl, scale);
            PutTop(Keybinds.FindDeep(pane.transform, "MrpShareLinkButton"), Keybinds.ActionId(Keybinds.Slot.ShareLink), on, tmpl, scale);
            PutTop(Keybinds.FindDeep(Keybinds.FindDeep(pane.transform, "Host Buttons"), "Edit"), Keybinds.ActionId(Keybinds.Slot.EditSettings), on, tmpl, scale);
        }
    }

    private const float TopScale = 1.25f; // 右上の段の字の大きさ (右下のボタンの字に対して)

    private static void Put(ActionButton b, int actionId, bool on) =>
        Put(b, b && on && actionId >= 0 && b.isActiveAndEnabled ? Text(actionId) : null);

    // ボタンが隠れている間は出さない
    private static void Put(ActionButton b, string text)
    {
        if (!b) return;
        var t = b.transform.Find(HintName);
        if (string.IsNullOrEmpty(text) || !b.isActiveAndEnabled)
        {
            if (t && t.gameObject.activeSelf) t.gameObject.SetActive(false);
            return;
        }
        if (!t) t = Make(b.transform, b.buttonLabelText ? b.buttonLabelText : b.cooldownTimerText, b.graphic, new Vector3(0.52f, 0.4f, -1f));
        Show(t, text);
    }

    // 右上の段のボタン: 絵の右下の角に、右下のボタンと同じ見た目の大きさで出す
    private static void PutTop(Transform host, int actionId, bool on, TextMeshPro tmpl, float scale, bool ownArt = false)
    {
        if (!host) return;
        var t = host.Find(HintName);
        string text = on && actionId >= 0 && host.gameObject.activeInHierarchy && tmpl && scale > 0f ? Text(actionId) : null;
        if (string.IsNullOrEmpty(text))
        {
            if (t && t.gameObject.activeSelf) t.gameObject.SetActive(false);
            return;
        }
        // 本編のボタンの絵 (Inactive / Active / Selected のうち出ている物) を先に選ぶ。チャットを開くと Inactive が消えて
        // 右へずれた台の絵 (Background) が先に見つかり、字が隣のボタンへずれるため。無い時 (ロビーの板のボタンなど) は最初の絵
        SpriteRenderer art = null, any = null;
        foreach (var sr in host.GetComponentsInChildren<SpriteRenderer>())
        {
            if (!sr.enabled || !sr.sprite || !sr.transform.parent || sr.transform.parent.name == HintName || sr.transform.name == "Back") continue;
            if (sr.transform.parent == host && sr.transform.name is "Inactive" or "Active" or "Selected") { art = sr; break; }
            if (!ownArt && !any) any = sr;
        }
        if (!art) art = any;
        if (!art)
        {
            if (t && t.gameObject.activeSelf) t.gameObject.SetActive(false);
            return;
        }
        if (!t) t = Make(host, tmpl, art, Vector3.zero);
        if (!t) return;
        var bounds = art.bounds;
        float cx = bounds.max.x - bounds.size.x * 0.08f;
        var corner = host.InverseTransformPoint(new Vector3(cx, bounds.min.y + bounds.size.y * 0.12f, bounds.center.z));
        t.localPosition = new Vector3(corner.x, corner.y, -1f);
        float hs = host.lossyScale.x;
        if (Mathf.Abs(hs) > 0.0001f) t.localScale = Vector3.one * (scale / hs);
        Show(t, text);
    }

    private static void Show(Transform t, string text)
    {
        if (!t) return;
        if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
        var tmp = t.Find("Text")?.GetComponent<TextMeshPro>();
        if (!tmp || tmp.text == text) return;
        tmp.text = text;
        // 地の板を字の幅に合わせる (右端をそろえて左へ伸ばす)
        float w = Mathf.Max(tmp.GetPreferredValues(text).x + Pad, BackH);
        var back = t.Find("Back");
        if (back)
        {
            back.localScale = new Vector3(w / BackAspect, BackH, 1f);
            back.localPosition = new Vector3(-w * 0.5f, 0f, 0.1f);
        }
        tmp.transform.localPosition = new Vector3(-w * 0.5f, 0f, 0f);
    }

    private const float BackH = 0.4f, Pad = 0.18f, BackAspect = 2f;
    private static Sprite _back;

    // 暗い板の上の白い字 (右下のボタンは右上の角に置く・左上は能力の残り回数の印がある)
    private static Transform Make(Transform host, TextMeshPro src, SpriteRenderer bsr, Vector3 pos)
    {
        if (!src) return null;
        var root = new GameObject(HintName);
        root.layer = host.gameObject.layer;
        root.transform.SetParent(host, false);
        root.transform.localPosition = pos;

        var back = new GameObject("Back") { layer = root.layer };
        back.transform.SetParent(root.transform, false);
        var sr = back.AddComponent<SpriteRenderer>();
        sr.sprite = _back ??= Fx.FxSprites.Rect(128, 64, (x, y) =>
        {
            // 角の丸い板 (縦の半分の丸み)
            float dx = Mathf.Max(Mathf.Abs(x * 2f) - 0.5f, 0f), dy = y;
            return Mathf.Clamp01((0.5f - Mathf.Sqrt(dx * dx + dy * dy)) * 40f);
        }, "MrpKeyHintBack");
        sr.color = new Color(0f, 0f, 0f, 0.7f);
        if (bsr) { sr.sortingLayerID = bsr.sortingLayerID; sr.sortingOrder = bsr.sortingOrder; }

        var go = UnityEngine.Object.Instantiate(src.gameObject, root.transform);
        go.name = "Text";
        var tr = go.GetComponent<TextTranslatorTMP>();
        if (tr) UnityEngine.Object.Destroy(tr);
        var tmp = go.GetComponent<TextMeshPro>();
        go.transform.localScale = Vector3.one;
        tmp.enableAutoSizing = false;
        tmp.fontSize = 3.6f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(2f, BackH);
        tmp.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        tmp.color = Color.white;
        tmp.text = "";
        var mr = go.GetComponent<MeshRenderer>();
        if (mr && bsr) { mr.sortingLayerID = bsr.sortingLayerID; mr.sortingOrder = bsr.sortingOrder; }
        return root.transform;
    }

    private static bool _pad;

    // 最後に触ったのがコントローラーか (つながっていなければ常にキーボード)
    internal static bool UsingPad()
    {
        try
        {
            if (!ReInput.isReady || ReInput.controllers.joystickCount == 0) return false;
            var t = ReInput.controllers.GetLastActiveControllerType();
            if (t == ControllerType.Joystick) return true;
            if (t == ControllerType.Keyboard || t == ControllerType.Mouse) return false;
            return _pad;
        }
        catch { return false; }
    }

    // 今の操作 (キーボードかコントローラー) でその操作を押すキー・ボタンの名前
    private static string Text(int actionId)
    {
        if (Cache.TryGetValue(actionId, out var s)) return s;
        s = ActionName(actionId, _pad);
        Cache[actionId] = s;
        return s;
    }

    // pad = true ならコントローラーのボタンの名前 (割り当てが無ければキー)、false ならキーの名前
    internal static string ActionName(int actionId, bool pad)
    {
        if (!ReInput.isReady) return null;
        string key = null, button = null;
        try
        {
            var maps = ReInput.players.GetPlayer(0).controllers.maps;
            var k = maps.GetFirstElementMapWithAction(ControllerType.Keyboard, actionId, true);
            if (k != null && k.keyboardKeyCode != KeyboardKeyCode.None) key = KeyName((KeyCode)(int)k.keyboardKeyCode);
            if (pad)
            {
                var j = maps.GetFirstElementMapWithAction(ControllerType.Joystick, actionId, true);
                if (j != null && !string.IsNullOrEmpty(j.elementIdentifierName)) button = PadName(j.elementIdentifierName);
            }
        }
        catch { }
        return pad && button != null ? button : key;
    }

    // コントローラーのボタンの名前を短くする (板に収まるように)
    private static string PadName(string n) => n switch
    {
        "Left Trigger" => "LT",
        "Right Trigger" => "RT",
        "Left Shoulder" => "LB",
        "Right Shoulder" => "RB",
        "Left Stick Button" => "LS",
        "Right Stick Button" => "RS",
        "D-Pad Up" => "↑",
        "D-Pad Down" => "↓",
        "D-Pad Left" => "←",
        "D-Pad Right" => "→",
        "Action Bottom Row 1" => "A",
        "Action Bottom Row 2" => "B",
        "Action Top Row 1" => "X",
        "Action Top Row 2" => "Y",
        _ => n.Replace("Button ", ""),
    };

    // 今の操作に合わせた名前 (キーボードのキーで書いた文の {key} を、コントローラーを使っている時はボタンの名前にする)
    internal static string NameFor(int actionId) => ActionName(actionId, _pad);

    // MRP の操作の名前 (コントローラーを使っている時はボタン、それ以外・割り当てが無い時はキー)
    internal static string SlotName(Keybinds.Slot slot)
    {
        if (_pad)
        {
            int id = Keybinds.ActionId(slot);
            var n = id >= 0 ? ActionName(id, true) : null;
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return KeyName(Keybinds.Key(slot));
    }

    internal static string KeyName(KeyCode k) => k switch
    {
        >= KeyCode.Alpha0 and <= KeyCode.Alpha9 => ((int)(k - KeyCode.Alpha0)).ToString(),
        KeyCode.LeftShift or KeyCode.RightShift => "Shift",
        KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
        KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
        KeyCode.Space => "Space",
        KeyCode.Return => "Enter",
        KeyCode.Escape => "Esc",
        KeyCode.UpArrow => "↑",
        KeyCode.DownArrow => "↓",
        KeyCode.LeftArrow => "←",
        KeyCode.RightArrow => "→",
        _ => k.ToString(),
    };

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder("hints");
        foreach (var kv in Cache) sb.Append($" {kv.Key}={kv.Value}");
        return sb.ToString();
    }
}
