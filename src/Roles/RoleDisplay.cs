using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職を画面に出す: イントロの役職名と説明・自分の名前の上の役職名・タスク欄の説明・会議の名札
internal static class RoleDisplay
{
    private static readonly List<GameObject> Tags = new();
    private static ImportantTextTask _header;

    public static void OnAssigned(RoleBase role)
    {
        if (!role.IsLocal) return;
        try { AddNameTag(role); }
        catch (Exception e) { Plugin.Logger.LogWarning($"name tag: {e.Message}"); }
        try { EnsureTaskHeader(role.Player); }
        catch (Exception e) { Plugin.Logger.LogWarning($"task header: {e.Message}"); }
    }

    public static void Clear()
    {
        foreach (var go in Tags)
        {
            if (go) UnityEngine.Object.Destroy(go);
        }
        Tags.Clear();
        if (_header)
        {
            // タスク欄の一覧に残すと本編が消えた物の文字を読みに行くので、先に外す
            var lp = PlayerControl.LocalPlayer;
            if (lp && lp.myTasks != null) lp.myTasks.Remove(_header.Cast<PlayerTask>());
            UnityEngine.Object.Destroy(_header.gameObject);
        }
        _header = null;
    }

    // タスク欄の先頭に役職の説明を入れる。本編がタスク欄の文字を組み立てる時にそのまま並ぶので、毎フレームの処理は増えない。
    // 本編はタスクを配り直すたびに一覧を作り直すので、配り終わった後にも呼ぶ (SetTasksPatch)
    public static void EnsureTaskHeader(PlayerControl p)
    {
        var role = RoleState.Local;
        if (role == null || !p || !p.AmOwner || p.myTasks == null) return;
        if (!_header)
        {
            var go = new GameObject("MrpRoleTask");
            go.transform.SetParent(p.transform, false);
            _header = go.AddComponent<ImportantTextTask>();
        }
        var task = _header.Cast<PlayerTask>();
        if (!p.myTasks.Contains(task)) p.myTasks.Insert(0, task);
        string text = $"<color={role.Color}>{role.Name}: {role.Description}";
        if (role.Team == Team.Neutral) text += "\n" + new Text("タスクは勝敗に関係ありません", "Your tasks do not count");
        _header.Text = text + "</color>";
    }

    public static void DetachTaskHeader(PlayerControl p)
    {
        if (_header && p.myTasks != null) p.myTasks.Remove(_header.Cast<PlayerTask>());
    }

    // 会議の名札に役職名を出す。自分の分と、設定で許された分 (死んだ後は全員・インポスター同士)
    public static void AddMeetingTag(PlayerVoteArea pva)
    {
        if (!pva) return;
        byte pid = pva.PlayerId.Value;
        var role = RoleState.Of(pid);
        var lp = PlayerControl.LocalPlayer;
        if (role == null || !lp || lp.Data == null) return;
        bool show = pid == lp.PlayerId
                    || (lp.Data.IsDead && RoleSettings.GhostsSeeRoles)
                    || (role.Team == Team.Impostor && RoleSettings.ImpostorsSeeRoles && lp.Data.Role && lp.Data.Role.IsImpostor);
        if (!show) return;

        var name = pva.NameText;
        var go = UnityEngine.Object.Instantiate(name.gameObject, name.transform.parent);
        go.name = "MrpMeetingRole";
        // 名前の下には本編の色の名前があるので、名前の上に小さく出す
        go.transform.localPosition = name.transform.localPosition + new Vector3(0f, 0.17f, 0f);
        go.transform.localScale = name.transform.localScale * 0.6f;
        go.GetComponent<TextMeshPro>().text = role.ColoredName;
    }

