// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Pteranodon.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// プテラノドン (クルー・エアシップだけ): 昇降機の乗り場のそばで「飛翔」すると、昇降機を使わずに反対の乗り場まで飛んで渡る
public sealed class Pteranodon : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#11802D";
    public override Text Name => new("Pteranodon.Name");
    public override string Reading => "ぷてらのどん";
    public override Text Blurb => new("Pteranodon.Blurb");
    public override string Origin => "SNR";
    public override byte[] OnlyMaps => new byte[] { 4 };
    public override HelpSection[] HelpSections => new HelpSection[] { new("Pteranodon.Fly", "snr_fly") };

    public static readonly FloatOpt FlyCooldown = new("Pteranodon.Cooldown", 5f, 0f, 120f, 2.5f, "s");

    private const float StartTime = 2f;

    private bool _flying;
    private Vector2 _start, _target, _current;
    private float _timer;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Pteranodon.Fly"), "snr_fly")
        {
            Cooldown = () => FlyCooldown,
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                var air = ShipStatus.Instance ? ShipStatus.Instance.TryCast<AirshipStatus>() : null;
                if (air == null || !air.GapPlatform || !lp.CanMove || _flying) return false;
                var plat = air.GapPlatform;
                if (plat.Target && plat.Target.PlayerId == lp.PlayerId) return false;
                Vector3 left = plat.transform.parent.TransformPoint(plat.LeftUsePosition);
                Vector3 right = plat.transform.parent.TransformPoint(plat.RightUsePosition);
                bool atLeft = Vector3.Distance(left, lp.transform.position) <= 0.9f;
                bool atRight = Vector3.Distance(right, lp.transform.position) <= 0.9f;
                if (!atLeft && !atRight) return false;
                var m = (true, (Vector2)(atLeft ? right : left), (Vector2)lp.transform.position);
                StatusCall.Send(m);
                SetStatus(lp, m.Item1, m.Item2, m.Item3);
                return true;
            },
        });
        Lifespan.OnRelease(() => { if (_flying) Land(_start); });
    }

    // 会議が始まったら飛ぶ前の場所に戻す
    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        if (!_flying) return;
        var m = (false, _start, _start);
        StatusCall.Send(m);
        SetStatus(Player, m.Item1, m.Item2, m.Item3);
    }

    // 全員の端末で: 放物線を描いて飛ぶ
    public override void OnFixedUpdate()
    {
        if (!_flying || !Player) return;
        float dt = Time.fixedDeltaTime;
        _timer -= dt;
        Vector2 pos = _current;
        if (Player.MyPhysics && Player.MyPhysics.Animations != null) Player.MyPhysics.Animations.PlayIdleAnimation();
        if (Player.cosmetics) Player.cosmetics.AnimateSkinIdle();
        float dx = _target.x - _start.x;
        float t = _timer + 0.025f;
        if (t > StartTime / 2f) pos.y += (t - StartTime / 2f) * 4f * dt;
        else pos.y -= (StartTime / 2f - t) * 4f * dt;
        pos.x += dx * dt * 0.5f;
        if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
        if (_timer <= 0f)
        {
            if (IsLocal)
            {
                var m = (false, (Vector2)Player.transform.position, _start);
                StatusCall.Send(m);
                SetStatus(Player, m.Item1, m.Item2, m.Item3);
            }
            return;
        }
        _current = pos;
        Player.transform.position = new Vector3(pos.x, pos.y, pos.y / 1000f);
    }

    private static readonly RemoteCall<(bool on, Vector2 target, Vector2 start)> StatusCall = new("Pteranodon.Status", Route.Anyone,
        (w, m) => { w.Write(m.on); w.Write(m.target.x); w.Write(m.target.y); w.Write(m.start.x); w.Write(m.start.y); },
        r => (r.ReadBoolean(), new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (sender, m) => SetStatus(sender, m.on, m.target, m.start));

    private static void SetStatus(PlayerControl p, bool on, Vector2 target, Vector2 start)
    {
        if (RoleState.Of(p) is not Pteranodon pt) return;
        if (on)
        {
            pt._target = target;
            pt._current = pt._start = start;
            pt._timer = StartTime;
            pt._flying = true;
            p.NetTransform.enabled = false;
            p.Collider.enabled = false;
            p.moveable = false;
        }
        else pt.Land(target);
    }

    private void Land(Vector2 at)
    {
        _flying = false;
        var p = Player;
        if (!p) return;
        p.NetTransform.enabled = true;
        p.Collider.enabled = true;
        p.moveable = true;
        p.transform.position = at;
        if (p.AmOwner) p.NetTransform.RpcSnapTo(at);
        else p.NetTransform.SnapTo(at);
    }
}
