// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Squid.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// イカ (クルー): 「警戒」している間にキルされると、キルを防いでキルした人の視界をしばらく奪い (墨)、
// 自分はしばらく速く逃げられる
public sealed class Squid : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#B0E0E6";
    public override Text Name => new("Squid.Name");
    public override string Reading => "いか";
    public override Text Blurb => new("Squid.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Squid.Guard", "snr_squid") };
    public static readonly FloatOpt GuardCooldown = new("Squid.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt GuardDuration = new("Squid.Duration", 10f, 2.5f, 30f, 2.5f, "s");
    public static readonly FloatOpt InkTime = new("Squid.InkTime", 5f, 1f, 30f, 1f, "s");
    public static readonly FloatOpt BoostTime = new("Squid.BoostTime", 5f, 1f, 30f, 1f, "s");

    private bool _guard;
    private float _boostUntil;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Squid.Guard"), "snr_squid")
        {
            Cooldown = () => GuardCooldown,
            Duration = () => GuardDuration,
            OnUse = () => { Set(true); return true; },
            OnEnd = () => Set(false),
        });
    }

    private void Set(bool on)
    {
        _guard = on;
        GuardCall.Send(on);
    }

    private static readonly RemoteCall<bool> GuardCall = new("Squid.Guard", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Squid s) s._guard = on; });

    private void OnMeetingStart(MeetingStartEvent e) => _guard = false;

    [HostOnly, OnlyMine]
    private void OnTargeted(BeforeMurderEvent e)
    {
        if (e.Canceled || !_guard || !e.Killer || e.Killer.PlayerId == PlayerId) return;
        e.Cancel("squid");
        _guard = false;
        PortKit.HostBlocked(e.Killer, e.Target);
        InkCall.Send((PlayerId, e.Killer.PlayerId));
        OnInk(PlayerId, e.Killer.PlayerId);
    }

    private static readonly RemoteCall<(byte squid, byte killer)> InkCall = new("Squid.Ink", Route.HostToAll,
        (w, m) => { w.Write(m.squid); w.Write(m.killer); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => OnInk(m.squid, m.killer));

    // 全員の端末で: キルした人なら視界を奪い、イカ本人なら速くする
    private static void OnInk(byte squidId, byte killerId)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        if (lp.PlayerId == killerId)
        {
            VisionEffects.Multiplier = 0.1f;
            PortKit.After(InkTime, () => VisionEffects.Multiplier = 1f);
        }
        if (lp.PlayerId == squidId && RoleState.Local is Squid s)
        {
            s._guard = false;
            s._boostUntil = Time.time + BoostTime;
        }
    }

    public override void ModifySpeed(ref float speed)
    {
        if (Time.time < _boostUntil) speed *= 1.5f;
    }
}
