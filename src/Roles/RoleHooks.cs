using HarmonyLib;

namespace MoreRolesPlus.Roles;

// 本編の処理から役職の Modify〜 を呼ぶ所。役職が誰にも付いていない間は何もしない。
// 新しい Modify〜 を RoleBase に足したら、ここに本編側の入口を足す。

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
internal static class VisionPatch
{
    // player は Unity の == (生存確認の呼び出し) を避けて is null で見る。
    // Airship は CalculateLightRadius を上書きしているので別に当てる。中で元の処理を呼んでいても二重に掛けない。
    // 粉塵の視界は役職と関係なく掛かるので、役職が誰にも付いていない時の早抜けより先に見る
    internal static int AirshipDepth;

    public static void Postfix([HarmonyArgument(0)] NetworkedPlayerInfo player, ref float __result)
    {
        if (AirshipDepth > 0 || player is null) return;
        Apply(player, ref __result);
    }

    internal static void Apply(NetworkedPlayerInfo player, ref float result)
    {
        if (Terrain.DustCloud.VisionMul < 1f && player.PlayerId == Terrain.DustCloud.LocalId) result *= Terrain.DustCloud.VisionMul;
        if (RoleState.All.Count == 0 && RoleState.AllAddons.Count == 0) return;
        RoleState.Of(player.PlayerId)?.ModifyVision(ref result);
        var addons = RoleState.AddonsOf(player.PlayerId);
        for (int i = 0; i < addons.Count; i++) addons[i].ModifyVision(ref result);
    }
}

[HarmonyPatch(typeof(AirshipStatus), nameof(AirshipStatus.CalculateLightRadius))]
internal static class AirshipVisionPatch
{
    public static void Prefix() => VisionPatch.AirshipDepth++;

    public static void Postfix([HarmonyArgument(0)] NetworkedPlayerInfo player, ref float __result)
    {
        if (player is null) return;
        VisionPatch.Apply(player, ref __result);
    }

    public static System.Exception Finalizer(System.Exception __exception)
    {
        VisionPatch.AirshipDepth--;
        return __exception;
    }
}

// キルの待ち時間の長さ (キルの後・会議の後に戻る秒数と、ボタンの減り方の基準)
[HarmonyPatch(typeof(LogicOptions), nameof(LogicOptions.GetKillCooldown))]
internal static class KillCooldownPatch
{
    public static void Postfix(ref float __result)
    {
        // 役職の無い人 (本編のまま) にもアドオンは付くので、役職の有無に関わらず自分のアドオンまで見る
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        RoleState.Local?.ModifyKillCooldown(ref __result);
        var addons = RoleState.AddonsOf(lp.PlayerId);
        for (int i = 0; i < addons.Count; i++) addons[i].ModifyKillCooldown(ref __result);
    }
}
