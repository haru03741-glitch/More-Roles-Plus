// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Static.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MoreRolesPlus.Net;
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

    // 全員の端末で: 試合の始めの居場所と生死。
    // 役職を配る時はまだイントロの前で、皆がロビーの位置にいる (Airship は湧き場所も選んでいない) ので、
    // ホストがイントロと湧き場所選びの後に 1 回だけ取って全員へ配る (端末ごとに取ると数がずれる)
    private static readonly List<Vector2> Spots = new();
    private static readonly HashSet<byte> AliveAtStart = new();
    private static object _match;
    private static bool _ready;

    public override void OnAssigned()
    {
        if (ReferenceEquals(_match, RoleState.Match)) return;
        _match = RoleState.Match;
        Spots.Clear();
        AliveAtStart.Clear();
        _ready = false;
        RoleState.Match.OnRelease(() => { Spots.Clear(); AliveAtStart.Clear(); _match = null; _ready = false; });
        if (!AmongUsClient.Instance.AmHost) return;
        float settled = -1f, giveUp = Time.time + 40f;
        UchuKit.Every(() =>
        {
            var lp = PlayerControl.LocalPlayer;
            bool started = ShipStatus.Instance && lp && !IntroCutscene.Instance && !MeetingHud.Instance && lp.CanMove;
            if (!started && Time.time < giveUp) { settled = -1f; return true; }
            if (settled < 0f) { settled = Time.time + 1f; return true; }
            if (Time.time < settled) return true;
            var snap = Snapshot();
            SpotsCall.Send(snap);
            Apply(snap);
            return false;
        });
    }

    // 生きている人ごとに id (1) + x (4) + y (4)
    private static byte[] Snapshot()
    {
        var list = new List<byte>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || p.Data.Disconnected || p.Data.IsDead) continue;
            var at = p.GetTruePosition();
            list.Add(p.PlayerId);
            list.AddRange(System.BitConverter.GetBytes(at.x));
            list.AddRange(System.BitConverter.GetBytes(at.y));
        }
        return list.ToArray();
    }

    private static readonly RemoteCall<byte[]> SpotsCall = new("Static.Spots", Route.HostToAll,
        (w, b) => w.WriteBytesAndSize(b), r => r.ReadBytesAndSize(), (_, b) => Apply(b));

    private static void Apply(byte[] b)
    {
        if (b == null || _match == null) return;
        Spots.Clear();
        AliveAtStart.Clear();
        for (int k = 0; k + 8 < b.Length; k += 9)
        {
            AliveAtStart.Add(b[k]);
            Spots.Add(new Vector2(System.BitConverter.ToSingle(b, k + 1), System.BitConverter.ToSingle(b, k + 5)));
        }
        _ready = true;
    }

    // 取れるまでは本編のまま (イントロ直後の 1 秒ほど)
    internal static bool Active => _ready && _match != null && RoleState.AllAddons.Any(a => a is Static);

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
