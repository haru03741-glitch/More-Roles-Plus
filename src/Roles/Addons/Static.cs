// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Static.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// スタティック (アドオン): 付いている間、全員のアドミンとバイタルが試合の始めの内容のまま固まる。
// 本人は付いていることに気付かない (設定で、死んだ後に分かる)
public sealed class Static : AddonBase
{
    public override string Color => "#E4FF19";
    public override Text Name => new("Static.Name");
    public override string Reading => "すたてぃっく";
    public override Text Blurb => new("Static.Blurb");
    public override string Origin => "Uchu";

    public static readonly BoolOpt CanSeeAll = new("Static.CanSeeAll", true);

    public override bool HiddenFromSelf => true;
    protected internal override bool SelfSeesWhenDead => CanSeeAll;

    // 全員の端末で: 試合の始めの居場所と生死
    private static readonly List<Vector2> Spots = new();
    private static readonly HashSet<byte> AliveAtStart = new();
    private static object _match;

    public override void OnAssigned()
    {
        if (ReferenceEquals(_match, RoleState.Match)) return;
        _match = RoleState.Match;
        Spots.Clear();
        AliveAtStart.Clear();
        RoleState.Match.OnRelease(() => { Spots.Clear(); AliveAtStart.Clear(); _match = null; });
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || p.Data.Disconnected) continue;
            if (!p.Data.IsDead) { AliveAtStart.Add(p.PlayerId); Spots.Add(p.GetTruePosition()); }
        }
    }

    internal static bool Active => _match != null && RoleState.AllAddons.Any(a => a is Static);

    internal static int CountIn(PlainShipRoom room)
    {
        if (!room || !room.roomArea) return 0;
        int n = 0;
        foreach (var s in Spots) if (room.roomArea.OverlapPoint(s)) n++;
        return n;
    }

    internal static bool WasAlive(byte id) => AliveAtStart.Contains(id);
}

[HarmonyPatch(typeof(MapCountOverlay), nameof(MapCountOverlay.Update))]
internal static class StaticAdminPatch
{
    public static void Postfix(MapCountOverlay __instance)
    {
        if (!Static.Active) return;
        try
        {
            var ship = ShipStatus.Instance;
            if (!ship || __instance.CountAreas == null) return;
            foreach (var area in __instance.CountAreas)
            {
                if (!area) continue;
                ship.FastRooms.TryGetValue(area.RoomType, out var room);
                area.UpdateCount(Static.CountIn(room));
            }
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"static admin: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(VitalsMinigame), nameof(VitalsMinigame.Update))]
internal static class StaticVitalsPatch
{
    public static void Postfix(VitalsMinigame __instance)
    {
        if (!Static.Active || __instance.vitals == null) return;
        try
        {
            foreach (var panel in __instance.vitals)
            {
                if (!panel || panel.PlayerInfo == null) continue;
                if (Static.WasAlive(panel.PlayerInfo.PlayerId))
                {
                    if (panel.IsDead || panel.IsDiscon) { panel.IsDead = false; panel.IsDiscon = false; panel.SetAlive(); }
                }
                else if (!panel.IsDead) panel.SetDead();
            }
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"static vitals: {e.Message}"); }
    }
}
