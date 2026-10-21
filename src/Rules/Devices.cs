// Based on https://github.com/tukasa0001/TownOfHost Modules/DisableDevice.cs (GPL-3.0) via EndlessHostRoles (GPL-3.0),
// originally from https://github.com/ykundesu/SuperNewRoles Mode/SuperHostRoles/BlockTool.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Rules;

// 機器の無効化 (アドミンの机・カメラ・バイタル・ドアログ)。全員が MRP を入れているので、各端末が自分の手元で
// その端末を「使えない」にするだけ (本編の使うボタンが光らない)。例外の陣営と「誰かが死んだら使える」はその時の自分で決める。
// 行は本編の「ゲーム設定」タブの終わりに並べる (GameTabRows)
[Settings(Tab.General, "Devices.Title", order: -57)]
public static class Devices
{
    public static readonly BoolOpt Skeld = Map("Devices.Skeld");
    public static readonly BoolOpt SkeldAdmin = Item("Devices.Admin", Skeld);
    public static readonly BoolOpt SkeldCamera = Item("Devices.Camera", Skeld);
    public static readonly BoolOpt Mira = Map("Devices.Mira");
    public static readonly BoolOpt MiraAdmin = Item("Devices.Admin", Mira);
    public static readonly BoolOpt MiraDoorLog = Item("Devices.DoorLog", Mira);
    public static readonly BoolOpt Polus = Map("Devices.Polus");
    public static readonly BoolOpt PolusAdmin = Item("Devices.Admin", Polus);
    public static readonly BoolOpt PolusCamera = Item("Devices.Camera", Polus);
    public static readonly BoolOpt PolusVitals = Item("Devices.Vitals", Polus);
    public static readonly BoolOpt Airship = Map("Devices.Airship");
    public static readonly BoolOpt AirshipCockpitAdmin = Item("Devices.CockpitAdmin", Airship);
    public static readonly BoolOpt AirshipRecordsAdmin = Item("Devices.RecordsAdmin", Airship);
    public static readonly BoolOpt AirshipCamera = Item("Devices.Camera", Airship);
    public static readonly BoolOpt AirshipVitals = Item("Devices.Vitals", Airship);
    public static readonly BoolOpt Fungle = Map("Devices.Fungle");
    public static readonly BoolOpt FungleCamera = Item("Devices.Camera", Fungle);
    public static readonly BoolOpt FungleVitals = Item("Devices.Vitals", Fungle);

    // 例外 (この人たちは使える)
    public static readonly BoolOpt Exceptions = new("Devices.Exceptions", false) { OffTab = true };
    public static readonly BoolOpt ExceptImpostors = Item("Devices.ExceptImpostors", Exceptions);
    public static readonly BoolOpt ExceptNeutrals = Item("Devices.ExceptNeutrals", Exceptions);
    public static readonly BoolOpt ExceptCrew = Item("Devices.ExceptCrew", Exceptions);
    public static readonly BoolOpt ExceptAfterDeath = Item("Devices.ExceptAfterDeath", Exceptions);

    public static Opt[] Rows => _rows ??= new Opt[]
    {
        Skeld, SkeldAdmin, SkeldCamera,
        Mira, MiraAdmin, MiraDoorLog,
        Polus, PolusAdmin, PolusCamera, PolusVitals,
        Airship, AirshipCockpitAdmin, AirshipRecordsAdmin, AirshipCamera, AirshipVitals,
        Fungle, FungleCamera, FungleVitals,
        Exceptions, ExceptImpostors, ExceptNeutrals, ExceptCrew, ExceptAfterDeath,
    };
    private static Opt[] _rows;

    private static BoolOpt Map(string key) => new(key, false) { OffTab = true };
    private static BoolOpt Item(string key, BoolOpt parent) => new(key, false) { OffTab = true, Parent = parent };

    private enum Kind { Admin, Camera, Vitals, DoorLog }

    // 今の船で無効にした端末 (船ができた時に 1 回選ぶ)
    private static readonly HashSet<IntPtr> Off = new();

    internal static bool Blocked(IntPtr console) => Off.Contains(console);

