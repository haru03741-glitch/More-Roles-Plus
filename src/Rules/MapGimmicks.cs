// Based on https://github.com/tukasa0001/TownOfHost (LadderPatch / MovingPlatformBehaviourPatch / DoorsReset / AirShipElectricalDoors, GPL-3.0)
// and https://github.com/EnhancedNetwork/TownofHost-Enhanced (DeconSystemPatch / zipline and spore options, GPL-3.0) via EndlessHostRoles (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Rules;

// マップの仕掛けのルール (設定は MapRules)。
// - ジップライン・動く床・胞子: 各端末は使えなくするだけ、ホストは届いた依頼を断る
// - 除染の時間: 全員が船ができた時に自分の手元の除染室に書く (時間を数えるのはホスト)
// - 会議の後のドア・Airship の電気室の壁: ホストが変えて本編の同期で配る
// - はしごの落下: ホストが降りた人を見て、降り切った所で倒す
internal static class MapGimmicks
{
    private static readonly System.Random Rng = new();

    // ---- ジップライン (Fungle) ----

    internal static bool ZiplineBlocked(NetworkedPlayerInfo pc, bool fromTop)
    {
        if (!MapRules.ZiplineRules || pc == null) return false;
        if (fromTop ? MapRules.ZiplineNoDown : MapRules.ZiplineNoUp) return true;
        bool impostor = pc.Role && pc.Role.IsImpostor;
        bool neutral = RoleState.Of(pc.PlayerId) is { Team: Team.Neutral };
        return impostor ? MapRules.ZiplineNoImpostors : neutral ? MapRules.ZiplineNoNeutrals : MapRules.ZiplineNoCrew;
    }

    // 端末の使えるかの問い合わせ (近づいている間は毎フレーム) は、陣営の見直しを 0.5 秒ごとにする
    private static long _zipAt;
    private static bool _zipTop, _zipBlocked;

    internal static bool ZiplineBlockedNow(NetworkedPlayerInfo pc, bool fromTop)
    {
        long now = Environment.TickCount64;
        if (now >= _zipAt || fromTop != _zipTop)
        {
            _zipBlocked = ZiplineBlocked(pc, fromTop);
            _zipTop = fromTop;
            _zipAt = now + 500;
        }
        return _zipBlocked;
    }

    // ---- 会議の後 (ホスト) ----

    internal static void AfterMeeting()
    {
        if (!AmongUsClient.Instance.AmHost || !ShipStatus.Instance) return;
        int map = MapRules.MapId;
        if (MapRules.AirshipVariableElectrical && map == 4 && ShipStatus.Instance.Systems.ContainsKey(SystemTypes.Decontamination))
        {
            var doors = ShipStatus.Instance.Systems[SystemTypes.Decontamination].TryCast<ElectricalDoors>();
            if (doors != null)
            {
                doors.Initialize();
                Plugin.Logger.LogInfo("map rules: airship electrical rerolled");
            }
        }
        if (MapRules.ResetDoors && map is 2 or 4 or 5) ResetDoors(MapRules.ResetDoorsMode.Value);
    }

    // 0 = 全部開ける・1 = 全部閉める・2 = 1 枚ずつばらばら。ラウンジのトイレと除染室のドアは閉めない (本編もサボタージュで閉めない)
    private static void ResetDoors(int mode)
    {
        var ship = ShipStatus.Instance;
        var all = ship.AllDoors;
        if (all == null) return;
        foreach (var door in all)
        {
            if (!door || door.Room is SystemTypes.Lounge or SystemTypes.Decontamination) continue;
            bool open = mode switch { 0 => true, 1 => false, _ => Rng.Next(2) == 0 };
            var plain = door.TryCast<PlainDoor>();
            if (plain != null) plain.SetDoorway(open);
        }
        if (ship.Systems.ContainsKey(SystemTypes.Doors))
        {
            var ds = ship.Systems[SystemTypes.Doors].TryCast<DoorsSystemType>();
            if (ds != null) ds.IsDirty = true;
        }
        Plugin.Logger.LogInfo($"map rules: doors reset mode {mode}");
    }

    // ---- はしごの落下 (ホスト) ----

    private static readonly Dictionary<byte, int> Falling = new(); // 落ちる人と、降り始めてからの tick

