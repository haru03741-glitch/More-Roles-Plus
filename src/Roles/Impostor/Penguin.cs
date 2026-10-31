// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Penguin.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ペンギン (インポスター): 「拉致」で近くの人を掴んで運ぶ。キルボタンか、時間切れで相手をキルする
public sealed class Penguin : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Penguin.Name");
    public override string Reading => "ぺんぎん";
    public override Text Blurb => new("Penguin.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Penguin.Grab", "snr_penguin") };
    public static readonly FloatOpt GrabCooldown = new("Penguin.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt GrabDuration = new("Penguin.Duration", 5f, 1f, 30f, 0.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Penguin.Grab"), "snr_penguin")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => GrabCooldown,
            Duration = () => GrabDuration,
            CanTarget = (_, t) => !PortKit.IsImpostor(t),
            OnUsed = (user, target) => Grab(user.PlayerId, target.PlayerId),
            OnEnd = () =>
            {
                var t = HeldId;
                Release();
                if (t != 255) KillCall.Send(t);
                if (t != 255 && AmongUsClient.Instance.AmHost) PortKit.HostKill(Player, PortKit.ById(t));
            },
        });
        Lifespan.OnRelease(Release);
    }

    private static readonly RemoteCall<byte> KillCall = new("Penguin.Kill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Penguin && !sender.AmOwner && PortKit.Alive(sender)) PortKit.HostKill(sender, PortKit.ById(id)); });

    // 掴んでいる人 → 掴まれた人 (全員の端末で持つ)
    private static readonly Dictionary<byte, byte> Held = new();

    private static readonly RemoteCall<byte> GrabCall = new("Penguin.Grab", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Penguin) Grab(sender.PlayerId, id); });

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
