using HarmonyLib;

namespace MoreRolesPlus.Menu;

// メインメニューに「mod が入っている」印と版を出す
[HarmonyPatch(typeof(VersionShower), nameof(VersionShower.Start))]
internal static class VersionShowerPatch
{
    public static void Postfix(VersionShower __instance)
    {
        Lang.Refresh();
        Net.OptionSync.RestoreOwn();
        Roles.RoleState.Clear(false); // 試合の途中で抜けた時の役職を残さない
        __instance.text.text += $"\n<color=#7FD4FF>More Roles Plus</color> v{Plugin.Version}";
        if (ModManager.InstanceExists) ModManager.Instance.ShowModStamp();
    }
}
