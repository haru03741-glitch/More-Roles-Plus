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
public sealed partial class Kunoichi : RoleBase
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
            Usable = () => _equipped && (!_hidden || ThrowWhileHidden), // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (!_equipped || (_hidden && !ThrowWhileHidden)) return false;
                Vector2 from = Player.GetTruePosition();
                float angle = Aim();
                if (Blocked(from, angle)) return false;
                byte seq = _seq++;
                ThrowCall.Send((from.x, from.y, angle, seq));
                Fly(this, from, angle, true, seq);
                return true;
            },
        });
        if (CanHide && HideButton)
            _hide = AddAbility(new Ability(new Text("Kunoichi.Hide"), "kunoichi_hide")
            {
                CooldownAtStart = false,
                Usable = () => !_hidden && _still >= HideWait, // 使えない間はボタンを暗くする
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
        if (_equipped == on) return;
        _equipped = on;
        if (_held) _held.gameObject.SetActive(on);
        // 本編のキルを止めるのはホストなので、持っているかをホストへ知らせる (ホストなら手元で通す)
        EquipCall.Send(on);
    }

    private static readonly RemoteCall<bool> EquipCall = new("Kunoichi.Equip", Route.ToHost,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is Kunoichi k) k._equippedHost = on; }, perSecond: 6);

    // 確かめ用: 投げる向きを固定する (度・0 = 右)。null = マウス / 歩いた向き
    internal static float? TestAngle;

    private float Aim()
    {
        if (TestAngle is float fixedAngle) return fixedAngle;
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

    private static readonly RemoteCall<(float x, float y, float a, byte seq)> ThrowCall = new("Kunoichi.Throw", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.a); w.Write(m.seq); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadByte()),
        (sender, m) => { if (RoleState.Of(sender) is Kunoichi k) Fly(k, new Vector2(m.x, m.y), m.a, false, m.seq); }, perSecond: 12);

    // 全員の端末で飛ばす。当たりの判定は投げた本人の端末 (頑丈な役職への当たりだけはその人の端末・Kunoichi.Body.cs)
    private static void Fly(Kunoichi k, Vector2 from, float angle, bool judge, byte seq)
    {
        var sr = PortKit.Marker(k.Lifespan, from, "kunoichi_kunai", 0.5f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, angle - 45f);
        k.AddKunai(seq, sr, judge);
        Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
        float speed = KunaiSpeed * 4f, gone = 0f;
        Vector2 at = from;
        UchuKit.Every(() =>
        {
            if (!sr) { k.DropKunai(seq); return false; }
            if (MeetingHud.Instance || !ShipStatus.Instance) { k.EndKunai(seq); return false; }
            float step = speed * Time.fixedDeltaTime;
            Vector2 next = at + dir * step;
            if (WallStops && PhysicsHelpers.AnythingBetween(at, next, Constants.ShipOnlyMask, false)) { k.EndKunai(seq); return false; }
            at = next;
            gone += step;
            sr.transform.position = new Vector3(at.x, at.y, at.y / 1000f - 0.5f);
            if (gone >= MaxTravel) { k.EndKunai(seq); return false; }
            if (!judge && !AnyToughHere()) return true;
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!PortKit.Alive(p) || p.PlayerId == k.PlayerId || p.inVent) continue;
                if (!FriendlyFire && PortKit.IsImpostor(p)) continue;
                bool tough = Duel.Tough(p);
                if (tough ? !DecidesFor(p) : !judge) continue;
                Vector2 d = p.GetTruePosition() - at;
                float along = Mathf.Abs(d.x * dir.x + d.y * dir.y);
                float side = Mathf.Abs(d.x * dir.y - d.y * dir.x);
                if (along > HitLength / 2f || side > HitWidth / 2f + 0.25f) continue;
                if (tough)
                {
                    // 刀で斬る・受け流す・踏み込みで抜けるは弾と同じ受け方 (気も同じだけ使う)
                    var fate = ToughCatch(p, dir);
                    if (fate == Neutral.Samurai.BulletFate.Pass) continue;
                    if (fate == Neutral.Samurai.BulletFate.Hit) ToughHitCall.Send((k.PlayerId, p.PlayerId));
                    else Spark(at.x, at.y, new Strike(StrikeElement.Slash, 1f, 1, at.x, at.y, -dir.x, -dir.y, p.PlayerId));
                    GoneCall.Send((k.PlayerId, seq));
                }
                else HitCall.Send(p.PlayerId);
                k.EndKunai(seq);
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
        if (Duel.Tough(t))
        {
            // 頑丈な役職は 1 本ごとに体力が減り、普通の人が倒れる本数で倒れる
            Duel.HostSmash(Player, t, (Duel.MaxHp + HitsToKill - 1) / HitsToKill);
            return;
        }
        _hits.TryGetValue(id, out int n);
        n++;
        bool kill = n >= HitsToKill;
        if (kill) n = 0;
        HitsCall.Send((PlayerId, id, (byte)n, kill));
        OnHits(PlayerId, id, (byte)n, kill);
        if (kill)
        {
            UchuKit.HostAbilityKill(Player, t, PortKit.Harm.Slash);
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
            if (t) PortKit.After(0.3f, () => { if (t) BodyKunai.Add(PortKit.Marker(RoleState.Match, t.GetTruePosition(), "kunoichi_kunai", 0.45f)); });
        }
    }

    // 死体に残ったクナイ (全員の端末で)。会議で消える
    private static readonly System.Collections.Generic.List<SpriteRenderer> BodyKunai = new();

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
        foreach (var r in BodyKunai) if (r) Object.Destroy(r.gameObject); // 死体に残ったクナイは会議で消える
        BodyKunai.Clear();
        // 会議では持ち主の端末もクナイをしまうので、ホストの控えも同じく戻す (電文が届かなくても食い違わない)
        _equippedHost = false;
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

    // ホストの端末で: この人がクナイを持っているか (持ち主の端末から届いた値)
    private bool _equippedHost;
}
