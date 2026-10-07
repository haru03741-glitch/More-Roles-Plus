using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 開発者だけが開けるゲーム内のコンソール。コマンドはテスト用ブリッジと同じ表 (help で一覧)。
// PC = Ctrl+Enter で開閉・打って Enter で実行・↑で前のコマンド。
// Android = 画面左の「DEV」を押すとキーボードが開き、決定で実行する。
// 開発者でなければ毎フレームの処理は名簿の確認 1 回だけ (名簿の無いビルドでは即終わる)。
internal static class DevConsole
{
    private const int MaxLines = 13;
    private const float Width = 7.6f, Height = 3.0f;   // 画面の高さは 6 (HUD の単位)
    private const float FontSize = 1.4f;

    private static readonly List<string> Lines = new();
    private static readonly List<string> History = new();
    private static readonly StringBuilder Input_ = new();

    private static GameObject _root;
    private static TextMeshPro _text;
    private static TextMeshPro _devButton;
    private static TouchScreenKeyboard _keyboard;
    private static int _historyIndex;
    private static bool _dirty;
    private static float _nextResolve;

    private static bool _open;
    public static bool IsOpen => _open; // 本編のキー操作を止める判定で毎フレーム読むので、Unity 側には問い合わせない

    public static void Tick()
    {
        if (!_open && !Resolved()) return;
        // 試合を抜けると HUD ごと消える。開いたままの印が残ると本編のキー操作が止まり続けるので閉じる
        if (!HudManager.InstanceExists || (_open && !_root))
        {
            _open = false;
            _keyboard = null;
            return;
        }

        if (OperatingSystem.IsAndroid()) TickAndroid();
        else TickPc();

        if (_dirty && IsOpen) Redraw();
    }

    // フレンドコードはログイン後に決まるので、決まるまでは 1 秒おきに引く
    private static bool Resolved()
    {
        if (!DevUsers.HasList) return false;
        if (DevUsers.IsResolved) return DevUsers.AmDev;
        if (Time.unscaledTime < _nextResolve) return false;
        _nextResolve = Time.unscaledTime + 1f;
        return DevUsers.AmDev;
    }

