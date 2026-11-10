// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Visor.cs, Scripts/Abilities/VisorAbility.cs (VisorModifierU; role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to Extreme Roles https://github.com/yukieiji/ExtremeRoles
using System.Linq;
using HarmonyLib;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// バイザー (アドオン・インポスター以外): 普通のマップを開くと、いつでもアドミンが見える (死体も数える)。
// 設定で、会議中も見える・マップを開いたまま動ける
public sealed class Visor : AddonBase
{
    public override string Color => "#1D3091";
    public override Text Name => new("Visor.Name");
    public override string Reading => "ばいざー";
    public override Text Blurb => new("Visor.Blurb");
    public override string Origin => "Uchu, ER";

    protected internal override bool DefaultOnImpostor => false;
    public override bool CanAttach(Team team, RoleBase role) => team != Team.Impostor && base.CanAttach(team, role);

    public static readonly BoolOpt CanMoveWithMap = new("Visor.CanMoveWithMapWatching", false);
    public static readonly BoolOpt CanUseOnMeeting = new("Visor.CanUseAdminOnMeeting", false);

    internal static bool LocalHas()
    {
        var lp = PlayerControl.LocalPlayer;
        return lp && lp.Data != null && !lp.Data.IsDead && RoleState.AddonsOf(lp.PlayerId).Any(a => a is Visor);
    }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.ShowNormalMap))]
internal static class VisorMapPatch
{
    // アドミンを出す共通の部分 (役職版のバイザーも使う)
    internal static void ShowAdmin(MapBehaviour map, bool canMove)
    {
        var overlay = map.countOverlay;
        if (!overlay) return;
        overlay.gameObject.SetActive(true);
        overlay.SetOptions(true, true);
        if (map.taskOverlay) map.taskOverlay.Hide();
        map.countOverlayAllowsMovement = canMove;
        if (!canMove && PlayerControl.LocalPlayer.NetTransform) PlayerControl.LocalPlayer.NetTransform.Halt();
    }

    public static void Postfix(MapBehaviour __instance)
    {
        try
        {
            if (!Visor.LocalHas()) return;
            if (MeetingHud.Instance && !Visor.CanUseOnMeeting) return;
            ShowAdmin(__instance, Visor.CanMoveWithMap);
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"visor map: {e.Message}"); }
    }
}
