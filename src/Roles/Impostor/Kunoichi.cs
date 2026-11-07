// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Kunoichi.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/ykundesu/SuperNewRoles (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// クノイチ (インポスター): 「装備」でクナイを持つ (持っている間は本編のキルは無い)。クナイはマウスの向きに投げ、
// 仲間以外に当たるとその人の当たった数が増え、決めた数で倒す。倒した死体にはクナイが残る。
// 止まっていると透明になれる (動くと解ける)。会議で全部解ける
public sealed class Kunoichi : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Kunoichi.Name");
    public override string Reading => "くのいち";
    public override Text Blurb => new("Kunoichi.Blurb");
    public override string Origin => "Uchu, SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("Kunoichi.Equip", "kunoichi_equip"), new("Kunoichi.Throw", "kunoichi_kunai"), new("Kunoichi.Hide", "kunoichi_hide") };

    public static readonly FloatOpt ThrowCooldown = new("Kunoichi.ThrowCooldown", 0.5f, 0f, 10f, 0.25f, "s");
    public static readonly FloatOpt KunaiSpeed = new("Kunoichi.KunaiSpeed", 3f, 0.5f, 10f, 0.5f, "x");
    public static readonly BoolOpt WallStops = new("Kunoichi.WallStops", true);
    public static readonly IntOpt HitsToKill = new("Kunoichi.HitsToKill", 10, 1, 30);
    public static readonly BoolOpt FriendlyFire = new("Kunoichi.FriendlyFire", false);
    public static readonly BoolOpt KunaiOnBody = new("Kunoichi.KunaiOnBody", true);
    public static readonly BoolOpt ResetHitsOnMeeting = new("Kunoichi.ResetHitsOnMeeting", false);
    public static readonly BoolOpt CanHide = new("Kunoichi.CanHide", true);
    public static readonly FloatOpt HideWait = new("Kunoichi.HideWait", 3f, 0.5f, 10f, 0.5f, "s") { When = () => CanHide };
    public static readonly BoolOpt HideButton = new("Kunoichi.HideButton", true) { When = () => CanHide };
    public static readonly BoolOpt ThrowWhileHidden = new("Kunoichi.ThrowWhileHidden", true) { When = () => CanHide };

    private const float MaxTravel = 30f, HitLength = 1.25f, HitWidth = 0.4f;

    private bool _equipped;
    private bool _hidden;
    private float _still;
    private Vector2 _lastPos;
    private SpriteRenderer _held;
    private Ability _throw, _hide;

    // 当たった数 (ホストが数えて全員へ配る)
    private readonly Dictionary<byte, int> _hits = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Kunoichi.Equip"), "kunoichi_equip")
        {
            CooldownAtStart = false,
            OnUse = () => { SetEquip(!_equipped); return false; },
        });
        _throw = AddAbility(new Ability(new Text("Kunoichi.Throw"), "kunoichi_kunai")
        {
            Cooldown = () => ThrowCooldown,
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (!_equipped || (_hidden && !ThrowWhileHidden)) return false;
                Vector2 from = Player.GetTruePosition();
                float angle = Aim();
                if (Blocked(from, angle)) return false;
                ThrowCall.Send((from.x, from.y, angle));
                Fly(this, from, angle, true);
                return true;
            },
        });
        if (CanHide && HideButton)
            _hide = AddAbility(new Ability(new Text("Kunoichi.Hide"), "kunoichi_hide")
            {
                CooldownAtStart = false,
                OnUse = () =>
                {
                    if (_hidden || _still < HideWait) return false;
                    Hide(true);
                    return false;
                },
            });
        if (IsLocal) Lifespan.OnRelease(() => { if (_held) Object.Destroy(_held.gameObject); });
    }

    private void SetEquip(bool on)
    {
        _equipped = on;
        if (_held) _held.gameObject.SetActive(on);
    }

    private float Aim()
    {
        var cam = Camera.main;
        Vector2 me = Player.GetTruePosition();
        if (!cam || System.OperatingSystem.IsAndroid())
        {
            var f = ShotKit.Facing;
            return Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
        }
        Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
        return Mathf.Atan2(mouse.y - me.y, mouse.x - me.x) * Mathf.Rad2Deg;
    }

    // 投げる所のすぐ先が壁なら投げられない
    private static bool Blocked(Vector2 from, float angle)
    {
        Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        return PhysicsHelpers.AnythingBetween(from, from + dir * 0.6f, Constants.ShipOnlyMask, false);
    }

    private static readonly RemoteCall<(float x, float y, float a)> ThrowCall = new("Kunoichi.Throw", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.a); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.Of(sender) is Kunoichi k) Fly(k, new Vector2(m.x, m.y), m.a, false); }, perSecond: 12);

    // 全員の端末で飛ばす。当たりの判定は投げた本人の端末
    private static void Fly(Kunoichi k, Vector2 from, float angle, bool judge)
    {
        var sr = PortKit.Marker(k.Lifespan, from, "kunoichi_kunai", 0.5f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, angle - 45f);
        Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        float speed = KunaiSpeed * 4f, gone = 0f;
        Vector2 at = from;
        UchuKit.Every(() =>
        {
            if (!sr) return false;
            if (MeetingHud.Instance || !ShipStatus.Instance) { Object.Destroy(sr.gameObject); return false; }
            float step = speed * Time.fixedDeltaTime;
            Vector2 next = at + dir * step;
            if (WallStops && PhysicsHelpers.AnythingBetween(at, next, Constants.ShipOnlyMask, false)) { Object.Destroy(sr.gameObject); return false; }
            at = next;
            gone += step;
            sr.transform.position = new Vector3(at.x, at.y, at.y / 1000f - 0.5f);
            if (gone >= MaxTravel) { Object.Destroy(sr.gameObject); return false; }
            if (!judge) return true;
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || p.PlayerId == k.PlayerId || p.inVent) continue;
                if (!FriendlyFire && PortKit.IsImpostor(p)) continue;
                Vector2 d = p.GetTruePosition() - at;
                float along = Mathf.Abs(d.x * dir.x + d.y * dir.y);
                float side = Mathf.Abs(d.x * dir.y - d.y * dir.x);
                if (along > HitLength / 2f || side > HitWidth / 2f + 0.25f) continue;
                HitCall.Send(p.PlayerId);
                Object.Destroy(sr.gameObject);
                return false;
            }
            return true;
        });
    }

    private static readonly RemoteCall<byte> HitCall = new("Kunoichi.Hit", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Kunoichi k && PortKit.Alive(sender)) k.HostHit(id); }, perSecond: 12);

    private void HostHit(byte id)
    {
        var t = PortKit.ById(id);
        if (!PortKit.Alive(t) || MeetingHud.Instance) return;
        _hits.TryGetValue(id, out int n);
        n++;
        bool kill = n >= HitsToKill;
        if (kill) n = 0;
        HitsCall.Send((PlayerId, id, (byte)n, kill));
        OnHits(PlayerId, id, (byte)n, kill);
        if (kill)
        {
            UchuKit.HostAbilityKill(Player, t);
        }
    }

    private static readonly RemoteCall<(byte k, byte t, byte n, bool kill)> HitsCall = new("Kunoichi.Hits", Route.HostToAll,
        (w, m) => { w.Write(m.k); w.Write(m.t); w.Write(m.n); w.Write(m.kill); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadBoolean()),
        (_, m) => OnHits(m.k, m.t, m.n, m.kill));

    private static void OnHits(byte kid, byte tid, byte n, bool kill)
    {
        if (RoleState.Of(kid) is not Kunoichi k) return;
        k._hits[tid] = n;
        if (!kill) return;
        RangedKillSnapPatch.Mark(kid, 2f); // 遠くから倒すので、体を相手の所へ動かさない
        if (KunaiOnBody)
        {
            var t = PortKit.ById(tid);
            if (t) PortKit.After(0.3f, () => { if (t) PortKit.Marker(RoleState.Match, t.GetTruePosition(), "kunoichi_kunai", 0.45f); });
        }
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_equipped) UchuKit.HideKillButton();
        var p = Player;
        if (!PortKit.Alive(p)) { SetEquip(false); return; }
        // 持っているクナイがマウスの向きに浮かぶ (本人だけ・壁で投げられない時は赤)
        if (_equipped && !MeetingHud.Instance)
        {
            if (!_held) _held = PortKit.Marker(Lifespan, p.GetTruePosition(), "kunoichi_kunai", 0.4f);
            float a = Aim();
            Vector2 dir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
            Vector2 at = p.GetTruePosition() + dir * 0.6f;
            _held.transform.position = new Vector3(at.x, at.y + 0.3f, -1f);
            _held.transform.rotation = Quaternion.Euler(0f, 0f, a - 45f);
            _held.color = Blocked(p.GetTruePosition(), a) ? new UnityEngine.Color(1f, 0.3f, 0.3f) : UnityEngine.Color.white;
            _held.gameObject.SetActive(true);
        }
        else if (_held) _held.gameObject.SetActive(false);
        _throw?.Button?.SetUsable(_equipped && (!_hidden || ThrowWhileHidden));

        // 止まっていると透明になれる
        Vector2 pos = p.GetTruePosition();
        bool moved = Vector2.Distance(pos, _lastPos) > 0.01f;
        _lastPos = pos;
        if (moved || MeetingHud.Instance)
        {
            _still = 0f;
            if (_hidden) Hide(false);
        }
        else _still += Time.fixedDeltaTime;
        if (!CanHide) return;
        _hide?.Button?.SetUsable(!_hidden && _still >= HideWait);
        if (!HideButton && !_hidden && _still >= HideWait) Hide(true);
    }

    private void Hide(bool on)
    {
        _hidden = on;
        Cloak.Self(on ? 9999f : 0f);
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (ResetHitsOnMeeting) _hits.Clear();
        if (!IsLocal) return;
        SetEquip(false);
        _hidden = false;
        _still = 0f;
    }

    // クノイチにだけ: 倒せる人の名前の横に、あと何回当てれば倒せるか
    public override string NameMark(PlayerControl seen)
    {
        if (seen.PlayerId == PlayerId || !PortKit.Alive(seen) || (!FriendlyFire && PortKit.IsImpostor(seen))) return null;
        _hits.TryGetValue(seen.PlayerId, out int n);
        return $"<color=#FF1919>[{HitsToKill - n}]</color>";
    }

    // ホスト: クナイを持っている間は本編のキルを通さない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (_equippedHost && UchuKit.AbilityKill != PlayerId) e.Cancel("kunoichi holds kunai");
    }

    private bool _equippedHost => _equipped;
}