    // 名前の文字を複製して、名前の少し上に役職名を出す (本編は複製した方を書き換えないので一度置けば残る)
    private static void AddNameTag(RoleBase role)
    {
        var name = role.Player.cosmetics.nameText;
        var go = UnityEngine.Object.Instantiate(name.gameObject, name.transform);
        go.name = "MrpRoleTag";
        // 前の役職名 (Destroy はフレームの終わりまで残る) まで複製されているので外す
        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            var child = go.transform.GetChild(i).gameObject;
            if (child.name == "MrpRoleTag")
            {
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }
        go.transform.localPosition = new Vector3(0f, 0.22f, 0f);
        go.transform.localScale = new Vector3(0.75f, 0.75f, 1f);
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.text = role.ColoredName;
        Tags.Add(go);
    }

    public static void ApplyIntro(IntroCutscene intro)
    {
        var role = RoleState.Local;
        if (role == null || !intro) return;
        var color = ParseColor(role.Color);
        intro.YouAreText.color = color;
        intro.RoleText.text = role.Name;
        intro.RoleText.color = color;
        intro.RoleBlurbText.text = role.Blurb;
        intro.RoleBlurbText.color = color;
    }

    internal static Color ParseColor(string hex)
    {
        if (hex != null && hex.StartsWith('#') && hex.Length >= 7
            && uint.TryParse(hex.AsSpan(1, 6), System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return new Color(((v >> 16) & 0xff) / 255f, ((v >> 8) & 0xff) / 255f, (v & 0xff) / 255f, 1f);
        return Color.white;
    }
}

// イントロで役職を見せる所 (コルーチン ShowRole の中身)。本編が役職名を書いた後に上書きする
[HarmonyPatch]
internal static class IntroShowRolePatch
{
    // コルーチンが持っている IntroCutscene (<>4__this)。IntroCutscene.Instance はこの時点で空のことがある
    private static PropertyInfo _owner;

    public static MethodBase TargetMethod()
    {
        var t = typeof(IntroCutscene).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .First(n => n.Name.Contains("ShowRole"));
        _owner = AccessTools.Property(t, "__4__this");
        return AccessTools.Method(t, "MoveNext");
    }

    public static void Postfix(object __instance)
    {
        if (RoleState.Local == null || _owner == null) return;
        RoleDisplay.ApplyIntro(_owner.GetValue(__instance) as IntroCutscene);
    }
}

// タスクが配り直された後 (コルーチン CoSetTasks が終わった時) に、タスク欄の説明を入れ直す
[HarmonyPatch]
internal static class SetTasksPatch
{
    private static PropertyInfo _owner;

    public static MethodBase TargetMethod()
    {
        var t = typeof(PlayerControl).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .First(n => n.Name.Contains("CoSetTasks"));
        _owner = AccessTools.Property(t, "__4__this");
        return AccessTools.Method(t, "MoveNext");
    }

    // 本編は配り直す時に一覧のタスクを全部破棄するので、その間は説明を一覧から外しておく
    // (破棄は予約でフレームの終わりまで生きているため、入れたままだと同じ物を入れ直して後で消える)
    public static void Prefix(object __instance)
    {
        if (RoleState.Local == null || _owner == null) return;
        var p = _owner.GetValue(__instance) as PlayerControl;
        if (p && p.AmOwner) RoleDisplay.DetachTaskHeader(p);
    }

    public static void Postfix(object __instance, bool __result)
    {
        if (__result || RoleState.Local == null || _owner == null) return;
        var p = _owner.GetValue(__instance) as PlayerControl;
        if (!p || !p.AmOwner) return;
        try { RoleDisplay.EnsureTaskHeader(p); }
        catch (Exception e) { Plugin.Logger.LogWarning($"task header: {e.Message}"); }
    }
}

// 会議が始まった時に、並んだ名札それぞれへ (名札を作る CreateButton は本編の中で埋め込まれていて後置が呼ばれない)
[HarmonyPatch(typeof(MeetingHud), "Start")]
internal static class MeetingTagPatch
{
    public static void Postfix(MeetingHud __instance)
    {
        if (RoleState.All.Count == 0) return;
        try
        {
            foreach (var pva in __instance.playerStates) RoleDisplay.AddMeetingTag(pva);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"meeting tag: {e.Message}"); }
    }
}
