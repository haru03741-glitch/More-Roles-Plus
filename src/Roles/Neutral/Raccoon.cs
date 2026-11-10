// Ported from https://github.com/rar006/TownOfHost-hamo Roles/Neutral/RaccoonParent.cs, Roles/Neutral/RaccoonChild.cs (GPL-3.0)
using System.Linq;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// アライグマ (第三陣営・キル役): キルボタンで近くの人を「掴む」(相手はアライグマの所へ引き寄せられ続ける)。
// 掴んだ相手にもう一度押すと倒す (他の人には押せない)。掴んでいる間は行き先の部屋が出され、着くと掴みが解けてその人の役職が分かる。
// 「子作り」(1 回) で近くの人を、次の会議の後にアライグマの子にする。アライグマが他のアライグマの親を掴もうとすると自分が死ぬ。
// 他のキル役がいなくなり、生きているアライグマの数が生きているクルーの数に並ぶと、アライグマの親子で勝つ。
// 元はホスト用 MOD なので、ホストが掴まれた人を 3 フレームごとに瞬間移動させ、状態を名前の下の字で見せていた。
// MRP は全員が MOD を入れているので、掴まれた人の端末が自分をアライグマの背中へ毎フレーム運び (なめらかに見える)、状態は画面の下の札に出す
public abstract class RaccoonBase : RoleBase
{
    public const string Pack = "Raccoon";

    public override Team BaseTeam => Team.Neutral;
    public override bool IsKiller => true;
    public override bool CanKill => false;     // 掴む・倒すは自前のボタン
    public override string Faction => Pack;
    public override string Origin => "TOH-hamo";

    protected abstract float KillCooldown { get; }
    protected abstract bool KillOnMeeting { get; }

    private byte _grabbed = 255;     // 全員の端末で同じ
    internal byte Grabbed => _grabbed;
    private byte _investigated = 255; // 自分の端末: 役職が分かった人
    private PlainShipRoom _room;     // 自分の端末: 行き先
    private WorldArrow _arrow;
    private HudReadout _readout;
    private Ability _grab;

