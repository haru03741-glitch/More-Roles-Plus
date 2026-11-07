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
        if (host) UchuKit.HostAbilityKill(user, t);
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
        Vector2 back = at + new Vector2(left ? 0.32f : -0.32f, 0f);
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
        if (PortKit.Alive(t) && PortKit.Alive(Player)) UchuKit.HostAbilityKill(Player, t);
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
        if (_grabbed == 255) { _readout.Set(true, new Text("Raccoon.Idle").ToString()); return; }
        if (!_room) { _readout.Set(false); return; }
        string room = TranslationController.Instance ? TranslationController.Instance.GetString(_room.RoomId) : _room.RoomId.ToString();
        _readout.Set(true, new Text("Raccoon.GoTo").With("room", $"<color=#FFD27A>{room}</color>").ToString());
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
