using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// サムライの見た目 (全員の画面): 体から離れて浮く拳が刀を握り、体の絵そのものを足元を軸に歪めて (縮む・伸びる・傾く・跳ぶ・回る) 動く。
// 抜刀・納刀は決めた振り付け (30 コマ/秒の止め絵の刻み)、振りは自分のなぞり / ホストから届いた一振りを追う。
// 振った所には太い三日月の帯 (SamuraiBlade.Smear)、体には残像とオーラ (SamuraiBlade.Body)。
// 姿は「右を向いた時」の値で持ち、左を向いている間は根を鏡に映す (振りの間だけはワールドの向きのまま)。
// 毎フレーム書くのは抜く・納める・振る・戻る間だけ。構えて止まっている間は 4 フレームに 1 回 (息づかいも 15 コマ/秒の刻み)
internal sealed partial class SamuraiBlade
{
    // 刀の絵 (Resources/Icons/katana_held.png・440x110): 握る所 (78, 68)。切っ先まで約 0.79 単位
    private const float Ppu = 460f;
    private const float GripPx = 78f, GripPy = 68f, SpriteW = 440f, SpriteH = 110f;
    private const float TipLen = 0.79f;
    // 拳の絵 (FxHands の握った手・1.4 単位) を 0.24 単位に
    private const float FistSize = 0.24f / 1.4f;
    private const float FistRot = -90f;       // 刀の向きに対する拳の絵の傾き
    private const float SwingGrip = 0.22f;    // 振る間の体の中心から拳までの長さ
    internal const float BladeReach = SwingGrip + TipLen; // 振った刀の切っ先が通る体の中心からの長さ
    private const float SwingDrop = -0.04f;   // 振る間の拳の高さのずれ
    private const float Hold = 0.10f;         // 振り抜いた形で止める秒
    private const float Recover = 0.20f;      // 止めた後に構えへ戻る秒
    private const float Frame = 1f / 30f;     // 振り付けの刻み
    private const int CheckEvery = 6, StanceEvery = 4;

    // 確かめ用: 振り付けと再生の時間を何倍に伸ばすか (bridge)
    internal static float SlowMo = 1f;

    private static readonly List<SamuraiBlade> Live = new();
    private static int _frame;

    // 自分の刀 (なぞりの向きを追う)
    internal static SamuraiBlade LocalFollow;

    // 姿 (右を向いた時)。体は足元を軸に Lean 度傾け Sx/Sy 倍・Turn は横の倍率 (負 = 背を向けた所)・Hop/Dx は体の持ち上げと踏み込み。
    // 拳は体の中心から (Fx, Fy)・Fs = 拳の大きさ (0 = 出ていない)・Blade = 刀の向き (度)・BladeA = 刀の濃さ・Aura = オーラの強さ
    private struct Pose
    {
        public float Lean, Sx, Sy, Turn, Hop, Dx, Fx, Fy, Fs, Blade, BladeA, Aura;

        public static Pose Lerp(in Pose a, in Pose b, float t) => new()
        {
            Lean = a.Lean + (b.Lean - a.Lean) * t, Sx = a.Sx + (b.Sx - a.Sx) * t, Sy = a.Sy + (b.Sy - a.Sy) * t,
            Turn = a.Turn + (b.Turn - a.Turn) * t, Hop = a.Hop + (b.Hop - a.Hop) * t, Dx = a.Dx + (b.Dx - a.Dx) * t,
            Fx = a.Fx + (b.Fx - a.Fx) * t, Fy = a.Fy + (b.Fy - a.Fy) * t, Fs = a.Fs + (b.Fs - a.Fs) * t,
            Blade = a.Blade + (b.Blade - a.Blade) * t, BladeA = a.BladeA + (b.BladeA - a.BladeA) * t, Aura = a.Aura + (b.Aura - a.Aura) * t,
        };
    }

