// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Busker.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// バスカー (クルー): 「偽装死」でその場に自分の死体を残して幽霊の姿になる。もう一度押すと死体の所で生き返るが、
// 時間切れ・会議が始まる・死体が消えると本当に死ぬ
public sealed class Busker : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFAC75";
    public override Text Name => new("Busker.Name");
    public override string Reading => "ばすかー";
    public override Text Blurb => new("Busker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Busker.Fake", "snr_busker") };
    public static readonly FloatOpt FakeCooldown = new("Busker.Cooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt FakeDuration = new("Busker.Duration", 30f, 5f, 60f, 5f, "s");

    private Ability _fake;
    private DeadBody _body;
    private bool _faking, _reborn;

    public override void OnAssigned()
    {
        _fake = AddAbility(new Ability(new Text("Busker.Fake"), "snr_busker")
        {
            Cooldown = () => FakeCooldown,
            Duration = () => FakeDuration,
            Cancellable = true,
            CancelLabel = new Text("Busker.Reborn"),
            CanCancel = CanReborn,
            OnUse = () =>
            {
                if (!PortKit.Alive(Player) || Player.inVent) return false;
                _reborn = false;
                FakeCall.Send(0);
                Fake(Player);
                return true;
            },
            OnCancel = () =>
            {
                _reborn = true;
                RebornCall.Send(0);
                Reborn(Player, true);
            },
            OnEnd = () =>
            {
                if (_reborn || !_faking) return;
                if (AmongUsClient.Instance.AmHost) HostRealDeath(Player);
                else EndCall.Send(0);
            },
        });
        Lifespan.OnRelease(CleanBody);
    }

    // 生き返れるか: 死体が残っていて通報されておらず、今いる所がマップの中
    private bool CanReborn()
    {
        if (!_body || !_body.gameObject.activeInHierarchy || _body.Reported) return false;
        var map = MapArea.MapDatabase.GetCurrentMapData();
        return map == null || map.CheckMapArea(PlayerControl.LocalPlayer.transform.position);
    }

    // 自分の端末で: 死体が消えたら時間切れと同じく本当に死ぬ
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_faking && !_reborn && !_body && _fake != null && _fake.InEffect) _fake.EndEffect();
    }

    // 会議が始まったら本当に死ぬ
    [HostOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        if (_faking) HostRealDeath(Player);
    }

    private static readonly RemoteCall<byte> FakeCall = new("Busker.Fake", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (sender, _) => { if (RoleState.Of(sender) is Busker) Fake(sender); });
    private static readonly RemoteCall<byte> RebornCall = new("Busker.Reborn", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (sender, _) => { if (RoleState.Of(sender) is Busker) Reborn(sender, true); });
    private static readonly RemoteCall<byte> EndCall = new("Busker.End", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (sender, _) => { if (RoleState.Of(sender) is Busker) HostRealDeath(sender); });
    private static readonly RemoteCall<byte> QuietCall = new("Busker.Quiet", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => Reborn(PortKit.ById(id), false));

    // 全員の端末で: 死体を置いて幽霊になる
    private static void Fake(PlayerControl p)
    {
        if (RoleState.Of(p) is not Busker b || !PortKit.Alive(p)) return;
        b._faking = true;
        b.MakeBody();
        p.Die(DeathReason.Kill, false);
        RoleManager.Instance.SetRole(p, AmongUs.GameOptions.RoleTypes.CrewmateGhost);
        if (p.AmOwner)
        {
            b._fake?.Button?.SetGhost(true);
            var hud = Vanilla.Hud;
            if (hud && hud.AbilityButton) hud.AbilityButton.gameObject.SetActive(false); // 幽霊の取り憑くボタンは使わない
        }
    }

    private void MakeBody()
    {
        CleanBody();
        var prefab = GameManager.Instance.deadBodyPrefab[0];
        var body = Object.Instantiate(prefab);
        body.enabled = true;
        body.gameObject.SetActive(true);
        body.ParentId = PlayerId;
        foreach (var r in body.bodyRenderers) Player.SetPlayerMaterialColors(r);
        Player.SetPlayerMaterialColors(body.bloodSplatter);
        Vector3 at = Player.transform.position + Player.KillAnimations[0].BodyOffset;
        at.z = at.y / 1000f;
        body.transform.position = at;
        _body = body;
    }

    private void CleanBody()
    {
        if (_body) Object.Destroy(_body.gameObject);
        _body = null;
    }

    // 全員の端末で: 生き返る (anim = ベントから出る動き)
    private static void Reborn(PlayerControl p, bool anim)
    {
        if (!p || RoleState.Of(p) is not Busker b || !b._faking) return;
        b._faking = false;
        b.CleanBody();
        p.Revive();
        RoleManager.Instance.SetRole(p, b.BaseRole);
        if (p.AmOwner)
        {
            b._fake?.Button?.SetGhost(false);
            ModButton.SetHudShown(true, false);
        }
        if (anim && p.MyPhysics) p.MyPhysics.StartCoroutine(p.MyPhysics.Animations.CoPlayExitVentAnimation());
    }

    // ホストの端末で: 本当に死ぬ (黙って生き返らせてから死体を残さずに倒す)
    private static void HostRealDeath(PlayerControl p)
    {
        if (!p || RoleState.Of(p) is not Busker b || !b._faking) return;
        QuietCall.Send(p.PlayerId);
        Reborn(p, false);
        PortKit.HostVanish(p, p);
    }
}
