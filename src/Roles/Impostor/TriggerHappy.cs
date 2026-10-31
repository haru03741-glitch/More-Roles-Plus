// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/TriggerHappy.cs, Roles/Ability/TriggerHappyAbility.cs,
// CustomObject/TriggerHappyBullet.cs, CustomObject/TriggerHappyGatlingGun.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
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
        public Vector2 Dir;
        public float Traveled;
        public bool Real;     // 撃った人の端末の弾 (当たり判定をする)
        public int IgnoreWise = -1;
        public float IgnoreTimer;
    }

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
            lr.material = new Material(Shader.Find("Sprites/Default"));
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
        if (_lastShot != null && _lastShot.Obj)
        {
            var mid = ((Vector2)_lastShot.Obj.transform.position + _lastFire) / 2f;
            Spawn(mid, dir, true);
            _batch.Add((mid, dir));
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
        var go = new GameObject("MrpTriggerHappyBullet");
        go.transform.position = new Vector3(pos.x, pos.y, -5f);
        go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.useWorldSpace = false;
        lr.SetPosition(0, new Vector3(-0.12f * BulletSize, 0f, 0f));
        lr.SetPosition(1, new Vector3(0.12f * BulletSize, 0f, 0f));
        lr.startWidth = lr.endWidth = 0.08f * BulletSize;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = new Color(1f, 0.6f, 0.1f, 0.6f);
        lr.endColor = new Color(1f, 0.95f, 0.5f, 1f);
        var s = new Shot { Obj = go, Dir = dir, Real = real };
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
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            var s = _shots[i];
            if (!s.Obj) { _shots.RemoveAt(i); continue; }
            if (s.IgnoreTimer > 0f && (s.IgnoreTimer -= dt) <= 0f) s.IgnoreWise = -1;
            Vector2 cur = s.Obj.transform.position;
            var step = s.Dir * Speed * dt;
            if (!PierceWalls && PhysicsHelpers.AnyNonTriggersBetween(cur, step.normalized, step.magnitude, Constants.ShipAndObjectsMask))
            {
                Remove(i);
                continue;
            }
            var next = cur + step;
            s.Obj.transform.position = new Vector3(next.x, next.y, -5f);
            s.Traveled += step.magnitude;
            if (s.Traveled >= SprayRange)
            {
                Remove(i);
                continue;
            }
            if (s.Real && TryHit(s, next)) Remove(i);
        }
    }

    private void Remove(int i)
    {
        if (_shots[i].Obj) Object.Destroy(_shots[i].Obj);
        _shots.RemoveAt(i);
    }

    // 撃った人の端末で: 弾が人に当たったか。賢者のバリアなら跳ね返す
    private bool TryHit(Shot s, Vector2 at)
    {
        float r = Radius * BulletSize + 0.3f;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId || p.inVent) continue;
            var pos = p.GetTruePosition();
            if (Vector2.Distance(pos + new Vector2(0f, 0.3f), at) > r) continue;
            if (!FriendlyFire && SameSide(p)) continue;
            if (!PierceWalls)
            {
                var to = pos - at;
                if (to.magnitude > 0.01f && PhysicsHelpers.AnyNonTriggersBetween(at, to.normalized, to.magnitude, Constants.ShipAndObjectsMask)) continue;
            }
            if (s.IgnoreWise == p.PlayerId) return false;
            if (RoleState.Of(p) is Crew.WiseMan w && w.Guarding)
            {
                Bounce(s);
                s.IgnoreWise = p.PlayerId;
                s.IgnoreTimer = 0.1f;
                return false;
            }
            Hit(p);
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

    // 来た向きの真逆 ±15° を除いた範囲へ跳ね返る
    private static void Bounce(Shot s)
    {
        float t = Random.Range(0f, 300f);
        float offset = t <= 150f ? 15f + t : 195f + (t - 150f);
        s.Dir = (Quaternion.Euler(0f, 0f, offset) * s.Dir).normalized;
        s.Obj.transform.position += (Vector3)(s.Dir * 0.05f);
        s.Obj.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(s.Dir.y, s.Dir.x) * Mathf.Rad2Deg);
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
            if (RoleState.Of(sender) is TriggerHappy && PortKit.ById(id) is { } t) HostKill(sender, t);
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
