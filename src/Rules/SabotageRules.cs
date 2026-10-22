// Based on https://github.com/tukasa0001/TownOfHost Patches/ISystemType/SwitchSystemPatch.cs (GPL-3.0)
// and https://github.com/EnhancedNetwork/TownofHost-Enhanced Patches/SabotageSystemPatch.cs (GPL-3.0) via EndlessHostRoles (GPL-3.0)
using HarmonyLib;
using Hazel;
using MoreRolesPlus.Options;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Rules;

// サボタージュのルール (種類ごとに無くす・制限時間・クールダウン・停電の細かい設定)。
// 押す側 (各端末) ではサボタージュの地図のボタンを効かなくし、受ける側 (ホスト) でも断る。
// 行は本編の「ゲーム設定」タブの終わりに並べる (GameTabRows)
[Settings(Tab.General, "SabotageRules.Title", order: -56)]
public static class SabotageRules
{
    public static readonly BoolOpt Disable = new("SabotageRules.Disable", false) { OffTab = true };
    public static readonly BoolOpt NoReactor = Item("SabotageRules.NoReactor", Disable);
    public static readonly BoolOpt NoSeismic = Item("SabotageRules.NoSeismic", Disable);
    public static readonly BoolOpt NoHeli = Item("SabotageRules.NoHeli", Disable);
    public static readonly BoolOpt NoOxygen = Item("SabotageRules.NoOxygen", Disable);
    public static readonly BoolOpt NoComms = Item("SabotageRules.NoComms", Disable);
    public static readonly BoolOpt NoLights = Item("SabotageRules.NoLights", Disable);
    public static readonly BoolOpt NoMixup = Item("SabotageRules.NoMixup", Disable);
    public static readonly BoolOpt NoDoors = Item("SabotageRules.NoDoors", Disable);

    public static readonly BoolOpt Cooldown = new("SabotageRules.Cooldown", false) { OffTab = true };
    public static readonly FloatOpt CooldownSeconds = new("SabotageRules.CooldownSeconds", 30f, 0f, 60f, 2.5f)
        { OffTab = true, Parent = Cooldown, Format = MapRules.Seconds };

    public static readonly BoolOpt TimeLimit = new("SabotageRules.TimeLimit", false) { OffTab = true };
    public static readonly FloatOpt SkeldReactor = Limit("SabotageRules.SkeldReactor", 30f);
    public static readonly FloatOpt SkeldOxygen = Limit("SabotageRules.SkeldOxygen", 30f);
    public static readonly FloatOpt MiraReactor = Limit("SabotageRules.MiraReactor", 45f);
    public static readonly FloatOpt MiraOxygen = Limit("SabotageRules.MiraOxygen", 45f);
    public static readonly FloatOpt PolusSeismic = Limit("SabotageRules.PolusSeismic", 60f);
    public static readonly FloatOpt AirshipHeli = Limit("SabotageRules.AirshipHeli", 90f);
    public static readonly FloatOpt FungleReactor = Limit("SabotageRules.FungleReactor", 60f);
    public static readonly FloatOpt FungleMixup = Limit("SabotageRules.FungleMixup", 10f);

    public static readonly BoolOpt Lights = new("SabotageRules.Lights", false) { OffTab = true };
    public static readonly BoolOpt LightsNoUndo = Item("SabotageRules.LightsNoUndo", Lights);
    public static readonly BoolOpt LightsNoViewingDeck = Item("SabotageRules.LightsNoViewingDeck", Lights);
    public static readonly BoolOpt LightsNoGapRoom = Item("SabotageRules.LightsNoGapRoom", Lights);
    public static readonly BoolOpt LightsNoCargo = Item("SabotageRules.LightsNoCargo", Lights);

    public static Opt[] Rows => _rows ??= new Opt[]
    {
        Disable, NoReactor, NoSeismic, NoHeli, NoOxygen, NoComms, NoLights, NoMixup, NoDoors,
        Cooldown, CooldownSeconds,
        TimeLimit, SkeldReactor, SkeldOxygen, MiraReactor, MiraOxygen, PolusSeismic, AirshipHeli, FungleReactor, FungleMixup,
        Lights, LightsNoUndo, LightsNoViewingDeck, LightsNoGapRoom, LightsNoCargo,
    };
    private static Opt[] _rows;

