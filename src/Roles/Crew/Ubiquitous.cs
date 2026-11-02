// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Ubiquitous.cs, CustomObject/Drone.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ユビキタス (クルー): 「呼ぶ / 戻す」で足元にドローンを出す (もう一度押すと消す)。「操作」の間は自分は動けず、カメラがドローンへ移って
// ドローンを飛ばせる (ドローンのまわりが見える)。操作中は「ドアを開ける」で近くの閉まったドアを開けられる。
// 会議が始まるとドローンはその場に止まり、設定の会議の数だけ残る。止まったドローンの近くにいる人は地図に映る
public sealed partial class Ubiquitous : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#389BDF";
    public override Text Name => new("Ubiquitous.Name");
    public override string Reading => "ゆびきたす";
    public override Text Blurb => new("Ubiquitous.Blurb");
    public override string Origin => "Nebula";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("Ubiquitous.Call", "snr_drone"), new("Ubiquitous.Operate", "snr_remote"), new("Ubiquitous.DoorHack", "snr_door") };

    public static readonly FloatOpt CallCooldown = new("Ubiquitous.CallCooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt OperateCooldown = new("Ubiquitous.OperateCooldown", 0f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt LimitOperate = new("Ubiquitous.LimitOperate", false);
    public static readonly FloatOpt OperateTime = new("Ubiquitous.OperateTime", 10f, 2.5f, 60f, 2.5f, "s") { When = () => LimitOperate };
    public static readonly IntOpt StayTurns = new("Ubiquitous.StayTurns", 5, 1, 20);
    public static readonly FloatOpt FlyingSpeed = new("Ubiquitous.FlyingSpeed", 1.5f, 0.25f, 3f, 0.25f, "x");
    public static readonly FloatOpt DoorHackCooldown = new("Ubiquitous.DoorHackCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt DoorHackScope = new("Ubiquitous.DoorHackScope", 0.5f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt VisibilityRange = new("Ubiquitous.VisibilityRange", 1f, 0.25f, 5f, 0.25f, "x");
    public static readonly BoolOpt MapShowColor = new("Ubiquitous.MapShowColor", false);

    private static readonly HashSet<int> LighterColors = new() { 3, 4, 5, 7, 10, 11, 13, 14, 17 };

    // 止まったドローン (全員の端末で持つ)
    private sealed class IdleDrone
    {
        public byte Owner;
        public GameObject Go;
        public int Turns;
        public Body Body;
    }
    private static readonly List<IdleDrone> Idle = new();

    // 飛んでいるドローン (自分の端末だけ)
    private GameObject _drone;
    private Rigidbody2D _body;
    private SpriteRenderer _rend;
    private int _spinFrame;   // プロペラの残像のコマ
    private GameObject _light;
    private float _hover;
    // 操作感: 速度は自前で持たず剛体の値 (壁に当たった後) から加減速する。向きは進む方へ回り、傾きは加減速と速さで決まる
    private float _yaw = -FxMath.PI * 0.5f;    // 床の上の向き (ラジアン・右 = 0・上 = π/2)。出した時は手前を向く
    private float _tilt;                       // 前へ倒れる角 (度・負 = 機首上げ)
    private float _vx, _vy;                    // 前の tick の速度 (加速度を出すため)
    private bool _shownFlip, _shownOp;        // 今の絵の左右反転と色 (変わった時だけ書く)
    internal static Vector2? DevInput;         // 確かめ用: 操作の入力を置き換える
    private Ability _operate;
    private SpriteRenderer[] _herePoints;
    // 羽音 (本人の端末だけ): 速さで高さと大きさが上がる。操作していない時は本人からの距離で小さくなる
    private AudioSource _hum;
    private float _humAge, _humGain = 1f, _humPan, _humVol = -1f, _humPitch = -1f, _humPanShown = 9f;
    private int _humTick;

    internal static bool Operating(PlayerControl p) =>
        p && p.AmOwner && RoleState.Of(p) is Ubiquitous u && u._drone && u._operate != null && u._operate.InEffect;

    public override void OnAssigned()
    {
        DroneArt.Prewarm(); // ドローンの絵は重いので、役職が付いた時に作業スレッドで焼き始める
        AddAbility(new Ability(new Text("Ubiquitous.Call"), "snr_drone")
        {
            Cooldown = () => CallCooldown,
            OnUse = () =>
            {
                if (!PortKit.Alive(Player) || _operate != null && _operate.InEffect) return false;
                if (_drone) DestroyDrone(true);
                else CreateDrone(Player.transform.position);
                return true;
            },
        });
        _operate = AddAbility(new Ability(new Text("Ubiquitous.Operate"), "snr_remote")
        {
            Cooldown = () => OperateCooldown,
            CooldownAtStart = false,
            Duration = () => LimitOperate ? OperateTime : 3600f,
            Cancellable = true,
            OnUse = () =>
            {
                if (!_drone || Minigame.Instance || !PortKit.Alive(Player)) return false;
                if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
                Follow(_drone.GetComponent<DroneTarget>());
                return true;
            },
            OnEnd = StopOperate,
            OnCancel = StopOperate,
        });
        AddAbility(new Ability(new Text("Ubiquitous.DoorHack"), "snr_door")
        {
            Cooldown = () => DoorHackCooldown,
            WhileStill = true, // 操作中は本人の体が止まっている
            OnUse = () =>
            {
                if (!_drone || !_operate.InEffect || _scanT >= 0f) return false;
                var doors = HackTargets();
                if (doors.Count == 0) return false;
                StartScan(doors.ToArray()); // センサーで読み取ってから開ける
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            if (!IsLocal) return;
            DestroyDrone();
            Follow(null);
            foreach (var h in _herePoints ?? new SpriteRenderer[0]) if (h) Object.Destroy(h.gameObject);
        });
        RoleState.Match.OnRelease(ClearIdle);
    }

    private static void ClearIdle()
    {
        foreach (var d in Idle)
        {
            Bodies.Remove(d.Body);
            if (d.Go) Object.Destroy(d.Go);
        }
        Idle.Clear();
    }

    // 手を離したドローンは惰性で少し流れてから止まる
    private void StopOperate() => Follow(null);

    private static void Follow(MonoBehaviour target)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.PlayerCam) return;
        hud.PlayerCam.SetTarget(target ? target : PlayerControl.LocalPlayer);
    }

    // ---- ドローン ----

    private static bool _registered;

    private static GameObject MakeDrone(Vector2 at, bool active, int yaw)
    {
        if (!_registered)
        {
            _registered = true;
            ClassInjector.RegisterTypeInIl2Cpp<DroneTarget>();
        }
        var go = new GameObject("UbiquitousDrone") { layer = LayerMask.NameToLayer("Players") };
        go.transform.position = new Vector3(at.x, at.y, active ? -1f : at.y / 1000f);
        go.AddComponent<DroneTarget>();
        var r = new GameObject("Renderer") { layer = LayerMask.NameToLayer("Objects") };
        r.transform.SetParent(go.transform, false);
        var sr = r.AddComponent<SpriteRenderer>();
        sr.sprite = DroneArt.Frame(yaw, DroneArt.Level, active ? 0 : DroneArt.Still, out bool flip);
        sr.flipX = flip;
        sr.color = active ? new UnityEngine.Color(1f, 1f, 1f, 0.5f) : UnityEngine.Color.white;
        // 床の影: 飛んでいる間は体の上下の揺れに付いて行かず、浮いている高さの分だけ下に置く
        var sh = new GameObject("Shadow") { layer = r.layer };
        sh.transform.SetParent(go.transform, false);
        sh.transform.localPosition = new Vector3(0f, active ? -0.46f : -0.22f, 0.001f);
        sh.transform.localScale = active ? new Vector3(0.28f, 0.28f, 1f) : new Vector3(0.32f, 0.32f, 1f);
        var ssr = sh.AddComponent<SpriteRenderer>();
        ssr.sprite = DroneArt.Shadow;
        ssr.color = new UnityEngine.Color(0f, 0f, 0f, active ? 0.28f : 0.4f);
        return go;
    }

    private void CreateDrone(Vector2 at)
    {
        _yaw = -FxMath.PI * 0.5f; _tilt = 0f; _vx = _vy = 0f; _shownFlip = _shownOp = false;
        _drone = MakeDrone(at, true, DroneArt.YawIndex(_yaw));
        _body = _drone.AddComponent<Rigidbody2D>();
        _body.gravityScale = 0f;
        _body.freezeRotation = true;
        _body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        _body.sleepMode = RigidbodySleepMode2D.NeverSleep;
        _body.interpolation = RigidbodyInterpolation2D.Interpolate;
        var col = _drone.AddComponent<CircleCollider2D>();
        col.offset = new Vector2(0f, -0.2f);
        col.radius = 0.2234f;
        _rend = _drone.transform.Find("Renderer").GetComponent<SpriteRenderer>();
        AddFlyingBody();
        Hearing.Self("ubiq_drone_up", HumVolume);
        var loop = Hearing.Clip("ubiq_drone");
        var sm = SoundManager.Instance;
        if (loop && sm)
        {
            _hum = _drone.AddComponent<AudioSource>();
            _hum.playOnAwake = false;
            _hum.loop = true;
            _hum.spatialBlend = 0f;
            _hum.volume = 0f;
            _hum.outputAudioMixerGroup = sm.SfxChannel;
            _hum.clip = loop;
            _hum.time = FxMath.Range(0f, loop.length * 0.9f);
            _hum.Play();
        }
        _humAge = 0f; _humGain = 1f; _humPan = 0f; _humVol = _humPitch = -1f; _humPanShown = 9f; _humTick = 0;
        // 操作中はドローンのまわりも見える (本編の視界の切り抜きをもう 1 つ置く)
        _light = new GameObject("LightChild") { layer = LayerMask.NameToLayer("Shadow") };
        _light.transform.SetParent(_drone.transform, false);
        _light.transform.localScale = Vector3.zero;
        var src = PlayerControl.LocalPlayer.LightPrefab;
        if (src)
        {
            _light.AddComponent<MeshFilter>().mesh = src.lightChildMesh;
            _light.AddComponent<MeshRenderer>().material.shader = src.LightCutawayMaterial.shader;
        }
    }

    // spinDown = 回転が落ちる音を鳴らす (自分で戻した時だけ。会議・死亡・試合の終わりでは鳴らさない)
    private void DestroyDrone(bool spinDown = false)
    {
        if (_operate != null && _operate.InEffect) _operate.EndEffect();
        RemoveFlyingBody();
        if (_drone)
        {
            if (spinDown)
            {
                Vector2 at = _drone.transform.position;
                Hearing.At("ubiq_drone_down", at.x, at.y, HumRange, HumVolume, null, Ears.Self);
            }
            Object.Destroy(_drone);
        }
        _hum = null;
        _scanT = -1f; // 読み取りの途中なら開けずにやめる (光はドローンの子なので一緒に消える)
        _beam = _glow = _dot = null;
        _drone = null;
        _body = null;
    }

    public override void OnFixedUpdate()
    {
        if (!IsLocal) return;
        if (_drone && !PortKit.Alive(Player)) DestroyDrone();
        if (!_drone) return;
        bool op = _operate.InEffect;
        float dt = Time.fixedDeltaTime;
        float max = FlyingSpeed * (Player.MyPhysics ? Player.MyPhysics.Speed : 2.5f);
        float ix = 0f, iy = 0f;
        if (op && !Minigame.Instance)
        {
            var hud = Vanilla.Hud;
            Vector2 delta = DevInput ?? (hud && hud.joystick != null ? hud.joystick.DeltaL : default);
            ix = delta.x; iy = delta.y;
        }
        Fly(_body.velocity, ix, iy, max, dt);
        var p = _drone.transform.position;
        _drone.transform.position = FxMath.V3(p.x, p.y, -1f);
        _hover += dt * 2.4f;
        if (_hover > FxMath.PI * 2f) _hover -= FxMath.PI * 2f;
        if (_rend)
        {
            // 浮いている揺れ: ゆっくりの上下に、速く飛んでいる時の細かい揺れを少し足す
            float speed = FxMath.Sqrt(_vx * _vx + _vy * _vy) / FxMath.Max(max, 0.01f);
            float bob = FxMath.Sin(_hover) * 0.022f + FxMath.Sin(_hover * 3.7f) * 0.006f * speed;
            _rend.transform.localPosition = FxMath.V3(0f, bob, 0f);
            int pitch = 0;
            for (int i = 1; i < DroneArt.PitchDeg.Length; i++)
                if (FxMath.Abs(DroneArt.PitchDeg[i] - _tilt) < FxMath.Abs(DroneArt.PitchDeg[pitch] - _tilt)) pitch = i;
            _rend.sprite = DroneArt.Frame(DroneArt.YawIndex(_yaw), pitch, ++_spinFrame & 1, out bool flip);
            if (flip != _shownFlip) _rend.flipX = _shownFlip = flip;
            if (op != _shownOp) _rend.color = op ? UnityEngine.Color.white : new UnityEngine.Color(1f, 1f, 1f, 0.5f);
            _shownOp = op;
        }
        UpdateHum(op, max, dt);
        if (_scanT >= 0f) TickScan(dt);
        float size = op && ShipStatus.Instance ? ShipStatus.Instance.MaxLightRadius * VisibilityRange * 5.25f : 0f;
        if (_light) _light.transform.localScale = new Vector3(size, size, 1f);
    }

    private const float HumVolume = 0.55f, HumRange = 8f;

    private void UpdateHum(bool op, float max, float dt)
    {
        if (!_hum) return;
        _humAge += dt;
        // 操作中はカメラがドローンにいるので近くの音・操作していない時は 0.2 秒ごとに本人からの距離を測り直す
        if (op) { _humGain = 1f; _humPan = 0f; }
        else if (_humTick-- <= 0)
        {
            _humTick = 6;
            var lp = PlayerControl.LocalPlayer;
            Vector2 at = _drone.transform.position;
            Vector2 me = lp ? lp.GetTruePosition() : at;
            float dx = at.x - me.x, dy = at.y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
            float near = d <= Hearing.FullDist ? 1f : FxMath.Max(0f, 1f - (d - Hearing.FullDist) / (HumRange - Hearing.FullDist));
            _humGain = near * FxMath.Sqrt(near);
            _humPan = FxMath.Clamp(dx / 10f, -0.7f, 0.7f);
        }
        float speed = FxMath.Min(1f, FxMath.Sqrt(_vx * _vx + _vy * _vy) / FxMath.Max(max, 0.01f));
        // 回り始めの音 (1.1 秒) の終わりの 0.3 秒に羽音を重ねてつなぐ
        float fadeIn = FxMath.Clamp((_humAge - 0.8f) / 0.3f, 0f, 1f);
        float vol = HumVolume * (0.7f + 0.3f * speed) * _humGain * fadeIn;
        float pitch = 0.94f + 0.16f * speed + 0.004f * _tilt;
        if (FxMath.Abs(vol - _humVol) > 0.01f) _hum.volume = _humVol = vol;
        if (FxMath.Abs(pitch - _humPitch) > 0.008f) _hum.pitch = _humPitch = pitch;
        if (FxMath.Abs(_humPan - _humPanShown) > 0.02f) _hum.panStereo = _humPanShown = _humPan;
    }

    internal string Describe()
    {
        if (!_drone) return "none";
        Vector2 at = _drone.transform.position;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"at={at.x.ToString("F2", inv)},{at.y.ToString("F2", inv)} v={_vx.ToString("F2", inv)},{_vy.ToString("F2", inv)} yaw={(_yaw / FxMath.Deg2Rad).ToString("F0", inv)} tilt={_tilt.ToString("F1", inv)} op={_operate.InEffect}";
    }

    // 本物のドローンのような動き (数値は 1/30 秒ごとの tick で):
    // 入力の向きへ 0.3 秒で速さが乗り、離すと 0.45 秒で惰性が抜ける。向きは進む方へ毎秒 400° まで回る。
    // 傾きは加速の向きへ前のめり (最大 20°)・ブレーキで機首上げ (−10°)・一定の速さで飛ぶ間は少し前へ倒れたまま
    private const float AccelTau = 0.30f, BrakeTau = 0.45f, TurnRate = 400f * FxMath.Deg2Rad, TiltTau = 0.10f;

    private void Fly(Vector2 now, float ix, float iy, float max, float dt)
    {
        // 壁に当たった分は剛体の速度に出ているので、それを今の速度として加減速する
        float vx = now.x, vy = now.y;
        bool input = ix * ix + iy * iy > 0.0025f;
        float k = 1f - FxMath.Exp(-dt / (input ? AccelTau : BrakeTau));
        vx += (ix * max - vx) * k;
        vy += (iy * max - vy) * k;
        if (!input && vx * vx + vy * vy < 0.0004f) vx = vy = 0f;
        _body.velocity = FxMath.V2(vx, vy);

        // 向き: 入力があればその向き、無ければ流れている向きへ
        float sp = FxMath.Sqrt(vx * vx + vy * vy);
        if (input || sp > max * 0.15f)
        {
            float want = input ? FxMath.Atan2(iy, ix) : FxMath.Atan2(vy, vx);
            float d = want - _yaw;
            while (d > FxMath.PI) d -= FxMath.PI * 2f;
            while (d < -FxMath.PI) d += FxMath.PI * 2f;
            float step = TurnRate * dt;
            _yaw += d > step ? step : d < -step ? -step : d;
            if (_yaw > FxMath.PI) _yaw -= FxMath.PI * 2f;
            else if (_yaw < -FxMath.PI) _yaw += FxMath.PI * 2f;
        }

        // 傾き: 機首の向きへの加速 (0.35 秒で最高速に乗る加速を 1 として 18°) と速さ (最高速で 8°)
        float fx = FxMath.Cos(_yaw), fy = FxMath.Sin(_yaw);
        float acc = ((vx - _vx) * fx + (vy - _vy) * fy) / dt / FxMath.Max(max / 0.35f, 0.01f);
        float want2 = FxMath.Clamp(18f * acc + 8f * sp / FxMath.Max(max, 0.01f), -10f, 20f);
        _tilt += (want2 - _tilt) * (1f - FxMath.Exp(-dt / TiltTau));
        _vx = vx; _vy = vy;
    }

    // 会議が始まったら、飛んでいるドローンをその場に止める
    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        if (!_drone) return;
        Vector2 at = _drone.transform.position;
        byte yaw = (byte)DroneArt.YawIndex(_yaw);
        DestroyDrone();
        Follow(null);
        IdleCall.Send((at, yaw));
        AddIdle(Player, at, yaw);
    }

    // 会議が終わったら、止まったドローンの残りの会議の数を減らす
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (FirstUbiquitous() != this) return;
        for (int i = Idle.Count - 1; i >= 0; i--)
        {
            if (Idle[i].Turns-- > 0) continue;
            Bodies.Remove(Idle[i].Body);
            if (Idle[i].Go) Object.Destroy(Idle[i].Go);
            Idle.RemoveAt(i);
        }
    }

    private static Ubiquitous FirstUbiquitous()
    {
        foreach (var r in RoleState.All) if (r is Ubiquitous u) return u;
        return null;
    }

    // 止めた場所と向き (止まったドローンは最後に向いていた方を向いたまま置く)
    private static readonly RemoteCall<(Vector2 at, byte yaw)> IdleCall = new("Ubiquitous.Idle", Route.Anyone,
        (w, v) => { w.Write(v.at.x); w.Write(v.at.y); w.Write(v.yaw); },
        r => (new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadByte()), (sender, v) => AddIdle(sender, v.at, v.yaw));

    private static void AddIdle(PlayerControl owner, Vector2 at, byte yaw)
    {
        if (!owner || RoleState.Of(owner) is not Ubiquitous) return;
        var d = new IdleDrone { Owner = owner.PlayerId, Go = MakeDrone(at, false, yaw), Turns = StayTurns };
        d.Body = IdleBody(d);
        Idle.Add(d);
    }

    // ---- ドアのセンサー (本人の端末だけ) ----
    // ドローンから赤い光の線を伸ばし、開けるドアを端から端まで順に読み取ってから開ける (ドアごとに ScanTime / ドアの数)
    private const float ScanTime = 0.5f;
    private const float BeamW = 0.018f, GlowW = 0.09f, DotSize = 0.2f;
    private int[] _scanIds;
    private float _scanT = -1f;
    private float[] _scanX, _scanY, _scanAx, _scanAy; // ドアの真ん中と、長い向きの半分
    private Transform _beam, _glow, _dot;
    private static Sprite _lineSprite, _dotSprite;

    private void StartScan(int[] ids)
    {
        var doors = ShipStatus.Instance.AllDoors;
        int n = ids.Length;
        _scanX = new float[n]; _scanY = new float[n]; _scanAx = new float[n]; _scanAy = new float[n];
        for (int i = 0; i < n; i++)
        {
            var d = doors[ids[i]];
            var col = CutDoors.ColliderOf(d);
            Vector2 c = d.transform.position;
            float hx = 0f, hy = 0.5f;
            if (col && col.enabled)
            {
                var b = col.bounds;
                Vector3 bc = b.center, e = b.extents;
                c = FxMath.V2(bc.x, bc.y);
                if (e.x > e.y) { hx = e.x; hy = 0f; } else { hx = 0f; hy = e.y; }
            }
            _scanX[i] = c.x; _scanY[i] = c.y; _scanAx[i] = hx * 0.85f; _scanAy[i] = hy * 0.85f;
        }
        _scanIds = ids;
        _scanT = 0f;
        _lineSprite ??= FxSprites.Rect(64, 16, (x, y) => FxMath.Exp(-y * y * 40f), "MrpUbiqBeam");
        _dotSprite ??= FxSprites.Rect(32, 32, FxSprites.Dot, "MrpUbiqDot");
        _glow = Part("SensorGlow", _lineSprite, FxMath.Rgba(1f, 0.15f, 0.1f, 0.22f));
        _beam = Part("SensorBeam", _lineSprite, FxMath.Rgba(1f, 0.3f, 0.25f, 0.95f));
        _dot = Part("SensorDot", _dotSprite, FxMath.Rgba(1f, 0.45f, 0.4f, 1f));
        Vector2 at = _drone.transform.position;
        Hearing.At("ubiq_sensor", at.x, at.y, HumRange, 0.6f, null, Ears.Self);
    }

    private Transform Part(string name, Sprite sp, Color c)
    {
        var go = new GameObject(name) { layer = _drone.layer };
        go.transform.SetParent(_drone.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = c;
        return go.transform;
    }

    private void TickScan(float dt)
    {
        _scanT += dt;
        if (_scanT >= ScanTime || !_beam)
        {
            var ids = _scanIds;
            if (_beam) Object.Destroy(_beam.gameObject);
            if (_glow) Object.Destroy(_glow.gameObject);
            if (_dot) Object.Destroy(_dot.gameObject);
            _beam = _glow = _dot = null;
            _scanT = -1f;
            if (ids == null || ids.Length == 0) return;
            DoorCall.Send(ids);
            OpenDoors(ids);
            return;
        }
        int n = _scanIds.Length;
        float seg = ScanTime / n;
        int k = FxMath.Min(n - 1, (int)(_scanT / seg));
        float u = (_scanT - k * seg) / seg;
        // 端で折り返さず一方向に読む (端で少しゆっくり)
        float s = -FxMath.Cos(FxMath.PI * u);
        float tx = _scanX[k] + _scanAx[k] * s, ty = _scanY[k] + _scanAy[k] * s;
        Vector3 p = _drone.transform.position;
        float sx = p.x, sy = p.y - 0.08f; // センサーは胴の下
        float dx = tx - sx, dy = ty - sy, len = FxMath.Sqrt(dx * dx + dy * dy);
        var rot = FxMath.RotZ(FxMath.Atan2(dy, dx) / FxMath.Deg2Rad);
        float mx = (sx + tx) * 0.5f, my = (sy + ty) * 0.5f;
        // 線の絵は幅 4 単位・高さ 1 単位。ドローンの少し奥に描く
        _beam.SetPositionAndRotation(FxMath.V3(mx, my, p.z + 0.004f), rot);
        _beam.localScale = FxMath.V3(len / 4f, BeamW, 1f);
        _glow.SetPositionAndRotation(FxMath.V3(mx, my, p.z + 0.005f), rot);
        _glow.localScale = FxMath.V3(len / 4f, GlowW, 1f);
        float pulse = DotSize * (0.9f + 0.1f * FxMath.Sin(_scanT * 60f));
        _dot.position = FxMath.V3(tx, ty, p.z + 0.003f);
        _dot.localScale = FxMath.V3(pulse, pulse, 1f);
    }

    // ---- ドア ----

    private List<int> HackTargets()
    {
        var list = new List<int>();
        var doors = ShipStatus.Instance ? ShipStatus.Instance.AllDoors : null;
        if (doors == null || !_drone) return list;
        for (int i = 0; i < doors.Length; i++)
        {
            var d = doors[i];
            if (!d || d.IsOpen || d.TryCast<AutoCloseDoor>()) continue;
            if (Vector2.Distance(_drone.transform.position, d.transform.position) > DoorHackScope * 3f) continue;
            list.Add(i);
        }
        return list;
    }

    private static readonly RemoteCall<int[]> DoorCall = new("Ubiquitous.Doors", Route.Anyone,
        (w, ids) => { w.WritePacked(ids.Length); foreach (var i in ids) w.WritePacked(i); },
        r => { var a = new int[r.ReadPackedInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadPackedInt32(); return a; },
        (_, ids) => OpenDoors(ids));

    private static void OpenDoors(int[] ids)
    {
        var ship = ShipStatus.Instance;
        var doors = ship ? ship.AllDoors : null;
        if (doors == null) return;
        foreach (int i in ids)
        {
            if (i < 0 || i >= doors.Length) continue;
            var d = doors[i];
            if (!d || d.IsOpen || d.TryCast<AutoCloseDoor>()) continue;
            var auto = d.TryCast<AutoOpenDoor>();
            if (auto != null && ship.Systems.TryGetValue(SystemTypes.Doors, out var sys) && sys.TryCast<AutoDoorsSystemType>() is { } autoSys)
            {
                // スケルドは同じ部屋のドアをまとめて開ける
                if (ship.Type == ShipStatus.MapType.Ship)
                {
                    bool any = false;
                    foreach (var other in doors)
                    {
                        var od = other ? other.TryCast<AutoOpenDoor>() : null;
                        if (od == null || od.Room != auto.Room) continue;
                        autoSys.SetDoor(od, true);
                        any = true;
                    }
                    if (!any) autoSys.SetDoor(auto, true);
                }
                else autoSys.SetDoor(auto, true);
                continue;
            }
            d.SetDoorway(true);
        }
    }

    // ---- 地図 ----

    // 止まった自分のドローンの近くにいる人を地図に映す
    internal void UpdateMap(MapBehaviour map)
    {
        if (!map || !map.HerePoint || !ShipStatus.Instance) return;
        if (_herePoints == null || _herePoints.Length == 0 || !_herePoints[0])
        {
            int n = Mathf.Max(1, PlayerControl.AllPlayerControls.Count - 1);
            _herePoints = new SpriteRenderer[n];
            for (int i = 0; i < n; i++)
            {
                _herePoints[i] = Object.Instantiate(map.HerePoint, map.HerePoint.transform.parent);
                _herePoints[i].material.SetInt(PlayerMaterial.MaskLayer, 255);
                _herePoints[i].gameObject.SetActive(false);
            }
        }
        var near = new List<PlayerControl>();
        // 死んだ後は置いたドローンの見張りも止める (幽霊は全員が見えるので、印だけが残って混ざる)
        if (!PortKit.Alive(Player)) { foreach (var hp in _herePoints) if (hp) hp.gameObject.SetActive(false); return; }
        float range = ShipStatus.Instance.MaxLightRadius * VisibilityRange;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId) continue;
            Vector2 pos = p.GetTruePosition();
            foreach (var d in Idle)
                if (d.Owner == PlayerId && d.Go && Vector2.Distance(d.Go.transform.position, pos) <= range) { near.Add(p); break; }
        }
        for (int i = 0; i < _herePoints.Length; i++)
        {
            var r = _herePoints[i];
            if (!r) continue;
            if (i >= near.Count) { r.gameObject.SetActive(false); continue; }
            var p = near[i];
            if (MapShowColor) p.SetPlayerMaterialColors(r);
            else PlayerMaterial.SetColors(LighterColors.Contains(p.Data.DefaultOutfit.ColorId) ? 7 : 6, r);
            Vector3 pos = p.GetTruePosition();
            pos /= ShipStatus.Instance.MapScale;
            pos.x *= Mathf.Sign(ShipStatus.Instance.transform.localScale.x);
            pos.z = -1f;
            r.transform.localPosition = pos;
            r.gameObject.SetActive(true);
        }
    }
}

// カメラが追いかける的 (本編のカメラは MonoBehaviour しか追えない)
public class DroneTarget : MonoBehaviour
{
    public DroneTarget(System.IntPtr ptr) : base(ptr) { }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
internal static class UbiquitousMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        if (RoleState.Local is Ubiquitous u) u.UpdateMap(__instance);
    }
}
