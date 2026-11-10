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
        if (pc == null || __instance.TaskTypes == null) return;
        var role = Roles.RoleState.Of(pc.PlayerId);
        var lim = role as IRepairLimit;
        // マッドメイト (マッドメイトとして配られた役職も) は設定で通信・停電・原子炉/酸素を直せない
        bool mad = role is Roles.RoleBase { Madden: true } || role is Roles.Crew.Madmate;
        if (lim == null && !mad) return;
        foreach (var t in __instance.TaskTypes)
        {
            if ((lim != null && ((t == TaskTypes.FixLights && !lim.CanFixLights) || (t == TaskTypes.FixComms && !lim.CanFixComms)))
                || (mad && Roles.Crew.Madmate.BlocksRepair(t)))
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
