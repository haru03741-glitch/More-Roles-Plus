using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// ロケットランチャーの大砲の見た目 (全員の端末で同じ流れ・電文は増やさない)。
// 装填: 撃つ人の前で床から大砲がせり上がり、撃つ人の色の手 (本編のペットをなでる手の絵) が相手の肩をつかんで持ち上げ、
//       上を向いた砲口へ押し込む。押し込んだ相手は見えなくなり、大砲は撃つ人の前に付いて動く。
// 発射: 筒が狙った向きへ回り、反動で下がって閃光と白煙。相手は砲口から飛び出し、飛ぶ間は後ろに煙の帯。少し後に大砲は沈んで消える。
// 置き場所はここが決め、相手の位置 (RocketLauncher.HoldPosition) もここの値を使う。毎フレームの算術は FxMath
internal sealed class RocketCannon
{
    // ---- 数値 ----
    private const float Side = 0.85f;            // 撃つ人の足元から大砲の台までの横の距離
    private const float Rise = 0.35f;            // せり上がる秒
    private const float GrabAt = 0.12f, SqueezeAt = 0.2f, LiftEnd = 0.55f, PushEnd = 0.8f, HandsGone = 0.95f;
    // 大砲は 3D の形から描いた向きごとのコマ (Resources/Icons/rl_cannon3d.png・1 コマ 160 四方・8 列)。数値は絵の作りと同じ
    private const int Cell = 160, Cols = 8, Yaws = 32;
    private const float Ppu = 125f, OriginY = 40f;
    private const float Squash = 0.58f, HeightK = 0.82f;  // 床の奥行き・高さが画面で縮む倍率
    private const float TrunnionH = 0.34f;      // 筒の軸の高さ (床から)
    private const float MuzzleLen = 0.58f;      // 筒の軸から砲口まで (筒に沿って)
    private const float LoadPitch = 60f, AimPitch = 10f;
    private const float AimTurn = 0.24f;        // 装填の後に筒を倒す秒
    private const float YawSpeed = 540f;        // 筒が狙いへ回る速さ (度/秒)
    private const float Inside = 0.2f;           // 押し込んだ相手の体の中心 (砲口から筒の奥へ)
    private const float Entry = 0.35f;           // 押し込む直前の相手の体の中心 (砲口から外へ)
    private const float HandSize = 0.42f, HandDx = 0.3f, HandDy = 0.12f;
    private const float HandRot = -60f;          // 左の手 (右の手は鏡)。指先が相手の体の側を向く
    private const float CannonHalf = 0.55f;      // 台の半分の幅 (壁から離す分)
    private const float Recoil = 0.12f, RecoilIn = 0.05f, RecoilBack = 0.3f;
    private const float FlashTime = 0.12f, FlashSize = 0.55f;
    private const float SinkAfter = 0.5f, SinkTime = 0.3f;
    private const float TrailEvery = 0.05f;
    private const float BodyToFeet = 0.3636f;   // 人の体の中心 (transform) から足元まで
    // 飛ぶ姿勢: 頭を飛ぶ向きへ倒し、飛び出した直後は縦に伸びて少しずつ戻る (体の向きの縦横の倍率)
    private const float FlyStretch0 = 1.28f, FlyStretch1 = 1.1f, FlySquash0 = 0.84f, FlySquash1 = 0.93f, FlySettle = 0.2f;

    // ---- 絵 ----
    private static Sprite _flash;
    private static readonly Sprite[] Frames = new Sprite[Yaws + 6];
    private static readonly Sprite[] Hand = new Sprite[3];   // 0 = 開いて近づく・1 = 肩に掛ける・2 = 握り込む
    private static readonly Vector2[] HandCenter = new Vector2[3];
    private static bool _searched;
    private static object _match; // 後始末を足した試合 (1 試合に 1 回)

    private static readonly List<RocketCannon> Live = new();
    private static readonly List<Puff> Puffs = new();
    private static readonly Stack<Puff> PuffPool = new();
    private const int MaxPuffs = 96;

    // 着弾 (爆発の広さ r に比べた大きさ)
    private const float ImpactCoreK = 1.5f, ImpactCoreLife = 0.16f;           // 白い核 (閃光の絵)
    private const float ImpactGlowK0 = 1.1f, ImpactGlowK1 = 2.6f, ImpactGlowLife = 0.32f; // 外の橙のにじみ
    private const int ImpactRing = 12, ImpactSparks = 10;
    private const float ShakeReach = 9f;

