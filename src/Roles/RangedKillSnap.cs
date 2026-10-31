using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 本編のキルの演出は、倒した人を倒された人の所へ瞬間移動させる (コルーチン CoPerformKill の中)。
// 離れた所から倒す役職 (ロケットランチャーの爆発・トリガーハッピーの弾) は、移された同じフレームのうちに元の場所へ戻す。
// どの端末でも同じに決まるよう、全員に届いている状態 (爆発した・乱射している) だけで判断する
[HarmonyLib.HarmonyPatch]
internal static class RangedKillSnapPatch
{
    private static System.Reflection.PropertyInfo _source;
    private static bool _has;
    private static Vector3 _saved;

    public static System.Reflection.MethodBase TargetMethod()
    {
        var t = System.Linq.Enumerable.First(typeof(KillAnimation).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            n => n.Name.Contains("CoPerformKill"));
        _source = HarmonyLib.AccessTools.Property(t, "source");
        if (_source == null) Plugin.Logger.LogWarning("RangedKillSnap: CoPerformKill has no source field (kill snap not undone)");
        return HarmonyLib.AccessTools.Method(t, "MoveNext");
    }

    public static void Prefix(object __instance)
    {
        _has = false;
        if (_source == null || RoleState.All.Count == 0) return;
        var src = _source.GetValue(__instance) as PlayerControl;
        if (!src || (!Impostor.RocketLauncher.RangedKill(src) && !Impostor.TriggerHappy.RangedKill(src))) return;
        _saved = src.transform.position;
        _has = true;
    }

    public static void Postfix(object __instance)
    {
        if (!_has) return;
        _has = false;
        var src = _source.GetValue(__instance) as PlayerControl;
        if (!src || !src.NetTransform) return;
        Vector3 now = src.transform.position;
        float dx = now.x - _saved.x, dy = now.y - _saved.y;
        if (dx * dx + dy * dy > 0.0025f) src.NetTransform.SnapTo(FxMath.V2(_saved.x, _saved.y));
    }
}
