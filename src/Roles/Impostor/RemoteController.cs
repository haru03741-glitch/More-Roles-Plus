// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/RemoteController.cs, Roles/Ability/RemoteControllerAbility.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// リモコン (インポスター): 「マーク」でクルーを 1 人選び、「操作」の間はその人を自分の操作で歩かせる (その人は自分では動けない)。
// 操作中の「遠隔キル」は、操作している人の近くの人を、操作している人の手で倒す (倒すと操作は終わる)
public sealed class RemoteController : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("RemoteController.Name");
    public override string Reading => "りもこん";
    public override Text Blurb => new("RemoteController.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("RemoteController.Mark", "snr_marker"), new("RemoteController.Operate", "snr_remote"), new("RemoteController.Kill", "snr_kill2") };

    public static readonly FloatOpt MarkCooldown = new("RemoteController.MarkCooldown", 20f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt OperationInfinite = new("RemoteController.OperationInfinite", false);
    public static readonly FloatOpt OperationDuration = new("RemoteController.OperationDuration", 10f, 2.5f, 120f, 2.5f, "s") { When = () => !OperationInfinite };

    // 操作されている人 (全員の端末で同じ)
    internal static readonly HashSet<byte> Controlled = new();

    private byte _target = 255;
    private bool _operating;
    private float _syncTimer;
    private Ability _operate;

    private PlayerControl Target => _target == 255 ? null : PortKit.ById(_target);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("RemoteController.Mark"), "snr_marker")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => MarkCooldown,
            CanTarget = (_, t) => (!IsLocal || _target == 255) && !PortKit.IsImpostor(t) && !IsMarkedByAnyone(t.PlayerId),
            OnUsed = (user, target) =>
            {
                if (RoleState.Of(user) is RemoteController rc) rc._target = target.PlayerId;
            },
        });
        _operate = AddAbility(new Ability(new Text("RemoteController.Operate"), "snr_remote")
        {
            CooldownAtStart = false,
            Duration = () => OperationInfinite ? 3600f : OperationDuration,
            Cancellable = true,
            OnUse = () =>
            {
                var t = Target;
                if (!PortKit.Alive(Player) || _operating || !PortKit.Alive(t)) return false;
                if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
                Follow(t);
                OperateCall.Send((true, _target));
                SetOperating(Player, true, _target);
                return true;
            },
            OnEnd = StopOperation,
            OnCancel = StopOperation,
        });
        AddAbility(new Ability(new Text("RemoteController.Kill"), "snr_kill2")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                var t = Target;
                if (!_operating || !PortKit.Alive(t)) return false;
                var victim = NearestVictim(t);
                if (!victim) return false;
                if (AmongUsClient.Instance.AmHost) HostRemoteKill(Player, victim.PlayerId);
                else KillCall.Send(victim.PlayerId);
                CancelAndClear();
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            if (_target != 255) Controlled.Remove(_target);
            if (IsLocal) Follow(PlayerControl.LocalPlayer);
        });
    }

    private static bool IsMarkedByAnyone(byte id)
    {
        foreach (var r in RoleState.All) if (r is RemoteController rc && rc._target == id) return true;
        return false;
    }

    // 操作している人のキルの届く所で、一番近いインポスター以外の生きている人
    private PlayerControl NearestVictim(PlayerControl from)
    {
        float reach = GameManager.Instance.LogicOptions.GetKillDistance();
        PlayerControl best = null;
        float bestD = reach;
        Vector2 at = from.GetTruePosition();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p == from || p.AmOwner || PortKit.IsImpostor(p)) continue;
            Vector2 pos = p.GetTruePosition();
            float d = Vector2.Distance(at, pos);
            if (d > bestD || PhysicsHelpers.AnythingBetween(at, pos, Constants.ShipAndObjectsMask, false)) continue;
            best = p;
            bestD = d;
        }
        return best;
    }

    private static void Follow(PlayerControl p)
    {
        var cam = Camera.main ? Camera.main.GetComponent<FollowerCamera>() : null;
        if (cam && p) cam.SetTarget(p);
    }

    private void StopOperation()
    {
        if (!_operating) return;
        OperateCall.Send((false, _target));
        SetOperating(Player, false, _target);
        Follow(PlayerControl.LocalPlayer);
        _target = 255;
        TargetCall.Send(255);
        Player.SetKillTimer(GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
    }

    private void CancelAndClear()
    {
        if (_operate != null && _operate.InEffect) _operate.EndEffect();
        else StopOperation();
        _target = 255;
        TargetCall.Send(255);
    }

    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e) => CancelAndClear();

    [LocalOnly]
    private void OnDeath(PlayerMurderedEvent e)
    {
        if (e.Target && e.Target.PlayerId == PlayerId) CancelAndClear();
    }

    // 自分の端末で: 操作中はスティックの向きを操作している人へ送る
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!_operating) return;
        var hud = Vanilla.Hud;
        if ((hud && hud.IsIntroDisplayed) || MeetingHud.Instance || ExileController.Instance) return;
        var t = Target;
        if (!PortKit.Alive(t) || !PortKit.Alive(Player))
        {
            CancelAndClear();
            return;
        }
        if (Minigame.Instance) return;
        Vector2 delta = hud && hud.joystick != null ? hud.joystick.DeltaL : Vector2.zero;
        _syncTimer -= Time.fixedDeltaTime;
        if (_syncTimer > 0f) return;
        _syncTimer = 0.02f;
        VelocityCall.Send((_target, delta));
        if (t.AmOwner && t.MyPhysics) t.MyPhysics.SetNormalizedVelocity(delta);
    }

    private static readonly RemoteCall<(bool on, byte target)> OperateCall = new("RemoteController.Operate", Route.Anyone,
        (w, m) => { w.Write(m.on); w.Write(m.target); }, r => (r.ReadBoolean(), r.ReadByte()), (sender, m) => SetOperating(sender, m.on, m.target));
    private static readonly RemoteCall<byte> TargetCall = new("RemoteController.Target", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => { if (RoleState.Of(sender) is RemoteController rc) rc._target = id; });
    private static readonly RemoteCall<(byte target, Vector2 v)> VelocityCall = new("RemoteController.Velocity", Route.Anyone,
        (w, m) => { w.Write(m.target); w.Write(m.v.x); w.Write(m.v.y); }, r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (_, m) =>
        {
            if (MeetingHud.Instance || ExileController.Instance) return;
            var t = PortKit.ById(m.target);
            if (t && t.AmOwner && t.MyPhysics && Controlled.Contains(m.target)) t.MyPhysics.SetNormalizedVelocity(m.v);
        });
    private static readonly RemoteCall<byte> KillCall = new("RemoteController.Kill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => HostRemoteKill(sender, id));

    private static void SetOperating(PlayerControl p, bool on, byte target)
    {
        if (RoleState.Of(p) is not RemoteController rc) return;
        rc._operating = on;
        if (on) { rc._target = target; Controlled.Add(target); }
        else Controlled.Remove(target);
        var t = PortKit.ById(target);
        if (!on && t && t.AmOwner && t.MyPhysics) t.MyPhysics.SetNormalizedVelocity(Vector2.zero);
    }

    // ホスト: 操作している人の手で倒す
    private static void HostRemoteKill(PlayerControl rcPlayer, byte victimId)
    {
        if (RoleState.Of(rcPlayer) is not RemoteController rc || !rc._operating) return;
        var puppet = rc.Target;
        var victim = PortKit.ById(victimId);
        if (!PortKit.Alive(puppet) || !PortKit.Alive(victim)) return;
        PortKit.HostKill(puppet, victim);
    }

    internal static bool Operating(PlayerControl p) => RoleState.Of(p) is RemoteController { _operating: true };
}

// 操作している人・されている人は自分では動けない
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CanMove), MethodType.Getter)]
internal static class RemoteControllerCanMovePatch
{
    public static void Postfix(PlayerControl __instance, ref bool __result)
    {
        if (!__result || !__instance.AmOwner) return;
        if (RemoteController.Controlled.Count == 0 && RoleState.Local is not RemoteController) return;
        if (RemoteController.Controlled.Contains(__instance.PlayerId) || RemoteController.Operating(__instance)) __result = false;
    }
}