    // 振り付けの 1 コマ。Ease = 前のコマからの移り方 (0 = 直線・1 = 始め速く・2 = 止め絵 (着いた瞬間に切り替え))
    private readonly struct Key
    {
        public readonly float T;
        public readonly int Ease;
        public readonly Pose P;

        public Key(float t, int ease, float lean, float sx, float sy, float hop, float fx, float fy, float fs, float blade, float bladeA, float aura, float dx = 0f, float turn = 1f)
        {
            T = t;
            Ease = ease;
            P = new Pose { Lean = lean, Sx = sx, Sy = sy, Turn = turn, Hop = hop, Dx = dx, Fx = fx, Fy = fy, Fs = fs, Blade = blade, BladeA = bladeA, Aura = aura };
        }
    }

    // 構え: 拳を体の前に・刀は前の上へ立てる
    private const float StFx = 0.25f, StFy = -0.05f, StBlade = 58f, StLean = -4f;
    // 歩く構え: 前のめりで刀を前へ寝かせる
    private const float WkFx = 0.27f, WkFy = -0.10f, WkBlade = 18f, WkLean = -9f;

    // 鞘 (Resources/Icons/katana_saya.png・刀と同じ枠と握る所): 納めた刀と同じ所・同じ向きに置く。
    // 正体が分かるので普段は出さず、抜く間・納める間・本人の画面の溜めの間だけ出す
    private const float SayaA = 200f;                        // 納めた刀の向き (腰から後ろの下へ)
    private const float SayaX = -0.06f, SayaY = -0.15f;      // 納めた時の握る所
    private const float FwdX = 0.9397f, FwdY = 0.3420f;      // 鞘から抜ける向き (SayaA の逆 = 20 度)
    private const float SayaFade = 0.15f;                    // 抜き終えてから鞘が消えるまでの秒
    private const float IaiDraw = 2f * Frame;                // 居合で振りの前に鞘から滑り出る秒

    // 抜刀: 縮んで後ろへ反り、拳が柄を握って鞘の向きに沿って刀を滑り出させる (鯉口を切る → 半ばまで) →
    // 2 コマで刀を頭の上を越して前へ振り抜き (三日月)・伸びて浮く → 行き過ぎ → 着地で潰れる → 刀を立てて構え
    private static readonly Key[] DrawKeys =
    {
        new(0.000f, 0, 0f, 1.00f, 1.00f, 0f, SayaX, SayaY, 0.0f, SayaA, 1f, 0f),
        new(0.067f, 1, 9f, 1.12f, 0.86f, 0f, SayaX + 0.08f * FwdX, SayaY + 0.08f * FwdY, 1.2f, SayaA, 1f, 0.35f),
        new(0.133f, 2, 10f, 1.13f, 0.85f, 0f, SayaX + 0.30f * FwdX, SayaY + 0.30f * FwdY, 1.0f, SayaA, 1f, 0.45f),
        new(0.200f, 1, -13f, 0.88f, 1.16f, 0.08f, 0.33f, 0.10f, 1.1f, 32f, 1f, 1f, 0.05f),
        new(0.267f, 1, -15f, 0.93f, 1.09f, 0.04f, 0.37f, -0.02f, 1.0f, -22f, 1f, 1f, 0.06f),
        new(0.333f, 1, -6f, 1.13f, 0.87f, 0f, 0.30f, -0.07f, 1.0f, -12f, 1f, 0.8f, 0.04f),
        new(0.433f, 1, -2f, 0.97f, 1.04f, 0f, 0.23f, -0.03f, 1.0f, 66f, 1f, 0.6f),
        new(0.533f, 1, StLean, 1.00f, 1.00f, 0f, StFx, StFy, 1.0f, StBlade, 1f, 0.45f),
    };
    // 抜刀の振り抜き (三日月): 鯉口から前へ (度・秒)
    private const float DrawSmA0 = SayaA, DrawSmA1 = 32f, DrawSmT0 = 0.133f, DrawSmT1 = 0.200f;

