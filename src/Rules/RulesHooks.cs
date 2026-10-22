using System;
using HarmonyLib;

namespace MoreRolesPlus.Rules;

// 試合のルールの入口: 船ができた時・毎 tick・会議が終わって歩けるようになった時
internal static class RulesHooks
{
    private static bool _meeting;
    private static int _quiet;
    private const int QuietTicks = 10; // 会議の画面と追放の演出が両方消えてから 0.2 秒 (入れ替わりの隙間を拾わない)

    internal static void OnShipStart()
    {
        _meeting = false;
        MapRules.ShipMap = MapRules.MapId;
        Run("devices", Devices.OnShipStart);
        Run("sabotage rules", SabotageRules.OnShipStart);
        Run("map gimmicks", MapGimmicks.OnShipStart);
        Run("select spawn", SelectSpawn.OnShipStart);
    }

    internal static void OnMeeting()
    {
        _meeting = true;
        _quiet = 0;
    }

    // FixedUpdate
    internal static void Tick()
    {
        RandomSpawn.Tick();
        RandomSpawn.TickAirship();
        SelectSpawn.Tick();
        MapGimmicks.Tick();
        if (!_meeting) return;
        if (MeetingHud.Instance || ExileController.Instance) { _quiet = 0; return; }
        if (++_quiet < QuietTicks) return;
        _meeting = false;
        if (!ShipStatus.Instance) return;
        Run("random spawn", RandomSpawn.AfterMeeting);
        Run("select spawn", SelectSpawn.AfterMeeting);
        Run("map gimmicks", MapGimmicks.AfterMeeting);
    }

    private static void Run(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Plugin.Logger.LogError($"{what}: {e}"); }
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class RulesShipStartPatch
{
    public static void Postfix() => RulesHooks.OnShipStart();
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class RulesMeetingPatch
{
    public static void Postfix() => RulesHooks.OnMeeting();
}