    protected void AddGrab()
    {
        AddAbility(_grab = new Ability(new Text("Raccoon.Grab"), "raccoon_grab")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => _grabbed == 255 ? KillCooldown : 1f,
            CanTarget = (_, t) => _grabbed == 255 || t.PlayerId == _grabbed,
            OnUsed = (user, t) => Use(user, t),
        });
        if (IsLocal)
        {
            _arrow = new WorldArrow(Lifespan, new Color32(160, 120, 90, 255));
            _readout = new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(160, 120, 90, 255));
        }
    }

    // 全員の端末で (ホストの確かめの後)
    private void Use(PlayerControl user, PlayerControl t)
    {
        bool host = AmongUsClient.Instance.AmHost;
        // 親を掴もうとしたら自分が死ぬ
        if (_grabbed == 255 && RoleState.Of(t) is Raccoon)
        {
            if (host) PortKit.HostSuicide(user);
            return;
        }
        if (_grabbed == 255)
        {
            _grabbed = t.PlayerId;
            _investigated = 255;
            if (IsLocal) { PickRoom(); RestartCooldown(); }
            return;
        }
        if (t.PlayerId != _grabbed) return;
        _grabbed = 255;
        _room = null;
        if (IsLocal) RestartCooldown();
        if (host) UchuKit.HostAbilityKill(user, t, PortKit.Harm.Blunt);
    }

    // 自分の端末: 掴んだ後はすぐ倒せる (1 秒)・放した後はキルの待ち時間から (ボタンの長さは作った時の値なので替える)
    private void RestartCooldown()
    {
        var b = _grab?.Button;
        if (b == null) return;
        b.Cooldown = _grab.CooldownSeconds;
        b.StartCooldown();
    }

    private void PickRoom()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        var me = Player.GetTruePosition();
        var rooms = ship.AllRooms.Where(r => r && r.roomArea && r.RoomId != SystemTypes.Ventilation && r.RoomId != SystemTypes.Hallway && !r.roomArea.OverlapPoint(me)).ToList();
        _room = rooms.Count == 0 ? null : rooms[Random.Range(0, rooms.Count)];
    }

    private static readonly RemoteCall<byte> ReleaseCall = new("Raccoon.Release", Route.Anyone,
        (w, m) => w.Write(m), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is RaccoonBase r) r.Release(); }, perSecond: 4);

    private void Release()
    {
        bool was = _grabbed != 255;
        _grabbed = 255;
        _room = null;
        if (was && IsLocal) RestartCooldown();
    }

    public override void OnFixedUpdate()
    {
        if (IsLocal) LocalTick();
        if (_grabbed == 255 || MeetingHud.Instance || ExileController.Instance) return;
        var t = PortKit.ById(_grabbed);
        if (AmongUsClient.Instance.AmHost && (!PortKit.Alive(Player) || !PortKit.Alive(t))) { ReleaseCall.Send(0); Release(); return; }
        // 掴まれた人の端末: 自分をアライグマの背中へ運び続ける (自分の位置は本編の同期で皆に届く)。ダミーはホストが運ぶ
        bool mine = t && (t.AmOwner || (t.isDummy && AmongUsClient.Instance.AmHost));
        if (mine && PortKit.Alive(t) && PortKit.Alive(Player)) Carry(t);
    }

    private void Carry(PlayerControl t)
    {
        if (t.inVent || t.inMovingPlat || (t.MyPhysics && t.MyPhysics.Animations != null && t.MyPhysics.Animations.IsPlayingAnyLadderAnimation())) return;
        Vector2 at = Player.GetTruePosition();
        // 背中の側へ少しずらす (壁の向こうになるならずらさない)
        bool left = Player.cosmetics && Player.cosmetics.FlipX;
        Vector2 back = at + new Vector2(left ? 0.08f : -0.08f, 0f);
        if (!PhysicsHelpers.AnythingBetween(at, back, Constants.ShipAndObjectsMask, false)) at = back;
        Vector2 now = t.GetTruePosition();
        if (t.MyPhysics && t.MyPhysics.body) t.MyPhysics.body.velocity = Vector2.zero;
        // 遠い時 (会議の後・はしごの後) は一気に、近い時は追いかける
        Vector2 next = Vector2.Distance(now, at) > 2.5f ? at : Vector2.Lerp(now, at, 0.5f);
        Vector2 offset = (Vector2)t.transform.position - now;
        t.NetTransform.SnapTo(next + offset);
    }

    private void LocalTick()
    {
        bool show = _grabbed != 255 && _room && PortKit.Alive(Player) && !MeetingHud.Instance;
        ShowReadout();
        _arrow?.SetActive(show);
        if (!show) return;
        _arrow.Update(_room.roomArea.bounds.center);
        // 行き先の部屋に着いた: 掴みを解いて役職が分かる
        if (_room.roomArea.OverlapPoint(Player.GetTruePosition()))
        {
            _investigated = _grabbed;
            ReleaseCall.Send(0);
            Release();
            var t = PortKit.ById(_investigated);
            if (t && RoleState.Of(t) is { } r)
                PortKit.Notice(new Text("Raccoon.Result").With("name", t.Data.PlayerName).With("role", r.ShownColoredName).ToString());
        }
    }

    // ホスト: 通報された時点 (会議に入る前) に、掴んでいる相手を倒す設定
    [HostOnly]
    private void OnMeetingCalled(MeetingCalledEvent e)
    {
        if (!KillOnMeeting || _grabbed == 255) return;
        var t = PortKit.ById(_grabbed);
        if (PortKit.Alive(t) && PortKit.Alive(Player)) UchuKit.HostAbilityKill(Player, t, PortKit.Harm.Blunt);
    }

    private void OnMeetingStart(MeetingStartEvent e) => Release();

    private void OnPlayerMurdered(PlayerMurderedEvent e)
    {
        if (e.Target && (e.Target.PlayerId == PlayerId || e.Target.PlayerId == _grabbed)) Release();
    }

    private void OnPlayerExiled(PlayerExiledEvent e)
    {
        if (e.Player && (e.Player.PlayerId == PlayerId || e.Player.PlayerId == _grabbed)) Release();
    }

    // 画面の下の札: 掴む前は「誰かを掴もう」、掴んだ後は行き先の部屋
    private void ShowReadout()
    {
        if (_readout == null) return;
        if (!PortKit.Alive(Player)) { _readout.Set(false); return; }
        if (_grabbed == 255) { _readout.Set(false); return; }
        if (!_room) { _readout.Set(false); return; }
        string room = TranslationController.Instance ? TranslationController.Instance.GetString(_room.RoomId) : _room.RoomId.ToString();
        _readout.Set(true, $"<color=#FFD27A>{room}</color>");
    }

    // 調べて分かった人の名前の上に役職
    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _investigated && RoleState.Of(seen) is { } r ? $"<size=70%>{r.ShownColoredName}</size>" : null;

    public override string MarkSeenBy(PlayerControl viewer) =>
        RoleState.Of(viewer) is RaccoonBase && viewer.PlayerId != PlayerId ? "<color=#a0785a>●</color>" : null;
}

