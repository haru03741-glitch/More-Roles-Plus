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
    public static readonly FloatOpt ExplosionRange = new("RocketLauncher.ExplosionRange", 0.75f, 0.25f, 2f, 0.25f, "x");
    public static readonly FloatOpt LaunchSpeed = new("RocketLauncher.LaunchSpeed", 1.5f, 0.5f, 3f, 0.25f, "x");
    public static readonly FloatOpt WallBlastSize = new("RocketLauncher.WallBlastSize", 2f, 0.5f, 3f, 0.25f);
    public static readonly IntOpt WallBlastPower = new("RocketLauncher.WallBlastPower", 100, 0, 100, 10, "%");
    public static readonly BoolOpt LimitLoadedTime = new("RocketLauncher.LimitLoadedTime", false);
    public static readonly FloatOpt LoadedTimeLimit = new("RocketLauncher.LoadedTimeLimit", 10f, 2.5f, 60f, 2.5f, "s") { When = () => LimitLoadedTime };
    public static readonly BoolOpt CanUseNormalKill = new("RocketLauncher.CanUseNormalKill", true);

    // 撃たれた人がぶつかった所で地形ごと爆発する (音・土煙・瓦礫・壁の穴は地形の仕組みが出す。閃光と煙の輪と揺れは RocketCannon.Impact)
    private static float BlastRadius => System.Math.Clamp(WallBlastSize, 0.5f, Terrain.TerrainPermit.MaxRadius);
    public override Terrain.TerrainPermit TerrainPermit => Terrain.TerrainPermit.None.WithBlast(BlastRadius);

    private const float BaseSpeed = 7.5f;
    private static float Speed => BaseSpeed * System.Math.Clamp(LaunchSpeed, 0.5f, 3f);
    // 飛べる長さ。地形の爆発は撃った人から一定の距離までしか通らないので、その手前で止める
    private const float MaxTravel = 18f;
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
            RocketCannon.Drop(this);
        });
    }

    private Vector2 HoldPosition()
    {
        // 大砲が出ていれば、装填の動き (持ち上げて砲口へ押し込む) と筒の中の位置はそちらが決める
        var cannon = RocketCannon.For(this);
        if (cannon != null && cannon.HoldPosition(out var at)) return at;
        bool left = Player.cosmetics && Player.cosmetics.FlipX;
        return (Vector2)Player.transform.position + new Vector2(left ? -0.45f : 0.45f, 0.25f);
    }

    // 狙いの向き: PC はマウスの方・スマホは最後に歩いた向き
    internal static Vector2? TestAim; // 試験用: 狙いの向きを固定する (bridge の rlaim)

    private Vector2 AimDir()
    {
        if (TestAim is { } fixedAim) return fixedAim;
        if (System.OperatingSystem.IsAndroid() || !Camera.main) return ShotKit.Facing;
        Vector2 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector2 from = Player.transform.position;
        float dx = mouse.x - from.x, dy = mouse.y - from.y, d = System.MathF.Sqrt(dx * dx + dy * dy);
        return d < 0.01f ? ShotKit.Facing : new Vector2(dx / d, dy / d);
    }

    private void Shoot()
    {
        Vector2 dir = AimDir();
        var cannon = RocketCannon.For(this);
        Vector2 start = cannon != null ? cannon.Muzzle(dir) : HoldPosition() + new Vector2(0f, -0.4f);
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
        RocketCannon.Load(rl, user, t);
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
            RocketCannon.Drop(this);
            return;
        }
        if (IsLocal)
        {
            ShotKit.Track();
            // 自分の画面だけ、筒を狙いの方へ向ける
            if (_held != 255 && RocketCannon.For(this) is { } c)
            {
                var a = AimDir();
                c.Aim(System.MathF.Atan2(a.y, a.x) * (180f / System.MathF.PI));
            }
        }
        if (_held != 255)
        {
            var t = PortKit.ById(_held);
            if (!PortKit.Alive(t)) { Release(_held); _held = 255; RocketCannon.Drop(this); }
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
        bool hit = false, wallHit = false;
        Vector2 at = next, wallPoint = next;
        if (IsLocal && !_exploded)
        {
            // 壁と物と家具 (層 12 は本編のマスクに入っていない) だけを、トリガーを飛ばして見る。
            // 人の層を入れると、飛ばしている本人の体に当たる
            float look = Speed * dt + 0.3f;
            float d = ShotKit.WallDistance(_pos, _dir, look, Constants.ShipAndObjectsMask | (1 << 12));
            if (d < look)
            {
                hit = wallHit = true;
                wallPoint = _pos + _dir * d;
                Plugin.Logger.LogInfo($"rocket launcher: hit wall at {wallPoint.x:0.00},{wallPoint.y:0.00}");
                at = wallPoint - _dir * 0.55f;
            }
            else if (CollideWithPlayers)
            {
                foreach (var p in PlayerControl.AllPlayerControls)
                {
                    if (!PortKit.Alive(p) || p == t || p.PlayerId == PlayerId || p.inVent) continue;
                    if (Vector2.Distance(next, p.GetTruePosition()) <= PlayerRadius + 0.3f) { hit = true; break; }
                }
            }
            if (_life * Speed >= MaxTravel) hit = true;
        }
        _pos = hit ? at : next;
        MoveTo(t, _pos);
        if (hit)
        {
            _exploded = true;
            var victims = Victims(t, _pos);
            ExplodeCall.Send((_pos, victims));
            Explode(Player, _pos, victims);
            // 壁に当たったら壁の上で、飛んできた向きへ涙形に抜ける爆発 (壁の奥まで壊れる)。人に当たった・止まった時は円
            var res = Terrain.TerrainApi.Blast(wallHit ? wallPoint : _pos, BlastRadius, wallHit ? _dir : Vector2.zero, wallHit ? WallBlastPower / 100f : 0f);
            if (!res.Ok) Plugin.Logger.LogInfo($"rocket launcher: terrain blast refused ({res.Why})");
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
        Explode(Player, m.Item1, m.Item2);
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
        RocketCannon.For(rl)?.Fire(dir);
        MoveTo(PortKit.ById(target), start);
    }

    private float _blastAt = -9f; // 全員の端末: 最後に爆発した時刻 (爆発で倒す本編のキルで撃った人を動かさないため)

    // 爆発から少しの間 (本編のキルが全員に届くまで) に起きたその人のキルは、離れた所からのキル
    internal static bool RangedKill(PlayerControl p) => RoleState.Of(p) is RocketLauncher rl && Time.time - rl._blastAt < 1.5f;

    private static void Explode(PlayerControl user, Vector2 at, List<byte> victims)
    {
        if (RoleState.Of(user) is not RocketLauncher rl) return;
        rl._blastAt = Time.time;
        bool flew = rl._shot != 255;
        foreach (var id in new[] { rl._held, rl._shot })
            if (id != 255)
            {
                MoveTo(PortKit.ById(id), at);
                Release(id);
            }
        rl._held = rl._shot = 255;
        rl._flying = false;
        // 撃つ前に終わった (会議で掴んでいた人が死んだ) 時は大砲ごと片付ける。撃った後は大砲が自分で沈む
        if (flew)
        {
            RocketCannon.For(rl)?.Landed();
            RocketCannon.Impact(at, BlastRadius);
        }
        else RocketCannon.Drop(rl);
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
