// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Frankenstein.cs, Modules/EndGamer.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// フランケンシュタイン (第三陣営): 近くの死体で「怪物を作る」と、その死体の姿の怪物になる。
// 怪物の間だけ待ち時間なしでキルでき、キルすると死体をその場に置いて元の場所へ戻る。怪物はキルされない (キルされかけると怪物が解ける)。
// 怪物のキルを設定の回数こなすと 1 人で勝つ。死体への矢印が見える
public sealed class Frankenstein : RoleBase
{
    private const string RoleColor = "#7AA992";

    public override Team Team => Team.Neutral;
    public override string Color => RoleColor;
    public override Text Name => new("Frankenstein.Name");
    public override string Reading => "ふらんけんしゅたいん";
    public override Text Blurb => new("Frankenstein.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Frankenstein.Create", "snr_monster") };
    public override bool IsKiller => true;

    public static readonly FloatOpt CreateCooldown = new("Frankenstein.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt WinKillCount = new("Frankenstein.WinKillCount", 3, 1, 10);
    public static readonly BoolOpt MonsterImpostorVision = new("Frankenstein.ImpostorVision", true);
    public static readonly BoolOpt MonsterCanVent = new("Frankenstein.CanVent", true);

    private byte _body = 255;          // 怪物にしている死体の持ち主 (255 = 怪物でない)
    private int _left = -1;            // 勝つまでに要るキルの数
    private Vector2 _playerOrigin;     // 怪物を作った時に立っていた場所 (自分の端末だけ)
    private Vector2 _bodyOrigin;       // 死体があった場所 (自分の端末だけ)
    private Ability _create;
    private BodyArrows _arrows;

    public bool IsMonster => _body != 255;
    private int Left => _left < 0 ? WinKillCount : _left;

    public override bool? CanVent => IsMonster && MonsterCanVent;
    public override void ModifyKillCooldown(ref float seconds) => seconds = 0f;

    public override void ModifyVision(ref float radius)
    {
        if (IsMonster && MonsterImpostorVision) PortKit.ImpostorVision(ref radius);
    }

    public override void OnAssigned()
    {
        _create = AddAbility(new Ability(new Text("Frankenstein.Create"), "snr_monster")
        {
            Cooldown = () => CreateCooldown,
            OnUse = () =>
            {
                if (IsMonster || !PortKit.Alive(Player)) return false;
                var body = FindBody();
                if (!body) return false;
                _playerOrigin = Player.GetTruePosition();
                _bodyOrigin = body.TruePosition;
                StartCall.Send(body.ParentId);
                Start(Player, body.ParentId);
                PortKit.Morph(PortKit.ById(body.ParentId));
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            if (IsLocal && IsMonster) EndLocal(_bodyOrigin, false);
        });
    }

    // 通報できる距離にある、通報されていない・誰も使っていない死体
    private DeadBody FindBody()
    {
        Vector2 me = Player.GetTruePosition();
        float max = Player.MaxReportDistance;
        DeadBody best = null;
        float bestD = float.MaxValue;
        foreach (var c in Physics2D.OverlapCircleAll(me, max, Constants.PlayersOnlyMask))
        {
            if (!c || !c.CompareTag("DeadBody")) continue;
            var b = c.GetComponent<DeadBody>();
            if (!b || !b.gameObject.activeInHierarchy || b.Reported || InUse(b.ParentId)) continue;
            float d = Vector2.Distance(me, b.TruePosition);
            if (d > max || d >= bestD || PhysicsHelpers.AnythingBetween(me, b.TruePosition, Constants.ShipAndObjectsMask, false)) continue;
            best = b;
            bestD = d;
        }
        return best;
    }

    private static bool InUse(byte id)
    {
        foreach (var r in RoleState.All) if (r is Frankenstein f && f._body == id) return true;
        return false;
    }

    private static DeadBody Body(byte id)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>()) if (b && b.ParentId == id) return b;
        return null;
    }

    private static void SetBodyActive(DeadBody b, bool on)
    {
        if (!b) return;
        b.Reported = !on;
        if (b.myCollider) b.myCollider.enabled = on;
        b.gameObject.SetActive(on);
    }

    // 全員の端末で: 死体を隠して怪物にする
    private static void Start(PlayerControl who, byte bodyId)
    {
        if (RoleState.Of(who) is not Frankenstein f || f.IsMonster || InUse(bodyId)) return;
        var b = Body(bodyId);
        if (!b) return;
        f._body = bodyId;
        SetBodyActive(b, false);
        Rules.GameRules.ApplyRoleVent(f);
    }

    // 全員の端末で: 怪物を解いて、死体を drop に置く
    private static void End(PlayerControl who, Vector2 drop, bool kill)
    {
        if (RoleState.Of(who) is not Frankenstein f || !f.IsMonster) return;
        var b = Body(f._body);
        if (b)
        {
            SetBodyActive(b, true);
            b.transform.position = new Vector3(drop.x, drop.y, drop.y / 1000f);
        }
        f._body = 255;
        if (kill) f._left = Mathf.Max(0, f.Left - 1);
        Rules.GameRules.ApplyRoleVent(f);
    }

    // 自分の端末から: 怪物を解いて元の場所へ戻る
    private void EndLocal(Vector2 drop, bool kill)
    {
        if (!IsMonster) return;
        EndCall.Send((drop, kill));
        End(Player, drop, kill);
        PortKit.Morph(null);
        if (PortKit.Alive(Player))
        {
            Net.Teleport.Self(_playerOrigin);
            var origin = _playerOrigin;
            PortKit.After(0.1f, () => { if (PortKit.Alive(Player)) Net.Teleport.Self(origin); });
        }
        _create?.Button?.StartCooldown();
    }

    private static readonly RemoteCall<byte> StartCall = new("Frankenstein.Start", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => Start(sender, id));
    private static readonly RemoteCall<(Vector2 drop, bool kill)> EndCall = new("Frankenstein.End", Route.Anyone,
        (w, m) => { w.Write(m.drop.x); w.Write(m.drop.y); w.Write(m.kill); },
        r => (new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadBoolean()), (sender, m) => End(sender, m.drop, m.kill));
    private static readonly RemoteCall<byte> HitCall = new("Frankenstein.Hit", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => Hit(id));

    // 怪物がキルされかけた: その怪物の端末で怪物を解く
    private static void Hit(byte id)
    {
        var p = PortKit.ById(id);
        if (p && p.AmOwner && RoleState.Of(p) is Frankenstein f) f.EndLocal(p.GetTruePosition(), false);
    }

    // ホスト: 怪物でない間はキルできない。怪物はキルされない
    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!e.Killer || !e.Target || e.Killer == e.Target) return;
        if (e.Killer.PlayerId == PlayerId && !IsMonster) e.Cancel("frankenstein is not a monster");
        else if (e.Target.PlayerId == PlayerId && IsMonster)
        {
            e.Cancel("frankenstein monster");
            PortKit.HostBlocked(e.Killer, e.Target);
            HitCall.Send(PlayerId);
            Hit(PlayerId);
        }
    }

    // 怪物でキルしたら、死体をその場に置いて元の場所へ戻る
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!IsLocal || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target == e.Killer || !IsMonster) return;
        EndLocal(Player.GetTruePosition(), true);
    }

    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e) => EndLocal(_bodyOrigin, false);

    public override void OnFixedUpdate()
    {
        // ホスト: 決まった数のキルをこなしたら 1 人で勝つ
        if (AmongUsClient.Instance.AmHost && PortKit.Alive(Player) && Left <= 0 && !MeetingHud.Instance) GameEnd.Win(this);
        if (!IsLocal) return;
        if (IsMonster && !PortKit.Alive(Player)) EndLocal(_bodyOrigin, false);
        HideKill();
        (_arrows ??= new BodyArrows(Lifespan, new Color32(122, 169, 146, 255))).Update(PortKit.Alive(Player));
    }

    // 怪物でない間はキルボタンを隠す
    private void HideKill()
    {
        var hud = Vanilla.Hud;
        if (!IsMonster && hud && hud.KillButton && hud.KillButton.gameObject.activeSelf) hud.KillButton.gameObject.SetActive(false);
    }

    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId == PlayerId ? $"<color={RoleColor}>({Left})</color>" : null;
}
