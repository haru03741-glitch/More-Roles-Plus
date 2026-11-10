// Ported from https://github.com/TheOtherRolesAU/TheOtherRoles TheOtherRoles/Patches/ShipStatusPatch.cs, PlayerControlPatch.cs, CustomOptionHolder.cs (GPL-3.0)
using HarmonyLib;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ライター (クルー): 視界が広い。停電でも船の明るさの「停電中の視界」倍までしか狭まらない
// (TOR と同じく、普通のクルーの視界の明るさの割合で、停電中の視界〜点灯中の視界の間を取る)
// 視界は懐中電灯の形 (本編のかくれんぼの懐中電灯を借りる・PC はマウス、スマホは歩く向き)
public sealed class Lighter : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#EEE5BE";
    public override Text Name => new("Lighter.Name");
    public override Text Blurb => new("Lighter.Blurb");
    public override string Origin => "TOR";

    public static readonly FloatOpt LightsOnVision = new("Lighter.LightsOnVision", 1.5f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt LightsOffVision = new("Lighter.LightsOffVision", 0.5f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt FlashlightWidth = new("Lighter.FlashlightWidth", 0.3f, 0.1f, 1f, 0.1f, "x");

    public override void OnAssigned()
    {
        if (!IsLocal) return;
        Relight();
        Lifespan.OnRelease(() => PortKit.After(0.2f, Relight)); // 外れた直後はまだライターに見えるので少し待つ
    }

    // 自分の視界を作り直させる (懐中電灯を付ける・外す)
    private static void Relight()
    {
        var me = PlayerControl.LocalPlayer;
        if (me && me.lightSource) me.AdjustLighting();
    }

    public override void ModifyVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        // 本編のクルーの視界 = Lerp(最小, 最大, 明るさ) × クルーの視界の設定。そこから明るさの割合を取り出す
        float mod = GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
        float neutral = mod > 0f ? radius / mod : radius;
        float lit = Mathf.InverseLerp(ship.MinLightRadius, ship.MaxLightRadius, neutral);
        radius = Mathf.Lerp(ship.MaxLightRadius * LightsOffVision, ship.MaxLightRadius * LightsOnVision, lit);
    }
}

// 自分がライターの間だけ、視界を懐中電灯にする (かくれんぼの試合は本編のまま)
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.AdjustLighting))]
internal static class LighterFlashlightPatch
{
    public static bool Prefix(PlayerControl __instance)
    {
        if (!__instance || __instance != PlayerControl.LocalPlayer || !__instance.lightSource) return true;
        if (GameOptionsManager.Instance.CurrentGameOptions.GameMode == AmongUs.GameOptions.GameModes.HideNSeek) return true;
        if (RoleState.Local is not Lighter || __instance.Data == null || __instance.Data.IsDead) return true;
        __instance.SetFlashlightInputMethod();
        __instance.lightSource.SetupLightingForGameplay(true, Lighter.FlashlightWidth, __instance.TargetFlashlight.transform);
        return false;
    }
}
