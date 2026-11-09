// Based on https://github.com/Gurge44/EndlessHostRoles and https://github.com/waffle-ful/Aeterna-End-K-not Modules/Zoom.cs,
// which credit https://github.com/tugaru1975/TownOfPlus (TOPmods/Zoom.cs) and https://github.com/Yumenopai/TownOfHost_Y (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MoreRolesPlus;

// Ctrl を押しながらマウスのホイール (スマホは 2 本指のピンチ) で世界のカメラを引いて広く見る。
// 使えるのはロビー・フリープレイ・死んだ後だけ (生きている人が引くと壁の影の外まで見えてしまう)。
// HUD の端に寄せる部品 (AspectPosition) は世界のカメラの大きさで位置を決めるので、HUD のカメラ (Hud/UI Camera) も同じだけ引いて
// 位置を計算し直させる (HUD は引いた分だけ小さく見える)。視界の影 (ShadowQuad) は 3 単位の画面に合わせた板なので、
// 引いている間は隠して、戻したら元の表示に戻す。
// ホイールが UI (ボタン・一覧・開いたパネル) の上で回った時と、AU の窓が前面でない時 (Windows は裏の窓にもホイールを送る) は引かない。
// パネルは開いている間、外側を押して閉じる当たり判定が画面全体を覆うので、開いている間はどこで回しても引かない。
// 毎フレームの問い合わせはホイール (スマホは指の数) の 1 つだけ。引いている間は 0.25 秒ごとに使える場面かを見直す
internal static class Zoom
{
    private const float Base = 3f, Max = 12f, Step = 1.25f;
    private static readonly bool Touch = OperatingSystem.IsAndroid();
    private static float _size = Base;
    private static Camera _cam, _ui;      // 引いたカメラ (場面が変わって消えたら戻す)
    private static float _pinch;          // 前のフレームの 2 本指の間 (0 = ピンチしていない)
    private static float _nextCheck;
    private static MeshRenderer _shadow;  // 隠した影 (戻す先)
    private static bool _shadowWasOn;
    // ロビーの開始ボタン (GameStartManager/StartGameArea の子)。「ホスト:」(Host Info) は画面の下の端から決まった所に寄せられるが、
    // 開始ボタンは端に寄せる部品 (AspectPosition) を持たないので、HUD のカメラを引くと画面の中ほどに残ってしまう。
    // 引いた分 (画面の下の端が下がった分) だけ下げて、「ホスト:」の下に並んだままにする
    private static readonly List<(Transform t, Vector3 pos)> _start = new();

    internal static bool Zoomed => _size > Base;

    public static void Tick()
    {
        float d = Touch ? Pinch() : CtrlWheel();
        if (d != 0f && Blocked() != null) d = 0f;
        if (d == 0f && !Zoomed) return;
        if (d != 0f || Time.unscaledTime >= _nextCheck)
        {
            _nextCheck = Time.unscaledTime + 0.25f;
            if (Zoomed && !_cam || !Allowed()) { Reset(); return; }
        }
        if (d == 0f) return;
        float size = d > 0f ? _size / Step : _size * Step;
        if (size < Base) size = Base;
        if (size > Max) size = Max;
        if (size == _size) return;
        Apply(size);
    }

    // PC は Ctrl を押しながらのホイールだけ (押していないホイールは一覧などのスクロールに任せる)
    private static float CtrlWheel()
    {
        float d = Input.mouseScrollDelta.y;
        if (d == 0f) return 0f;
        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ? d : 0f;
    }

    // 2 本指の間が広がれば寄る (+)・狭まれば引く (−)
    private static float Pinch()
    {
        if (Input.touchCount != 2) { _pinch = 0f; return 0f; }
        Vector2 a = Input.GetTouch(0).position, b = Input.GetTouch(1).position;
        float dx = a.x - b.x, dy = a.y - b.y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float prev = _pinch;
        _pinch = dist;
        if (prev == 0f) return 0f;
        float diff = dist - prev;
        // 指の揺れで寄ったり引いたりしないよう、画面の高さの 4% 動いてから 1 段
        float need = Screen.height * 0.04f;
        if (diff > -need && diff < need) { _pinch = prev; return 0f; }
        return diff;
    }

    // ホイール (ピンチ) をズームに使わない理由 (null = 使う)。動いたフレームだけ呼ぶ
    private static string Blocked()
    {
        if (!Touch && !GameWindowInFront()) return "unfocused";
        // 本編がボタンの反応に使う「指/マウスの下のいちばん手前の UI」。一覧の欄もパネルの背景もここに出る
        if (PassiveButtonManager.InstanceExists && PassiveButtonManager.Instance.currentOver) return PassiveButtonManager.Instance.currentOver.name;
        return null;
    }