public sealed class Raccoon : RaccoonBase
{
    public override string Color => "#a0785a";
    public override Text Name => new("Raccoon.Name");
    public override string Reading => "あらいぐま";
    public override Text Blurb => new("Raccoon.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Raccoon.Grab", "raccoon_grab"), new("Raccoon.MakeChild", "raccoon_child") };

    public static readonly FloatOpt KillCooldownAfterEnd = new("Raccoon.KillCooldown", 30f, 0f, 180f, 0.5f, "s");
    public static readonly BoolOpt KillOnMeetingIfGrabbing = new("Raccoon.KillOnMeetingIfGrabbing", false);
    public static readonly FloatOpt MakeChildCooldown = new("Raccoon.MakeChildCooldown", 30f, 0f, 180f, 0.5f, "s");
    public static readonly FloatOpt ChildKillCooldown = new("Raccoon.ChildKillCooldown", 35f, 0f, 180f, 0.5f, "s");
    public static readonly BoolOpt ChildKillOnMeetingIfGrabbing = new("Raccoon.ChildKillOnMeetingIfGrabbing", false);

    protected override float KillCooldown => KillCooldownAfterEnd;
    protected override bool KillOnMeeting => KillOnMeetingIfGrabbing;

    private byte _pending = 255; // 全員の端末で同じ: 次の会議の後に子になる人

    public override void OnAssigned()
    {
        AddGrab();
        AddAbility(new Ability(new Text("Raccoon.MakeChild"), "raccoon_child")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => MakeChildCooldown,
            MaxUses = () => 1,
            CanTarget = (_, t) => RoleState.Of(t) is not RaccoonBase,
            OnUsed = (_, t) => _pending = t.PlayerId,
        });
    }

    // ホスト: 会議の後に子へ変える
    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_pending == 255) return;
        var t = PortKit.ById(_pending);
        _pending = 255;
        if (PortKit.Alive(t) && RoleState.Of(t) is not RaccoonBase) RoleState.Change(t, RoleState.Proto<RaccoonChild>());
    }
}

public sealed class RaccoonChild : RaccoonBase
{
    public override string Color => "#c9a27a";
    public override Text Name => new("RaccoonChild.Name");
    public override string Reading => "あらいぐまのこ";
    public override Text Blurb => new("RaccoonChild.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("Raccoon.Grab", "raccoon_grab") };
    public override bool MadeByOthers => true;

    protected override float KillCooldown => Raccoon.ChildKillCooldown;
    protected override bool KillOnMeeting => Raccoon.ChildKillOnMeetingIfGrabbing;

    public override void OnAssigned() => AddGrab();
}

// 掴まれた人を担いで運ぶ見た目 (全員の端末・毎フレーム)。
// 掴まれた人の体 (BodyForms)・着せ替え (cosmetics)・名前 (Names) を体の中心で横倒しに回し、アライグマの頭の上へ持ち上げ、
// アライグマが歩くと弾むように揺らす。位置の同期・当たり判定は本体のまま (RaccoonBase.Carry が運ぶ)。放したら元の置き方へ戻す
internal static class RaccoonCarryFx
{
    private sealed class Part
    {
        public Transform T;
        public Vector3 Pos;
        public Quaternion Rot;
    }

