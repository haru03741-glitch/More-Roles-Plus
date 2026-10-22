// Based on the task progress next to player names in https://github.com/eDonnes124/Town-Of-Us-R source/Patches/Roles/Role.cs (GPL-3.0)
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 名前の右にタスクの進み具合 "(2/5)" を小さく出す (端末ごとの設定)。見えるのは自分の分と、自分が死んだ後のクルー側の全員の分
// (他人の分は幽霊が役職を見られる設定の時だけ。数字の無い人から陣営が分かってしまうため)。ロビーに戻ったら消す。
// 名前の文字を複製して置き、タスクが配られた時・誰かがタスクを終えた時・自分が死んだ時にだけ書き直す (毎フレームは触らない)
internal static class NameTaskTag
{
    internal const string TagName = "MrpTaskTag";
    private const float Gap = 0.08f;

    public static void RefreshAll()
    {
        foreach (var p in PlayerControl.AllPlayerControls) Refresh(p);
    }

    public static void Refresh(PlayerControl p)
    {
        try { Apply(p); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"task tag: {e.Message}"); }
    }

    private static void Apply(PlayerControl p)
    {
        if (!p || p.cosmetics == null) return;
        var name = p.cosmetics.nameText;
        if (!name) return;
        var tag = name.transform.Find(TagName);
        string text = Text(p);
        if (text == null)
        {
            if (tag) tag.gameObject.SetActive(false);
            return;
        }
        TextMeshPro tmp;
        if (!tag)
        {
            var go = Object.Instantiate(name.gameObject, name.transform);
            go.name = TagName;
            Roles.RoleDisplay.RemoveClonedTags(go);
            go.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
            tmp = go.GetComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.rectTransform.pivot = new Vector2(0f, 0.5f);
            tag = go.transform;
        }
        else tmp = tag.GetComponent<TextMeshPro>();
        tag.gameObject.SetActive(true);
        // 名前の幅の半分だけ右へ (名前は真ん中揃え)
        float half = name.GetPreferredValues(name.text).x * 0.5f;
        tag.localPosition = new Vector3(half + Gap, 0f, 0f);
        tmp.text = text;
        tmp.ForceMeshUpdate(); // 複製した字は元の名前の形を持ったままなので、書いたフレームで作り直す
    }

    // 出す文 (出さない人は null)
    private static string Text(PlayerControl p)
    {
        if (!ClientOptions.TaskOnName.Value || !ShipStatus.Instance || !Options.GameModeSwitch.IsNormal) return null;
        var me = PlayerControl.LocalPlayer;
        var data = p.Data;
        if (!me || data == null || data.Disconnected) return null;
        bool meDead = me.Data != null && me.Data.IsDead;
        if (!p.AmOwner && !(meDead && Roles.RoleSettings.GhostSees)) return null;
        if (!IsCrewSide(data)) return null;
        var tasks = data.Tasks;
        if (tasks == null || tasks.Count == 0) return null;
        int done = 0;
        for (int i = 0; i < tasks.Count; i++)
            if (tasks[i] != null && tasks[i].Complete) done++;
        string color = done == tasks.Count ? "#7CFF7C" : "#FAD934";
        return $"<color={color}>({done}/{tasks.Count})</color>";
    }

    private static bool IsCrewSide(NetworkedPlayerInfo data)
    {
        var mrp = Roles.RoleState.Of(data.PlayerId);
        if (mrp != null) return mrp.Team == Roles.Team.Crew;
        var role = data.Role;
        return role && role.TeamType == RoleTeamTypes.Crewmate;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
internal static class NameTaskTagCompletePatch
{
    public static void Postfix(PlayerControl __instance) => NameTaskTag.Refresh(__instance);
}

// 自分が死んだら、クルー側の全員の進み具合が見えるようになる
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Die))]
internal static class NameTaskTagDiePatch
{
    public static void Postfix(PlayerControl __instance)
    {
        try
        {
            if (__instance && __instance.AmOwner) NameTaskTag.RefreshAll();
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"task tag: {e.Message}"); }
    }
}

// 役職が替わったら出すかどうかが変わる (フリープレイの役職選び・試合中の付け直し)
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SetRole))]
internal static class NameTaskTagRolePatch
{
    public static void Postfix([HarmonyArgument(0)] PlayerControl targetPlayer) => NameTaskTag.Refresh(targetPlayer);
}

// ロビーに戻ったら前の試合の数字を消す (人は残るので名前の横に残ってしまう)
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class NameTaskTagLobbyPatch
{
    public static void Postfix() => NameTaskTag.RefreshAll();
}