    private static BoolOpt Item(string key, BoolOpt parent) => new(key, false) { OffTab = true, Parent = parent };
    private static FloatOpt Limit(string key, float def) => new(key, def, 5f, 120f, 5f) { OffTab = true, Parent = TimeLimit, Format = MapRules.Seconds };

    // その種類のサボタージュを無くしているか (起こす時の SystemTypes で)
    internal static bool IsOff(SystemTypes t) => Disable && t switch
    {
        SystemTypes.Reactor => NoReactor,
        SystemTypes.Laboratory => NoSeismic,
        SystemTypes.HeliSabotage => NoHeli,
        SystemTypes.LifeSupp => NoOxygen,
        SystemTypes.Comms => NoComms,
        SystemTypes.Electrical => NoLights,
        SystemTypes.MushroomMixupSabotage => NoMixup,
        _ => false,
    };

    // Airship の停電の板 (使えなくする物・元の座標)
    private static readonly (float X, float Y)[] AirshipPanels = { (-12.93f, -11.28f), (13.92f, 6.43f), (30.56f, 2.12f) };

    private static bool PanelOff(int i) => Lights && i switch
    {
        0 => LightsNoViewingDeck,
        1 => LightsNoGapRoom,
        _ => LightsNoCargo,
    };

    // 停電を直す人の近くの板が使えないか (ホストの断り)
    internal static bool PanelBlockedAt(Vector2 pos)
    {
        if (!Lights || MapRules.MapId != 4) return false;
        for (int i = 0; i < AirshipPanels.Length; i++)
        {
            if (!PanelOff(i)) continue;
            float x = MapFlip.Flipped ? MapFlip.Mirror(AirshipPanels[i].X) : AirshipPanels[i].X;
            float dx = pos.x - x, dy = pos.y - AirshipPanels[i].Y;
            if (dx * dx + dy * dy < 4f) return true;
        }
        return false;
    }

    // 船ができた後: 制限時間を各システムに書く (全員・ホストの数え始めに使われる)・Airship の板を使えなくする
    internal static void OnShipStart()
    {
        SabotageRulesHeliPatch._was = false;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (TimeLimit)
        {
            int map = MapRules.MapId;
            var sys = ship.Systems;
            if (sys.ContainsKey(SystemTypes.Reactor))
            {
                var r = sys[SystemTypes.Reactor].TryCast<ReactorSystemType>();
                if (r != null) r.ReactorDuration = map == 5 ? FungleReactor : map == 1 ? MiraReactor : SkeldReactor;
            }
            if (sys.ContainsKey(SystemTypes.Laboratory))
            {
                var r = sys[SystemTypes.Laboratory].TryCast<ReactorSystemType>();
                if (r != null) r.ReactorDuration = PolusSeismic;
            }
            if (sys.ContainsKey(SystemTypes.LifeSupp))
            {
                var o = sys[SystemTypes.LifeSupp].TryCast<LifeSuppSystemType>();
                if (o != null) o.LifeSuppDuration = map == 1 ? MiraOxygen : SkeldOxygen;
            }
            if (sys.ContainsKey(SystemTypes.MushroomMixupSabotage))
            {
                var m = sys[SystemTypes.MushroomMixupSabotage].TryCast<MushroomMixupSabotageSystem>();
                if (m != null) m.secondsForAutoHeal = FungleMixup;
            }
        }
        if (Lights && MapRules.MapId == 4)
        {
            int n = 0;
            foreach (var c in Object.FindObjectsOfType<Console>())
            {
                if (!c || !HasTask(c, TaskTypes.FixLights)) continue;
                var p = c.transform.position;
                if (!PanelBlockedAt(new Vector2(p.x, p.y))) continue;
                foreach (var col in c.GetComponents<Collider2D>()) col.enabled = false;
                n++;
            }
            Plugin.Logger.LogInfo($"sabotage rules: {n} light panels off");
        }
    }

