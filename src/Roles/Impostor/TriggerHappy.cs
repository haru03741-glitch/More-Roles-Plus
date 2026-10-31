// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/TriggerHappy.cs, Roles/Ability/TriggerHappyAbility.cs,
// CustomObject/TriggerHappyBullet.cs, CustomObject/TriggerHappyGatlingGun.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// トリガーハッピー (インポスター): 「乱射」でしばらく機関銃を構え、マウスの方へ弾をばらまき続ける。
// 同じ人に決まった数当たると倒れる。弾は壁で止まり (設定で抜ける)、賢者のバリアには跳ね返される
public sealed class TriggerHappy : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("TriggerHappy.Name");
    public override string Reading => "とりがーはっぴー";
    public override Text Blurb => new("TriggerHappy.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("TriggerHappy.Spray", "snr_gun") };

    public static readonly IntOpt UseLimit = new("TriggerHappy.UseLimit", 1, 1, 10);
    public static readonly FloatOpt SprayCooldown = new("TriggerHappy.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt SyncKillCooldown = new("TriggerHappy.SyncKillCooldown", false);
    public static readonly FloatOpt SprayDuration = new("TriggerHappy.Duration", 5f, 1f, 15f, 1f, "s");
    public static readonly IntOpt RequiredHits = new("TriggerHappy.RequiredHits", 50, 5, 100, 5);
    public static readonly FloatOpt SprayRange = new("TriggerHappy.Range", 5f, 1f, 30f, 1f, "m");
    public static readonly FloatOpt BulletSize = new("TriggerHappy.BulletSize", 1f, 0.25f, 3f, 0.25f);
    public static readonly BoolOpt PierceWalls = new("TriggerHappy.PierceWalls", false);
    public static readonly ChoiceOpt SpreadAngle = new("TriggerHappy.SpreadAngle", 2,
        Text.Raw("15°"), Text.Raw("30°"), Text.Raw("45°"), Text.Raw("60°"));
    public static readonly BoolOpt FriendlyFire = new("TriggerHappy.FriendlyFire", true);
    public static readonly BoolOpt KillSound = new("TriggerHappy.KillSound", true);

    private const float Speed = 10f, FireInterval = 1f / 60f, BatchInterval = 0.2f, Radius = 0.15f, Hand = 0.8f, Height = 0.35f;

    private sealed class Shot
    {
        public GameObject Obj;
        public Transform Tr;
        public float X, Y, Dx, Dy; // 位置と向き (毎 tick は Unity を介さずここで進める)
        public float Traveled;
        public float MaxTravel;    // 射程か、撃った時に測った壁までの距離
        public bool Real;          // 撃った人の端末の弾 (当たり判定をする)
    }

    private const int MaxLive = 160; // 1 人ぶんの飛んでいる弾の上限 (止まった端末の後で一度に湧いても重くしない)
    private const int MaxBatch = 40; // 1 通に載せる弾の数 (16B × 40 = 640B)
    private float _activeUntil;      // ホスト: 乱射の終わりから少しの間はキルの申告を受ける (まとめて送るぶんの遅れ)

    private readonly List<Shot> _shots = new();
    private readonly Dictionary<byte, int> _hits = new();
    private readonly List<(Vector2 pos, Vector2 dir)> _batch = new();
    private Ability _spray;
    private bool _active;
    private GameObject _gun;
    private float _angle, _fireTimer, _batchTimer;
    private Vector2 _aim = Vector2.right, _lastFire;
    private Shot _lastShot;

    public override void OnAssigned()
    {
        _spray = AddAbility(new Ability(new Text("TriggerHappy.Spray"), "snr_gun")
        {
            Cooldown = () => SprayCooldown,
            Duration = () => SprayDuration,
            MaxUses = () => UseLimit,
            OnUse = () =>
            {
                if (!Player.CanMove) return false;
                StateCall.Send(true);
                SetActive(true);
                if (SyncKillCooldown) Player.SetKillTimer(GameManager.Instance.LogicOptions.GetKillCooldown());
                return true;
            },
            OnEnd = () =>
            {
                Flush();
                StateCall.Send(false);
                SetActive(false);
            },
        });
        Lifespan.OnRelease(() =>
        {
            SetActive(false);
            ClearShots();
        });
    }

    private static readonly RemoteCall<bool> StateCall = new("TriggerHappy.State", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is TriggerHappy t) t.SetActive(on); });

    // 全員の端末で: 機関銃を構える / しまう
    private void SetActive(bool on)
    {
        _active = on;
        if (!on) _activeUntil = Time.time + 1f;
        _hits.Clear();
        _batch.Clear();
        _lastShot = null;
        _fireTimer = _batchTimer = 0f;
        if (on && !_gun)
        {
            _gun = new GameObject("MrpGatlingGun");
            var lr = _gun.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.useWorldSpace = false;
            lr.SetPosition(0, new Vector3(-0.35f, 0f, 0f));
            lr.SetPosition(1, new Vector3(0.45f, 0f, 0f));
            lr.startWidth = 0.22f;
            lr.endWidth = 0.12f;
            lr.sharedMaterial = ShotKit.LineMat;
            lr.startColor = lr.endColor = new Color(0.2f, 0.2f, 0.22f);
        }
        if (!on && _gun)
        {
            Object.Destroy(_gun);
            _gun = null;
        }
    }

    private Vector2 Aim()
    {
        if (System.OperatingSystem.IsAndroid() || !Camera.main) return ShotKit.Facing;
        Vector2 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        var d = mouse - Player.GetTruePosition();
        return d.sqrMagnitude < 0.0001f ? _aim : d.normalized;
    }

    public override void OnFixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (IsLocal) ShotKit.Track();
        if (MeetingHud.Instance || !PortKit.Alive(Player))
        {
            if (_active) SetActive(false);
            ClearShots();
            return;
        }
        MoveShots(dt);
        if (!_active) return;
        if (IsLocal)
        {
            // 向きはなめらかにマウスへ寄せる
            var target = Aim();
            float want = Mathf.Atan2(target.y, target.x) * Mathf.Rad2Deg;
            _angle = Mathf.LerpAngle(_angle, want, dt / 0.1f);
            _aim = new Vector2(Mathf.Cos(_angle * Mathf.Deg2Rad), Mathf.Sin(_angle * Mathf.Deg2Rad));
            if (!Player.inVent && (_fireTimer -= dt) <= 0f)
            {
                _fireTimer = FireInterval;
                Fire();
            }
            if ((_batchTimer += dt) >= BatchInterval)
            {
                Flush();
                _batchTimer = 0f;
                if (SyncKillCooldown) Player.SetKillTimer(GameManager.Instance.LogicOptions.GetKillCooldown());
            }
        }
        if (_gun)
        {
            var dir = new Vector2(Mathf.Cos(_angle * Mathf.Deg2Rad), Mathf.Sin(_angle * Mathf.Deg2Rad));
            var at = Player.GetTruePosition() + dir * Hand;
            _gun.transform.position = new Vector3(at.x, at.y + Height, Player.transform.position.z - 0.5f);
            _gun.transform.rotation = Quaternion.Euler(0f, 0f, _angle);
            _gun.SetActive(!Player.inVent);
        }
    }

    // 自分の端末で: 1 発撃つ (前の弾との間にもう 1 発足して弾幕を詰める)
    private void Fire()
    {
        float half = new[] { 15, 30, 45, 60 }[Mathf.Clamp(SpreadAngle.Value, 0, 3)] * 0.5f;
        var dir = (Vector2)(Quaternion.Euler(0f, 0f, Random.Range(-half, half)) * _aim);
        var pos = Player.GetTruePosition() + _aim * 0.6f + new Vector2(0f, Height);
        var shot = Spawn(pos, dir, true);
        _batch.Add((pos, dir));
        if (_batch.Count >= MaxBatch) Flush();
        if (_lastShot != null && _lastShot.Obj)
        {
            var mid = new Vector2((_lastShot.X + _lastFire.x) * 0.5f, (_lastShot.Y + _lastFire.y) * 0.5f);
            Spawn(mid, dir, true);
            _batch.Add((mid, dir));
            if (_batch.Count >= MaxBatch) Flush();
        }
        _lastShot = shot;
        _lastFire = pos;
    }

    // 自分の端末で: 撃った弾を他の人の画面にもまとめて送る
    private void Flush()
    {
        if (_batch.Count == 0) return;
        if (!MeetingHud.Instance) BatchCall.Send((_angle, _batch.ToArray()));
        _batch.Clear();
    }

    private static readonly RemoteCall<(float angle, (Vector2 pos, Vector2 dir)[] shots)> BatchCall = new("TriggerHappy.Batch", Route.Anyone,
        (w, m) =>
        {
            w.Write(m.angle);
            w.Write((ushort)m.shots.Length);
            foreach (var (pos, dir) in m.shots) { w.Write(pos.x); w.Write(pos.y); w.Write(dir.x); w.Write(dir.y); }
        },
        r =>
        {
            float angle = r.ReadSingle();
            var shots = new (Vector2, Vector2)[r.ReadUInt16()];
            for (int i = 0; i < shots.Length; i++)
                shots[i] = (new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle()));
            return (angle, shots);
        },
        (sender, m) =>
        {
            if (RoleState.Of(sender) is not TriggerHappy t || MeetingHud.Instance) return;
            t._angle = m.angle;
            foreach (var (pos, dir) in m.shots) t.Spawn(pos, dir, false);
        });

    private Shot Spawn(Vector2 pos, Vector2 dir, bool real)
    {
        if (_shots.Count >= MaxLive) Remove(0); // 一番古い弾から消す
        var go = new GameObject("MrpTriggerHappyBullet");
        var tr = go.transform;
        tr.position = FxMath.V3(pos.x, pos.y, -5f);
        tr.rotation = Quaternion.Euler(0f, 0f, FxMath.Atan2(dir.y, dir.x) * (180f / System.MathF.PI));
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.useWorldSpace = false;
        lr.SetPosition(0, new Vector3(-0.12f * BulletSize, 0f, 0f));
        lr.SetPosition(1, new Vector3(0.12f * BulletSize, 0f, 0f));
        lr.startWidth = lr.endWidth = 0.08f * BulletSize;
        lr.sharedMaterial = ShotKit.LineMat;
        lr.startColor = new Color(1f, 0.6f, 0.1f, 0.6f);
        lr.endColor = new Color(1f, 0.95f, 0.5f, 1f);
        // 壁までの距離は撃った時に 1 回だけ測る (飛んでいる間に壁を調べ直さない)
        float range = SprayRange;
        float max = PierceWalls ? range : ShotKit.WallDistance(pos, dir, range);
        var s = new Shot { Obj = go, Tr = tr, X = pos.x, Y = pos.y, Dx = dir.x, Dy = dir.y, MaxTravel = max, Real = real };
        _shots.Add(s);
        return s;
    }

    private void ClearShots()
    {
        foreach (var s in _shots) if (s.Obj) Object.Destroy(s.Obj);
        _shots.Clear();
    }

    private void MoveShots(float dt)
    {
        if (_shots.Count == 0) return;
        bool cached = false;
        float step = Speed * dt;
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            var s = _shots[i];
            if (!s.Obj) { _shots.RemoveAt(i); continue; }
            s.X += s.Dx * step;
            s.Y += s.Dy * step;
            s.Traveled += step;
            if (s.Traveled >= s.MaxTravel)
            {
                Remove(i);
                continue;
            }
            s.Tr.position = FxMath.V3(s.X, s.Y, -5f);
            if (!s.Real) continue;
            if (!cached) { CacheTargets(); cached = true; }
            if (TryHit(s)) Remove(i);
        }
    }

    // 撃った人の端末で: 当たりの候補の位置を tick に 1 回だけ読む
    private readonly List<(PlayerControl p, float x, float y)> _targets = new();
    private void CacheTargets()
    {
        _targets.Clear();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId || p.inVent) continue;
            if (!FriendlyFire && SameSide(p)) continue;
            var pos = p.GetTruePosition();
            _targets.Add((p, pos.x, pos.y + 0.3f));
        }
    }

    private void Remove(int i)
    {
        if (_shots[i].Obj) Object.Destroy(_shots[i].Obj);
        _shots.RemoveAt(i);
    }

    // 撃った人の端末で: 弾が人に当たったか (遠くの壁は撃った時の距離で切ってあるので、体と弾の間の壁だけ確かめる)
    private bool TryHit(Shot s)
    {
        float r = Radius * BulletSize + 0.3f, r2 = r * r;
        for (int k = 0; k < _targets.Count; k++)
        {
            var (p, px, py) = _targets[k];
            float dx = px - s.X, dy = py - s.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;
            if (!PierceWalls && d2 > 0.0001f)
            {
                float d = FxMath.Sqrt(d2);
                if (PhysicsHelpers.AnyNonTriggersBetween(FxMath.V2(s.X, s.Y), FxMath.V2(dx / d, dy / d), d, Constants.ShipAndObjectsMask)) continue;
            }
            Hit(p);
            _targets.RemoveAt(k);
            return true;
        }
        return false;
    }

    // 同じ陣営 (仲間に当てない設定の時)
    private bool SameSide(PlayerControl p)
    {
        if (PortKit.IsImpostor(Player) && PortKit.IsImpostor(p)) return true;
        return !PortKit.IsImpostor(Player) && !PortKit.IsNeutral(Player) && !PortKit.IsImpostor(p) && !PortKit.IsNeutral(p);
    }

    private void Hit(PlayerControl p)
    {
        _hits.TryGetValue(p.PlayerId, out int n);
        if (++n < RequiredHits)
        {
            _hits[p.PlayerId] = n;
            return;
        }
        _hits.Remove(p.PlayerId);
        KillCall.Send(p.PlayerId);
        if (AmongUsClient.Instance.AmHost) HostKill(Player, p);
        if (KillSound)
        {
            byte id = p.PlayerId;
            PortKit.After(0.1f, () =>
            {
                var v = PortKit.ById(id);
                var lp = PlayerControl.LocalPlayer;
                if (v && !PortKit.Alive(v) && lp && SoundManager.Instance) SoundManager.Instance.PlaySound(lp.KillSfx, false, 0.8f);
            });
        }
    }

    private static readonly RemoteCall<byte> KillCall = new("TriggerHappy.Kill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) =>
        {
            // 乱射中 (か終わった直後) の人の申告だけ受ける
            if (RoleState.Of(sender) is TriggerHappy th && (th._active || Time.time < th._activeUntil) && PortKit.ById(id) is { } t) HostKill(sender, t);
        });

    // ホストの端末で: 陣営やキルを防ぐ役職に関係なく倒す
    private static void HostKill(PlayerControl killer, PlayerControl target)
    {
        if (!PortKit.Alive(killer) || !PortKit.Alive(target)) return;
        killer.RpcMurderPlayer(target, true);
    }

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (SyncKillCooldown && e.Killer && e.Killer.PlayerId == PlayerId) _spray?.Button?.StartCooldown();
    }
}
