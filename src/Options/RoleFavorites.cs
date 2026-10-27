using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace MoreRolesPlus.Options;

// 役職のお気に入り (自分の手元だけ・部屋には送らない)。役職辞典と設定画面の一覧で絞り込みと印に使う。
// 保存先 = BepInEx/config/MoreRolesPlus/favorites.txt (役職の Id を 1 行に 1 つ)
internal static class RoleFavorites
{
    private static HashSet<string> _ids;

    private static string PathOf => Path.Combine(Paths.ConfigPath, "MoreRolesPlus", "favorites.txt");

    private static HashSet<string> Ids
    {
        get
        {
            if (_ids != null) return _ids;
            _ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(PathOf))
                    foreach (string line in File.ReadAllLines(PathOf))
                        if (line.Trim().Length > 0) _ids.Add(line.Trim());
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"favorites load: {e.Message}"); }
            return _ids;
        }
    }

    public static int Count => Ids.Count;

    public static bool Has(Roles.Assignable a) => a != null && Ids.Contains(a.Id);

    public static void Toggle(Roles.Assignable a)
    {
        if (a == null) return;
        if (!Ids.Remove(a.Id)) Ids.Add(a.Id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf));
            File.WriteAllLines(PathOf, Ids);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"favorites save: {e.Message}"); }
    }
}
