using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 役職の絵。本編の役職は本編の役職アイコン、MRP の役職は本編と同じ描き方 (白の塗り・黒の太い縁・陣営色の差し色) の
// 自前の絵 (Resources/Icons/role_<Id>.png)。本編の役職の型に絵が付いていないクルーメイトは自前の絵、
// インポスターは本編の役職アイコンの絵の中から名前で探す。
internal static class RoleArt
{
    private static HashSet<string> _own;   // 自前の絵がある役職の Id (無い役職で読みに行って毎回エラーを出さないため)
    private static readonly Dictionary<RoleTypes, Sprite> VanillaIcons = new();
    private static Sprite _impostor;
    private static int _scans; // 見つからない版で、開くたびに全部の絵を走査し続けないため

    public static Sprite Of(Assignable a)
    {
        if (a == null) return null;
        if (a is not Roles.Builtin.VanillaRole v) return Own(a.Id);
        // 本編の役職の一覧を総なめするので 1 回だけ (無い役職の null も覚える・消えた絵は探し直す)
        if (!VanillaIcons.TryGetValue(v.Type, out var s) || (!ReferenceEquals(s, null) && !s))
        {
            s = Menu.MatchInfoExtension.Base(v.Type)?.RoleIconSolid;
            VanillaIcons[v.Type] = s ? s : null;
        }
        if (s) return s;
        return v.Type switch
        {
            RoleTypes.Crewmate => Own("Crewmate"),
            RoleTypes.Impostor => Impostor(),
            _ => null,
        };
    }

    public static Sprite Own(string id)
    {
        if (id == null) return null;
        if (_own == null)
        {
            _own = new HashSet<string>();
            const string prefix = "MoreRolesPlus.Resources.Icons.role_";
            foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith(prefix) && n.EndsWith(".png")) _own.Add(n.Substring(prefix.Length, n.Length - prefix.Length - 4));
        }
        return _own.Contains(id) ? ButtonIcons.Get("role_" + id) : null;
    }

    private static Sprite Impostor()
    {
        if (_impostor || _scans >= 3) return _impostor;
        _scans++;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var sp = o.TryCast<Sprite>();
            if (sp && sp.name == "ICON-Impostor")
            {
                _impostor = sp;
                _scans = 0;
                break;
            }
        }
        return _impostor;
    }
}
