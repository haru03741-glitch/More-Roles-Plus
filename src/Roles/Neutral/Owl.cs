// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Owl.cs, Roles/Ability/OwlAbility.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// フクロウ (第三陣営・キル役): まず「巣作り」でベントを巣にすると、キルできるようになる。死体を「運搬」して巣で隠すと
// 隠した数が増え、設定の数に達すると「特殊停電」で他の人の視界を停電の時のように狭められる。
// 停電の時だけキル・運搬できるようにもできる
public sealed class Owl : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#A96B2E";
    public override Text Name => new("Owl.Name");
    public override string Reading => "ふくろう";
    public override Text Blurb => new("Owl.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("Owl.Nest", "snr_nest"), new("Owl.Transport", "snr_grab"), new("Owl.Blackout", "snr_blackout") };
    public override bool IsKiller => true;

    public static readonly FloatOpt KillCooldown = new("Owl.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt Vent = new("Owl.CanVent", false);
    public static readonly IntOpt BlackoutBodies = new("Owl.BlackoutBodies", 1, 0, 15);
    public static readonly FloatOpt BlackoutCooldown = new("Owl.BlackoutCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt BlackoutTime = new("Owl.BlackoutTime", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt KillOutsideBlackout = new("Owl.KillOutsideBlackout", true);
    public static readonly BoolOpt TransportOutsideBlackout = new("Owl.TransportOutsideBlackout", true);

    public override bool? CanVent => Vent;
    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    private static readonly Vector3 Hidden = new(9999f, 9999f, 0f);

    private int _nest = -1;      // 巣のベントの番号
    private int _bodies;         // 巣に隠した死体の数
    private byte _carry = 255;   // 運んでいる死体の持ち主
    private bool _blackout;
    private float _contraction = 1f;
    private System.Func<float, float> _filter;
    private Ability _transport;

    private bool CanTransport => TransportOutsideBlackout || PortKit.LightsOut();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Owl.Nest"), "snr_nest")
        {
            MaxUses = () => 1,
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (_nest >= 0) return false;
                var v = PortKit.NearestVent(PlayerControl.LocalPlayer.GetTruePosition(), 1f);
                if (!v) return false;
                NestCall.Send(v.Id);
                SetNest(Player, v.Id);
                return true;
            },
        });
        _transport = AddAbility(new Ability(new Text("Owl.Transport"), "snr_grab")
        {
            CooldownAtStart = false,
            Duration = () => 3600f,
            Cancellable = true,
            CancelLabel = new Text("Owl.Drop"),
            OnUse = () =>
            {
                if (_nest < 0 || !CanTransport || !Player.CanMove) return false;
                var body = FreeBody();
                if (!body) return false;
                CarryCall.Send(body.ParentId);
                SetCarry(Player, body.ParentId);
                return true;
            },
            OnEnd = DropHere,
            OnCancel = DropHere,
        });
        AddAbility(new Ability(new Text("Owl.Blackout"), "snr_blackout")
        {
            Cooldown = () => BlackoutCooldown,
            Duration = () => BlackoutTime,
            OnUse = () =>
            {
                if (_nest < 0 || _bodies < BlackoutBodies) return false;
                BlackoutCall.Send(true);
                SetBlackout(Player, true);
                return true;
            },
            OnEnd = () =>
            {
                BlackoutCall.Send(false);
                SetBlackout(Player, false);
            },
        });
        _filter = Filter;
        SnrKit.VisionFilters.Add(_filter);
        Lifespan.OnRelease(() => SnrKit.VisionFilters.Remove(_filter));
    }

    // 自分の画面で: 特殊停電の間は (インポスターの視界でなければ) 停電の時の広さへ縮める
    private float Filter(float radius)
    {
        if (!_blackout && _contraction >= 1f) return radius;
        var lp = PlayerControl.LocalPlayer;
        var ship = ShipStatus.Instance;
        if (!lp || lp.PlayerId == PlayerId || !ship || PortKit.IsImpostor(lp)) return radius;
        float crew = GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
        return Mathf.Min(radius, Mathf.Lerp(ship.MinLightRadius, ship.MaxLightRadius, _contraction) * crew);
    }

    private DeadBody FreeBody()
    {
        DeadBody best = null;
        float bestD = 2f;
        Vector2 me = Player.transform.position;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || b.Reported || IsCarried(b.ParentId)) continue;
            float d = Vector2.Distance(me, b.transform.position);
            if (d <= bestD) { best = b; bestD = d; }
        }
        return best;
    }

    private static bool IsCarried(byte id)
    {
        foreach (var r in RoleState.All) if (r is Owl o && o._carry == id) return true;
        return false;
    }

    // 運搬を終える: 巣のそばなら死体を隠して数を増やす
    private void DropHere()
    {
        if (_carry == 255) return;
        bool hide = AtNest();
        var m = (_carry, hide);
        DropCall.Send(m);
        Drop(Player, m.Item1, hide);
    }

    private bool AtNest()
    {
        var vent = Vent_(_nest);
        if (!vent || !Player.Collider) return false;
        Vector2 center = Player.Collider.bounds.center;
        Vector2 at = vent.transform.position;
        return Vector2.Distance(center, at) <= vent.UsableDistance
            && !PhysicsHelpers.AnythingBetween(Player.Collider, center, at, Constants.ShipOnlyMask, false);
    }

    private static Vent Vent_(int id)
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllVents == null || id < 0) return null;
        foreach (var v in ship.AllVents) if (v && v.Id == id) return v;
        return null;
    }

    private static DeadBody Body(byte id)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>()) if (b && b.ParentId == id) return b;
        return null;
    }

    public override void OnFixedUpdate()
    {
        // 特殊停電の視界は 0.5 秒かけて縮む・戻る
        float target = _blackout ? 0f : 1f;
        if (_contraction != target) _contraction = Mathf.MoveTowards(_contraction, target, Time.fixedDeltaTime * 2f);
        if (IsLocal) HideKill();
        if (_carry == 255) return;
        if (IsLocal && (!CanTransport || !PortKit.Alive(Player)))
        {
            CarryAbort();
            return;
        }
        var b = Body(_carry);
        if (b) b.transform.position = Player.transform.position;
    }

    private void CarryAbort()
    {
        var m = (_carry, false);
        DropCall.Send(m);
        Drop(Player, m.Item1, false);
        if (_transport != null && _transport.InEffect) _transport.EndEffect();
    }

    // キルは巣を作った後だけ。停電の時だけの設定ならその時だけ
    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Killer == e.Target) return;
        if (_nest < 0) e.Cancel("owl has no nest");
        else if (!KillOutsideBlackout && !PortKit.LightsOut()) e.Cancel("owl kills only in blackout");
    }

    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        if (_blackout)
        {
            BlackoutCall.Send(false);
            SetBlackout(Player, false);
        }
    }

    private void OnMeetingEnd(MeetingEndEvent e) => _carry = 255;

    // 巣が無い間・停電の時だけの設定で停電でない間はキルボタンを隠す
    private void HideKill()
    {
        var hud = Vanilla.Hud;
        bool can = _nest >= 0 && (KillOutsideBlackout || PortKit.LightsOut());
        if (!can && hud && hud.KillButton && hud.KillButton.gameObject.activeSelf) hud.KillButton.gameObject.SetActive(false);
    }

    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId == PlayerId && _nest >= 0 ? $"<color=#A96B2E>({_bodies})</color>" : null;

    private static readonly RemoteCall<int> NestCall = new("Owl.Nest", Route.Anyone,
        (w, id) => w.WritePacked(id), r => r.ReadPackedInt32(), (sender, id) => SetNest(sender, id));
    private static readonly RemoteCall<byte> CarryCall = new("Owl.Carry", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => SetCarry(sender, id));
    private static readonly RemoteCall<(byte, bool)> DropCall = new("Owl.Drop", Route.Anyone,
        (w, m) => { w.Write(m.Item1); w.Write(m.Item2); }, r => (r.ReadByte(), r.ReadBoolean()), (sender, m) => Drop(sender, m.Item1, m.Item2));
    private static readonly RemoteCall<bool> BlackoutCall = new("Owl.Blackout", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(), (sender, on) => SetBlackout(sender, on));

    private static void SetNest(PlayerControl p, int id)
    {
        if (RoleState.Of(p) is Owl o && o._nest < 0) o._nest = id;
    }

    private static void SetCarry(PlayerControl p, byte id)
    {
        if (RoleState.Of(p) is Owl o) o._carry = id;
    }

    private static void Drop(PlayerControl p, byte id, bool hide)
    {
        if (RoleState.Of(p) is not Owl o) return;
        o._carry = 255;
        if (!hide) return;
        var b = Body(id);
        if (b) b.transform.position = Hidden;
        o._bodies++;
    }

    private static void SetBlackout(PlayerControl p, bool on)
    {
        if (RoleState.Of(p) is Owl o) o._blackout = on;
    }
}