    private sealed class Lift
    {
        public PlayerControl Pc;
        public Part Body, Cos, Names, CarrierNames;
        public Vector3 Pivot;   // 体の絵の中心 (根の中の座標)
        public float Scale = 1f;
        public float Since;
        public float Turn;      // 今の傾き (向きを変えた時になめらかに回す)
    }

    private const float RaiseSeconds = 0.22f;
    private const float Height = 0.55f;  // アライグマの体の中心から持ち上げる高さ
    private const float Tilt = 82f;      // 横倒しの角度
    private const float CarrierNameLift = 0.6f, NameGap = 0.55f; // 名前が担いだ体に重ならないように

    // 運び方: Up = 運ぶ人の体の中心から上へ・Side = 進む向きへ (負なら後ろ)・Tilt = 傾き (正で頭が後ろ)・
    // Struggle = もがく揺れ・NameGap / CarrierLift = 名前を離す高さ・WalkBob = 歩く時の上下
    private sealed class Pose
    {
        public float Up, Side, Tilt, Struggle, NameGap, CarrierLift, WalkBob;
    }

    // アライグマ: 肩に横倒しで担ぐ
    private static readonly Pose Shoulder = new() { Up = Height, Side = -0.04f, Tilt = Tilt, NameGap = NameGap, CarrierLift = CarrierNameLift, WalkBob = 0.07f };
    // ペンギン: 前に抱えて少し持ち上げ、相手は反り返ってもがく
    private static readonly Pose Hug = new() { Up = 0.12f, Side = 0.3f, Tilt = -22f, Struggle = 9f, NameGap = 0.42f, CarrierLift = 0f, WalkBob = 0.04f };

    private static readonly System.Collections.Generic.Dictionary<byte, Lift> Lifted = new();
    private static readonly System.Collections.Generic.List<byte> Drop = new();