    private enum PuffKind : byte { Smoke, Core, Glow, Spark }

    private sealed class Puff
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public float Start, Life, Size0, Size1, X, Y, Vx, Vy, Z;
        public bool Warm;
        public PuffKind Kind;
        public Sprite Art;
    }

    private readonly RoleBase _owner;
    private PlayerControl _launcher, _target;
    private GameObject _root;
    private Transform _rootTf, _cannonTf, _flashTf;
    private SpriteRenderer _cannonSr, _flashSr;
    private int _frame = -1;
    private GameObject _handL, _handR;
    private SpriteRenderer _handLsr, _handRsr;
    private int _handFrame = -1;
    private bool _handsOn, _flashOn;
    private float _handAlpha = -1f, _recoilSet = -1f;
    private float _room = -1f, _roomSide, _roomAt = -9f; // 置く側の壁までの空き (0.15 秒ごとに測り直す)
    private float _loadStart = -1f, _fireStart = -1f, _lastTrail;
    private float _side = 1f, _aim;              // _aim = 画面の上の狙いの向き (度)
    private float _yaw;                          // 見えている筒の床の上の向き (度)
    private float _offX = float.NaN;             // 撃つ人から台までの横のずれ (左右を替える時は滑らせる)
    private bool _local;                         // 撃つ人の端末 (狙いが分かる)
    private bool _hidden, _flying, _done;
    private Vector2 _p0, _hold;
    private bool _hasHold;
    // 飛ぶ姿勢で動かした相手の体と着せ替え (戻すために元の値を控える)
    private Transform _poseBody, _poseCos;
    private Vector3 _bodyPos0, _bodyScale0, _cosPos0, _cosScale0;
    private float _poseCx, _poseCy, _poseAng;
    private bool _posed;

    private RocketCannon(RoleBase owner) => _owner = owner;

    internal static RocketCannon For(RoleBase owner)
    {
        foreach (var c in Live) if (c._owner == owner) return c;
        return null;
    }

    // 掴んだ時 (全員の端末)
    internal static void Load(RoleBase owner, PlayerControl launcher, PlayerControl target)
    {
        For(owner)?.Remove();
        if (!launcher || !target || !Search()) return;
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(() => { ClearAll(); _match = null; });
        }
        var c = new RocketCannon(owner) { _launcher = launcher, _target = target, _local = launcher.AmOwner };
        c._side = launcher.cosmetics && launcher.cosmetics.FlipX ? -1f : 1f;
        // 向いている側に壁が近ければ反対側へ
        var feet = launcher.GetTruePosition();
        if (Room(feet, c._side) < Side + CannonHalf && Room(feet, -c._side) > Room(feet, c._side)) c._side = -c._side;
        c._aim = c._yaw = c._side > 0f ? 0f : 180f;
        var tp = target.transform.position;
        c._p0 = FxMath.V2(tp.x, tp.y);
        c._loadStart = Time.time;
        if (!c.Build(launcher.Data != null ? launcher.Data.DefaultOutfit.ColorId : 0)) return;
        Live.Add(c);
        c.Place(0f);
        GunSound.Load(launcher);
    }

    // 相手の体の中心を置く所 (装填の動きの途中も)。大砲が無ければ false
    internal bool HoldPosition(out Vector2 at)
    {
        at = _hold;
        return _hasHold && !_done;
    }

    // 砲口 (撃つ向きの筒の先)。発射の時に相手を出す所
    internal Vector2 Muzzle(Vector2 dir)
    {
        float yaw = YawOf(FxMath.Atan2(dir.y, dir.x) * (180f / System.MathF.PI));
        return ModelPoint(yaw, AimPitch, MuzzleLen);
    }

    // 自分の端末だけ: 狙いの向き (度)
    internal void Aim(float deg) => _aim = deg;

    // 発射 (全員の端末)
    internal void Fire(Vector2 dir)
    {
        if (_done) return;
        _aim = FxMath.Atan2(dir.y, dir.x) * (180f / System.MathF.PI);
        _yaw = YawOf(_aim);
        _fireStart = Time.time;
        _flying = true;
        _lastTrail = 0f;
        Show();
        BeginPose();
        var m = Muzzle(dir);
        GunSound.Fire(_launcher, m);
        if (_flashTf)
        {
            _flashTf.position = FxMath.V3(m.x, m.y, Z() - 0.002f);
            _flashTf.rotation = FxMath.RotZ(FxMath.Range(0f, 360f));
        }
        for (int i = 0; i < 6; i++)
        {
            float a = FxMath.Range(-0.6f, 0.6f);
            float c = FxMath.Cos(a), s = FxMath.Sin(a);
            float vx = (dir.x * c - dir.y * s) * FxMath.Range(0.4f, 1.1f), vy = (dir.x * s + dir.y * c) * FxMath.Range(0.4f, 1.1f) + 0.15f;
            SpawnPuff(m.x, m.y, vx, vy, 0.22f, FxMath.Range(0.5f, 0.75f), FxMath.Range(0.5f, 0.7f), Z() - 0.001f, i < 2);
        }
    }

    // 飛んだ相手が止まった (爆発・会議・抜けた)
    internal void Landed()
    {
        GunSound.StopFly();
        _flying = false;
        EndPose();
        Show();
    }

    // 撃たれた人が爆発した所 (全員の端末)。白い核 → 橙のにじみ → 火の色から白へ変わる煙の輪と火花。近い人ほど画面が揺れる
    internal static void Impact(Vector2 at, float r)
    {
        GunSound.Blast(at);
        if (!Search()) return;
        float z = (at.y - 1.5f) / 1000f; // そばに立つ人より手前
        SpawnPuff(at.x, at.y, 0f, 0f, r * ImpactGlowK0, r * ImpactGlowK1, ImpactGlowLife, z + 0.0002f, true, PuffKind.Glow);
        SpawnPuff(at.x, at.y, 0f, 0f, r * ImpactCoreK * 0.7f, r * ImpactCoreK, ImpactCoreLife, z, true, PuffKind.Core);
        for (int i = 0; i < ImpactRing; i++)
        {
            float a = (i + FxMath.Range(-0.3f, 0.3f)) * (2f * System.MathF.PI / ImpactRing);
            float v = r * FxMath.Range(1.3f, 2.0f);
            SpawnPuff(at.x, at.y, FxMath.Cos(a) * v, FxMath.Sin(a) * v * 0.75f, 0.35f, r * 0.55f + 0.3f, FxMath.Range(0.8f, 1.1f), z + 0.0004f, true);
        }
        for (int i = 0; i < ImpactSparks; i++)
        {
            float a = FxMath.Range(0f, 2f * System.MathF.PI);
            float v = r * FxMath.Range(3f, 5f);
            SpawnPuff(at.x, at.y, FxMath.Cos(a) * v, FxMath.Sin(a) * v, 0.16f, 0.08f, FxMath.Range(0.25f, 0.4f), z - 0.0002f, true, PuffKind.Spark, a * (180f / System.MathF.PI));
        }
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (!lp || !hud || !hud.PlayerCam) return;
        var me = lp.transform.position;
        float dx = me.x - at.x, dy = me.y - at.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d >= ShakeReach) return;
        float k = 1f - d / ShakeReach;
        hud.PlayerCam.ShakeScreen(0.2f + 0.25f * k, 0.5f + 2.5f * k * k);
    }

    // 撃たずに終わった (役職が外れた・死んだ)
    internal static void Drop(RoleBase owner) => For(owner)?.Remove();

    internal static void ClearAll()
    {
        GunSound.StopFly();
        for (int i = Live.Count - 1; i >= 0; i--) Live[i].Remove();
        foreach (var p in Puffs) if (p.Go) Object.Destroy(p.Go);
        Puffs.Clear();
        foreach (var p in PuffPool) if (p.Go) Object.Destroy(p.Go);
        PuffPool.Clear();
        _searched = false;
    }

    // ---- 毎フレーム ----

    internal static void Tick()
    {
        if (Live.Count == 0 && Puffs.Count == 0) return;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var c = Live[i];
            if (!c._root || !c._launcher) { c.Remove(); continue; }
            c.Place(now - c._loadStart);
            if (c._fireStart >= 0f && now - c._fireStart > SinkAfter + SinkTime && !c._flying) c.Remove();
        }
        TickPuffs(now);
    }

    private void Place(float t)
    {
        var lp = _launcher.transform.position;
        var feet = FxMath.V2(lp.x, lp.y - BodyToFeet);
        // 装填が済んだら、大砲は狙いの側 (他の人の画面では向いている側) へ回る。その側が壁で詰まっていれば今の側のまま
        if (_fireStart < 0f && t > PushEnd)
        {
            float want = _local ? (FxMath.Cos(_aim * (System.MathF.PI / 180f)) < 0f ? -1f : 1f)
                                : (_launcher.cosmetics && _launcher.cosmetics.FlipX ? -1f : 1f);
            if (want != _side && Room(feet, want) >= CannonHalf + 0.35f) _side = want;
        }
        // 壁にめり込まないよう、置く側の空きに合わせて手前へ詰める
        if (_roomSide != _side || Time.time - _roomAt > 0.15f)
        {
            _room = Room(feet, _side);
            _roomSide = _side;
            _roomAt = Time.time;
        }
        float room = _room;
        float dist = FxMath.Clamp(room - CannonHalf, 0.35f, Side);
        float goal = _side * dist;
        if (float.IsNaN(_offX)) _offX = goal;
        else _offX += (goal - _offX) * FxMath.Clamp01(Time.deltaTime * 12f);
        float fx = lp.x + _offX, fy = feet.y;
        float z = Z();
        _rootTf.position = FxMath.V3(fx, fy, z);

        // せり上がる (少し行き過ぎて戻る)・撃った後は沈む
        float sy;
        if (_fireStart >= 0f && Time.time - _fireStart > SinkAfter) sy = FxMath.Clamp01(1f - (Time.time - _fireStart - SinkAfter) / SinkTime);
        else if (t < Rise * 0.8f) sy = EaseOut(t / (Rise * 0.8f)) * 1.08f;
        else sy = t < Rise ? 1.08f - 0.08f * ((t - Rise * 0.8f) / (Rise * 0.2f)) : 1f;
        _rootTf.localScale = FxMath.V3(1f, sy, 1f);

        // 筒の向き: 装填の間は撃つ人の側へ上向き → 済んだら 3 段で倒し、狙いの向きへ回る (撃った後は撃った向きのまま)
        int frame;
        float baseYaw = _side > 0f ? 0f : 180f;
        if (_fireStart < 0f && t <= PushEnd + AimTurn)
        {
            int step = t <= PushEnd ? 0 : (int)FxMath.Clamp(1f + (t - PushEnd) / AimTurn * 3f, 1f, 3f);
            _yaw = baseYaw;
            frame = step < 3 ? Yaws + (_side > 0f ? 0 : 3) + step : YawFrame(_yaw);
        }
        else
        {
            if (_fireStart < 0f)
            {
                float want = _local ? YawOf(_aim) : baseYaw;
                float d = DeltaDeg(_yaw, want), max = YawSpeed * Time.deltaTime;
                _yaw += d > max ? max : d < -max ? -max : d;
            }
            frame = YawFrame(_yaw);
        }
        if (frame != _frame && Frames[frame])
        {
            _frame = frame;
            _cannonSr.sprite = Frames[frame];
        }
        float recoil = 0f;
        if (_fireStart >= 0f)
        {
            float f = Time.time - _fireStart;
            recoil = f < RecoilIn ? Recoil * f / RecoilIn : Recoil * FxMath.Clamp01(1f - (f - RecoilIn) / RecoilBack);
        }
        if (recoil != _recoilSet)
        {
            _recoilSet = recoil;
            float ar = _aim * (System.MathF.PI / 180f);
            _cannonTf.localPosition = FxMath.V3(-FxMath.Cos(ar) * recoil, -FxMath.Sin(ar) * recoil, 0f);
        }

        // 閃光
        if (_flashSr)
        {
            float f = _fireStart >= 0f ? Time.time - _fireStart : 99f;
            bool on = f < FlashTime;
            if (on)
            {
                float k = f < FlashTime * 0.3f ? f / (FlashTime * 0.3f) : 1f - (f - FlashTime * 0.3f) / (FlashTime * 0.7f);
                float s = FlashSize * (0.6f + 0.4f * k);
                _flashTf.localScale = FxMath.V3(s, s, 1f);
                _flashSr.color = FxMath.Rgba(1f, 1f, 1f, FxMath.Clamp01(k * 1.4f));
            }
            if (_flashOn != on) { _flashOn = on; _flashSr.enabled = on; }
        }

        // 相手の置き場所 (装填の動き)
        if (_fireStart < 0f) PlaceTarget(t);
        else
        {
            _hasHold = false;
            if (_posed) PoseFly(Time.time - _fireStart);
            if (_flying && _target && Time.time - _lastTrail >= TrailEvery)
            {
                _lastTrail = Time.time;
                var p = _target.transform.position;
                float warm = Time.time - _fireStart;
                SpawnPuff(p.x + FxMath.Range(-0.05f, 0.05f), p.y - 0.1f + FxMath.Range(-0.05f, 0.05f), 0f, 0.1f, 0.18f, 0.42f, 0.45f, p.z + 0.001f, warm < 0.15f);
            }
        }
        PlaceHands(t);
    }

    private void PlaceTarget(float t)
    {
        float baseYaw = _side > 0f ? 0f : 180f;
        var m = ModelPoint(baseYaw, LoadPitch, MuzzleLen);
        var axis = ModelAxis(baseYaw, LoadPitch);
        float ax = axis.x, ay = axis.y;
        float ex = m.x + ax * Entry, ey = m.y + ay * Entry;    // 押し込む直前
        float ix = m.x - ax * Inside, iy = m.y - ay * Inside;  // 押し込んだ後 (筒の中)
        float x, y;
        if (t < SqueezeAt)
        {
            x = _p0.x;
            y = _p0.y + (t > GrabAt ? 0.05f * (t - GrabAt) / (SqueezeAt - GrabAt) : 0f);
        }
        else if (t < LiftEnd)
        {
            // 弧を描いて砲口の上へ (2 次ベジェ・ゆっくり出てゆっくり着く)
            float u = EaseInOut((t - SqueezeAt) / (LiftEnd - SqueezeAt));
            float cx = (_p0.x + ex) * 0.5f, cy = FxMath.Max(_p0.y, ey) + 0.7f;
            float a = (1f - u) * (1f - u), b = 2f * (1f - u) * u, c = u * u;
            x = a * _p0.x + b * cx + c * ex;
            y = a * (_p0.y + 0.05f) + b * cy + c * ey;
        }
        else if (t < PushEnd)
        {
            float u = EaseIn((t - LiftEnd) / (PushEnd - LiftEnd));
            x = ex + (ix - ex) * u;
            y = ey + (iy - ey) * u;
            if (u > 0.6f) Hide();
        }
        else
        {
            x = ix;
            y = iy;
            Hide();
        }
        _hold = FxMath.V2(x, y);
        _hasHold = true;
        if (_hidden && _target && _target.Visible) _target.Visible = false; // 本編が出し直すことがある
    }

    private void PlaceHands(float t)
    {
        if (!_handL) return;
        bool on = _fireStart < 0f && t < HandsGone;
        if (_handsOn != on) { _handsOn = on; _handL.SetActive(on); _handR.SetActive(on); }
        if (!on) return;
        int frame = t < GrabAt ? 0 : t < SqueezeAt ? 1 : 2;
        if (frame != _handFrame)
        {
            _handFrame = frame;
            _handLsr.sprite = _handRsr.sprite = Hand[frame];
        }
        float al = t < 0.1f ? t / 0.1f : t > PushEnd ? 1f - (t - PushEnd) / (HandsGone - PushEnd) : 1f;
        al = FxMath.Clamp01(al);
        if (al != _handAlpha)
        {
            _handAlpha = al;
            var col = FxMath.Rgba(1f, 1f, 1f, al);
            _handLsr.color = col;
            _handRsr.color = col;
        }
        // 手は相手の肩の上 (相手の体の中心に対して)。押し込んだ後は筒の中の位置に付いていく
        Vector2 body = _hasHold ? _hold : _p0;
        var hc = HandCenter[frame];
        float z = Z() - 0.004f;
        // 絵の中心が置きたい所に来るよう、回した後の中心のずれを引く (右の手は左右の鏡)
        float r = HandRot * (System.MathF.PI / 180f), cs = FxMath.Cos(r), sn = FxMath.Sin(r);
        float ox = (hc.x * cs - hc.y * sn) * HandSize, oy = (hc.x * sn + hc.y * cs) * HandSize;
        _handL.transform.position = FxMath.V3(body.x - HandDx - ox, body.y + HandDy - oy, z);
        _handR.transform.position = FxMath.V3(body.x + HandDx + ox, body.y + HandDy - oy, z);
    }

    // ---- 飛ぶ姿勢 ----

    // 相手の体と着せ替えを、体の絵の中心のまわりに回せるように控える (名札は回さない)
    private void BeginPose()
    {
        if (_posed || !_target || !_target.cosmetics) return;
        var root = _target.transform;
        _poseBody = root.Find("BodyForms");
        _poseCos = _target.cosmetics.transform;
        if (_poseBody) { _bodyPos0 = _poseBody.localPosition; _bodyScale0 = _poseBody.localScale; }
        _cosPos0 = _poseCos.localPosition;
        _cosScale0 = _poseCos.localScale;
        float sc = root.localScale.y;
        if (sc < 0.01f) sc = 1f;
        var bodyRef = _target.cosmetics.currentBodySprite;
        var body = bodyRef != null ? bodyRef.BodySprite : null;
        if (body)
        {
            Vector3 c = body.bounds.center, r = root.position;
            _poseCx = (c.x - r.x) / sc;
            _poseCy = (c.y - r.y) / sc;
        }
        else _poseCx = _poseCy = 0f;
        _poseAng = _aim - 90f; // 体の上 (頭) が飛ぶ向きを向く角度
        _posed = true;
        PoseFly(0f);
    }

    private void PoseFly(float t)
    {
        if (!_target) { _posed = false; return; }
        float k = EaseOut(t / FlySettle);
        float sy = FlyStretch0 + (FlyStretch1 - FlyStretch0) * k, sx = FlySquash0 + (FlySquash1 - FlySquash0) * k;
        // 中心 (cx, cy) を伸ばして回した先が元の中心に重なるようにずらす
        var off = FxMath.RotateZ(_poseAng, _poseCx * sx, _poseCy * sy);
        float ox = _poseCx - off.x, oy = _poseCy - off.y;
        var rot = FxMath.RotZ(_poseAng);
        if (_poseBody)
        {
            _poseBody.localPosition = FxMath.V3(_bodyPos0.x + ox, _bodyPos0.y + oy, _bodyPos0.z);
            _poseBody.localRotation = rot;
            _poseBody.localScale = FxMath.V3(_bodyScale0.x * sx, _bodyScale0.y * sy, _bodyScale0.z);
        }
        if (_poseCos)
        {
            _poseCos.localPosition = FxMath.V3(_cosPos0.x + ox, _cosPos0.y + oy, _cosPos0.z);
            _poseCos.localRotation = rot;
            _poseCos.localScale = FxMath.V3(_cosScale0.x * sx, _cosScale0.y * sy, _cosScale0.z);
        }
    }

    private void EndPose()
    {
        if (!_posed) return;
        _posed = false;
        if (_poseBody)
        {
            _poseBody.localPosition = _bodyPos0;
            _poseBody.localRotation = FxMath.RotZ(0f);
            _poseBody.localScale = _bodyScale0;
        }
        if (_poseCos)
        {
            _poseCos.localPosition = _cosPos0;
            _poseCos.localRotation = FxMath.RotZ(0f);
            _poseCos.localScale = _cosScale0;
        }
        _poseBody = _poseCos = null;
    }

    // ---- 作る・消す ----

    private bool Build(int color)
    {
        try
        {
            _root = new GameObject("MrpRocketCannon");
            var ship = ShipStatus.Instance;
            if (ship) _root.transform.SetParent(ship.transform, true);
            _rootTf = _root.transform;
            var cn = new GameObject("Cannon");
            _cannonTf = cn.transform;
            _cannonTf.SetParent(_rootTf, false);
            _cannonSr = cn.AddComponent<SpriteRenderer>();
            var fl = new GameObject("Flash");
            _flashTf = fl.transform;
            _flashTf.SetParent(_rootTf, true);
            _flashSr = fl.AddComponent<SpriteRenderer>();
            _flashSr.sprite = _flash;
            _flashSr.enabled = false;

            if (Hand[0] && Hand[1] && Hand[2])
            {
                var mat = FxHands.PlayerMat();
                _handL = NewHand(mat, color, false, out _handLsr);
                _handR = NewHand(mat, color, true, out _handRsr);
            }
            return true;
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"rocket cannon: {e}");
            Remove();
            return false;
        }
    }

    private GameObject NewHand(Material mat, int color, bool flip, out SpriteRenderer sr)
    {
        // 手は大砲の根の子にしない (せり上がる時の縦の伸び縮みで潰れるので)
        var go = new GameObject(flip ? "MrpRocketHandR" : "MrpRocketHandL");
        var ship = ShipStatus.Instance;
        if (ship) go.transform.SetParent(ship.transform, true);
        go.transform.localScale = FxMath.V3(HandSize, HandSize, 1f);
        sr = go.AddComponent<SpriteRenderer>();
        if (mat) sr.sharedMaterial = mat;
        PlayerMaterial.SetColors(color, sr);
        sr.flipX = flip;
        sr.sprite = Hand[0];
        go.transform.rotation = FxMath.RotZ(flip ? -HandRot : HandRot);
        go.SetActive(false);
        return go;
    }

    private void Remove()
    {
        _done = true;
        EndPose();
        Show();
        if (_root) Object.Destroy(_root);
        if (_handL) Object.Destroy(_handL);
        if (_handR) Object.Destroy(_handR);
        _root = _handL = _handR = null;
        Live.Remove(this);
    }

    private void Hide()
    {
        if (_hidden || !_target) return;
        _hidden = true;
        _target.Visible = false;
    }

    private void Show()
    {
        if (!_hidden) return;
        _hidden = false;
        if (_target) _target.Visible = true;
    }

    // ---- 小物 ----

    // 足元から side の向きの壁 (物・家具を含む) までの距離 (見る長さまで)
    private static float Room(Vector2 feet, float side) =>
        ShotKit.WallDistance(feet, FxMath.V2(side, 0f), Side + CannonHalf + 0.2f, Constants.ShipAndObjectsMask | (1 << 12));

    // 3D の大砲の点を画面へ: 筒の軸から筒に沿って len 進んだ所 (床の上の向き yaw・上向き pitch)。せり上がりの縦の倍率も掛ける
    private Vector2 ModelPoint(float yaw, float pitch, float len)
    {
        float y = yaw * (System.MathF.PI / 180f), p = pitch * (System.MathF.PI / 180f);
        float cp = FxMath.Cos(p);
        float dx = cp * FxMath.Cos(y) * len;
        float dy = cp * FxMath.Sin(y) * len * Squash + (TrunnionH + FxMath.Sin(p) * len) * HeightK;
        var r = _rootTf.position;
        float sy = _rootTf.localScale.y;
        return FxMath.V2(r.x + dx, r.y + dy * sy);
    }

    // 筒の向きの画面の上の単位ベクトル
    private static Vector2 ModelAxis(float yaw, float pitch)
    {
        float y = yaw * (System.MathF.PI / 180f), p = pitch * (System.MathF.PI / 180f);
        float cp = FxMath.Cos(p);
        float dx = cp * FxMath.Cos(y), dy = cp * FxMath.Sin(y) * Squash + FxMath.Sin(p) * HeightK;
        float n = FxMath.Sqrt(dx * dx + dy * dy);
        return n < 1e-5f ? FxMath.V2(1f, 0f) : FxMath.V2(dx / n, dy / n);
    }

    // 画面の上の向き (度) → 床の上の向き (度)。画面の縦は床の奥行きが縮んだ物なので伸ばして戻す
    private static float YawOf(float screenDeg)
    {
        float r = screenDeg * (System.MathF.PI / 180f);
        return FxMath.Atan2(FxMath.Sin(r) / Squash, FxMath.Cos(r)) * (180f / System.MathF.PI);
    }

    private static int YawFrame(float yaw)
    {
        int i = (int)System.MathF.Round(yaw / (360f / Yaws));
        return ((i % Yaws) + Yaws) % Yaws;
    }

    // 台が人と同じ奥行きの決まり (足元の y に体の高さを足して /1000) で前後に並ぶように
    private float Z() => _rootTf ? (_rootTf.position.y + BodyToFeet) / 1000f : 0f;

    private static float DeltaDeg(float from, float to)
    {
        float d = (to - from) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return d;
    }

    private static float EaseOut(float u) { u = FxMath.Clamp01(u); return 1f - (1f - u) * (1f - u); }
    private static float EaseIn(float u) { u = FxMath.Clamp01(u); return u * u; }
    private static float EaseInOut(float u) { u = FxMath.Clamp01(u); return u * u * (3f - 2f * u); }

    private static void SpawnPuff(float x, float y, float vx, float vy, float s0, float s1, float life, float z, bool warm, PuffKind kind = PuffKind.Smoke, float rot = float.NaN)
    {
        if (Puffs.Count >= MaxPuffs) return;
        var p = PuffPool.Count > 0 ? PuffPool.Pop() : null;
        if (p == null || !p.Go)
        {
            var go = new GameObject("MrpRocketPuff");
            var ship = ShipStatus.Instance;
            if (ship) go.transform.SetParent(ship.transform, true);
            p = new Puff { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        var art = kind == PuffKind.Core ? _flash : kind == PuffKind.Spark ? Terrain.DebrisArt.Spark : Terrain.DebrisArt.Puff;
        if (!ReferenceEquals(p.Art, art)) { p.Art = art; p.Sr.sprite = art; }
        p.Kind = kind;
        p.Go.SetActive(true);
        p.Start = Time.time;
        p.Life = life;
        p.Size0 = s0;
        p.Size1 = s1;
        p.X = x; p.Y = y; p.Vx = vx; p.Vy = vy; p.Z = z;
        p.Warm = warm;
        p.Tf.rotation = FxMath.RotZ(float.IsNaN(rot) ? FxMath.Range(0f, 360f) : rot);
        Puffs.Add(p);
    }

    private static void TickPuffs(float now)
    {
        for (int i = Puffs.Count - 1; i >= 0; i--)
        {
            var p = Puffs[i];
            float f = (now - p.Start) / p.Life;
            if (f >= 1f || !p.Go)
            {
                if (p.Go) { p.Go.SetActive(false); PuffPool.Push(p); }
                Puffs.RemoveAt(i);
                continue;
            }
            float t = now - p.Start;
            float drag = p.Kind == PuffKind.Spark ? 1f - f * 0.3f : 1f - f * 0.6f;
            float s = p.Size0 + (p.Size1 - p.Size0) * EaseOut(f);
            p.Tf.position = FxMath.V3(p.X + p.Vx * t * drag, p.Y + p.Vy * t * drag, p.Z);
            p.Tf.localScale = FxMath.V3(s, s, 1f);
            switch (p.Kind)
            {
                case PuffKind.Core: // 白い核は一瞬で抜ける
                    p.Sr.color = FxMath.Rgba(1f, 1f, 0.92f, FxMath.Clamp01((1f - f) * 1.6f));
                    break;
                case PuffKind.Glow: // 明るい黄から橙へ移りながら消える (濃いと明るい床の上で茶色の円盤に見える)
                    p.Sr.color = FxMath.Rgba(1f, 0.95f - 0.35f * f, 0.65f - 0.35f * f, (1f - f) * (1f - f) * 0.5f);
                    break;
                case PuffKind.Spark:
                    p.Sr.color = FxMath.Rgba(1f, 0.9f - 0.4f * f, 0.45f - 0.3f * f, 1f - f * f);
                    break;
                default:
                {
                    // 撃った瞬間・爆発の煙は火の色から白へ・あとは白から薄れる
                    float w = p.Warm ? FxMath.Clamp01(1f - f * 4f) : 0f;
                    p.Sr.color = FxMath.Rgba(1f, 1f - 0.25f * w, 1f - 0.6f * w, (1f - f) * 0.9f);
                    break;
                }
            }
        }
    }

    // ---- 絵を探す (本編のペットをなでる手は人の部品と一緒に常に読まれている) ----

    private static bool Search()
    {
        if (Frames[0] && _flash) return true;
        if (_searched) return false;
        _searched = true;
        _flash = ButtonIcons.Load("rl_flash", 200f, FxMath.V2(0.5f, 0.5f));
        var atlas = ButtonIcons.Load("rl_cannon3d", Ppu, FxMath.V2(0.5f, 0.5f));
        var tex = atlas ? atlas.texture : null;
        if (tex)
        {
            int rows = tex.height / Cell;
            for (int k = 0; k < Frames.Length; k++)
            {
                float x = (k % Cols) * Cell, y = (rows - 1 - k / Cols) * Cell; // 絵の行は上から・テクスチャの y は下から
                Frames[k] = Sprite.Create(tex, new Rect(x, y, Cell, Cell), FxMath.V2(0.5f, OriginY / Cell), Ppu);
                Frames[k].name = "MrpRocketCannon" + k;
            }
        }
        string[] names = { "pet_hand0003", "pet_hand0007", "pet_hand0019" };
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (!s) continue;
            string nm = s.name;
            if (nm == null || nm.Length != 12 || !nm.StartsWith("pet_hand00", System.StringComparison.Ordinal)) continue;
            for (int i = 0; i < names.Length; i++)
                if (!Hand[i] && nm == names[i])
                {
                    Hand[i] = s;
                    var c = s.bounds.center;
                    HandCenter[i] = FxMath.V2(c.x, c.y);
                }
        }
        // 手の絵が見つからなければ手だけ出さない (大砲と着弾は出す)
        if (!Hand[0] || !Hand[1] || !Hand[2]) Plugin.Logger.LogWarning("rocket cannon: petting hand art not found");
        return Frames[0] && _flash;
    }
}
