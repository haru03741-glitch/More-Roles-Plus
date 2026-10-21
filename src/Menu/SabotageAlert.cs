// The replaced flash loop follows https://github.com/All-Of-Us-Mods/MiraAPI (HudManagerFlashPatches, LGPL-2.1)
using System;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 原子炉・酸素などのサボタージュ中の画面の赤い点滅と警報音を、端末ごとに切る (光や音が苦手な人向け)。
// 両方 ON (既定) の時は本編の点滅をそのまま使う。どちらかを切った時だけ本編の点滅を始めず、ここで 1 秒ごとに残した方だけを出す。
// 本編はコルーチンで回すが、managed の IEnumerator を il2cpp の StartCoroutine に渡すと GCHandle が残るので、刻みの見張りで回す
internal static class SabotageAlert
{
    private const long PeriodMs = 1000;
    private static readonly Color Red = new(1f, 0f, 0f, 0.37254903f); // 本編の点滅の色

    private static HudManager _hud;
    private static bool _reactor, _oxy;
    private static bool _shown;   // 自分が FullScreen を出したか
    private static bool _lit;     // 点滅の今の位相
    private static long _nextMs;
    private static int _shipGen;

    internal static int Started { get; private set; } // 確認用: 本編の点滅の代わりに始めた回数

    private static bool Running => _reactor || _oxy;

    // 本編の点滅を始める前: 両方 ON なら本編へ任せる (true)。どちらかが OFF なら自分で回す (false)
    internal static bool OnStart(HudManager hud, bool reactor)
    {
        if (ClientOptions.SabotageFlash.Value && ClientOptions.SabotageAlarm.Value) return true;
        if (!Running)
        {
            _nextMs = 0;
            _shipGen = Terrain.GameClock.ShipGen;
            Started++;
            Plugin.Logger.LogInfo($"[SabotageAlert] start reactor={reactor} flash={ClientOptions.SabotageFlash.Value} alarm={ClientOptions.SabotageAlarm.Value}");
        }
        _hud = hud;
        if (reactor) _reactor = true; else _oxy = true;
        return false;
    }

    internal static void OnStop(bool reactor)
    {
        if (!Running) return;
        if (reactor) _reactor = false; else _oxy = false;
        if (!Running) End();
    }

    public static void Tick()
    {
        if (!Running) return;
        long now = Environment.TickCount64;
        if (now < _nextMs) return;
        _nextMs = now + PeriodMs;
        // 船が替わった・HUD が消えた・会議が始まった (本編が止めを呼ばない道があっても残さない)
        if (!_hud || _shipGen != Terrain.GameClock.ShipGen || MeetingHud.Instance) { _reactor = _oxy = false; End(); return; }

        // 点滅の位相は自分で持つ (FullScreen は他の暗転とも共用なので、点ける時は毎回色も書く)
        var fs = _hud.FullScreen;
        if (ClientOptions.SabotageFlash.Value && fs)
        {
            _lit = !_lit;
            if (_lit) fs.color = Red;
            fs.gameObject.SetActive(_lit);
            _shown = true;
        }
        else if (_shown) HideFlash();

        if (ClientOptions.SabotageAlarm.Value && ShipStatus.Instance && ShipStatus.Instance.SabotageSound && SoundManager.Instance)
            SoundManager.Instance.PlaySound(ShipStatus.Instance.SabotageSound, false, 1f, null);
    }

    private static void End()
    {
        if (_shown) HideFlash();
        _hud = null;
    }

    private static void HideFlash()
    {
        _shown = _lit = false;
        if (_hud && _hud.FullScreen) _hud.FullScreen.gameObject.SetActive(false);
    }

    // 確認用
    internal static string Describe() =>
        $"OK sabalert flash={(ClientOptions.SabotageFlash.Value ? "on" : "off")} alarm={(ClientOptions.SabotageAlarm.Value ? "on" : "off")} running={(_reactor ? "reactor " : "")}{(_oxy ? "oxy" : "")} shown={_shown} started={Started}";
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.StartReactorFlash))]
internal static class SabotageAlertReactorStartPatch
{
    public static bool Prefix(HudManager __instance) => SabotageAlert.OnStart(__instance, true);
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.StartOxyFlash))]
internal static class SabotageAlertOxyStartPatch
{
    public static bool Prefix(HudManager __instance) => SabotageAlert.OnStart(__instance, false);
}

// 止める処理は本編のまま (部屋の灯りも戻す)。自分が出した点滅は自分で消す
[HarmonyPatch(typeof(HudManager), nameof(HudManager.StopReactorFlash))]
internal static class SabotageAlertReactorStopPatch
{
    public static void Postfix() => SabotageAlert.OnStop(true);
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.StopOxyFlash))]
internal static class SabotageAlertOxyStopPatch
{
    public static void Postfix() => SabotageAlert.OnStop(false);
}