    // 船ができた後: 端末を種類で見分け、設定で無効にした物を控える
    internal static void OnShipStart()
    {
        Off.Clear();
        _exemptAt = 0;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        int map = MapRules.MapId;
        foreach (var c in Object.FindObjectsOfType<MapConsole>())
            if (c && IsOff(map, Kind.Admin, c.name)) Off.Add(c.Pointer);
        foreach (var c in Object.FindObjectsOfType<SystemConsole>())
        {
            var mg = c ? c.MinigamePrefab : null;
            if (!mg) continue;
            string type = mg.GetIl2CppType().Name;
            Kind? kind = type.Contains("Vitals") ? Kind.Vitals
                : type.Contains("SecurityLog") ? Kind.DoorLog
                : type.Contains("Surveillance") ? Kind.Camera
                : null;
            if (kind != null && IsOff(map, kind.Value, c.name)) Off.Add(c.Pointer);
        }
        if (Off.Count > 0) Plugin.Logger.LogInfo($"devices: {Off.Count} consoles off on map {map}");
    }

    private static bool IsOff(int map, Kind kind, string name) => map switch
    {
        0 or 3 => Skeld && (kind == Kind.Admin ? SkeldAdmin : kind == Kind.Camera && SkeldCamera),
        1 => Mira && (kind == Kind.Admin ? MiraAdmin : kind == Kind.DoorLog && MiraDoorLog),
        2 => Polus && kind switch { Kind.Admin => PolusAdmin, Kind.Camera => PolusCamera, Kind.Vitals => PolusVitals, _ => false },
        4 => Airship && kind switch
        {
            Kind.Admin => name.Contains("cockpit") ? AirshipCockpitAdmin : AirshipRecordsAdmin,
            Kind.Camera => AirshipCamera,
            Kind.Vitals => AirshipVitals,
            _ => false,
        },
        5 => Fungle && kind switch { Kind.Camera => FungleCamera, Kind.Vitals => FungleVitals, _ => false },
        _ => false,
    };

    // 例外の見直しは近づいている間 0.5 秒ごと (毎フレーム全員を数えない)
    private static long _exemptAt;
    private static bool _exempt;

    private static bool ExemptNow(NetworkedPlayerInfo pc)
    {
        long now = Environment.TickCount64;
        if (now >= _exemptAt)
        {
            _exempt = Exempt(pc);
            _exemptAt = now + 500;
        }
        return _exempt;
    }

    // 自分が例外に当たるか (無効にした端末に近づいた時だけ見る)
    private static bool Exempt(NetworkedPlayerInfo pc)
    {
        if (!Exceptions || pc == null) return false;
        if (ExceptAfterDeath && AnyoneDied()) return true;
        var role = RoleState.Of(pc.PlayerId);
        bool impostor = pc.Role && pc.Role.IsImpostor;
        bool neutral = role is { Team: Team.Neutral };
        if (impostor) return ExceptImpostors;
        if (neutral) return ExceptNeutrals;
        return ExceptCrew;
    }

    private static bool AnyoneDied()
    {
        var gd = GameData.Instance;
        if (!gd) return false;
        var all = gd.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p != null && p.IsDead && !p.Disconnected) return true;
        }
        return false;
    }

    // 端末の CanUse の後: 無効にした端末なら使えない
    internal static void Gate(IntPtr console, NetworkedPlayerInfo pc, ref float result, ref bool canUse, ref bool couldUse)
    {
        if (Off.Count == 0 || !Off.Contains(console) || ExemptNow(pc)) return;
        canUse = couldUse = false;
        result = float.MaxValue;
    }

    internal static string Describe() => $"off={Off.Count}";
}

[HarmonyPatch(typeof(MapConsole), nameof(MapConsole.CanUse))]
internal static class DevicesMapConsolePatch
{
    public static void Postfix(MapConsole __instance, [HarmonyArgument(0)] NetworkedPlayerInfo pc, ref float __result,
        [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse) =>
        Devices.Gate(__instance.Pointer, pc, ref __result, ref canUse, ref couldUse);
}

[HarmonyPatch(typeof(SystemConsole), nameof(SystemConsole.CanUse))]
internal static class DevicesSystemConsolePatch
{
    public static void Postfix(SystemConsole __instance, [HarmonyArgument(0)] NetworkedPlayerInfo pc, ref float __result,
        [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse) =>
        Devices.Gate(__instance.Pointer, pc, ref __result, ref canUse, ref couldUse);
}
