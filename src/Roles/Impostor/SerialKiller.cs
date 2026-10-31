// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/SerialKiller.cs, Roles/Ability/SuicideTimerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// シリアルキラー (インポスター): キルのクールダウンが短い代わりに、最初のキルの後は決まった時間キルしないと自殺する
public sealed class SerialKiller : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("SerialKiller.Name");
    public override string Reading => "しりあるきらー";
    public override Text Blurb => new("SerialKiller.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt KillCooldown = new("SerialKiller.KillCooldown", 20f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt SuicideTime = new("SerialKiller.SuicideTime", 40f, 2.5f, 120f, 2.5f, "s");
    public static readonly BoolOpt ResetOnMeeting = new("SerialKiller.ResetOnMeeting", false);
    public static readonly BoolOpt Vent = new("SerialKiller.CanVent", true);

    private bool _armed;
    private float _left;

    public override bool? CanVent => Vent;

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        _armed = true;
        Restart();
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_armed && ResetOnMeeting) Restart();
    }

    private void Restart()
    {
        _left = SuicideTime;
        StatusEffects.ShowFor("SerialKiller", new Text("SerialKiller.Timer"), Color, _left);
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!_armed || MeetingHud.Instance || ExileController.Instance) return;
        if (!PortKit.Alive(Player))
        {
            _armed = false;
            StatusEffects.Hide("SerialKiller");
            return;
        }
        _left -= Time.fixedDeltaTime;
        if (_left > 0f) return;
        _armed = false;
        StatusEffects.Hide("SerialKiller");
        PortKit.Suicide();
    }
}
