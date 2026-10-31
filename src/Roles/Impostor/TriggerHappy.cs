// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/TriggerHappy.cs, Roles/Ability/TriggerHappyAbility.cs,
// CustomObject/TriggerHappyBullet.cs, CustomObject/TriggerHappyGatlingGun.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Fx;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// トリガーハッピー (インポスター): 「乱射」でしばらく機関銃を構える。構えている間、PC は右クリックを押している間だけマウスの方へ、
// スマホは画面の右半分の射撃スティックを倒している間だけその向きへ弾をばらまく。
// 同じ人に決まった数当たると倒れる。弾は壁で止まり (設定で抜ける)、同じ所に当て続けると壁が削れて穴が開く
public sealed class TriggerHappy : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("TriggerHappy.Name");
    public override string Reading => "とりがーはっぴー";
    public override Text Blurb => new("TriggerHappy.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("TriggerHappy.Spray", "snr_gun") };

    public static readonly BoolOpt Unlimited = new("TriggerHappy.Unlimited", false);
    public static readonly IntOpt UseLimit = new("TriggerHappy.UseLimit", 1, 1, 10) { When = () => !Unlimited };
    public static readonly FloatOpt SprayCooldown = new("TriggerHappy.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt SyncKillCooldown = new("TriggerHappy.SyncKillCooldown", false);
    public static readonly FloatOpt SprayDuration = new("TriggerHappy.Duration", 5f, 1f, 15f, 1f, "s");
    public static readonly IntOpt RequiredHits = new("TriggerHappy.RequiredHits", 50, 5, 100, 5);
    public static readonly FloatOpt SprayRange = new("TriggerHappy.Range", 5f, 1f, 30f, 1f, "m");
    public static readonly FloatOpt BulletSize = new("TriggerHappy.BulletSize", 1f, 0.25f, 3f, 0.25f);
    public static readonly BoolOpt PierceWalls = new("TriggerHappy.PierceWalls", false);
    public static readonly IntOpt Ricochets = new("TriggerHappy.Ricochets", 2, 0, 5) { When = () => !PierceWalls };
    public static readonly ChoiceOpt SpreadAngle = new("TriggerHappy.SpreadAngle", 2,
        Text.Raw("15°"), Text.Raw("30°"), Text.Raw("45°"), Text.Raw("60°"));
    public static readonly BoolOpt FriendlyFire = new("TriggerHappy.FriendlyFire", true);
    public static readonly BoolOpt KillSound = new("TriggerHappy.KillSound", true);


    private const float Speed = 10f, FireInterval = 1f / 60f, BatchInterval = 0.2f, Radius = 0.15f;
    private const int ChipEvery = 12;         // 壁の同じ所 (0.5 四方) にこの数の弾が当たるごとに壁を 1 回叩く
    private const float ChipGap = 0.25f;      // 壁を叩く依頼の間 (秒)
    private const float ChipForce = 0.3f;
    private static readonly float[] SpreadDeg = { 15f, 30f, 45f, 60f };

    // 弾の当たりの計算 (見た目は GatlingFx の筋。tick ごとにここで進める)
    private sealed class Shot
    {
        public float X, Y, Dx, Dy;   // 位置と向き
        public float Traveled;
        public float MaxTravel;      // 今の筋の長さ (残りの射程か、飛び始めに測った壁までの距離)
        public float Left;           // この筋を飛び始めた時の残りの射程
        public float Nx, Ny;         // 当たる壁の面の向き (跳ね返りに使う)
        public int Bounces;          // あと何回跳ね返るか
        public float Wait;           // 飛び始めるまでの秒
        public bool Real;            // 撃った人の端末の弾 (当たり判定と壁を削る数え)
        public bool Wall;            // 飛び切ったら壁に当たった所
        public GatlingFx.Tracer Tracer;
        public int Gen;
    }

    private const float BounceKeep = 0.75f; // 跳ね返ると残りの射程がこの割合になる
    private const int MaxLive = 200; // 1 人ぶんの飛んでいる弾の上限 (止まった端末の後で一度に湧いても重くしない)
    private const int MaxBatch = 40; // 1 通に載せる弾の数 (16B × 40 = 640B)
    private float _activeUntil;      // 乱射の終わりから少しの間はキルの申告を受ける (まとめて送るぶんの遅れ)。全員の端末で同じ

    // 構えている間 (と終わった直後) のその人のキルは、弾で離れた所から倒したキル
    internal static bool RangedKill(PlayerControl p) => RoleState.Of(p) is TriggerHappy th && (th._active || Time.time < th._activeUntil + 0.5f);

    // 弾を遠くの壁へ当てて削る (撃った人から射程まで)。構えている間と、飛んでいる弾が届き切るまでだけ
    public override Terrain.TerrainPermit TerrainPermit =>
        _active || Time.time < _activeUntil + 3f ? Terrain.TerrainPermit.None.WithRangedHit(SprayRange + 0.6f, ChipGap) : Terrain.TerrainPermit.None;

    private readonly List<Shot> _shots = new();
    private readonly Stack<Shot> _shotPool = new();
    private readonly Dictionary<byte, int> _hits = new();
    private readonly Dictionary<long, int> _chips = new();
    private readonly List<(Vector2 pos, Vector2 dir)> _batch = new();
    private Ability _spray;
    private bool _active, _firing;
    private bool _byRight, _rightPrev, _leftPrev, _leftOnUi; // 自分の端末: 右クリックで構えた (離すとしまう)・前の tick の右ボタン
    private GatlingFx.Gun _gun;
    private float _angle, _sentAngle = float.NaN, _fireTimer, _batchTimer, _nextChip;
    private float _lastKillAt = -99f; // ホスト: この人の弾のキルを最後に受けた時刻
    private Vector2 _aim = Vector2.right;
    private Shot _lastShot;
    private Vector2 _lastFire;

    public override void OnAssigned()
    {
        _spray = AddAbility(new Ability(new Text("TriggerHappy.Spray"), "snr_gun")
        {
            Cooldown = () => SprayCooldown,
            Duration = () => SprayDuration,
            MaxUses = () => Unlimited ? 0 : UseLimit,
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
                Flush(true);
                StateCall.Send(false);
                SetActive(false);
            },
        });
        Lifespan.OnRelease(() =>
        {
            SetActive(false);
            if (IsLocal) MoveBlock.Gun = false;
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
        _chips.Clear();
        _batch.Clear();
        _lastShot = null;
        _firing = false;
        _sentAngle = float.NaN;
        _fireTimer = _batchTimer = 0f;
        if (IsLocal)
        {
            FireStick.Arm(on);
            MoveBlock.Gun = on;
            if (!on) _byRight = false;
        }
        if (on && _gun == null)
        {
            _gun = GatlingFx.AddGun(Player);
            if (_gun != null) _gun.Angle = _angle = Player.cosmetics && Player.cosmetics.FlipX ? 180f : 0f;
            _aim = FxMath.V2(FxMath.Cos(_angle * (System.MathF.PI / 180f)), FxMath.Sin(_angle * (System.MathF.PI / 180f)));
        }
        if (!on && _gun != null)
        {
            GatlingFx.RemoveGun(_gun);
            _gun = null;
        }
    }

    // 自分の端末: 狙いの向きと撃っているか。
    // PC = マウスの方へ左クリックを押している間・キーボードだけなら乱射のキーを押している間は歩く向きへ。スマホ = 射撃スティック
    private bool ReadInput(out Vector2 aim)
    {
        if (System.OperatingSystem.IsAndroid() || FireStick.ForceShow || FireStick.Held)
        {
            aim = FireStick.Held ? FireStick.Dir : _aim;
            return FireStick.Held;
        }
        aim = _aim;
        var key = SprayKey();
        if (key != KeyCode.None && Input.GetKey(key))
        {
            aim = ShotKit.Facing;
            return true;
        }
        var cam = Camera.main;
        if (!cam) return false;
        Vector3 m = Input.mousePosition;
        Vector3 w = cam.ScreenToWorldPoint(m);
        var c = Player.transform.position;
        float dx = w.x - c.x, dy = w.y - c.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d > 0.01f) aim = FxMath.V2(dx / d, dy / d);
        // ボタンやパネルの上で押し始めた左クリックでは撃たない (離すまで)
        bool left = Input.GetMouseButton(0);
        if (left && !_leftPrev) _leftOnUi = FireStick.OverUi();
        _leftPrev = left;
        return left && !_leftOnUi;
    }

    // 乱射の能力に割り当てたキー (能力のボタンの並びの番号のキー)
    private KeyCode SprayKey()
    {
        int i = _spray != null ? _spray.Index : -1;
        return i is >= 0 and < 3 ? Menu.Keybinds.Key((Menu.Keybinds.Slot)i) : KeyCode.None;
    }

    // PC: 右クリックを押すと構え、離すとしまう (待ち時間・回数はボタンを押したのと同じ)
    private void ReadRightClick()
    {
        bool right = Input.GetMouseButton(1);
        bool pressed = right && !_rightPrev;
        _rightPrev = right;
        if (_active)
        {
            if (_byRight && !right) { _byRight = false; _spray?.EndEffect(); }
            return;
        }
        if (!pressed || _spray?.Button == null || !Player.CanMove || Player.inVent || Menu.Emotes.Covered()) return;
        _spray.Button.Press();
        _byRight = _active;
    }

    public override void OnFixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (MeetingHud.Instance || !PortKit.Alive(Player))
        {
            if (_active) SetActive(false);
            _rightPrev = true; // 右を押したまま会議が終わっても、押し始めと取らない
            ClearShots();
            return;
        }
        MoveShots(dt);
        if (IsLocal && !System.OperatingSystem.IsAndroid()) ReadRightClick();
        if (!_active) return;
        bool vent = Player.inVent;
        if (IsLocal)
        {
            ShotKit.Track();
            _firing = ReadInput(out var target) && !vent && Player.CanMove;
            // 向きはなめらかに狙いへ寄せる
            float want = FxMath.Atan2(target.y, target.x) * (180f / System.MathF.PI);
            _angle += DeltaDeg(_angle, want) * FxMath.Clamp01(dt / 0.06f);
            float ar = _angle * (System.MathF.PI / 180f);
            _aim = FxMath.V2(FxMath.Cos(ar), FxMath.Sin(ar));
            if (_firing && (_fireTimer -= dt) <= 0f)
            {
                _fireTimer = FireInterval;
                Fire();
            }
            if ((_batchTimer += dt) >= BatchInterval)
            {
                Flush(false);
                _batchTimer = 0f;
                if (SyncKillCooldown) Player.SetKillTimer(GameManager.Instance.LogicOptions.GetKillCooldown());
            }
        }
        if (_gun != null)
        {
            _gun.Angle = _angle;
            _gun.Hidden = vent;
        }
    }

    // 自分の端末で: 1 発撃つ (前の弾との間にもう 1 発足して弾幕を詰める)
    private void Fire()
    {
        float half = SpreadDeg[FxMath.Clamp(SpreadAngle.Value, 0, 3)] * 0.5f;
        float a = (_angle + FxMath.Range(-half, half)) * (System.MathF.PI / 180f);
        var dir = FxMath.V2(FxMath.Cos(a), FxMath.Sin(a));
        var pos = _gun != null ? GatlingFx.Muzzle(_gun) : Player.GetTruePosition();
        if (_gun != null) _gun.FiredAt = Time.time;
        var shot = Spawn(pos, dir, true, 0f);
        Queue(pos, dir);
        if (_lastShot != null && _lastShot.Tracer != null)
        {
            var mid = FxMath.V2((_lastShot.X + _lastFire.x) * 0.5f, (_lastShot.Y + _lastFire.y) * 0.5f);
            Spawn(mid, dir, true, 0f);
            Queue(mid, dir);
        }
        _lastShot = shot;
        _lastFire = pos;
    }

    private void Queue(Vector2 pos, Vector2 dir)
    {
        _batch.Add((pos, dir));
        if (_batch.Count >= MaxBatch) Flush(true);
    }

    // 自分の端末で: 撃った弾と銃の向きを他の人の画面へまとめて送る (撃っていない間は向きが変わった時だけ)
    private void Flush(bool force)
    {
        if (MeetingHud.Instance) { _batch.Clear(); return; }
        bool turned = float.IsNaN(_sentAngle) || FxMath.Abs(DeltaDeg(_sentAngle, _angle)) > 3f;
        if (_batch.Count == 0 && !turned && !force) return;
        BatchCall.Send((_angle, _batch.ToArray()));
        _sentAngle = _angle;
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
            // 1 通に載る数を超える分は読まない (壊れた電文で物を作りすぎない)
            int n = System.Math.Min((int)r.ReadUInt16(), MaxBatch);
            var shots = new (Vector2, Vector2)[n];
            for (int i = 0; i < n; i++)
                shots[i] = (FxMath.V2(r.ReadSingle(), r.ReadSingle()), FxMath.V2(r.ReadSingle(), r.ReadSingle()));
            return (angle, shots);
        },
        (sender, m) =>
        {
            if (RoleState.Of(sender) is not TriggerHappy t || MeetingHud.Instance || !t._active) return;
            if (!float.IsNaN(m.angle) && !float.IsInfinity(m.angle)) t._angle = m.angle;
            int n = m.shots.Length;
            Vector2 at = sender.GetTruePosition();
            for (int i = 0; i < n; i++)
            {
                var (pos, dir) = m.shots[i];
                float d2 = dir.x * dir.x + dir.y * dir.y;
                if (!(d2 > 0.5f && d2 < 1.5f)) continue;
                // 撃った人の手元から出た弾だけ (壊れた値・遠すぎる位置は出さない)
                float ox = pos.x - at.x, oy = pos.y - at.y;
                if (!(ox * ox + oy * oy < 9f)) continue;
                // まとめて届いた弾は、撃った時の間隔へばらして飛ばす
                t.Spawn(pos, dir, false, BatchInterval * i / n);
            }
            if (n > 0 && t._gun != null) t._gun.FiredAt = Time.time + BatchInterval;
        });

    // real = 自分の弾 (当たりと壁の数えをする)。他の人の弾は見た目の筋だけ
    // 他の人の弾も跳ね返りを見せるために同じく一覧で進める (当たりと壁の数えは自分の弾だけ)
    private Shot Spawn(Vector2 pos, Vector2 dir, bool real, float wait)
    {
        if (_shots.Count >= MaxLive) Remove(0); // 一番古い弾から消す
        var s = _shotPool.Count > 0 ? _shotPool.Pop() : new Shot();
        s.X = pos.x; s.Y = pos.y; s.Dx = dir.x; s.Dy = dir.y;
        s.Wait = wait;
        s.Real = real;
        s.Bounces = PierceWalls ? 0 : Ricochets;
        Segment(s, SprayRange, wait);
        _shots.Add(s);
        return s;
    }

    // 今の位置と向きから次の筋を始める。壁までの距離はこの時に 1 回だけ測る (飛んでいる間に壁を調べ直さない)
    private static void Segment(Shot s, float left, float wait)
    {
        s.Traveled = 0f;
        s.Left = left;
        if (PierceWalls) { s.MaxTravel = left; s.Wall = false; }
        else
        {
            s.Wall = ShotKit.WallHit(FxMath.V2(s.X, s.Y), FxMath.V2(s.Dx, s.Dy), left, out s.MaxTravel, out var n);
            s.Nx = n.x; s.Ny = n.y;
        }
        s.Tracer = GatlingFx.Shoot(s.X, s.Y, s.Dx, s.Dy, Speed, s.MaxTravel, s.Wall, BulletSize, wait);
        s.Gen = s.Tracer != null ? s.Tracer.Gen : 0;
    }

    // 壁で跳ね返す (面の向きで鏡に映した向きへ・残りの射程は減る)。跳ね返れないほど残りが短ければ false
    private static bool Bounce(Shot s, float hx, float hy)
    {
        float left = (s.Left - s.MaxTravel) * BounceKeep;
        if (s.Bounces <= 0 || left < 0.3f) return false;
        float dot = s.Dx * s.Nx + s.Dy * s.Ny;
        if (dot >= 0f) return false; // 面の向きが取れていない
        s.Dx -= 2f * dot * s.Nx;
        s.Dy -= 2f * dot * s.Ny;
        float d = FxMath.Sqrt(s.Dx * s.Dx + s.Dy * s.Dy);
        if (d < 0.001f) return false;
        s.Dx /= d; s.Dy /= d;
        // 壁の面から少し離して次の筋を始める (同じ面にすぐ当たり直さない)
        s.X = hx + s.Nx * 0.03f;
        s.Y = hy + s.Ny * 0.03f;
        s.Bounces--;
        Segment(s, left, 0f);
        return true;
    }

    private void ClearShots()
    {
        for (int i = _shots.Count - 1; i >= 0; i--) Remove(i);
    }

    private void MoveShots(float dt)
    {
        if (_shots.Count == 0) return;
        bool cached = false;
        float step = Speed * dt;
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            var s = _shots[i];
            if (s.Wait > 0f) { s.Wait -= dt; continue; }
            s.X += s.Dx * step;
            s.Y += s.Dy * step;
            s.Traveled += step;
            if (s.Traveled >= s.MaxTravel)
            {
                // 筋は自分で飛び切って着弾を出すので止めない
                s.Tracer = null;
                if (s.Wall)
                {
                    float over = s.Traveled - s.MaxTravel;
                    float hx = s.X - s.Dx * over, hy = s.Y - s.Dy * over;
                    if (s.Real) Chip(hx, hy, s.Dx, s.Dy);
                    if (Bounce(s, hx, hy)) continue;
                }
                Remove(i);
                continue;
            }
            if (!s.Real) continue;
            if (!cached) { CacheTargets(); cached = true; }
            if (TryHit(s)) Remove(i);
        }
    }

    // 撃った人の端末で: 壁に当たった弾を場所ごとに数え、決まった数ごとに壁を叩く
    private void Chip(float hx, float hy, float dx, float dy)
    {
        long key = ((long)(int)FxMath.Floor(hx * 2f) << 32) ^ (uint)(int)FxMath.Floor(hy * 2f);
        _chips.TryGetValue(key, out int n);
        n++;
        if (n >= ChipEvery && Time.time >= _nextChip)
        {
            var res = Terrain.TerrainApi.Shoot(FxMath.V2(hx - dx * 0.4f, hy - dy * 0.4f), FxMath.V2(dx, dy), ChipForce);
            // ホストの受ける間 (ChipGap) より少し空けて、ネットの揺れで詰まった依頼を断られないように
            if (res.Ok) { n -= ChipEvery; _nextChip = Time.time + ChipGap * 1.25f; }
        }
        _chips[key] = n;
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
        var s = _shots[i];
        if (ReferenceEquals(s, _lastShot)) _lastShot = null; // 使い回された弾から間の弾を足さない
        if (s.Tracer != null) GatlingFx.Stop(s.Tracer, s.Gen);
        s.Tracer = null;
        _shots.RemoveAt(i);
        _shotPool.Push(s);
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
            // 構えている (か終わった直後の) 人の申告で、相手が射程の中にいて、当たりうる (壁越しでない・仲間に当てない設定なら仲間でない) 時だけ受ける。
            // 当たるのに要る弾数を撃ち切れない速さの申告 (同じ人の次のキルまでの間) も受けない
            if (MeetingHud.Instance || RoleState.Of(sender) is not TriggerHappy th || !(th._active || Time.time < th._activeUntil)) return;
            if (PortKit.ById(id) is not { } t || !PortKit.Alive(t)) return;
            if (!FriendlyFire && th.SameSide(t)) return;
            Vector2 a = sender.GetTruePosition(), b = t.GetTruePosition();
            float dx = b.x - a.x, dy = b.y - a.y, reach = SprayRange + 2f, d = FxMath.Sqrt(dx * dx + dy * dy);
            if (d > reach) return;
            // 跳ね返る弾は角の向こうへも届く (道のりは真っすぐの距離より長いので、射程の確かめはそのまま効く)
            if (!PierceWalls && Ricochets == 0 && d > 0.01f && PhysicsHelpers.AnyNonTriggersBetween(a, FxMath.V2(dx / d, dy / d), d, Constants.ShipAndObjectsMask)) return;
            float gap = RequiredHits * FireInterval * 0.25f;
            if (Time.time - th._lastKillAt < gap) return;
            th._lastKillAt = Time.time;
            HostKill(sender, t);
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

    private static float DeltaDeg(float from, float to)
    {
        float d = (to - from) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return d;
    }

    // ---- 確かめ用 (bridge) ----

    internal string Describe() =>
        $"active={_active} firing={_firing} angle={_angle:0} shots={_shots.Count} chips={_chips.Count} hits={_hits.Count} gun={(_gun != null)}";
}
