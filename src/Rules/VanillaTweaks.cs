// Parallel medbay scans, skipping the walk to the medbay pad, hiding vent animations out of sight and
// cooldowns during minigames based on https://github.com/AU-Avengers/TOU-Mira (VanillaTweakOptions, GPL-3.0)
// Dead impostors cannot sabotage based on https://github.com/TheOtherRolesAU/TheOtherRoles (GPL-3.0)
using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// 本編の細かい調整。行は本編の「ゲーム設定」タブの終わりに並べる (GameTabRows)。どれも既定は本編のまま
[Settings(Tab.General, "VanillaTweaks.Title", order: -50)]
public static class VanillaTweaks
{
    public static readonly BoolOpt ParallelScan = new("VanillaTweaks.ParallelScan", false) { OffTab = true };
    public static readonly BoolOpt ScanNoWalk = new("VanillaTweaks.ScanNoWalk", false) { OffTab = true };
    public static readonly BoolOpt TickInTasks = new("VanillaTweaks.TickInTasks", true) { OffTab = true };
    public static readonly BoolOpt HideVentOutOfSight = new("VanillaTweaks.HideVentOutOfSight", false) { OffTab = true };
    public static readonly BoolOpt DeadImpNoSabotage = new("VanillaTweaks.DeadImpNoSabotage", false) { OffTab = true };

    private static Opt[] _rows;
    public static Opt[] Rows => _rows ??= new Opt[] { ParallelScan, ScanNoWalk, TickInTasks, HideVentOutOfSight, DeadImpNoSabotage };

    // 待ち時間を進めてよいか。本編は作業画面 (Minigame) を開いている間も動けない扱い (CanMove = false) になる
    internal static bool CooldownTicks(PlayerControl lp) => lp.CanMove || (TickInTasks && Minigame.Instance);

    // 本編のキルの待ち時間は作業画面を開いている間も進む (実測)。止める設定の時だけ本編が減らした分を足し戻す
    internal static void Tick()
    {
        if (TickInTasks || !ShipStatus.Instance) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.killTimer <= 0f || lp.Data == null || lp.Data.IsDead || !Minigame.Instance) return;
        var role = lp.Data.Role;
        if (!role || !role.CanUseKillButton || Roles.KillAbility.Owns) return;
        lp.killTimer += Time.fixedDeltaTime;
    }

    // 自分の視界に入っているか (視界の届く距離の中で、間に壁や物が無い)
    internal static bool InSight(PlayerControl p)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || p == null || p.AmOwner) return true;
        if (lp.Data != null && lp.Data.IsDead) return true;
        Vector2 from = lp.GetTruePosition(), d = p.GetTruePosition() - from;
        float mag = d.magnitude;
        if (!lp.lightSource || mag >= lp.lightSource.viewDistance) return false;
        return mag < 0.01f || !PhysicsHelpers.AnyNonTriggersBetween(from, d / mag, mag, Constants.ShipAndObjectsMask);
    }

    internal static bool BlocksSabotage(PlayerControl p) => DeadImpNoSabotage && p && p.Data != null && p.Data.IsDead;
}

// 医務室のスキャンを何人でも同時に: 自分の画面では順番待ちの列を空にして自分の番にする
[HarmonyPatch(typeof(MedScanMinigame), nameof(MedScanMinigame.FixedUpdate))]
internal static class ParallelScanPatch
{
    public static void Prefix(MedScanMinigame __instance)
    {
        if (!VanillaTweaks.ParallelScan) return;
        var lp = PlayerControl.LocalPlayer;
        var scan = __instance.medscan;
        if (!lp || scan == null) return;
        scan.CurrentUser = lp.PlayerId;
        scan.UsersList.Clear();
    }
}

// スキャンの前に台へ歩いて行かない: 歩く手順 (台へ・台の上の位置へ) を歩かずに終わった扱いにする
[HarmonyPatch]
internal static class ScanNoWalkPatch
{
    public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var t in typeof(MedScanMinigame).GetNestedTypes())
            if (t.Name.Contains("WalkToPad") || t.Name.Contains("WalkToOffset"))
                yield return AccessTools.Method(t, "MoveNext");
    }

    public static bool Prefix(Il2CppObjectBase __instance, ref bool __result)
    {
        if (!VanillaTweaks.ScanNoWalk) return true;
        var mg = __instance.GetType().GetProperty("__4__this")?.GetValue(__instance) as MedScanMinigame;
        if (mg == null) return true;
        mg.state = mg.state switch
        {
            MedScanMinigame.PositionState.None => MedScanMinigame.PositionState.WalkingToPad,
            MedScanMinigame.PositionState.WalkingToPad => MedScanMinigame.PositionState.WalkingToOffset,
            _ => (MedScanMinigame.PositionState)(-1),
        };
        if (mg.state == (MedScanMinigame.PositionState)(-1)) mg.walking = null;
        __result = false;
        return false;
    }
}

// 見えていない所のベントの出入りでは蓋の動き (と音) を出さない
[HarmonyPatch(typeof(Vent))]
internal static class HideVentPatch
{
    [HarmonyPatch(nameof(Vent.EnterVent)), HarmonyPrefix]
    public static bool Enter(Vent __instance, PlayerControl pc) =>
        !VanillaTweaks.HideVentOutOfSight || !__instance.EnterVentAnim || VanillaTweaks.InSight(pc);

    [HarmonyPatch(nameof(Vent.ExitVent)), HarmonyPrefix]
    public static bool Exit(Vent __instance, PlayerControl pc, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!VanillaTweaks.HideVentOutOfSight || !__instance.ExitVentAnim || VanillaTweaks.InSight(pc)) return true;
        __result = Effects.Wait(0f); // 呼び出し元はこの戻りを待つので空の待ちを返す
        return false;
    }
}

// 死んだインポスターのサボタージュ: ボタンを出さず、地図はふつうの地図で開く
[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class DeadSabotageButtonPatch
{
    public static void Postfix(HudManager __instance, PlayerControl localPlayer)
    {
        if (VanillaTweaks.BlocksSabotage(localPlayer) && __instance.SabotageButton) __instance.SabotageButton.ToggleVisible(false);
    }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.ShowSabotageMap))]
internal static class DeadSabotageMapPatch
{
    public static bool Prefix(MapBehaviour __instance)
    {
        if (!VanillaTweaks.BlocksSabotage(PlayerControl.LocalPlayer)) return true;
        __instance.ShowNormalMap();
        return false;
    }
}
