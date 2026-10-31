// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Ability/MechanicAbility.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// メカニック (ナイス / イビル) の共通: ベントの姿になって、近くのベントを運ぶ。運んでいる間そのベントには入れない
internal sealed class MechanicKit
{
    private static readonly HashSet<int> Moving = new();
    private static readonly Dictionary<byte, MechanicKit> ByOwner = new();

    private readonly RoleBase _role;
    private Vent _vent;
    private Vector3 _restPos; // 動ける間に最後にいた所 (止まった時にそこへ置く)

    internal MechanicKit(RoleBase role) => _role = role;

    internal Ability Make(Text label, string icon, System.Func<float> cooldown, System.Func<float> duration) => new(label, icon)
    {
        Cooldown = cooldown,
        Duration = duration,
        Cancellable = true,
        OnUse = () =>
        {
            var p = _role.Player;
            var v = PortKit.NearestVent(p.GetTruePosition(), 1f);
            if (!v || Moving.Contains(v.Id) || p.inVent) return false;
            var orig = v.transform.position;
            Call.Send((v.Id, true, orig));
            Set(p, v.Id, true, orig);
            return true;
        },
        OnEnd = Stop,
    };

    internal void Stop()
    {
        if (!_vent) return;
        var p = _role.Player;
        Call.Send((_vent.Id, false, _restPos));
        Set(p, _vent.Id, false, _restPos);
    }

    private static readonly RemoteCall<(int vent, bool on, Vector3 pos)> Call = new("Mechanic.Vent", Route.Anyone,
        (w, m) => { w.WritePacked(m.vent); w.Write(m.on); w.Write(m.pos.x); w.Write(m.pos.y); w.Write(m.pos.z); },
        r => (r.ReadPackedInt32(), r.ReadBoolean(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())),
        (sender, m) => Set(sender, m.vent, m.on, m.pos));

    // 全員の端末で: ベントを掴む / 置く
    private static void Set(PlayerControl p, int ventId, bool on, Vector3 pos)
    {
        if (!p || !ShipStatus.Instance) return;
        Vent vent = null;
        foreach (var v in ShipStatus.Instance.AllVents) if (v && v.Id == ventId) { vent = v; break; }
        if (!vent) return;
        var kit = RoleState.Of(p) switch { Crew.NiceMechanic n => n.Kit, Impostor.EvilMechanic e => e.Kit, _ => null };
        if (on)
        {
            Moving.Add(ventId);
            RefreshButtons();
            EjectLocal(vent);
            if (kit != null) { kit._vent = vent; kit._restPos = vent.transform.position; }
            Vector2 at = p.GetTruePosition();
            vent.transform.position = new Vector3(at.x, at.y, p.transform.position.z + 0.0025f);
            Hide(p, true);
            ByOwner[p.PlayerId] = kit;
        }
        else
        {
            Moving.Remove(ventId);
            RefreshButtons();
            if (kit != null) kit._vent = null;
            vent.transform.position = pos;
            Hide(p, false);
            ByOwner.Remove(p.PlayerId);
        }
    }

    // 全員の端末で毎フレーム: 掴んでいる人の所へベントを運ぶ
    internal void Tick()
    {
        var p = _role.Player;
        if (!_vent || !p || p.Data == null || p.Data.IsDead) return;
        Vector2 at = p.transform.position;
        _vent.transform.position = new Vector3(at.x, at.y - 0.2f, p.transform.position.z + 0.0025f);
        Hide(p, true);
        if (p.moveable) _restPos = _vent.transform.position;
    }

    internal static void Hide(PlayerControl p, bool on)
    {
        if (on && p.cosmetics && p.cosmetics.currentBodySprite != null && p.cosmetics.currentBodySprite.BodySprite)
            p.cosmetics.currentBodySprite.BodySprite.material.SetFloat("_Outline", 0f);
        PortKit.SetOpacity(p, on ? 0f : 1f);
    }

    internal static bool IsMoving(Vent v) => v && Moving.Contains(v.Id);

    internal static void Clear() => Moving.Clear();

    private static void EjectLocal(Vent vent)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !lp.MyPhysics || !lp.inVent) return;
        bool inIt = Vent.currentVent ? Vent.currentVent.Id == vent.Id : false;
        if (ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Ventilation, out var sys))
        {
            var vs = sys.TryCast<VentilationSystem>();
            if (vs != null && vs.PlayersInsideVents.TryGetValue(lp.PlayerId, out byte id)) inIt = id == vent.Id;
        }
        if (!inIt) return;
        vent.SetButtons(false);
        lp.MyPhysics.RpcExitVent(vent.Id);
    }

    private static void RefreshButtons()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.inVent && Vent.currentVent) Vent.currentVent.SetButtons(true);
    }

    // ベントの中の矢印: 運ばれているベントへの矢印を止め、掃除中の印を出す
    internal static void ApplyIndicators(Vent src)
    {
        if (!src || src.NearbyVents == null || src.Buttons == null || src.CleaningIndicators == null) return;
        int n = Mathf.Min(src.NearbyVents.Length, Mathf.Min(src.Buttons.Length, src.CleaningIndicators.Length));
        for (int i = 0; i < n; i++)
        {
            if (!IsMoving(src.NearbyVents[i])) continue;
            if (src.Buttons[i]) src.Buttons[i].enabled = false;
            if (src.CleaningIndicators[i]) src.CleaningIndicators[i].SetActive(true);
        }
    }
}

[HarmonyPatch(typeof(Vent), nameof(Vent.SetButtons))]
internal static class MechanicSetButtonsPatch
{
    public static void Postfix(Vent __instance, bool enabled)
    {
        if (enabled) MechanicKit.ApplyIndicators(__instance);
    }
}

[HarmonyPatch(typeof(Vent), nameof(Vent.UpdateArrows))]
internal static class MechanicUpdateArrowsPatch
{
    public static void Postfix(Vent __instance) => MechanicKit.ApplyIndicators(__instance);
}

[HarmonyPatch(typeof(Vent), nameof(Vent.TryMoveToVent))]
internal static class MechanicMovePatch
{
    public static bool Prefix(Vent otherVent, ref string error, ref bool __result)
    {
        if (!MechanicKit.IsMoving(otherVent)) return true;
        error = "Vent is moving";
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.RpcEnterVent))]
internal static class MechanicEnterVentPatch
{
    public static bool Prefix([HarmonyArgument(0)] int id)
    {
        if (!ShipStatus.Instance) return true;
        foreach (var v in ShipStatus.Instance.AllVents)
            if (v && v.Id == id) return !MechanicKit.IsMoving(v);
        return true;
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
internal static class MechanicResetPatch
{
    public static void Prefix() => MechanicKit.Clear();
}