    // 前面の窓がこのプロセスのゲームの窓か。Application.isFocused は別の窓 (ターミナルなど) が前面でも true のままのことがある (実測)。
    // BepInEx のコンソールも同じプロセスの窓なので、窓の種類 (UnityWndClass) まで見る
#if ANDROID
    internal static bool GameWindowInFront() => true;
#else
    private static readonly StringBuilder ClassName = new(32);

    internal static bool GameWindowInFront()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid != (uint)Environment.ProcessId) return false;
        ClassName.Clear();
        return GetClassNameW(h, ClassName, ClassName.Capacity) > 0 && ClassName.ToString() == "UnityWndClass";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int max);
#endif

    // dev: 今ホイールを回したらズームに使うか (使わないなら理由)
    internal static string Describe() => $"OK zoomgate size={_size:0.##} blocked={Blocked() ?? "-"} allowed={Allowed()}";

    private static bool Allowed()
    {
        var client = AmongUsClient.Instance;
        if (!client) return false;
        if (MeetingHud.Instance || ExileController.Instance || Minigame.Instance || GameSettingMenu.Instance
            || PlayerCustomizationMenu.Instance || Dev.DevConsole.IsOpen) return false;
        if (MapBehaviour.Instance && MapBehaviour.Instance.IsOpen) return false;
        var chat = Vanilla.Chat;
        if ((chat && chat.IsOpenOrOpening) || UI.TextField.Typing) return false;
        if (LobbyBehaviour.Instance && !ShipStatus.Instance) return true;
        if (!ShipStatus.Instance) return false;
        if (client.NetworkMode == NetworkModes.FreePlay) return true;
        var lp = PlayerControl.LocalPlayer;
        return lp && lp.Data != null && lp.Data.IsDead;
    }

    private static void Apply(float size)
    {
        var cam = Camera.main;
        if (!cam) { _size = Base; return; }
        var hud = Vanilla.Hud;
        _cam = cam;
        _ui = hud ? hud.UICamera : null;
        _size = size;
        cam.orthographicSize = size;
        if (_ui) _ui.orthographicSize = size;
        if (size > Base) HideShadow();
        else RestoreShadow();
        PlaceStart(size);
        Relayout();
    }

    private static void PlaceStart(float size)
    {
        var gsm = Vanilla.StartManager;
        var area = gsm ? gsm.transform.Find("StartGameArea") : null;
        if (_start.Count > 0 && (!_start[0].t || _start[0].t.parent != area)) RestoreStart();
        if (!area) return;
        if (_start.Count == 0)
            for (int i = 0; i < area.childCount; i++)
            {
                var c = area.GetChild(i);
                if (!c.GetComponent<AspectPosition>()) _start.Add((c, c.localPosition));
            }
        float down = size - Base;
        foreach (var (t, pos) in _start)
            if (t) t.localPosition = new Vector3(pos.x, pos.y - down, pos.z);
        if (size <= Base) _start.Clear();
    }

    private static void RestoreStart()
    {
        foreach (var (t, pos) in _start)
            if (t) t.localPosition = pos;
        _start.Clear();
    }

    // 確かめ用 (ブリッジ): ホイールで引いた時と同じに
    internal static string ApplyForTest(float size)
    {
        Apply(Mathf.Clamp(size, Base, Max));
        var gsm = Vanilla.StartManager;
        var btn = gsm ? gsm.transform.Find("StartGameArea/StartButton (Host)") : null;
        return $"size={_size:0.##} start={(btn ? btn.localPosition.ToString() : "-")}";
    }

    // HUD の端に寄せる部品を今のカメラの大きさで置き直す (本編が画面の大きさが変わった時に呼ぶのと同じ)
    internal static void Relayout()
    {
        // 受け手に場面の切り替えで消えかけた物がいると例外を出すので、ここで止める
        try { ResolutionManager.ResolutionChanged?.Invoke((float)Screen.width / Screen.height, Screen.width, Screen.height, Screen.fullScreen); }
        catch (Exception e) { Plugin.Logger.LogWarning($"zoom relayout: {e.Message}"); }
    }

    private static void HideShadow()
    {
        var hud = Vanilla.Hud;
        var q = hud ? hud.ShadowQuad : null;
        if (!q || _shadow) return;
        _shadow = q;
        _shadowWasOn = q.gameObject.activeSelf;
        q.gameObject.SetActive(false);
        Terrain.ShadowView.SetShadowShown(false);
    }

    private static void RestoreShadow()
    {
        if (_shadow && _shadowWasOn) _shadow.gameObject.SetActive(true);
        if (_shadow) Terrain.ShadowView.SetShadowShown(true);
        _shadow = null;
    }

    // 使えない場面になった・試合が切り替わった: カメラと影を元に戻す
    public static void Reset()
    {
        _pinch = 0f;
        if (!Zoomed && !_shadow) return;
        _size = Base;
        if (_cam) _cam.orthographicSize = Base;
        if (_ui) _ui.orthographicSize = Base;
        _cam = null;
        _ui = null;
        RestoreShadow();
        RestoreStart();
        Relayout();
    }
}
