// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Spelunker.cs, Events/PCEvents/DoorConsoleUseEvent.cs (GPL-3.0)
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// スペランカーとモディファイアスペランカーの共通: 自分の端末だけで、危ない所で確率で死ぬ。
// ベントに近づいた時・はしごに乗った時・ドアを開けた時はその場で、昇降機は向こう岸に着いた時に死ぬ。
// 通信妨害や停電が決まった時間続いても死ぬ
internal sealed class SpelunkerRisk
{
    private bool _ladder, _lift, _dead, _nearVent;
    private float _comms, _lights;
    private Vector2? _deathAt;  // 昇降機で死ぬ所

    // 自分がスペランカーなら、ドアを開けた時の確率 (いなければ -1)
    internal static int LocalDoorChance()
    {
        if (RoleState.Local is Neutral.Spelunker) return Neutral.Spelunker.DoorChance;
        var lp = PlayerControl.LocalPlayer;
        if (lp) foreach (var a in RoleState.AddonsOf(lp.PlayerId)) if (a is Addons.ModifierSpelunker) return Addons.ModifierSpelunker.DoorChance;
        return -1;
    }

    internal static SpelunkerRisk LocalRisk()
    {
        if (RoleState.Local is Neutral.Spelunker s) return s.Risk;
        var lp = PlayerControl.LocalPlayer;
        if (lp) foreach (var a in RoleState.AddonsOf(lp.PlayerId)) if (a is Addons.ModifierSpelunker m) return m.Risk;
        return null;
    }

    // commsLimit / lightsLimit < 0 ならサボタージュでは死なない
    public void Tick(PlayerControl p, float commsLimit, float lightsLimit, int ventChance, int liftChance, int ladderChance)
    {
        var hud = Vanilla.Hud;
        if (_dead || !PortKit.Alive(p) || MeetingHud.Instance || ExileController.Instance || hud && hud.IsIntroDisplayed) return;
        // ベントに近づいた時
        bool near = false;
        var ship = ShipStatus.Instance;
        if (ship && ship.AllVents != null)
            foreach (var v in ship.AllVents)
                if (v && Vector2.Distance(v.transform.position, p.transform.position) <= v.UsableDistance) { near = true; break; }
        if (near && !_nearVent) Roll(ventChance);
        _nearVent = near;
        // はしご
        bool ladder = p.onLadder;
        if (ladder && !_ladder) Roll(ladderChance);
        _ladder = ladder;
        // 昇降機: 乗った時に決め、向こう岸に着いたら死ぬ
        bool lift = p.inMovingPlat;
        if (lift && !_lift && liftChance > 0 && Random.Range(0, 100) < liftChance)
        {
            var plat = Object.FindObjectOfType<MovingPlatformBehaviour>();
            if (plat && plat.transform.parent)
                _deathAt = plat.transform.parent.TransformPoint(!plat.IsLeft ? plat.LeftUsePosition : plat.RightUsePosition);
        }
        _lift = lift;
        if (_deathAt is Vector2 at && Vector2.Distance(at, p.transform.position) < 0.5f)
        {
            _deathAt = null;
            Die();
            return;
        }
        float dt = Time.fixedDeltaTime;
        _comms = Sabotaged(p, TaskTypes.FixComms) ? _comms + dt : 0f;
        _lights = PortKit.LightsOut() ? _lights + dt : 0f;
        if ((commsLimit >= 0f && _comms >= commsLimit) || (lightsLimit >= 0f && _lights >= lightsLimit)) Die();
    }

    public void OnExiled() => _deathAt = null;

    public void Roll(int chance)
    {
        if (!_dead && chance > 0 && Random.Range(0, 100) < chance) Die();
    }

    private void Die()
    {
        _dead = true;
        PortKit.Suicide();
    }

    private static bool Sabotaged(PlayerControl p, TaskTypes type)
    {
        if (p.myTasks == null) return false;
        foreach (var t in p.myTasks)
            if (t && t.TaskType == type) return true;
        return false;
    }
}

// ドアの操作盤で開けた時 (自分の端末)
[HarmonyPatch(typeof(DoorConsole), nameof(DoorConsole.Use))]
internal static class SpelunkerDoorPatch
{
    public static void Postfix(DoorConsole __instance)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null) return;
        int chance = SpelunkerRisk.LocalDoorChance();
        if (chance <= 0) return;
        __instance.CanUse(lp.Data, out bool canUse, out _);
        if (canUse) SpelunkerRisk.LocalRisk()?.Roll(chance);
    }
}