    internal static void OnClimb(PlayerPhysics physics, Ladder source)
    {
        if (!MapRules.LadderDeath || !AmongUsClient.Instance.AmHost || !physics || !source || !source.Destination) return;
        var p = physics.myPlayer;
        if (!p || p.Data == null || p.Data.IsDead) return;
        if (source.transform.position.y <= source.Destination.transform.position.y) return; // 上りは落ちない
        if (Rng.Next(100) >= MapRules.LadderDeathChance) return;
        Falling[p.PlayerId] = 0;
    }

    private const int FallWaitTicks = 15; // 降り始めてから見始めるまで (はしごに乗った印が立つのを待つ)
    private const int FallGiveUpTicks = 30 * 15;

    internal static void Tick()
    {
        if (Falling.Count == 0) return;
        if (!AmongUsClient.Instance.AmHost || !ShipStatus.Instance || MeetingHud.Instance) { Falling.Clear(); return; }
        List<byte> done = null;
        foreach (var id in new List<byte>(Falling.Keys))
        {
            int t = Falling[id] + 1;
            Falling[id] = t;
            if (t < FallWaitTicks) continue;
            var data = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
            var p = data?.Object;
            if (!p || data.IsDead || t > FallGiveUpTicks) { (done ??= new()).Add(id); continue; }
            if (p.onLadder) continue;
            (done ??= new()).Add(id);
            p.RpcMurderPlayer(p, true);
            Plugin.Logger.LogInfo($"map rules: {id} fell from a ladder");
        }
        if (done != null) foreach (var id in done) Falling.Remove(id);
    }

    internal static void OnShipStart()
    {
        Falling.Clear();
        _zipAt = 0;
        SetDecon();
    }

    // 除染室の時間 (Mira HQ・Polus)。全員が船ができた時に書く (時間を数えるのはホスト・扉の開いている間は各自)
    private static void SetDecon()
    {
        if (!MapRules.Decon) return;
        int map = MapRules.MapId;
        if (map is not (1 or 2)) return;
        foreach (var d in UnityEngine.Object.FindObjectsOfType<DeconSystem>())
        {
            if (!d) continue;
            d.DeconTime = map == 1 ? MapRules.MiraDeconTime : MapRules.PolusDeconTime;
            d.DoorOpenTime = map == 1 ? MapRules.MiraDeconDoorTime : MapRules.PolusDeconDoorTime;
        }
    }
}

[HarmonyPatch(typeof(ZiplineConsole), nameof(ZiplineConsole.CanUse))]
internal static class ZiplineConsolePatch
{
    public static void Postfix(ZiplineConsole __instance, [HarmonyArgument(0)] NetworkedPlayerInfo pc, ref float __result,
        [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse)
    {
        if (!MapRules.ZiplineRules || !MapGimmicks.ZiplineBlockedNow(pc, __instance.atTop)) return;
        canUse = couldUse = false;
        __result = float.MaxValue;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckUseZipline))]
internal static class ZiplineHostPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(2)] bool fromTop) =>
        !AmongUsClient.Instance.AmHost || !__instance || !MapGimmicks.ZiplineBlocked(__instance.Data, fromTop);
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckSporeTrigger))]
internal static class SporeHostPatch
{
    public static bool Prefix() => !(MapRules.FungleNoSpores && AmongUsClient.Instance.AmHost);
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.ClimbLadder))]
internal static class LadderClimbPatch
{
    public static void Postfix(PlayerPhysics __instance, [HarmonyArgument(0)] Ladder source)
    {
        try { MapGimmicks.OnClimb(__instance, source); }
        catch (Exception e) { Plugin.Logger.LogError($"ladder: {e}"); }
    }
}

// Airship の動く床を止める (本編の「故障中」の札を出し、床は止めた位置へ)
[HarmonyPatch(typeof(MovingPlatformBehaviour))]
internal static class PlatformPatch
{
    internal static bool Off;

    [HarmonyPatch(nameof(MovingPlatformBehaviour.Start)), HarmonyPrefix]
    public static void Start(MovingPlatformBehaviour __instance)
    {
        Off = MapRules.AirshipNoPlatform;
        if (!Off) return;
        __instance.transform.localPosition = __instance.DisabledPosition;
        var air = ShipStatus.Instance ? ShipStatus.Instance.TryCast<AirshipStatus>() : null;
        if (air && air.outOfOrderPlat) air.outOfOrderPlat.SetActive(true);
    }

    [HarmonyPatch(nameof(MovingPlatformBehaviour.Use), typeof(PlayerControl)), HarmonyPrefix]
    public static bool Use() => !Off;

    [HarmonyPatch(nameof(MovingPlatformBehaviour.SetSide)), HarmonyPrefix]
    public static bool SetSide() => !Off;
}
