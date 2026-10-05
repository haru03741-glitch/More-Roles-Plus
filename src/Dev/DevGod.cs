using System.Collections.Generic;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 開発者用: 全員の役職を自分の画面にだけ出す (名前の上と会議の名札)。他の人には何も送らない。
// 名前の上の文字は切り替えた時と役職が配られた時に置くだけで、毎フレームの処理は無い。
internal static class DevGod
{
    public static bool On { get; private set; }

    private static readonly List<GameObject> Tags = new();

    public static void Set(bool on)
    {
        On = on;
        Refresh();
    }

    // 置き直す (切り替え・役職の配り直し・生き返り)
    public static void Refresh()
    {
        Clear();
        if (!On || !GameData.Instance) return;
        var lp = PlayerControl.LocalPlayer;
        foreach (var d in GameData.Instance.AllPlayers)
        {
            if (d == null || !d.Object || d.Object == lp) continue;
            string label = Label(d.PlayerId);
            if (label != null) AddTag(d.Object, label);
        }
    }

    public static void Clear()
    {
        foreach (var go in Tags)
            if (go) Object.Destroy(go);
        Tags.Clear();
    }

    // MRP の役職があればその名前、無ければ本編の役職名
    public static string Label(byte pid)
    {
        var role = RoleState.Of(pid);
        if (role != null) return role.ColoredName;
        var d = GameData.Instance ? GameData.Instance.GetPlayerById(pid) : null;
        var vanilla = d?.Role;
        if (!vanilla) return null;
        return $"<color=#{ColorUtility.ToHtmlStringRGB(vanilla.TeamColor)}>{vanilla.NiceName}</color>";
    }

    private static void AddTag(PlayerControl p, string label)
    {
        var name = p.cosmetics.nameText;
        var go = Object.Instantiate(name.gameObject, name.transform);
        go.name = "MrpDevTag";
        for (int i = go.transform.childCount - 1; i >= 0; i--)
            Object.Destroy(go.transform.GetChild(i).gameObject);
        go.transform.localPosition = new Vector3(0f, 0.22f, 0f);
        go.transform.localScale = new Vector3(0.75f, 0.75f, 1f);
        go.GetComponent<TextMeshPro>().text = label;
        Tags.Add(go);
    }
}