    // 納刀: 血振り (前の下へ鋭く払う) → 止め → 刀を後ろへ回して切っ先を鯉口に当て、鞘の向きに沿って滑り込ませる →
    // カチリで小さく沈む → 拳と鞘が消える
    private static readonly Key[] SheatheKeys =
    {
        new(0.000f, 0, StLean, 1.00f, 1.00f, 0f, StFx, StFy, 1.0f, StBlade, 1f, 0.45f),
        new(0.067f, 1, -10f, 0.95f, 1.06f, 0f, 0.34f, -0.12f, 1.0f, -42f, 1f, 0.6f, 0.03f),
        new(0.200f, 2, -9f, 0.96f, 1.05f, 0f, 0.34f, -0.13f, 1.0f, -44f, 1f, 0.4f, 0.03f),
        new(0.333f, 1, 3f, 1.02f, 0.98f, 0f, SayaX + 0.40f * FwdX, SayaY + 0.40f * FwdY, 1.0f, SayaA, 1f, 0.2f),
        new(0.400f, 1, 5f, 1.08f, 0.91f, 0f, SayaX - 0.01f * FwdX, SayaY - 0.01f * FwdY, 1.1f, SayaA, 1f, 0.1f),
        new(0.533f, 1, 0f, 1.00f, 1.00f, 0f, SayaX, SayaY, 0.0f, SayaA, 0f, 0f),
    };
    private const float SheatheSayaIn = 0.267f, SheatheSayaOut = 0.433f;   // 納刀で鞘が出始める秒・消え始める秒
    private const float SheatheSmA0 = StBlade, SheatheSmA1 = -42f, SheatheSmT0 = 0f, SheatheSmT1 = 0.067f;

    private readonly Samurai _role;
    private GameObject _root;
    private Transform _rootTf, _gripTf, _fistTf, _sayaTf;
    private SpriteRenderer _sr, _fist, _saya;
    // 鞘の濃さ・鞘を後ろへ引く長さ (鞘引き)・刀より手前に置くか (刀が中にある間)。居合で抜いた時刻
    private float _sayaAlpha, _sayaBack, _iaiT = -10f;
    private bool _sayaFront, _sayaOn;
    private bool _drawn, _hidden, _flip, _fallenTint, _swinging, _moving, _spent;
    private float _drawT = -10f;    // 抜く/納める動作を始めた時刻
    private float _inv = 1f;
    private int _tick, _colorId = -1;
    // 他人の一振りの再生 (始めた時刻・始めの角度・掃引角・秒・修羅の色)
    private float _repT = -10f, _repA0, _repSweep, _repDur;
    private bool _repFallen;
    // 振り終えた後: 止めて戻る (止めた時刻と姿)
    private float _endT = -10f;
    private Pose _endPose, _last;
    private bool _endWorld;
    // 自分のなぞりの一振りの始めの角度
    private bool _localStroke;
    private float _localA0;
    private float _lastX, _lastY;
    private float _dashUntil = -10f;

    internal SamuraiBlade(Samurai role) => _role = role;

    // iai = 居合: 抜く振り付けを飛ばし、続く一振りでそのまま抜く
    internal void SetDrawn(bool drawn, bool iai = false)
    {
        _drawn = drawn;
        _drawT = iai ? Time.time - (DrawKeys[^1].T + Frame) * SlowMo : Time.time;
        _iaiT = drawn && iai ? Time.time : -10f;
        if (!drawn) _spent = false;
        if (drawn) { Ensure(); _sheatheSlow = 1f; }
        else StrikeSheathed();
        if (!_hidden)
        {
            if (!drawn) SamuraiSound.Sheathe(_role.Player, _sheatheSlow * SlowMo);
            else if (!iai) SamuraiSound.Draw(_role.Player);
        }
        if (!_root) return;
        // 振りの途中で納めたら振りを捨てる
        _repT = _endT = _thT = -10f;
        _localStroke = false;
        SpawnGlint(drawn ? 0.10f : 0.38f * _sheatheSlow, drawn);
        Track();
    }

    // 踏み込み: seconds の間、残像を残す
    internal void Dash(float seconds)
    {
        if (!_drawn || !Ensure()) return;
        _dashUntil = Time.time + seconds;
        if (!_hidden) SamuraiSound.Dash(_role.Player);
        Track();
    }

    // 気が尽きた間は刀が下がって切っ先が床を向く
    internal bool Spent => _spent;

    internal void SetSpent(bool spent)
    {
        if (spent && !_spent && _drawn && !_hidden) SamuraiSound.Spent(_role.Player);
        _spent = spent;
        if (_root) Track();
    }

    internal void SetFallen(bool tint)
    {
        _fallenTint = tint;
        if (_sr) _sr.color = Tint(_last.BladeA);
    }

