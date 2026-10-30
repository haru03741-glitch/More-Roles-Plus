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
    private static int _scans; // 見つからない版・絵が捨てられた時に、開くたびに全部の絵を走査し続けないため

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

    // 二つ名のマーク (Resources/Icons/mark_<名前>.png)。役職の一覧で名前のすぐ右に付ける
    public static Sprite Mark(Epithet e) => e == Epithet.None ? null : ButtonIcons.Get("mark_" + e);

    // 役職とマップの挿絵 (Resources/Illus/<id>.jpg・設定画面の右の欄の後ろに敷く)。無い役職は null (代わりの絵は出さない)。
    // 大きいので持つのは最近の数枚だけ (捨てる時はテクスチャごと)
    private static HashSet<string> _illus;
    private static readonly List<(string id, Sprite sp)> IllusCache = new();
    private const int IllusKeep = 4;

    public static Sprite Illustration(Assignable a) =>
        a == null ? null : Illustration(a is Roles.Builtin.VanillaRole v ? v.Type.ToString() : a.Id);

    // id = 役職の Id (本編の役職は RoleTypes の名前)・マップは "Map_<番号>"
    public static Sprite Illustration(string id)
    {
        if (_illus == null)
        {
            _illus = new HashSet<string>();
            const string prefix = "MoreRolesPlus.Resources.Illus.";
            foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith(prefix) && n.EndsWith(".jpg")) _illus.Add(n.Substring(prefix.Length, n.Length - prefix.Length - 4));
        }
        if (!_illus.Contains(id)) return null;
        for (int i = 0; i < IllusCache.Count; i++)
        {
            if (IllusCache[i].id != id) continue;
            var hit = IllusCache[i];
            IllusCache.RemoveAt(i);
            if (!hit.sp) break;
            IllusCache.Add(hit);
            return hit.sp;
        }
        var sp = ButtonIcons.Decode("Illus", id, 100f, new Vector2(0.5f, 0.5f), "jpg");
        if (!sp) return null;
        IllusCache.Add((id, sp));
        if (IllusCache.Count > IllusKeep)
        {
            var old = IllusCache[0].sp;
            IllusCache.RemoveAt(0);
            if (old)
            {
                Object.Destroy(old.texture);
                Object.Destroy(old);
            }
        }
        return sp;
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
                break;
            }
        }
        return _impostor;
    }
}
