// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/WiseMan.cs, Roles/Ability/WiseManAbility.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 賢者 (クルー): 「聖なるバリア」でしばらく動けなくなる代わりに、キルを跳ね返す (キルした人が死ぬ)
public sealed class WiseMan : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#85D1D0";
    public override Text Name => new("WiseMan.Name");
    public override string Reading => "けんじゃ";
    public override Text Blurb => new("WiseMan.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("WiseMan.Barrier", "snr_barrier") };

    public static readonly FloatOpt BarrierCooldown = new("WiseMan.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt BarrierDuration = new("WiseMan.Duration", 10f, 2.5f, 30f, 2.5f, "s");
    public static readonly BoolOpt CanWalk = new("WiseMan.CanWalk", false);

    private bool _guard;
    internal bool Guarding => _guard;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("WiseMan.Barrier"), "snr_barrier")
        {
            Cooldown = () => BarrierCooldown,
            Duration = () => BarrierDuration,
            OnUse = () => { Set(true); return true; },
            OnEnd = () => Set(false),
        });
    }

    // 本人の端末から全員へ: バリアを張った / 解いた (キルを跳ね返すかはホストが見る)
    private void Set(bool on)
    {
        _guard = on;
        GuardCall.Send(on);
    }

    private static readonly RemoteCall<bool> GuardCall = new("WiseMan.Guard", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) =>
        {
            if (RoleState.Of(sender) is WiseMan w) w._guard = on;
        });

    private void OnMeetingStart(MeetingStartEvent e) => _guard = false;

    public override void ModifySpeed(ref float speed)
    {
        if (_guard && !CanWalk) speed = 0f;
    }

    [HostOnly, OnlyMine]
    private void OnTargeted(BeforeMurderEvent e)
    {
        if (e.Canceled || !_guard || !e.Killer || e.Killer.PlayerId == PlayerId) return;
        e.Cancel("wiseman");
        var killer = e.Killer;
        // キルの判定の中で別のキルを起こさないよう、少し後で跳ね返す
        PortKit.After(0.05f, () => PortKit.HostKill(Player, killer));
    }
}