    internal static void Tick()
    {
        if (Lifted.Count == 0 && RoleState.All.Count == 0) return;
        Drop.Clear();
        foreach (var k in Lifted.Keys) Drop.Add(k);
        if (ShipStatus.Instance && !MeetingHud.Instance && !ExileController.Instance)
        {
            var all = RoleState.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is not RaccoonBase rb || rb.Grabbed == 255) continue;
                var carrier = rb.Player;
                var t = PortKit.ById(rb.Grabbed);
                if (!PortKit.Alive(carrier) || !PortKit.Alive(t) || !t.cosmetics || !carrier.cosmetics) continue;
                if (t.inVent || carrier.inVent || carrier.onLadder || t.onLadder) continue;
                Drop.Remove(t.PlayerId);
                try { Apply(carrier, t, Shoulder); }
                catch (System.Exception e) { Plugin.Logger.LogWarning($"raccoon carry: {e.Message}"); }
            }
        }
        foreach (var id in Drop) Restore(id);
    }

    private static Part Take(Transform t) => t ? new Part { T = t, Pos = t.localPosition, Rot = t.localRotation } : null;

    private static Vector3 BodyCenter(PlayerControl pc)
    {
        var b = pc.cosmetics && pc.cosmetics.currentBodySprite != null ? pc.cosmetics.currentBodySprite.BodySprite : null;
        return b ? b.bounds.center : pc.transform.position;
    }

    private static Lift Begin(PlayerControl t)
    {
        var root = t.transform;
        // 体を動かしている他の演出を先に元へ戻させる (戻さないと、ずれた姿を元として控えて書き戻す)
        SamuraiBlade.ReleaseBody(t.PlayerId);
        var l = new Lift
        {
            Pc = t,
            Body = Take(root.Find("BodyForms")),
            Cos = Take(t.cosmetics.transform),
            Names = Take(root.Find("Names")),
            Since = Time.time,
        };
        float sc = root.localScale.y;
        l.Scale = sc > 0.01f ? sc : 1f;
        Vector3 c = BodyCenter(t) - root.position;
        l.Pivot = new Vector3(c.x / l.Scale, c.y / l.Scale, 0f);
        return l;
    }

    private static void Apply(PlayerControl carrier, PlayerControl t, Pose pose)
    {
        if (!Lifted.TryGetValue(t.PlayerId, out var l))
        {
            Lifted[t.PlayerId] = l = Begin(t);
            l.CarrierNames = Take(carrier.transform.Find("Names"));
        }
        float up = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - l.Since) / RaiseSeconds));
        bool left = carrier.cosmetics.FlipX;
        var body = carrier.MyPhysics ? carrier.MyPhysics.body : null;
        bool moving = body && body.velocity.sqrMagnitude > 0.05f;
        // 担がれている本人の画面: 視界が少し狭まり、運ぶ人の歩みで揺れる
        if (t.AmOwner) SenseFx.Carried(moving);
        float ph = Time.time * 10.5f;
        float bob = moving ? Mathf.Abs(Mathf.Sin(ph)) * pose.WalkBob : Mathf.Sin(Time.time * 2.2f) * 0.015f;
        float sway = moving ? Mathf.Sin(ph) * 7f : Mathf.Sin(Time.time * 1.7f) * 2f;
        // もがく: 速い小刻みな揺れに、時々大きく暴れる
        if (pose.Struggle > 0f)
        {
            float fit = Mathf.PerlinNoise(Time.time * 1.3f, t.PlayerId * 0.37f);
            sway += Mathf.Sin(Time.time * 23f + t.PlayerId) * pose.Struggle * (0.4f + fit);
            bob += Mathf.Abs(Mathf.Sin(Time.time * 17f)) * 0.03f * fit;
        }
        // 頭は進む向きの後ろ側へ (Tilt が正の時。負なら前へ反る)
        l.Turn = Mathf.MoveTowards(l.Turn, left ? -pose.Tilt : pose.Tilt, Time.deltaTime * 600f);
        var rot = Quaternion.Euler(0f, 0f, (l.Turn + sway) * up);

        // 体の中心を、アライグマの体の中心の上へ
        var root = t.transform;
        Vector3 restCenter = root.position + l.Pivot * l.Scale;
        Vector3 cc = BodyCenter(carrier);
        Vector3 target = new(cc.x + (left ? -pose.Side : pose.Side), cc.y + pose.Up + bob, restCenter.z);
        Vector3 d = (target - restCenter) / l.Scale * up;
        d.z = -0.012f * up / l.Scale; // アライグマより手前に描く

        Place(l.Body, l.Pivot, rot, d);
        Place(l.Cos, l.Pivot, rot, d);
        // 名前は傾けずに持ち上げるだけ
        if (l.Names != null && l.Names.T) l.Names.T.localPosition = l.Names.Pos + new Vector3(d.x, d.y + pose.NameGap * up / l.Scale, 0f);
        // アライグマの名前は担いだ人の上へ
        if (l.CarrierNames != null && l.CarrierNames.T)
        {
            float cs = carrier.transform.localScale.y > 0.01f ? carrier.transform.localScale.y : 1f;
            l.CarrierNames.T.localPosition = l.CarrierNames.Pos + new Vector3(0f, pose.CarrierLift * up / cs, 0f);
        }
    }

    // 体の中心を軸に回して、ずらす
    private static void Place(Part p, Vector3 pivot, Quaternion rot, Vector3 d)
    {
        if (p == null || !p.T) return;
        Vector3 off = p.Pos - pivot;
        off.z = 0f;
        Vector3 np = pivot + rot * off + d;
        np.z = p.Pos.z + d.z;
        p.T.localPosition = np;
        p.T.localRotation = rot * p.Rot;
    }

    private static void Put(Part p)
    {
        if (p == null || !p.T) return;
        p.T.localPosition = p.Pos;
        p.T.localRotation = p.Rot;
    }

    private static void Restore(byte id)
    {
        if (!Lifted.TryGetValue(id, out var l)) return;
        Lifted.Remove(id);
        Put(l.Body);
        Put(l.Cos);
        Put(l.Names);
        Put(l.CarrierNames);
    }
}
