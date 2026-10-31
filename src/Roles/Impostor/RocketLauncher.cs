// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/RocketLauncher.cs, Roles/Ability/RocketLauncherAbility.cs,
// CustomObject/RocketLauncherProjectile.cs, CustomObject/RocketLauncherHeldPlayer.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ロケットランチャー (インポスター): 「装填」で近くのクルーを掴み (その人は動けない)、「発射」で向いている方へ撃ち出す。
// 撃たれた人は壁 (設定で人) に当たると爆発し、周りの人ごと死ぬ。会議が始まると掴んでいる・飛んでいる人は死ぬ
public sealed class RocketLauncher : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("RocketLauncher.Name");
    public override string Reading => "ろけっとらんちゃー";
    public override Text Blurb => new("RocketLauncher.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("RocketLauncher.Load", "snr_grab"), new("RocketLauncher.Shot", "snr_rocket") };

    public static readonly FloatOpt LaunchCooldown = new("RocketLauncher.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt CollideWithPlayers = new("RocketLauncher.CollideWithPlayers", true);
    public static readonly BoolOpt KillImpostors = new("RocketLauncher.KillImpostors", false);
    public static readonly FloatOpt ExplosionRange = new("RocketLauncher.ExplosionRange", 0.5f, 0.25f, 2f, 0.25f, "x");
    public static readonly BoolOpt LimitLoadedTime = new("RocketLauncher.LimitLoadedTime", false);
    public static readonly FloatOpt LoadedTimeLimit = new("RocketLauncher.LoadedTimeLimit", 10f, 2.5f, 60f, 2.5f, "s") { When = () => LimitLoadedTime };
    public static readonly BoolOpt CanUseNormalKill = new("RocketLauncher.CanUseNormalKill", true);

    private const float Speed = 7.5f;
    private const float MaxLifetime = 8f;
    private const float PlayerRadius = 0.1f;

    // 掴まれている・飛んでいる人 (全員の端末で同じ)。動けない
    internal static readonly HashSet<byte> Held = new();

    private byte _held = 255;
    private float _heldFor;
    private bool _flying;
    private byte _shot = 255;
    private Vector2 _pos, _dir;
    private float _life;
    private bool _exploded;
    private Ability _load;

    private bool Busy => _held != 255 || _flying;

    public override void OnAssigned()
    {
        _load = AddAbility(new Ability(new Text("RocketLauncher.Load"), "snr_grab")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => LaunchCooldown,
            CanTarget = (_, t) => (!IsLocal || !Busy) && !PortKit.IsImpostor(t),
            OnUsed = (user, target) => Grab(user, target.PlayerId),
        });
        AddAbility(new Ability(new Text("RocketLauncher.Shot"), "snr_rocket")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                if (_held == 255 || _flying || !Player.CanMove) return false;
                Shoot();
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            if (_held != 255) Release(_held);
            if (_shot != 255) Release(_shot);
        });
    }

    private Vector2 HoldPosition()
    {
        bool left = Player.cosmetics && Player.cosmetics.FlipX;
        return (Vector2)Player.transform.position + new Vector2(left ? -0.45f : 0.45f, 0.25f);
    }

    private void Shoot()
    {
        Vector2 start = HoldPosition() + new Vector2(0f, -0.4f);
        Vector2 dir = Player.cosmetics && Player.cosmetics.FlipX ? Vector2.left : Vector2.right;
        var m = (_held, start, dir);
        ShotCall.Send(m);
        StartShot(Player, m.Item1, m.Item2, m.Item3);
        if (CanUseNormalKill) Player.SetKillTimer(GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
    }

    private static void Grab(PlayerControl user, byte target)
    {
        if (RoleState.Of(user) is not RocketLauncher rl) return;
        var t = PortKit.ById(target);
        if (!PortKit.Alive(t)) return;
        if (rl._held != 255 && rl._held != target) Release(rl._held);
        rl._held = target;
        rl._heldFor = 0f;
        Held.Add(target);
        if (t.MyPhysics && t.MyPhysics.body) t.MyPhysics.body.velocity = Vector2.zero;
    }

    private static void Release(byte id)
    {
        Held.Remove(id);
        var t = PortKit.ById(id);
        if (t && t.NetTransform) t.NetTransform.enabled = true;
    }

    private static void MoveTo(PlayerControl p, Vector2 at)
    {
        if (!p) return;
        p.transform.position = new Vector3(at.x, at.y, at.y / 1000f);
        if (p.MyPhysics && p.MyPhysics.body) p.MyPhysics.body.velocity = Vector2.zero;
    }

    public override void OnFixedUpdate()
    {
        if (!PortKit.Alive(Player))
        {
            if (_held != 255) { Release(_held); _held = 255; }
            if (_flying) { Release(_shot); _flying = false; _shot = 255; }
            return;
        }
        if (_held != 255)
        {
            var t = PortKit.ById(_held);
            if (!PortKit.Alive(t)) { Release(_held); _held = 255; }
            else if (!MeetingHud.Instance)
            {
                MoveTo(t, HoldPosition());
                _heldFor += Time.fixedDeltaTime;
                if (IsLocal && LimitLoadedTime && _heldFor >= LoadedTimeLimit) Shoot();
            }
        }
        if (_flying) Fly();
        if (IsLocal)
        {
            var hud = Vanilla.Hud;
            if ((!CanUseNormalKill || Busy) && hud && hud.KillButton && hud.KillButton.gameObject.activeSelf) hud.KillButton.gameObject.SetActive(false);
        }
    }

    // 全員の端末で飛ばす。当たりの判定は撃った人の端末でする
    private void Fly()
    {
        var t = PortKit.ById(_shot);
        if (!PortKit.Alive(t))
        {
            Release(_shot);
            _flying = false;
            _shot = 255;
            return;
        }
        float dt = Time.fixedDeltaTime;
        _life += dt;
        Vector2 step = _dir * Speed * dt;
        Vector2 next = _pos + step;
        bool hit = false;
        Vector2 at = next;
        if (IsLocal && !_exploded)
        {
            var wall = Physics2D.Raycast(_pos, _dir, step.magnitude + 0.3f, Constants.ShipAndAllObjectsMask);
            if (wall.collider)
            {
                hit = true;
                at = wall.point - _dir * 0.55f;
            }
            else if (CollideWithPlayers)
            {
                foreach (var p in PlayerControl.AllPlayerControls)
                {
                    if (!PortKit.Alive(p) || p == t || p.PlayerId == PlayerId || p.inVent) continue;
                    if (Vector2.Distance(next, p.GetTruePosition()) <= PlayerRadius + 0.3f) { hit = true; break; }
                }
            }
            if (_life >= MaxLifetime) hit = true;
        }
        _pos = hit ? at : next;
        MoveTo(t, _pos);
        if (hit)
        {
            _exploded = true;
            var victims = Victims(t, _pos);
            ExplodeCall.Send((_pos, victims));
            Explode(Player, _pos, victims);
        }
    }

    private List<byte> Victims(PlayerControl launched, Vector2 at)
    {
        var list = new List<byte>();
        if (PortKit.Alive(launched)) list.Add(launched.PlayerId);
        float range = ExplosionRange * 2f;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p == launched || p.inVent || p.PlayerId == PlayerId) continue;
            if (!KillImpostors && PortKit.IsImpostor(p)) continue;
            if (Vector2.Distance(at, p.GetTruePosition()) > range) continue;
            list.Add(p.PlayerId);
        }
        return list;
    }

    // 会議が始まったら、掴んでいる人・飛んでいる人はその場で死ぬ
    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        byte id = _held != 255 ? _held : _flying ? _shot : (byte)255;
        if (id == 255) return;
        var t = PortKit.ById(id);
        var m = ((Vector2)(t ? t.transform.position : Player.transform.position), new List<byte> { id });
        ExplodeCall.Send(m);
        Explode(Player, m.Item1, m.Item2, false);
    }

    private static readonly RemoteCall<(byte target, Vector2 start, Vector2 dir)> ShotCall = new("RocketLauncher.Shot", Route.Anyone,
        (w, m) => { w.Write(m.target); w.Write(m.start.x); w.Write(m.start.y); w.Write(m.dir.x); w.Write(m.dir.y); },
        r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (sender, m) => StartShot(sender, m.target, m.start, m.dir));

    private static readonly RemoteCall<(Vector2 at, List<byte> victims)> ExplodeCall = new("RocketLauncher.Explode", Route.Anyone,
        (w, m) =>
        {
            w.Write(m.at.x); w.Write(m.at.y);
            w.WritePacked(m.victims.Count);
            foreach (var v in m.victims) w.Write(v);
        },
        r =>
        {
            var at = new Vector2(r.ReadSingle(), r.ReadSingle());
            int n = r.ReadPackedInt32();
            var list = new List<byte>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadByte());
            return (at, list);
        },
        (sender, m) => Explode(sender, m.at, m.victims));

    private static void StartShot(PlayerControl user, byte target, Vector2 start, Vector2 dir)
    {
        if (RoleState.Of(user) is not RocketLauncher rl) return;
        rl._held = 255;
        rl._shot = target;
        rl._flying = true;
        rl._exploded = false;
        rl._pos = start;
        rl._dir = dir;
        rl._life = 0f;
        Held.Add(target);
        MoveTo(PortKit.ById(target), start);
    }

    private static void Explode(PlayerControl user, Vector2 at, List<byte> victims, bool effect = true)
    {
        if (RoleState.Of(user) is not RocketLauncher rl) return;
        foreach (var id in new[] { rl._held, rl._shot })
            if (id != 255)
            {
                MoveTo(PortKit.ById(id), at);
                Release(id);
            }
        rl._held = rl._shot = 255;
        rl._flying = false;
        if (effect)
        {
            ShotKit.Beam(at + new Vector2(-ExplosionRange, 0f), at + new Vector2(ExplosionRange, 0f), new UnityEngine.Color(1f, 0.5f, 0f, 0.8f), ExplosionRange * 2f, 0.4f);
            if (PlayerControl.LocalPlayer && Vector2.Distance(PlayerControl.LocalPlayer.transform.position, at) < 4f) PortKit.Flash(new UnityEngine.Color(1f, 0.5f, 0f), 0.4f);
        }
        if (!AmongUsClient.Instance.AmHost) return;
        _exploding = true;
        try
        {
            foreach (var id in victims)
            {
                var v = PortKit.ById(id);
                if (PortKit.Alive(v) && PortKit.Alive(user)) PortKit.HostKill(user, v);
            }
        }
        finally { _exploding = false; }
    }

    // 普通のキルは設定で止める・掴んでいる / 飛ばしている間は止める
    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Killer == e.Target) return;
        if (_exploding) return;
        if (!CanUseNormalKill) e.Cancel("rocket launcher cannot kill normally");
    }

    private static bool _exploding;
}
