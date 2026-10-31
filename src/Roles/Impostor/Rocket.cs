// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Rocket.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ロケット (インポスター): 「掴む」で近くの人を掴んで運び、「打ち上げる」で空へ打ち上げて倒す (死体は残らない)
public sealed class Rocket : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Rocket.Name");
    public override string Reading => "ろけっと";
    public override Text Blurb => new("Rocket.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Rocket.Grab", "snr_grab"), new("Rocket.Launch", "snr_rocket") };
    public static readonly FloatOpt GrabCooldown = new("Rocket.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt GrabDuration = new("Rocket.Duration", 10f, 1f, 30f, 0.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Rocket.Grab"), "snr_grab")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => GrabCooldown,
            Duration = () => GrabDuration,
            CanTarget = (_, t) => !PortKit.IsImpostor(t),
            OnUsed = (user, target) => Grab(user.PlayerId, target.PlayerId),
            OnEnd = Release,
        });
        AddAbility(new Ability(new Text("Rocket.Launch"), "snr_rocket")
        {
            OnUse = () =>
            {
                var t = HeldId;
                if (t == 255) return false;
                Release();
                LaunchCall.Send(t);
                if (AmongUsClient.Instance.AmHost) PortKit.HostVanish(Player, PortKit.ById(t));
                return true;
            },
        });
        Lifespan.OnRelease(Release);
    }

    private static readonly RemoteCall<byte> LaunchCall = new("Rocket.Launch", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) =>
        {
            if (RoleState.Of(sender) is not Rocket) return;
            PortKit.Flash(new Color(1f, 0.6f, 0.1f), 0.4f);
            if (AmongUsClient.Instance.AmHost && PortKit.Alive(sender)) PortKit.HostVanish(sender, PortKit.ById(id));
        });

    // 掴んでいる人 → 掴まれた人 (全員の端末で持つ)
    private static readonly Dictionary<byte, byte> Held = new();

    private static readonly RemoteCall<byte> GrabCall = new("Rocket.Grab", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Rocket) Grab(sender.PlayerId, id); });

    private static void Grab(byte holder, byte target)
    {
        if (target == 255) Held.Remove(holder);
        else Held[holder] = target;
    }

    private void Release()
    {
        if (!Held.ContainsKey(PlayerId)) return;
        GrabCall.Send(255);
        Grab(PlayerId, 255);
    }

    private byte HeldId => Held.TryGetValue(PlayerId, out var t) ? t : (byte)255;

    // 掴まれた本人の端末で: 掴んだ人の所へ体を寄せる
    public override void OnFixedUpdate()
    {
        var lp = PlayerControl.LocalPlayer;
        if (HeldId != lp.PlayerId) return;
        if (MeetingHud.Instance || !PortKit.Alive(Player) || !PortKit.Alive(lp)) return;
        var at = (Vector2)Player.transform.position;
        lp.MyPhysics.body.velocity = Vector2.zero;
        lp.MyPhysics.body.position = at;
        lp.transform.position = new Vector3(at.x, at.y, lp.transform.position.z);
    }

    private void OnDeath(PlayerMurderedEvent e)
    {
        if (e.Target.PlayerId == PlayerId || e.Target.PlayerId == HeldId) Grab(PlayerId, 255);
    }

    private void OnMeeting(MeetingStartEvent e) => Held.Clear();
}
