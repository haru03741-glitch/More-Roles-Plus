// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Ability/WaveCannonAbility.cs, WaveCannonObj/*.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 波動砲を持つ役職 (波動砲・波動砲ジャッカル)
internal interface IWaveCannon
{
    WaveCannonGun Gun { get; }
}

// 波動砲の本体: ボタンを押すとその場に砲台を置いて動けなくなり、充填が終わると左右の向いている方へ
// 壁を抜けるビーム (戦車) か、走っていくサンタ (サンタ) を撃つ。当たった人は陣営に関係なく死ぬ。
// 賢者のバリアに当たると、ビームはそこで止まって別の向きへ跳ね返る。弾を込めた時は賢者も貫く
internal sealed class WaveCannonGun
{
    public const int Tank = 0, Santa = 1, Bullet = 2;
    private const float ShootTime = 2.88f, HalfHeight = 0.55f, Length = 60f;
    private const float SantaInterval = 0.3f, SantaSpeed = 6.5f, SantaRadius = 0.7f, SantaRange = 30f;
    private static readonly float[] WiseAngles = { 135f, 90f, 270f, 225f };

    private readonly RoleBase _role;
    private readonly Func<int> _type;
    private readonly Func<float> _charge;
    private readonly Func<bool> _sync, _sound, _distributed;
    private readonly Func<float> _killCooldown;
    public Ability Ability;

    // 込められた弾 (いなければ -1) とその時の充填時間
    public int BulletId = -1;
    private int _soundQueue;
    public float BulletCharge;

    private Cannon _cannon;
    public bool Active => _cannon != null;
    public bool ShootingBullet => _cannon != null && _cannon.Type == Bullet;

    private sealed class Cannon
    {
        public Vector2 Pos;
        public bool Left;
        public int Type;
        public bool Shooting;
        public float Timer = ShootTime, SantaTimer;
        public int Wise = -1;            // ビームを止めた賢者
        public Vector2 WisePos;
        public float WiseAngle;
        public GameObject Root, Charge, Beam, Reflect;
        public readonly HashSet<byte> Hit = new();
    }

    // 撃ち終わっても走り続けるサンタ
    private readonly List<(GameObject go, Vector2 dir, Vector2 origin)> _santas = new();

    public WaveCannonGun(RoleBase role, Func<int> type, Func<float> charge, Func<bool> sync, Func<bool> sound, Func<bool> distributed, Func<float> killCooldown)
    {
        _role = role;
        _type = type;
        _charge = charge;
        _sync = sync;
        _sound = sound;
        _distributed = distributed;
        _killCooldown = killCooldown;
        role.Lifespan.OnRelease(() =>
        {
            Detach();
            ClearSantas();
        });
    }

    // 役職の OnAssigned で AddAbility に渡す
    public Ability Make(Text label, Func<float> cooldown) => Ability = new Ability(label, "snr_wave")
    {
        Cooldown = cooldown,
        Duration = () => BulletId >= 0 ? BulletCharge : _charge(),
        OnUse = Fire,
        OnEnd = () =>
        {
            ShootCall.Send(0);
            Shoot();
        },
    };

    private PlayerControl Player => _role.Player;

    private bool Fire()
    {
        if (!Player.CanMove) return false;
        int type = BulletId >= 0 ? Bullet : _type();
        var pos = (Vector2)Player.transform.position;
        bool left = Player.cosmetics && Player.cosmetics.FlipX;
        SpawnCall.Send((pos, left, (byte)type));
        Spawn(pos, left, type);
        if (_sync()) Player.SetKillTimer(_killCooldown());
        return true;
    }

    private static readonly RemoteCall<(Vector2 pos, bool left, byte type)> SpawnCall = new("WaveCannon.Spawn", Route.Anyone,
        (w, m) => { w.Write(m.pos.x); w.Write(m.pos.y); w.Write(m.left); w.Write(m.type); },
        r => (new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadBoolean(), r.ReadByte()),
        (sender, m) => { if (RoleState.Of(sender) is IWaveCannon c) c.Gun.Spawn(m.pos, m.left, m.type); });

