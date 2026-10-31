using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定画面の「役職」タブで使う陣営の分け方・名前・色。タブの中身は RolePage。
internal static class RoleMenu
{
    // 陣営の札の番号: 0〜3 = Team、4 = アドオン
    internal const int AddonKind = 4;
    private static readonly Color AddonColor = new(0.72f, 0.5f, 0.95f);
    private static readonly Team[] Teams = { Team.Crew, Team.Impostor, Team.Madmate, Team.Neutral };

    public static Text TabName => new("RoleMenu.TabName");

    public static Text DefaultDescription =>
        new Text(System.OperatingSystem.IsAndroid() ? "RoleMenu.DescriptionTouch" : "RoleMenu.Description")
            .With("enabledCount", EnabledCount()).With("count", Registry.Roles.Count);

    private static int EnabledCount()
    {
        int n = 0;
        foreach (var r in Registry.Roles) if (r.Chance.Value > 0) n++;
        return n;
    }

    public static bool HasContent()
    {
        if (Registry.Roles.Count > 0 || Registry.Addons.Count > 0) return true;
        foreach (var t in Teams)
            if (Registry.SectionsOf(TabOf(t)).Count > 0) return true;
        return false;
    }

    private static Tab TabOf(Team t) => t switch { Team.Impostor => Tab.Impostor, Team.Madmate => Tab.Madmate, Team.Neutral => Tab.Neutral, _ => Tab.Crew };

    private static Text TeamName(Team t) => t switch
    {
        Team.Impostor => new Text("RoleMenu.Impostor"),
        Team.Madmate => new Text("RoleMenu.Madmate"),
        Team.Neutral => new Text("RoleMenu.Neutral"),
        _ => new Text("RoleMenu.Crewmate"),
    };

    private static Color TeamColor(Team t) => t switch
    {
        Team.Impostor => new Color(1f, 0.35f, 0.35f),
        Team.Madmate => new Color(0.8f, 0.22f, 0.32f),
        Team.Neutral => new Color(1f, 0.7f, 0.25f),
        _ => new Color(0.55f, 0.85f, 1f),
    };

    internal static Tab KindTab(int k) => k == AddonKind ? Tab.Addon : TabOf((Team)k);
    internal static Text KindName(int k) => k == AddonKind ? new Text("RoleMenu.Addons") : TeamName((Team)k);
    internal static Color KindColor(int k) => k == AddonKind ? AddonColor : TeamColor((Team)k);

    // 札を出すか。中身 (役職・設定) の無い陣営の札は出さない
    internal static bool KindShown(int k) => Registry.SectionsOf(KindTab(k)).Count > 0;
}
