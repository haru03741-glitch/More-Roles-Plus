// Based on https://github.com/Gurge44/EndlessHostRoles and https://github.com/waffle-ful/Aeterna-End-K-not Modules/Zoom.cs,
// which credit https://github.com/tugaru1975/TownOfPlus (TOPmods/Zoom.cs) and https://github.com/Yumenopai/TownOfHost_Y (GPL-3.0)
using System;
using UnityEngine;

namespace MoreRolesPlus;

// マウスのホイール (スマホは 2 本指のピンチ) で世界のカメラを引いて広く見る。
// 使えるのはロビー・フリープレイ・死んだ後だけ (生きている人が引くと壁の影の外まで見えてしまう)。
// HUD の端に寄せる部品 (AspectPosition) は世界のカメラの大きさで位置を決めるので、HUD のカメラ (Hud/UI Camera) も同じだけ引いて
// 位置を計算し直させる (HUD は引いた分だけ小さく見える)。視界の影 (ShadowQuad) は 3 単位の画面に合わせた板なので、
// 引いている間は隠して、戻したら元の表示に戻す。
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

    internal static bool Zoomed => _size > Base;

    public static void Tick()
    {
        float d = Touch ? Pinch() : Input.mouseScrollDelta.y;
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

    private static bool Allowed()
    {
        var client = AmongUsClient.Instance;
        if (!client) return false;
        if (MeetingHud.Instance || ExileController.Instance || Minigame.Instance || GameSettingMenu.Instance
            || PlayerCustomizationMenu.Instance || Dev.DevConsole.IsOpen) return false;
        if (MapBehaviour.Instance && MapBehaviour.Instance.IsOpen) return false;
        var chat = Vanilla.Chat;
        if (chat && chat.IsOpenOrOpening) return false;
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
        Relayout();
    }

    // HUD の端に寄せる部品を今のカメラの大きさで置き直す (本編が画面の大きさが変わった時に呼ぶのと同じ)
    private static void Relayout()
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
        Relayout();
    }
}
