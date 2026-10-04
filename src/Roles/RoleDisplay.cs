using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職を画面に出す: イントロの役職名と説明・自分の名前の上の役職名
internal static class RoleDisplay
{
    private static readonly List<GameObject> Tags = new();

    public static void OnAssigned(RoleBase role)
    {
        if (!role.IsLocal) return;
        try { AddNameTag(role); }
        catch (Exception e) { Plugin.Logger.LogWarning($"name tag: {e.Message}"); }
    }

    public static void Clear()
    {
        foreach (var go in Tags)
        {
            if (go) UnityEngine.Object.Destroy(go);
        }
        Tags.Clear();
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