    private static void TickPc()
    {
        if (!IsOpen && Input.GetKeyDown(KeyCode.Return))
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl) { SetOpen(true); return; }
            // Shift+Enter+C = チャット欄を出す / 隠す (EndKnot と同じ押し方)
            if ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && Input.GetKey(KeyCode.C))
            {
                var chat = Vanilla.Chat;
                if (chat) DevChat.SetShown(!chat.gameObject.activeSelf);
                return;
            }
        }
        else if (IsOpen && Input.GetKeyDown(KeyCode.Return) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
        {
            SetOpen(false);
            return;
        }
        if (!IsOpen) return;

        if (Input.GetKeyDown(KeyCode.UpArrow) && History.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            Input_.Clear().Append(History[_historyIndex]);
            _dirty = true;
        }
        if (Input.GetKeyDown(KeyCode.DownArrow) && History.Count > 0)
        {
            _historyIndex = Math.Min(History.Count, _historyIndex + 1);
            Input_.Clear();
            if (_historyIndex < History.Count) Input_.Append(History[_historyIndex]);
            _dirty = true;
        }

        // 文字はキーを 1 つずつ見る (Input.inputString は Android 版のゲームに無いので使わない)。開いている間だけ
        if (Input.GetKeyDown(KeyCode.Backspace) && Input_.Length > 0) { Input_.Length--; _dirty = true; }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            Submit(Input_.ToString());
            Input_.Clear();
            _dirty = true;
            return;
        }
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        foreach (var (key, lower, upper) in Keys)
        {
            if (!Input.GetKeyDown(key)) continue;
            Input_.Append(shift ? upper : lower);
            _dirty = true;
        }
    }

    private static readonly (KeyCode key, char lower, char upper)[] Keys = BuildKeys();

    private static (KeyCode, char, char)[] BuildKeys()
    {
        var list = new List<(KeyCode, char, char)>();
        for (int i = 0; i < 26; i++) list.Add((KeyCode.A + i, (char)('a' + i), (char)('A' + i)));
        for (int i = 0; i < 10; i++)
        {
            list.Add((KeyCode.Alpha0 + i, (char)('0' + i), (char)('0' + i)));
            list.Add((KeyCode.Keypad0 + i, (char)('0' + i), (char)('0' + i)));
        }
        list.Add((KeyCode.Space, ' ', ' '));
        list.Add((KeyCode.Minus, '-', '_'));
        list.Add((KeyCode.KeypadMinus, '-', '-'));
        list.Add((KeyCode.Period, '.', '.'));
        list.Add((KeyCode.KeypadPeriod, '.', '.'));
        return list.ToArray();
    }

    private static void TickAndroid()
    {
        EnsureDevButton();
        if (_keyboard != null)
        {
            var status = _keyboard.status;
            if (status == TouchScreenKeyboard.Status.Done) { Submit(_keyboard.text); _keyboard = null; }
            else if (status != TouchScreenKeyboard.Status.Visible) _keyboard = null;
            return;
        }
        if (!Input.GetMouseButtonDown(0) || !_devButton) return;
        // 押した所が「DEV」の上か (HUD のカメラの座標で比べる)
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        if (!cam) return;
        Vector3 p = cam.ScreenToWorldPoint(Input.mousePosition);
        Vector3 b = _devButton.transform.position;
        if (Math.Abs(p.x - b.x) > 0.45f || Math.Abs(p.y - b.y) > 0.3f) return;
        if (!IsOpen) SetOpen(true);
        _keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default, false, false, false);
    }

    private static void SetOpen(bool open)
    {
        if (open && !_root) Build();
        if (!_root) { _open = false; return; }
        _root.SetActive(open);
        _open = open;
        Plugin.Logger.LogInfo($"dev console {(open ? "open" : "close")}");
        _historyIndex = History.Count;
        _dirty = true;
    }

    // テスト用ブリッジから: 開け閉めと 1 行の入力 (名簿が無くても試せるように)
    internal static void BridgeOpen(bool open) => SetOpen(open);
    internal static void BridgeType(string line) { if (!_open) SetOpen(true); Submit(line); }

    private static void Submit(string line)
    {
        line = line?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            if (!OperatingSystem.IsAndroid()) SetOpen(false);
            return;
        }
        if (line is "close" or "exit") { SetOpen(false); return; }
        if (History.Count == 0 || History[^1] != line) History.Add(line);
        _historyIndex = History.Count;
        Add($"<color=#9ad>> {Escape(line)}</color>");
        Bridge.TestBridge.Run(line, s => Add(Color(s)));
        Plugin.Logger.LogInfo($"dev> {line}");
    }

    private static string Color(string s)
    {
        string e = Escape(s);
        if (s.StartsWith("ERR")) return $"<color=#f77>{e}</color>";
        if (s.StartsWith("OK")) return $"<color=#9e9>{e}</color>";
        return e;
    }

    // コマンドの出力に < があると TMP のタグとして読まれる
    private static string Escape(string s) => s.Replace("<", "<​");

    private static void Add(string line)
    {
        Lines.Add(line);
        if (Lines.Count > 200) Lines.RemoveRange(0, Lines.Count - 200);
        _dirty = true;
    }

    private static void Redraw()
    {
        _dirty = false;
        if (!_text) return;
        var sb = new StringBuilder();
        int from = Math.Max(0, Lines.Count - MaxLines);
        for (int i = from; i < Lines.Count; i++) sb.Append(Lines[i]).Append('\n');
        if (OperatingSystem.IsAndroid()) sb.Append("<color=#888>DEV を押して入力</color>");
        else sb.Append("<color=#fff>> ").Append(Escape(Input_.ToString())).Append("_</color>");
        _text.text = sb.ToString();
    }

    private static void Build()
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        var src = hud.TaskPanel ? hud.TaskPanel.taskText : null;
        if (!src) return;

        _root = new GameObject("MrpDevConsole");
        _root.layer = hud.gameObject.layer;
        _root.transform.SetParent(hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0.3f, -800f);

        var bg = new GameObject("Bg");
        bg.layer = _root.layer;
        bg.transform.SetParent(_root.transform, false);
        bg.transform.localScale = new Vector3(Width, Height, 1f);
        var sr = bg.AddComponent<SpriteRenderer>();
        sr.sprite = WhiteSprite();
        sr.color = new Color(0f, 0f, 0f, 0.8f);

        var go = UnityEngine.Object.Instantiate(src.gameObject, _root.transform);
        go.name = "Text";
        for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var c in go.GetComponents<Component>())
            if (!c.TryCast<Transform>() && !c.TryCast<TextMeshPro>() && !c.TryCast<Renderer>() && !c.TryCast<MeshFilter>()) UnityEngine.Object.Destroy(c);
        go.transform.localPosition = new Vector3(0f, 0f, -1f);
        go.transform.localScale = Vector3.one;
        _text = go.GetComponent<TextMeshPro>();
        _text.rectTransform.sizeDelta = new Vector2(Width - 0.3f, Height - 0.2f);
        _text.alignment = TextAlignmentOptions.BottomLeft;
        _text.enableAutoSizing = false; // 複製元 (タスク欄) は自動で大きさを変える
        _text.fontSize = FontSize;
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Truncate;
        _text.color = new Color(0.85f, 0.85f, 0.85f, 1f);
        _text.richText = true;

        if (Lines.Count == 0) Add("<color=#888>MRP dev console — help で一覧 / close で閉じる</color>");
    }

    private static void EnsureDevButton()
    {
        if (_devButton) return;
        var hud = Vanilla.Hud;
        if (!hud) return;
        var src = hud.TaskPanel ? hud.TaskPanel.taskText : null;
        var cam = hud.UICamera;
        if (!src || !cam) return;
        var go = UnityEngine.Object.Instantiate(src.gameObject, hud.transform);
        go.name = "MrpDevButton";
        for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var c in go.GetComponents<Component>())
            if (!c.TryCast<Transform>() && !c.TryCast<TextMeshPro>() && !c.TryCast<Renderer>() && !c.TryCast<MeshFilter>()) UnityEngine.Object.Destroy(c);
        // 画面の左端の中ほど
        float halfW = cam.orthographicSize * cam.aspect;
        go.transform.localPosition = new Vector3(-halfW + 0.55f, -0.6f, -800f);
        go.transform.localScale = Vector3.one;
        _devButton = go.GetComponent<TextMeshPro>();
        _devButton.rectTransform.sizeDelta = new Vector2(1f, 0.6f);
        _devButton.alignment = TextAlignmentOptions.Center;
        _devButton.enableAutoSizing = false;
        _devButton.fontSize = 2f;
        _devButton.text = "<mark=#000000AA>DEV</mark>";
    }

    private static Sprite _white;
    private static Sprite WhiteSprite()
    {
        if (_white) return _white;
        var tex = Texture2D.whiteTexture;
        _white = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
        _white.hideFlags = HideFlags.HideAndDontSave;
        return _white;
    }
}

// コンソールに打っている間は、本編のキー操作 (移動・使う・キル・地図など) を止める。
// 止めるだけだと開く直前の移動の向きが残って歩き続けるので、向きも 0 にする
[HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
internal static class DevConsoleKeyBlock
{
    public static bool Prefix(KeyboardJoystick __instance)
    {
        if (!DevConsole.IsOpen) return true;
        __instance.del = Vector2.zero;
        return false;
    }
}
