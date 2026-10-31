// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Nun.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;
using BepInEx.Unity.IL2CPP.Utils.Collections;

namespace MoreRolesPlus.Roles.Impostor;

// ぬーん (インポスター・エアシップだけ): 「ぬーん」で昇降機を遠くから動かす (乗っている人ごと運ぶ)
public sealed class Nun : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Nun.Name");
    public override string Reading => "ぬーん";
    public override Text Blurb => new("Nun.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Nun.Move", "snr_lift") };
    public override byte[] OnlyMaps => new byte[] { 4 };

    public static readonly FloatOpt NunCooldown = new("Nun.Cooldown", 30f, 2.5f, 180f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Nun.Move"), "snr_lift")
        {
            Cooldown = () => NunCooldown,
            OnUse = () =>
            {
                var air = ShipStatus.Instance ? ShipStatus.Instance.TryCast<AirshipStatus>() : null;
                if (air == null || !air.GapPlatform || !Player.CanMove) return false;
                var plat = air.GapPlatform;
                var msg = (plat.IsLeft, plat.Target ? plat.Target.PlayerId : (byte)255);
                UseCall.Send(msg);
                Use(msg.Item1, msg.Item2);
                return true;
            },
        });
    }

    private static readonly RemoteCall<(bool left, byte target)> UseCall = new("Nun.Use", Route.Anyone,
        (w, m) => { w.Write(m.left); w.Write(m.target); }, r => (r.ReadBoolean(), r.ReadByte()),
        (sender, m) => { if (RoleState.Of(sender) is Nun) Use(m.left, m.target); });

    // 全員の端末で: 昇降機を動かす
    private static void Use(bool startIsLeft, byte targetId)
    {
        var air = Object.FindObjectOfType<AirshipStatus>();
        if (!air || !air.GapPlatform) return;
        air.GapPlatform.StopAllCoroutines();
        air.GapPlatform.StartCoroutine(Move(air.GapPlatform, startIsLeft, targetId).WrapToIl2Cpp());
    }

    private static System.Collections.IEnumerator Move(MovingPlatformBehaviour platform, bool startIsLeft, byte targetId)
    {
        platform.IsLeft = startIsLeft;
        var target = targetId == 255 ? null : PortKit.ById(targetId);
        bool has = target;
        bool carry = has && Vector3.Distance(target.transform.position, platform.transform.position) <= 1.25f;
        if (has) platform.Target = target;
        if (!platform.Target) platform.Target = PlayerControl.LocalPlayer;
        Vector3 use = !startIsLeft ? platform.LeftUsePosition : platform.RightUsePosition;
        Vector3 dest = !startIsLeft ? platform.LeftPosition : platform.RightPosition;
        Vector3 worldUse = platform.transform.parent.TransformPoint(use);
        Vector3 worldDest = platform.transform.parent.TransformPoint(dest);
        Vector3 offset = Vector3.zero, carryTo = Vector3.zero;
        if (has)
        {
            Restore(target, !carry);
            if (carry)
            {
                target.moveable = false;
                target.NetTransform.SetPaused(true);
                target.SetKinematic(true);
                target.inMovingPlat = true;
                target.ForceKillTimerContinue = true;
                offset = target.transform.position - platform.transform.position;
                carryTo = worldDest + offset;
            }
        }
        if (Constants.ShouldPlaySfx())
        {
            SoundManager.Instance.StopNamedSound("PlatformMoving");
            SoundManager.Instance.PlayDynamicSound("PlatformMoving", platform.MovingSound, true, (DynamicSound.GetDynamicsFunction)platform.SoundDynamics, SoundManager.Instance.SfxChannel);
        }
        platform.IsLeft = !startIsLeft;
        if (carry)
            yield return Effects.All(new Il2CppSystem.Collections.IEnumerator[] {
                Effects.Slide2D(platform.transform, platform.transform.localPosition, dest, target.MyPhysics.Speed),
                Effects.Slide2DWorld(target.transform, target.transform.position, carryTo, target.MyPhysics.Speed) });
        else
            yield return Effects.Slide2D(platform.transform, platform.transform.localPosition, dest, PlayerControl.LocalPlayer.MyPhysics.Speed);
        if (Constants.ShouldPlaySfx()) SoundManager.Instance.StopNamedSound("PlatformMoving");
        if (has)
        {
            if (carry)
            {
                yield return target.MyPhysics.WalkPlayerTo(worldUse);
                yield return Effects.Wait(0.1f);
            }
            if (target) Restore(target, true);
        }
        platform.Target = null;
    }

    private static void Restore(PlayerControl t, bool release)
    {
        if (!t) return;
        t.MyPhysics.enabled = true;
        t.MyPhysics.ResetMoveState();
        t.MyPhysics.ResetAnimState();
        t.MyPhysics.body.velocity = Vector2.zero;
        t.Collider.enabled = true;
        t.NetTransform.enabled = true;
        if (!release) return;
        t.inMovingPlat = false;
        t.onLadder = false;
        t.moveable = true;
        t.ForceKillTimerContinue = false;
        t.NetTransform.SetPaused(false);
        t.SetKinematic(false);
        t.NetTransform.SnapTo(t.transform.position);
        t.SetPetPosition(t.transform.position);
    }
}