    // 他人の一振りを再生する (a0 度から sweep 度を seconds で)
    internal void Replay(float a0, float sweep, float seconds, bool fallen)
    {
        if (!_drawn || !Ensure()) return;
        _repT = Time.time;
        _repA0 = a0;
        _repSweep = sweep;
        _repDur = FxMath.Max(seconds, 0.05f);
        _repFallen = fallen;
        _endT = -10f;
        Track();
    }

    // FixedUpdate ごと (役職から)。数回に 1 回だけ、死んだら片付ける
    internal void Tick()
    {
        if (++_tick < CheckEvery) return;
        _tick = 0;
        if (!_root) return;
        var pc = _role.Player;
        if (!pc || pc.Data == null || pc.Data.IsDead) { Dispose(); return; }
        if (_drawn) Track();
    }

    internal void Dispose()
    {
        Live.Remove(this);
        EndCharge();
        RestoreBody();
        DisposeFx();
        DisposeStrike();
        if (_root) Object.Destroy(_root);
        _root = null;
        if (LocalFollow == this) LocalFollow = null;
    }

    private bool Ensure()
    {
        if (_root) return true;
        var pc = _role.Player;
        if (!pc) return false;
        var blade = ButtonIcons.Load("katana_held", Ppu, FxMath.V2(GripPx / SpriteW, 1f - GripPy / SpriteH));
        var saya = ButtonIcons.Load("katana_saya", Ppu, FxMath.V2(GripPx / SpriteW, 1f - GripPy / SpriteH));
        var fist = FxHands.GripSprite();
        var mat = FxHands.PlayerMat();
        if (!blade || !saya || !fist || !mat) return false;
        int layer = pc.gameObject.layer;
        _root = new GameObject("MrpKatana") { layer = layer };
        _rootTf = _root.transform;
        _rootTf.SetParent(pc.transform, false);
        float scale = pc.transform.localScale.x;
        _inv = scale > 0.01f ? 1f / scale : 1f;
        var grip = new GameObject("Grip") { layer = layer };
        _gripTf = grip.transform;
        _gripTf.SetParent(_rootTf, false);
        // 刀は拳の奥
        var bgo = new GameObject("Blade") { layer = layer };
        bgo.transform.SetParent(_gripTf, false);
        bgo.transform.localPosition = FxMath.V3(0f, 0f, 0.0006f);
        _sr = bgo.AddComponent<SpriteRenderer>();
        _sr.sprite = blade;
        // 鞘は納めた刀と同じ置き方 (握る所が同じ枠の絵)。刀より奥/手前は WritePose で
        var sgo = new GameObject("Saya") { layer = layer };
        _sayaTf = sgo.transform;
        _sayaTf.SetParent(_rootTf, false);
        _saya = sgo.AddComponent<SpriteRenderer>();
        _saya.sprite = saya;
        _saya.enabled = false;
        var fgo = new GameObject("Fist") { layer = layer };
        _fistTf = fgo.transform;
        _fistTf.SetParent(_gripTf, false);
        _fistTf.localRotation = FxMath.RotZ(FistRot);
        _fist = fgo.AddComponent<SpriteRenderer>();
        _fist.sharedMaterial = mat;
        _fist.sprite = fist;
        _colorId = -1;
        RefreshColor();
        _flip = pc.cosmetics && pc.cosmetics.FlipX;
        _last = Rest();
        EnsureFx(layer);
        return true;
    }

    // 拳は本人の今の色 (変身すると変わる)
    private void RefreshColor()
    {
        var pc = _role.Player;
        if (!_fist || !pc || pc.Data == null) return;
        int id = pc.Data.DefaultOutfit.ColorId;
        if (pc.CurrentOutfit != null) id = pc.CurrentOutfit.ColorId;
        if (id == _colorId) return;
        _colorId = id;
        if (id >= 0 && id < Palette.PlayerColors.Length) PlayerMaterial.SetColors(id, _fist);
    }

    // 列に入る時に見え方を確かめ直す (列の外では確かめないので、演出で隠された後の印が残っている。
    // 残ったままだと抜く最初のコマの鞘と刀が出ない)
    private void Track()
    {
        if (Live.Contains(this)) return;
        if (_root) Check();
        Live.Add(this);
    }

