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
        // 行を足すと画面の下端で切れるので版と同じ行に出す
        if (!Native.MrpNative.Loaded)
            __instance.text.text += " <color=#FF6060>" + new Text("C++ の部品が無いので水と試合開始は使えません", "Native module missing: no water, cannot start games") + "</color>";
        if (ModManager.InstanceExists) ModManager.Instance.ShowModStamp();
    }
}
