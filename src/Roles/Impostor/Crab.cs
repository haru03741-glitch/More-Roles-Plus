// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Crab.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// カニ (インポスター): 「カニ」でしばらく全員が横にしか歩けなくなる
public sealed class Crab : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Crab.Name");
    public override string Reading => "かに";
    public override Text Blurb => new("Crab.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Crab.Crab", "snr_crab") };
    public static readonly FloatOpt CrabCooldown = new("Crab.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt CrabDuration = new("Crab.Duration", 10f, 2.5f, 60f, 2.5f, "s");

    private static float _until;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Crab.Crab"), "snr_crab")
        {
            Cooldown = () => CrabCooldown,
            OnUse = () => { CrabCall.Send(0); _until = Time.time + CrabDuration; return true; },
        });
    }

    private static readonly RemoteCall<byte> CrabCall = new("Crab.Crab", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Crab) _until = Time.time + CrabDuration; });

    // 全員の端末で (カニの役職の分だけ): 自分の縦の動きを止める
    public override void OnFixedUpdate()
    {
        if (Time.time >= _until || MeetingHud.Instance) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !lp.MyPhysics || !lp.MyPhysics.body) return;
        var v = lp.MyPhysics.body.velocity;
        if (v.y != 0f) lp.MyPhysics.body.velocity = new Vector2(v.x, 0f);
    }
}