    private Color Tint(float a) => _fallenTint ? FxMath.Rgba(1f, 0.45f, 0.5f, a) : FxMath.Rgba(1f, 1f, 1f, a);

    private static Pose Rest() => DrawKeys[0].P;

    // 毎フレーム (動いている刀だけ)
    internal static void TickAll()
    {
        SamuraiScreen.Tick();
        IaiScreen.Tick();
        TickFx();
        TickStrikes();
        if (Live.Count == 0) return;
        _frame++;
        bool check = _frame % CheckEvery == 0;
        bool stanceFrame = _frame % StanceEvery == 0;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var b = Live[i];
            if (!b._root) { Live.RemoveAt(i); continue; }
            if (check) b.Check();
            b.FadeSmear(now);
            if (!b.Busy(now) && !stanceFrame) continue;
            if (!b.Apply(now)) { Live.RemoveAt(i); continue; }
        }
    }

    // 向きと見え方 (ベントの中・透明・停電中のカメラの暗視では隠す)
    private void Check()
    {
        var pc = _role.Player;
        if (!pc) return;
        bool hidden = !pc.Visible || Rules.NightVisionCams.Active;
        if (hidden != _hidden)
        {
            _hidden = hidden;
            _sr.enabled = _fist.enabled = !hidden;
            if (hidden) _saya.enabled = _sayaOn = false;
            if (hidden) { RestoreBody(); HideFx(); HideStrike(); }
        }
        if (!_swinging) _flip = pc.cosmetics && pc.cosmetics.FlipX;
        RefreshColor();
    }

    // 毎フレーム書く必要があるか (抜く・納める・振る・止めて戻る間)
    private bool Busy(float now)
    {
        if ((now - _drawT) / SlowMo < 0.6f * _sheatheSlow || now < _dashUntil || LeapBusy(now)) return true;
        if ((now - _iaiT) / SlowMo < IaiDraw + SayaFade) return true;
        if (this == LocalFollow && (SwipeInput.Dragging || _localStroke) && !_spent) return true;
        if (_powArmed || _lockOn) return true;
        if ((now - _repT) / SlowMo <= _repDur + 0.05f || ThrustBusy(now)) return true;
        return (now - _endT) / SlowMo < Hold + IaiHold * _strokePow + _holdExtra + Recover || SmearLive;
    }

    // 振り付けの t 秒の姿
    private static Pose Eval(Key[] keys, float t)
    {
        if (t <= keys[0].T) return keys[0].P;
        for (int i = 1; i < keys.Length; i++)
        {
            var b = keys[i];
            if (t > b.T) continue;
            var a = keys[i - 1];
            float u = (t - a.T) / (b.T - a.T);
            if (b.Ease == 2) return a.P;
            if (b.Ease == 1) u = 1f - (1f - u) * (1f - u);
            return Pose.Lerp(a.P, b.P, u);
        }
        return keys[^1].P;
    }

    // 構え (歩いていれば前のめり)・息づかい
    private Pose Stance(float now)
    {
        var p = DrawKeys[^1].P;
        float w = _moving ? 1f : 0f;
        p.Fx = FxMath.Lerp(StFx, WkFx, w);
        p.Fy = FxMath.Lerp(StFy, WkFy, w);
        p.Blade = FxMath.Lerp(StBlade, WkBlade, w);
        p.Lean = FxMath.Lerp(StLean, WkLean, w);
        float br = FxMath.Sin(now * 4.4f);
        p.Fy += br * 0.012f;
        p.Sy += br * 0.012f;
        p.Sx -= br * 0.008f;
        p.Blade += br * 2f;
        p.Aura = 0.45f + 0.1f * FxMath.Sin(now * 2.3f);
        if (_spent)
        {
            // 肩で息をして、刀は下へ垂れる
            float pant = FxMath.Sin(now * 9f);
            p.Blade = -62f + pant * 3f;
            p.Fx = 0.2f;
            p.Fy = -0.16f;
            p.Lean = 7f;
            p.Sy = 0.93f + pant * 0.025f;
            p.Sx = 1.04f - pant * 0.015f;
            p.Aura = 0f;
        }
        return p;
    }

    // 今の振りの向き (度・ワールド)。振っていなければ false。a0 = この一振りの始めの角度 (三日月の尾)
    private bool SwingAngle(float now, out float angle, out float speed, out bool inStroke, out float a0)
    {
        angle = speed = a0 = 0f;
        inStroke = false;
        // 気が尽きて刀が下がっている間は、押したままのなぞりを追わない
        if (this == LocalFollow && SwipeInput.Dragging && !_spent)
        {
            angle = SwipeInput.Angle;
            speed = SwipeInput.Speed;
            inStroke = SwipeInput.InStroke;
            if (inStroke && !_localStroke) _localA0 = angle - speed * 0.05f;
            _localStroke = inStroke;
            a0 = _localA0;
            return true;
        }
        _localStroke = false;
        float t = (now - _repT) / SlowMo;
        if (t < 0f || t > _repDur) return false;
        float k = FxMath.Clamp01(t / _repDur);
        // 振りは始めが速く終わりで止まる
        float e = 1f - (1f - k) * (1f - k);
        angle = _repA0 + _repSweep * e;
        speed = _repSweep / _repDur * 2f * (1f - k);
        inStroke = true;
        a0 = _repA0;
        return true;
    }

    // 振っている間の姿 (ワールドの向き): 拳は体の周りの円・刀は外へ・体は振る向きへ傾き伸びて踏み込む・大きく回す振りは体ごと回る
    private Pose SwingPose(float angle, float speed, float progress, float sweep)
    {
        float ar = angle * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float fast = FxMath.Clamp01(FxMath.Abs(speed) / 700f);
        // 振りの始めは溜めて縮み、振り抜く所で伸びて前へ出る
        float bulge = FxMath.Sin(FxMath.Clamp01(progress) * FxMath.PI);
        var p = new Pose
        {
            Fx = co * SwingGrip, Fy = si * SwingGrip + SwingDrop, Fs = 1f + 0.15f * fast, Blade = angle, BladeA = 1f,
            Lean = -FxMath.Clamp(speed / 700f, -1f, 1f) * 20f,
            Sx = 1f - 0.14f * fast * bulge, Sy = 1f + 0.16f * fast * bulge, Hop = 0.06f * fast * bulge,
            Dx = co * 0.14f * fast, Turn = 1f, Aura = 0.6f + 0.4f * fast,
        };
        // 大きく回す振りは体ごと回る (横の倍率が 1 → -1 → 1)
        if (FxMath.Abs(sweep) >= 160f) p.Turn = FxMath.Cos(FxMath.Clamp01(progress) * 2f * FxMath.PI);
        return p;
    }

    // t の姿を書く。片付けてよければ false
    private bool Apply(float now)
    {
        var pc = _role.Player;
        if (!pc) return false;
        // 跳んでいる間は抜いていても納めていても跳ぶ姿 (向きはワールドのまま)
        if (LeapBusy(now))
        {
            var lp = LeapPose(now, pc);
            _swinging = true;
            _sayaAlpha = 0f;
            if (!_root.activeSelf) _root.SetActive(true);
            _last = lp;
            WritePose(pc, lp, true);
            return true;
        }
        float td = (now - _drawT) / SlowMo;
        // 居合の後の納刀はゆっくり (鍔が鳴る所で遅れ斬撃が走る)
        if (!_drawn) td /= _sheatheSlow;
        Pose p;
        bool world = false;
        bool drawing = td < DrawKeys[^1].T;
        float ti = (now - _iaiT) / SlowMo;
        bool sliding = _drawn && ti < IaiDraw && !_lockOn;
        _sayaAlpha = 0f;
        _sayaBack = 0f;
        _sayaFront = true;
        if (!_drawn && td >= SheatheKeys[^1].T && ChargePose(now, out var cp))
        {
            p = cp;
            _sayaAlpha = 1f;
        }
        else if (!_drawn)
        {
            if (td >= SheatheKeys[^1].T)
            {
                EndCharge();
                _swinging = false;
                _root.SetActive(false);
                RestoreBody();
                HideFx();
                return false;
            }
            p = Eval(SheatheKeys, FxMath.Floor(td / Frame) * Frame);
            _sayaAlpha = FxMath.Clamp01((td - SheatheSayaIn) / Frame) * (1f - FxMath.Clamp01((td - SheatheSayaOut) / (SheatheKeys[^1].T - SheatheSayaOut)));
            if (td >= SheatheSmT0 && td <= SheatheSmT1 + Frame) DrawSmear(SheatheSmA0, FxMath.Lerp(SheatheSmA0, SheatheSmA1, (td - SheatheSmT0) / (SheatheSmT1 - SheatheSmT0)), td > SheatheSmT1, 0.6f, false);
        }
        else if (drawing)
        {
            float tf = FxMath.Floor(td / Frame) * Frame;
            p = Eval(DrawKeys, tf);
            // 鯉口を切る所で鞘を少し引き、半ばまで抜く所でさらに引く (鞘引き)。振り抜いた後に消える
            _sayaBack = tf >= DrawKeys[2].T ? 0.07f : tf >= DrawKeys[1].T ? 0.03f : 0f;
            _sayaFront = tf < DrawSmT1;
            _sayaAlpha = 1f - FxMath.Clamp01((td - DrawSmT1) / SayaFade);
            if (td >= DrawSmT0 && td <= DrawSmT1 + Frame) DrawSmear(DrawSmA0, FxMath.Lerp(DrawSmA0, DrawSmA1, (td - DrawSmT0) / (DrawSmT1 - DrawSmT0)), td > DrawSmT1, 1f, false);
        }
        else if (_lockOn)
        {
            p = LockPose(now, pc);
            world = true;
            LockSparks(now, pc);
        }
        else if (ThrustPose(now, out var tp))
        {
            if (_wasInStroke) EndStroke();
            _wasInStroke = false;
            p = tp;
            world = true;
            _endPose = p;
            _endT = now;
            _endWorld = true;
        }
        else if (SwingAngle(now, out float a, out float speed, out bool inStroke, out float a0))
        {
            float sweep = a - a0;
            bool live = this == LocalFollow && SwipeInput.Dragging && !_spent;
            float prog = live ? FxMath.Clamp01(FxMath.Abs(sweep) / 360f) : FxMath.Clamp01((now - _repT) / SlowMo / _repDur);
            float shownSweep = live ? sweep : _repSweep;
            if (inStroke && !_wasInStroke) BeginStroke();
            else if (!inStroke && _wasInStroke) EndStroke();
            _wasInStroke = inStroke;
            p = SwingPose(a, speed, prog, shownSweep);
            if (_strokePow > 0f) IaiLunge(ref p, a, prog);
            world = true;
            if (inStroke && !sliding)
            {
                DrawSmear(a0, a, false, 1f, true);
                SpawnGhost(now);
            }
            else if (SmearLive) ReleaseSmear();
            _endPose = p;
            _endT = now;
            _endWorld = true;
        }
        else
        {
            if (_wasInStroke) EndStroke();
            _wasInStroke = false;
            if (_swinging) ReleaseSmear();
            float te = (now - _endT) / SlowMo;
            float hold = Hold + IaiHold * _strokePow + _holdExtra;
            if (_endWorld && te < hold + Recover)
            {
                // 振り抜いた形で止め、構えへ戻る (戻る間は右/左の向きへ鏡に移る)
                p = _endPose;
                world = true;
                if (te >= hold)
                {
                    float u = (te - hold) / Recover;
                    u = u * u * (3f - 2f * u);
                    var st = Stance(now);
                    if (_flip) Mirror(ref st);
                    p = Pose.Lerp(_endPose, st, u);
                }
            }
            else
            {
                _endWorld = false;
                _strokePow = _auraHot = _holdExtra = 0f;
                SampleMoving(pc);
                p = Stance(FxMath.Floor(now * 15f) / 15f);
            }
        }
        if (_drawn && ti < IaiDraw + SayaFade && !_lockOn)
        {
            if (sliding)
            {
                // 居合: 振りの前の 2 コマで、柄を握った拳が鞘の向きに沿って刀を滑り出させる (三日月はまだ出さない)
                int k = ti < Frame ? 0 : 1;
                float d = k == 0 ? 0.24f : 0.50f;
                p = new Pose
                {
                    Lean = 10f, Sx = 1.12f, Sy = 0.86f, Turn = 1f, Hop = 0f, Dx = 0f,
                    Fx = SayaX + d * FwdX, Fy = SayaY + d * FwdY, Fs = 1.15f, Blade = SayaA, BladeA = 1f,
                    Aura = FxMath.Max(p.Aura, 0.8f),
                };
                world = false;
                _sayaBack = k == 0 ? 0.04f : 0.09f;
                _sayaFront = true;
                _sayaAlpha = 1f;
            }
            else
            {
                _sayaBack = 0.09f;
                _sayaFront = false;
                _sayaAlpha = 1f - FxMath.Clamp01((ti - IaiDraw) / SayaFade);
            }
        }
        if (now < _dashUntil) SpawnGhost(now);
        _swinging = world;
        if (!_root.activeSelf) _root.SetActive(true);
        _last = p;
        WritePose(pc, p, world);
        return true;
    }

    // 鞘は納めた刀の所に止めたまま (体の傾きは継がない = 刀と軸がずれない)。mirror = 振りの間 (根を返さない) に左を向いている
    private void WriteSaya(bool mirror)
    {
        bool on = !_hidden && _sayaAlpha > 0.01f;
        if (_sayaOn != on) _saya.enabled = _sayaOn = on;
        if (!on) return;
        float x = SayaX - _sayaBack * FwdX, y = SayaY - _sayaBack * FwdY;
        // 刀の奥は拳の z から刀の差 (0.0006) だけ奥。中にある間は鞘を刀の手前へ
        float z = _sayaFront ? -0.0027f : -0.0021f;
        _sayaTf.localPosition = FxMath.V3(mirror ? -x : x, y, z);
        _sayaTf.localRotation = FxMath.RotZ(mirror ? 180f - SayaA : SayaA);
        _sayaTf.localScale = FxMath.V3(1f, mirror ? -1f : 1f, 1f);
        _saya.color = Tint(_sayaAlpha);
    }

    private static void Mirror(ref Pose p)
    {
        p.Lean = -p.Lean;
        p.Dx = -p.Dx;
        p.Fx = -p.Fx;
        p.Blade = 180f - p.Blade;
    }

    // 4 フレームごとの位置の差で歩いているかを見る
    private void SampleMoving(PlayerControl pc)
    {
        var at = pc.transform.position;
        float dx = at.x - _lastX, dy = at.y - _lastY;
        _lastX = at.x;
        _lastY = at.y;
        _moving = dx * dx + dy * dy > 0.0004f;
    }

    // 姿を体・拳・刀へ書く。world = 右/左の向きに関わらず値をそのまま使う (振りの間)
    private void WritePose(PlayerControl pc, in Pose p, bool world)
    {
        bool flip = !world && _flip;
        float m = flip ? -1f : 1f;
        _rootTf.localScale = FxMath.V3(m * _inv, _inv, 1f);
        // 刀は体の中心から。左半分を向く間は刃を下に保つため上下を返す (根は返さない)
        float blade = p.Blade;
        float gsy = world && (FxMath.Repeat(blade + 90f, 360f) > 180f) ? -1f : 1f;
        float fs = FxMath.Max(p.Fs, 0f) * FistSize;
        _gripTf.localPosition = FxMath.V3(p.Fx + p.Dx, p.Fy + p.Hop, -0.003f);
        _gripTf.localRotation = FxMath.RotZ(blade);
        _gripTf.localScale = FxMath.V3(1f, gsy, 1f);
        _fistTf.localScale = FxMath.V3(fs, fs, 1f);
        _sr.color = Tint(FxMath.Clamp01(p.BladeA));
        WriteSaya(world && _flip);
        if (_hidden) return;
        var body = new Pose { Lean = p.Lean * m, Sx = p.Sx, Sy = p.Sy, Turn = p.Turn, Hop = p.Hop, Dx = p.Dx * m };
        WriteBody(pc, body);
        WriteAura(p.Aura, p.Hop);
    }

    internal string Describe() =>
        $"root={(_root ? (_root.activeSelf ? "on" : "off") : "none")} drawn={_drawn} hidden={_hidden} flip={_flip} live={Live.Contains(this)} swing={_swinging} moving={_moving} posed={_posed} smear={SmearLive} blade={_last.Blade:0} lean={_last.Lean:0.0} iai[armed={_lastArmedTier} strikes={_strikes} delayed={_delayedRuns}]";
}