    private static bool HasTask(Console c, TaskTypes t)
    {
        var types = c.TaskTypes;
        if (types == null) return false;
        for (int i = 0; i < types.Length; i++) if (types[i] == t) return true;
        return false;
    }

    internal static string Describe()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return "no ship";
        var sys = ship.Systems;
        var sb = new System.Text.StringBuilder();
        if (sys.ContainsKey(SystemTypes.Reactor)) sb.Append($" reactor={sys[SystemTypes.Reactor].TryCast<ReactorSystemType>()?.ReactorDuration}");
        if (sys.ContainsKey(SystemTypes.Laboratory)) sb.Append($" seismic={sys[SystemTypes.Laboratory].TryCast<ReactorSystemType>()?.ReactorDuration}");
        if (sys.ContainsKey(SystemTypes.LifeSupp)) sb.Append($" o2={sys[SystemTypes.LifeSupp].TryCast<LifeSuppSystemType>()?.LifeSuppDuration}");
        if (sys.ContainsKey(SystemTypes.HeliSabotage)) sb.Append($" heli={sys[SystemTypes.HeliSabotage].TryCast<HeliSabotageSystem>()?.Countdown:0.#}");
        if (sys.ContainsKey(SystemTypes.MushroomMixupSabotage)) sb.Append($" mixup={sys[SystemTypes.MushroomMixupSabotage].TryCast<MushroomMixupSabotageSystem>()?.secondsForAutoHeal}");
        if (sys.ContainsKey(SystemTypes.Sabotage)) sb.Append($" sabTimer={sys[SystemTypes.Sabotage].TryCast<SabotageSystemType>()?.Timer:0.#}");
        return sb.ToString();
    }
}

// ホスト: 無くした種類のサボタージュと、死んだ人のサボタージュ (VanillaTweaks.DeadImpNoSabotage) は起こさない。
// 起こした後はクールダウンを設定の秒にする
[HarmonyPatch(typeof(SabotageSystemType), nameof(SabotageSystemType.UpdateSystem))]
internal static class SabotageRulesUpdatePatch
{
    public static bool Prefix(SabotageSystemType __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
    {
        if (!AmongUsClient.Instance.AmHost) return true;
        if (VanillaTweaks.BlocksSabotage(player))
        {
            Plugin.Logger.LogInfo($"sabotage rules: refused from dead {player.PlayerId}");
            return false;
        }
        if (!SabotageRules.Disable) return true;
        var r = MessageReader.Get(msgReader);
        var t = (SystemTypes)r.ReadByte();
        r.Recycle();
        if (!SabotageRules.IsOff(t)) return true;
        Plugin.Logger.LogInfo($"sabotage rules: {t} refused");
        return false;
    }

    public static void Postfix(SabotageSystemType __instance)
    {
        if (!SabotageRules.Cooldown || !AmongUsClient.Instance.AmHost || __instance.Timer <= 0f) return;
        __instance.Timer = SabotageRules.CooldownSeconds;
        __instance.IsDirty = true;
    }
}

// ホスト: Airship の墜落の制限時間。本編の長さは型の定数なので、始まった時に残り時間を書く (本編の同期で全員へ)
[HarmonyPatch(typeof(HeliSabotageSystem), nameof(HeliSabotageSystem.Deteriorate))]
internal static class SabotageRulesHeliPatch
{
    internal static bool _was;

    public static void Postfix(HeliSabotageSystem __instance)
    {
        if (!SabotageRules.TimeLimit || !AmongUsClient.Instance.AmHost) return;
        bool active = __instance.IsActive;
        if (active && !_was)
        {
            __instance.Countdown = SabotageRules.AirshipHeli;
            __instance.IsDirty = true;
        }
        _was = active;
    }
}

// ホスト: ドアを閉めるサボタージュを無くす
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CloseDoorsOfType))]
internal static class SabotageRulesDoorsPatch
{
    public static bool Prefix() => !(SabotageRules.Disable && SabotageRules.NoDoors);
}

// 各端末: サボタージュの地図のボタンを押しても起こさない (無くした種類)
[HarmonyPatch(typeof(MapRoom))]
internal static class SabotageRulesMapRoomPatch
{
    [HarmonyPatch(nameof(MapRoom.SabotageReactor)), HarmonyPrefix]
    public static bool Reactor() => !SabotageRules.IsOff(MapRules.MapId == 4 ? SystemTypes.HeliSabotage : SystemTypes.Reactor);

