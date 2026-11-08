using System;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 地上の名前の色を今の陣営で塗り直す。本編は最初の配役の時、その時の陣営で名前を塗り (自分がインポスター側なら
// インポスター側の人を赤)、後から本編の役職を付け替えても塗り直さない。MRP は本編が選んだインポスターを配り直すことがあるので、
// そのままだと配り直された人の名前が前の陣営の色のまま残る (クルーへ移った人と元の仲間が互いに赤く見える・新しい仲間が白く見える)。
// 本編の役職が付くたびとイントロの後に、本編と同じ規則で全員の名前を塗り直す (毎フレームの名前の色を自分で書く役職とアドオンは、その後も自分で書く)
internal static class NameColors
{
    internal static void Refresh()
    {
        // かくれんぼは本編が鬼の名前を別の規則で塗るので触らない
        if (!Options.GameModeSwitch.IsNormal) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || !lp.Data.Role) return;
        bool meImp = lp.Data.Role.IsImpostor;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || !p.Data.Role || !p.cosmetics || !p.cosmetics.nameText) continue;
            var name = p.cosmetics.nameText;
            var c = meImp && p.Data.Role.IsImpostor ? p.Data.Role.NameColor : Color.white;
            c.a = name.color.a;
            name.color = c;
        }
    }
}

// 試合の途中で陣営が替わった時 (プロキシの入れ替え・ミラクルの交換・アムネシアなど) も、本編の役職が付いた所で塗り直す
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SetRole))]
internal static class NameColorsSetRolePatch
{
    public static void Postfix()
    {
        if (!ShipStatus.Instance) return;
        try { NameColors.Refresh(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"name colors: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class NameColorsGameStartPatch
{
    public static void Postfix()
    {
        try { NameColors.Refresh(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"name colors: {e.Message}"); }
    }
}
