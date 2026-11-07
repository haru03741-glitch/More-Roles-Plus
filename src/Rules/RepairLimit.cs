using HarmonyLib;

namespace MoreRolesPlus.Rules;

// 役職が直せない破壊工作・ベントの中で動けないこと (モニターの設定)
public interface IRepairLimit
{
    bool CanFixLights { get; }
    bool CanFixComms { get; }
    bool CanMoveInVent { get; }
}

[HarmonyPatch(typeof(Console), nameof(Console.CanUse))]
internal static class RepairLimitConsolePatch
{
    public static void Postfix(Console __instance, ref float __result, [HarmonyArgument(0)] NetworkedPlayerInfo pc,
        [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse)
    {
        if (pc == null || Roles.RoleState.Of(pc.PlayerId) is not IRepairLimit lim || __instance.TaskTypes == null) return;
        foreach (var t in __instance.TaskTypes)
        {
            if ((t == TaskTypes.FixLights && !lim.CanFixLights) || (t == TaskTypes.FixComms && !lim.CanFixComms))
            {
                canUse = couldUse = false;
                __result = float.MaxValue;
                return;
            }
        }
    }
}

[HarmonyPatch(typeof(Vent), nameof(Vent.SetButtons))]
internal static class RepairLimitVentMovePatch
{
    public static void Prefix([HarmonyArgument(0)] ref bool enabled)
    {
        if (enabled && Roles.RoleState.Local is IRepairLimit { CanMoveInVent: false }) enabled = false;
    }
}
