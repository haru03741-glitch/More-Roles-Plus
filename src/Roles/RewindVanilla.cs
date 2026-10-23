using System;
using HarmonyLib;

namespace MoreRolesPlus.Roles;

// 本編の変身と透明を時戻しで取り消す。どちらも電文を受けた全員の端末が自分の手元で姿を替えるので、
// 各端末がその入口で前の姿を控え、逆再生がその時刻を越えた時に自分の手元だけで元へ戻す (電文は出さない)。
// 持ち主の残り時間は TimeRewind が本編の役職の欄として戻す。戻した後は本編がその残りを消化して普段どおり変身を解く・姿を現す
internal static class RewindVanilla
{
    internal static int Shifts, Vanishes; // 確かめ用: 取り消した回数

    // 人ごとの今の見た目が透明か (本編の役職の欄は押した時に立ち、見た目は消える演出の後に替わるので、欄でなく見た目を持つ)
    private static readonly bool[] Invisible = new bool[256];

    internal static bool IsInvisible(byte id) => Invisible[id];

    internal static void SetInvisible(byte id, bool on) => Invisible[id] = on;

    internal static void Clear() => Array.Clear(Invisible, 0, Invisible.Length);

    internal static void UndoShift(PlayerControl pc, int prevTarget)
    {
        if (!pc || pc.Data == null) return;
        PlayerControl to = pc;
        if (prevTarget >= 0 && prevTarget != pc.PlayerId)
        {
            var d = GameData.Instance ? GameData.Instance.GetPlayerById((byte)prevTarget) : null;
            to = d != null ? d.Object : null;
            if (!to) return; // 化けていた相手が抜けた: 姿のデータが無いので今のまま
        }
        pc.Shapeshift(to, false);
        Shifts++;
        RewindPlayback.Refresh(pc.PlayerId);
    }

    internal static void UndoVanish(PlayerControl pc, bool wasInvisible)
    {
        if (!pc || pc.Data == null) return;
        pc.SetRoleInvisibility(wasInvisible, false, false);
        Vanishes++;
        RewindPlayback.Refresh(pc.PlayerId);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Shapeshift))]
internal static class RewindVanillaShift
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl targetPlayer)
    {
        if (!Rewind.Recording || !__instance || !targetPlayer) return;
        var pc = __instance;
        int prev = pc.shapeshiftTargetPlayerId;
        if (prev == targetPlayer.PlayerId || (prev < 0 && targetPlayer.PlayerId == pc.PlayerId)) return; // 姿が替わらない
        Rewind.Undo(_ => RewindVanilla.UndoShift(pc, prev));
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetRoleInvisibility))]
internal static class RewindVanillaVanish
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] bool isActive)
    {
        if (!__instance) return;
        var pc = __instance;
        bool was = RewindVanilla.IsInvisible(pc.PlayerId);
        RewindVanilla.SetInvisible(pc.PlayerId, isActive);
        if (Rewind.Recording && was != isActive) Rewind.Undo(_ => RewindVanilla.UndoVanish(pc, was));
    }
}

// 取り消しで変身し直す時に、変身の跡 (脱いだ皮) を二重に残さない
[HarmonyPatch(typeof(ShapeshifterRole), nameof(ShapeshifterRole.SetEvidence))]
internal static class RewindVanillaEvidence
{
    public static bool Prefix() => !RewindJournal.Undoing && !TimeRewind.Playing;
}