    private static readonly RemoteCall<byte> ShootCall = new("WaveCannon.Shoot", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is IWaveCannon c) c.Gun.Shoot(); });

    private static readonly RemoteCall<(byte wise, float angle)> WiseCall = new("WaveCannon.Wise", Route.HostToAll,
        (w, m) => { w.Write(m.wise); w.Write(m.angle); }, r => (r.ReadByte(), r.ReadSingle()),
        (sender, m) => { foreach (var r in RoleState.All) if (r is IWaveCannon c && c.Gun._cannon != null && c.Gun._cannon.Shooting && c.Gun._cannon.Wise < 0) c.Gun.OnWise(m.wise, m.angle); });

    // 全員の端末で: 砲台を置く
    private void Spawn(Vector2 pos, bool left, int type)
    {
        Detach(false);
        var c = _cannon = new Cannon { Pos = pos, Left = left, Type = type };
        Color color = Player.Data != null ? (Color)Palette.PlayerColors[Player.Data.DefaultOutfit.ColorId] : Color.white;
        c.Root = new GameObject("MrpWaveCannon");
        var body = Line(c.Root, "Tank", color, 0.9f);
        body.SetPosition(0, new Vector3(pos.x - 0.6f, pos.y - 0.1f, pos.y / 1000f - 0.1f));
        body.SetPosition(1, new Vector3(pos.x + 0.6f, pos.y - 0.1f, pos.y / 1000f - 0.1f));
        var barrel = Line(c.Root, "Barrel", color * 0.8f, 0.35f);
        float dir = left ? -1f : 1f;
        barrel.SetPosition(0, new Vector3(pos.x, pos.y + 0.15f, pos.y / 1000f - 0.11f));
        barrel.SetPosition(1, new Vector3(pos.x + dir * 1.1f, pos.y + 0.15f, pos.y / 1000f - 0.11f));
        c.Charge = new GameObject("Charge");
        c.Charge.transform.SetParent(c.Root.transform, false);
        var glow = Line(c.Charge, "Glow", BeamColor(type), 0.1f);
        var tip = new Vector3(pos.x + dir * 1.2f, pos.y + 0.15f, -5f);
        glow.SetPosition(0, tip);
        glow.SetPosition(1, tip + new Vector3(dir * 0.01f, 0f, 0f));
        Hide(true);
    }

    private static Color BeamColor(int type) => type == Bullet ? new Color(1f, 0.85f, 0.2f, 0.9f) : new Color(0.3f, 0.8f, 1f, 0.85f);

    private static LineRenderer Line(GameObject parent, string name, Color c, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.useWorldSpace = true;
        lr.startWidth = lr.endWidth = width;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = c;
        return lr;
    }

    private Vector2 Dir => _cannon.Left ? Vector2.left : Vector2.right;
    private Vector2 Muzzle => _cannon.Pos + Dir * 0.9f + new Vector2(0f, 0.15f);

    // 全員の端末で: 撃つ
    private void Shoot()
    {
        var c = _cannon;
        if (c == null || c.Shooting) return;
        c.Shooting = true;
        if (c.Charge) Object.Destroy(c.Charge);
        if (c.Type != Santa)
        {
            var beam = Line(c.Root, "Beam", BeamColor(c.Type), HalfHeight * 2f);
            c.Beam = beam.gameObject;
            SetBeam(Length);
        }
        else SpawnSanta();
        // 弾を込めていたら、弾は撃ち出されて消える
        if (BulletId >= 0)
        {
            var b = PortKit.ById((byte)BulletId);
            if (b && AmongUsClient.Instance.AmHost) PortKit.HostVanish(b, b);
            BulletId = -1;
        }
    }

    private void SetBeam(float length)
    {
        var lr = _cannon.Beam ? _cannon.Beam.GetComponent<LineRenderer>() : null;
        if (!lr) return;
        Vector2 a = Muzzle, b = a + Dir * length;
        lr.SetPosition(0, new Vector3(a.x, a.y, -5f));
        lr.SetPosition(1, new Vector3(b.x, b.y, -5f));
    }

    private void SpawnSanta()
    {
        var c = _cannon;
        var go = new GameObject("MrpWaveCannonSanta");
        go.transform.position = new Vector3(Muzzle.x, Muzzle.y, -5f);
        var lr = Line(go, "Sled", new Color(0.85f, 0.1f, 0.1f, 1f), 0.9f);
        lr.useWorldSpace = false;
        lr.SetPosition(0, new Vector3(-0.5f, 0f, 0f));
        lr.SetPosition(1, new Vector3(0.5f, 0f, 0f));
        var hat = Line(go, "Trim", Color.white, 0.25f);
        hat.useWorldSpace = false;
        hat.SetPosition(0, new Vector3(-0.5f, 0.45f, -0.01f));
        hat.SetPosition(1, new Vector3(0.5f, 0.45f, -0.01f));
        _santas.Add((go, Dir, c.Pos));
        c.SantaTimer = SantaInterval;
    }

    // 全員の端末で: 賢者のバリアがビームを止めた
    private void OnWise(byte wise, float angle)
    {
        var c = _cannon;
        var w = PortKit.ById(wise);
        if (c == null || !w) return;
        c.Wise = wise;
        c.WisePos = w.GetTruePosition();
        c.WiseAngle = angle;
        SetBeam(Mathf.Max(0f, Mathf.Abs(c.WisePos.x - Muzzle.x) - 0.8f));
        var lr = Line(c.Root, "Reflect", BeamColor(c.Type), HalfHeight * 2f);
        c.Reflect = lr.gameObject;
        Vector2 b = c.WisePos + ReflectDir * Length;
        lr.SetPosition(0, new Vector3(c.WisePos.x, c.WisePos.y, -5f));
        lr.SetPosition(1, new Vector3(b.x, b.y, -5f));
        if (w && RoleManager.Instance && RoleManager.Instance.protectAnim)
        {
            var anim = Object.Instantiate(RoleManager.Instance.protectAnim, w.gameObject.transform);
            anim.SetMaskLayerBasedOnWhoShouldSee(true);
            anim.Play(w, null, w.cosmetics.FlipX, RoleEffectAnimation.SoundType.Global);
        }
    }

    private Vector2 ReflectDir => Quaternion.Euler(0f, 0f, _cannon.WiseAngle) * (Vector3)Dir;

    // 全員の端末で毎フレーム (役職の OnFixedUpdate から)
    public void Tick()
    {
        if (_santas.Count > 0)
        {
            if (MeetingHud.Instance) ClearSantas();
            else
            {
                MoveSantas(Time.fixedDeltaTime);
                if (AmongUsClient.Instance.AmHost) HostSantaHits();
            }
        }
        var c = _cannon;
        if (c == null) return;
        if (!PortKit.Alive(Player) || MeetingHud.Instance)
        {
            Detach();
            return;
        }
        // 砲台に乗っている間は動けない
        if (Player.AmOwner)
        {
            Player.moveable = false;
            if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
        }
        Player.transform.position = new Vector3(c.Pos.x, c.Pos.y, Player.transform.position.z);
        if (Player.NetTransform) Player.NetTransform.SnapTo(c.Pos);
        Hide(true);
        float dt = Time.fixedDeltaTime;
        if (!c.Shooting)
        {
            // 充填中は砲口の光が育つ
            var glow = c.Charge ? c.Charge.GetComponentInChildren<LineRenderer>() : null;
            if (glow) glow.startWidth = glow.endWidth = Mathf.Min(1.1f, glow.startWidth + dt * 0.4f);
            return;
        }
        if (c.Type == Santa)
        {
            if ((c.SantaTimer -= dt) <= 0f) SpawnSanta();
        }
        else if (AmongUsClient.Instance.AmHost) HostHits();
        if ((c.Timer -= dt) <= 0f) Detach();
    }

    private void MoveSantas(float dt)
    {
        for (int i = _santas.Count - 1; i >= 0; i--)
        {
            var (s, dir, origin) = _santas[i];
            if (!s) { _santas.RemoveAt(i); continue; }
            s.transform.position += (Vector3)(dir * SantaSpeed * dt);
            if (Vector2.Distance(s.transform.position, origin) > SantaRange)
            {
                Object.Destroy(s);
                _santas.RemoveAt(i);
            }
        }
    }

    private void ClearSantas()
    {
        foreach (var s in _santas) if (s.go) Object.Destroy(s.go);
        _santas.Clear();
    }

    // ホストの端末で: サンタに触れた人を倒す (賢者のバリアは本編のキルと同じく跳ね返す)
    private void HostSantaHits()
    {
        if (!PortKit.Alive(Player)) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == Player.PlayerId) continue;
            Vector2 at = p.GetTruePosition();
            foreach (var s in _santas)
            {
                if (!s.go || Vector2.Distance(s.go.transform.position, at) > SantaRadius) continue;
                if (RoleState.Of(p) is Crew.WiseMan w && w.Guarding) PortKit.HostKill(Player, p);
                else Player.RpcMurderPlayer(p, true);
                break;
            }
        }
    }

    // ホストの端末で: 当たった人を倒す。砲台に近い人から順に見て、賢者のバリアに当たったらそこで止まる
    private void HostHits()
    {
        var c = _cannon;
        var list = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && p.PlayerId != Player.PlayerId && p.PlayerId != c.Wise) list.Add(p);
        list.Sort((a, b) =>
        {
            int d = Mathf.Abs(a.transform.position.x - c.Pos.x).CompareTo(Mathf.Abs(b.transform.position.x - c.Pos.x));
            return d != 0 ? d : a.PlayerId.CompareTo(b.PlayerId);
        });
        foreach (var p in list)
        {
            if (!Touching(p)) continue;
            if (c.Type != Bullet && c.Wise < 0 && RoleState.Of(p) is Crew.WiseMan w && w.Guarding)
            {
                float angle = WiseAngles[UnityEngine.Random.Range(0, WiseAngles.Length)];
                WiseCall.Send((p.PlayerId, angle));
                OnWise(p.PlayerId, angle);
                continue;
            }
            Kill(p);
        }
    }

    private bool Touching(PlayerControl p)
    {
        var c = _cannon;
        float len = Length;
        if (c.Wise >= 0)
        {
            len = Mathf.Max(0f, Mathf.Abs(c.WisePos.x - Muzzle.x) - 0.8f);
            if (ShotKit.OnLine(c.WisePos, c.WisePos + ReflectDir * Length, HalfHeight, p) >= 0f) return true;
        }
        Vector2 a = Muzzle - Dir * 1f;  // 砲台のすぐ後ろも含める
        return ShotKit.OnLine(a, Muzzle + Dir * len, HalfHeight, p) >= 0f;
    }

    // 陣営やキルを防ぐ役職に関係なく倒す
    private void Kill(PlayerControl p)
    {
        if (!_cannon.Hit.Add(p.PlayerId)) return;
        Player.RpcMurderPlayer(p, true);
    }

    // 撃った人の端末で: 自分のビームで倒した時の音とクールダウン
    public void OnMurdered(PlayerMurderedEvent e)
    {
        if (!Player || !Player.AmOwner || !e.Killer || e.Killer.PlayerId != Player.PlayerId) return;
        if ((_cannon != null && _cannon.Shooting || _santas.Count > 0) && _sound())
        {
            if (!_distributed()) PlayKill();
            else PortKit.After((_soundQueue++ % 10) * 0.1f, PlayKill);
        }
        if (_sync()) Ability?.Button?.StartCooldown();
    }

    private void PlayKill()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.KillSfx && SoundManager.Instance) SoundManager.Instance.PlaySound(lp.KillSfx, false, 0.8f);
    }

    private void Hide(bool hide)
    {
        if (!Player || !Player.cosmetics) return;
        Player.cosmetics.gameObject.SetActive(!hide);
        var body = Player.cosmetics.currentBodySprite;
        if (body != null && body.BodySprite) body.BodySprite.gameObject.SetActive(!hide);
    }

    // 全員の端末で: 砲台を片付ける
    public void Detach(bool reset = true)
    {
        var c = _cannon;
        if (c == null) return;
        _cannon = null;
        if (c.Root) Object.Destroy(c.Root);
        if (!Player) return;
        Hide(false);
        if (Player.AmOwner)
        {
            Player.moveable = true;
            if (reset && _sync()) Player.SetKillTimer(_killCooldown());
        }
        if (Player.MyPhysics && Player.MyPhysics.Animations) Player.MyPhysics.Animations.PlayIdleAnimation();
    }
}