    [HarmonyPatch(nameof(MapRoom.SabotageHeli)), HarmonyPrefix]
    public static bool Heli() => !SabotageRules.IsOff(SystemTypes.HeliSabotage);

    [HarmonyPatch(nameof(MapRoom.SabotageSeismic)), HarmonyPrefix]
    public static bool Seismic() => !SabotageRules.IsOff(SystemTypes.Laboratory);

    [HarmonyPatch(nameof(MapRoom.SabotageOxygen)), HarmonyPrefix]
    public static bool Oxygen() => !SabotageRules.IsOff(SystemTypes.LifeSupp);

    [HarmonyPatch(nameof(MapRoom.SabotageComms)), HarmonyPrefix]
    public static bool Comms() => !SabotageRules.IsOff(SystemTypes.Comms);

    [HarmonyPatch(nameof(MapRoom.SabotageLights)), HarmonyPrefix]
    public static bool LightsOff() => !SabotageRules.IsOff(SystemTypes.Electrical);

    [HarmonyPatch(nameof(MapRoom.SabotageMushroomMixup)), HarmonyPrefix]
    public static bool Mixup() => !SabotageRules.IsOff(SystemTypes.MushroomMixupSabotage);

    [HarmonyPatch(nameof(MapRoom.SabotageDoors)), HarmonyPrefix]
    public static bool Doors() => !(SabotageRules.Disable && SabotageRules.NoDoors);
}

// サボタージュの地図を開いた時: 無くした種類のボタンを隠す
[HarmonyPatch(typeof(InfectedOverlay), nameof(InfectedOverlay.Start))]
internal static class SabotageRulesOverlayPatch
{
    public static void Postfix(InfectedOverlay __instance)
    {
        if (!SabotageRules.Disable || __instance.rooms == null) return;
        foreach (var room in __instance.rooms)
        {
            if (!room) continue;
            if (room.special && room.special.gameObject.activeSelf && SpecialOff(room.room)) room.special.gameObject.SetActive(false);
            if (room.door && SabotageRules.NoDoors) room.door.gameObject.SetActive(false);
        }
    }

    // 部屋のボタンが起こすサボタージュ (本編の地図の部屋の並び)
    private static bool SpecialOff(SystemTypes room) => room switch
    {
        SystemTypes.Reactor => SabotageRules.IsOff(MapRules.MapId == 4 ? SystemTypes.HeliSabotage : SystemTypes.Reactor),
        SystemTypes.Laboratory => SabotageRules.IsOff(SystemTypes.Laboratory),
        SystemTypes.LifeSupp => SabotageRules.IsOff(SystemTypes.LifeSupp),
        SystemTypes.Comms => SabotageRules.IsOff(SystemTypes.Comms),
        SystemTypes.Electrical => SabotageRules.IsOff(SystemTypes.Electrical),
        _ => false,
    };
}

// ホスト: 停電のスイッチ。直したスイッチを戻させない・使えなくした板からの操作を断る
[HarmonyPatch(typeof(SwitchSystem), nameof(SwitchSystem.UpdateSystem))]
internal static class SabotageRulesSwitchPatch
{
    private const byte DamageBit = 0x80; // 本編の SwitchSystem.DamageSystem

    public static bool Prefix(SwitchSystem __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
    {
        if (!SabotageRules.Lights || !AmongUsClient.Instance.AmHost) return true;
        var r = MessageReader.Get(msgReader);
        byte amount = r.ReadByte();
        r.Recycle();
        if ((amount & DamageBit) != 0) return true; // 停電を起こす側
        if (player && SabotageRules.PanelBlockedAt(player.GetTruePosition())) return false;
        if (SabotageRules.LightsNoUndo)
        {
            // amount = 押したスイッチの番号。もう合っているスイッチは動かさない
            byte knob = (byte)(1 << amount);
            if ((__instance.ActualSwitches & knob) == (__instance.ExpectedSwitches & knob)) return false;
        }
        return true;
    }
}
