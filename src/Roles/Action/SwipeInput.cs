using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 武器をなぞりで振る入力 (自分の端末だけ)。PC = 左ボタンを押したままマウスを動かす・スマホ = 画面の右半分をなぞる。
// 武器の向き = 体の中心から指/カーソルへの角度。角度が速く回った区間を一振り (stroke) として切り出し、OnStroke で渡す。
// 体から外へ真っすぐ速く押し出した動きは突き (thrust) として OnThrust で渡す (回す = 斬り・押す = 突き)。
// 構えている間 (Armed) は PC のマウスで歩く操作を止める (歩くのはキーだけ)。
// 毎フレームの問い合わせは、構えている間だけ。なぞっている間だけ画面の座標をワールドへ写す (1 フレーム 1 回)
internal static class SwipeInput
{
    // 一振りと数える角速度 (度/秒)。始め・終わり・最小の掃引角・1 振りの最長
    internal const float StartSpeed = 320f, EndSpeed = 140f, MinSweep = 35f, MaxSweep = 270f, MaxSeconds = 0.6f;
    private const float EndHold = 0.05f; // 遅くなってから終わりと決めるまで
    // 突きと数える外向きの速さ (単位/秒)・押し出した長さ・押した向きと外向きの揃い (cos)。カメラの写す範囲 3 の時の値で、寄り引きに合わせて伸び縮みさせる
    private const float ThrustSpeed = 4.5f, ThrustDist = 0.7f, ThrustAlign = 0.8f;
    // 体にこれより近いと角度が少しの横ずれで跳ぶので、一振りを始めない
    private const float NearRadius = 0.25f;

    private static readonly bool Touch = OperatingSystem.IsAndroid();

    // 構えている人 (自分)。役職が毎回入れ直す。毎フレーム見るのは C# の側の印だけ (Unity の生存確認を毎回しない)
    internal static PlayerControl Owner;
    internal static bool Armed { get; private set; }
    private static Camera _cam; // 構えた時に 1 回だけ引く (Camera.main は中で探し直す)

    internal static bool Dragging { get; private set; }
    internal static float Angle { get; private set; }      // 今の武器の向き (度・連続)
    internal static bool InStroke { get; private set; }
    internal static float Speed { get; private set; }      // 角速度 (度/秒・符号付き)

    // 一振り: 始めの角度 (度)・符号付きの掃引角・かかった秒
    internal static event Action<float, float, float> OnStroke;
    // 突き: 押し出した向き (度)
    internal static event Action<float> OnThrust;
    // 一振りが始まった (速さが閾値を越えた所)。押してから始まるまでの溜めは Time.time - PressedAt
    internal static event Action OnStrokeStart;
    internal static float PressedAt { get; private set; }
    // スマホ: 押し始めがここで true を返す所 (役職のボタン) なら、なぞりにしない
    internal static Func<Vector2, bool> TouchBlocked;
    // 役職が今は突けない (気が尽きた) 間は立てる。押しっぱなしのなぞりは捨てずに、突きだけ数えない
    internal static bool NoThrust;

    private static int _finger = -1;
    private static float _prevT, _strokeStartA, _strokeStartT, _slowT;
    private static int _strokeSign;
    // 体からカーソルへの前のフレームの差・外向きの速さ (ならした値)・突きの押し出しの始め
    private static float _ox, _oy, _vr, _thX0, _thY0, _thSlowT, _scale = 1f;
    private static bool _thOn, _noStart;
    // 確かめ用の作り物のなぞり (bridge): a0 → a1 を ms で
    private static float _synA0, _synA1, _synStart = -1f, _synDur, _synHold;
    private static bool _synThrust;
    internal static int Strokes, Thrusts;
    // 押すたびに増える番号 (同じなぞりの中で続けた振りかを役職が見分ける)
    internal static int DragId;
    internal static string Last = "-";

    internal static void Arm(PlayerControl owner)
    {
        bool armed = owner;
        if (armed == Armed && (!armed || Owner == owner)) return;
        if (!armed) End(false);
        Owner = armed ? owner : null;
        Armed = armed;
        _cam = armed ? Camera.main : null;
    }

    // hold = 押したまま動かさずに待つ秒 (溜め)
    internal static void Synth(float a0, float a1, float seconds, float hold = 0f)
    {
        _synA0 = a0;
        _synA1 = a1;
        _synDur = Math.Max(seconds, 0.02f);
        _synHold = Math.Max(hold, 0f);
        _synThrust = false;
        _synStart = Time.time;
    }

    // 確かめ用: hold 秒押したまま待ってから angle 度へ突く
    internal static void SynthThrust(float angle, float hold = 0f)
    {
        _synA0 = angle;
        _synHold = Math.Max(hold, 0f);
        _synThrust = true;
        _synStart = Time.time;
    }

    internal static void Tick()
    {
        if (!Armed)
        {
            if (Dragging) End(false);
            return;
        }
        if (!Owner) { Arm(null); return; }
        float now = Time.time;
        if (_synStart >= 0f && _synThrust)
        {
            if (!Dragging) Begin(_synA0, now);
            if (now - _synStart < _synHold) { Step(_synA0, now); return; }
            _synStart = -1f;
            _synThrust = false;
            Fire(_synA0);
            return;
        }
        if (_synStart >= 0f)
        {
            float k = (now - _synStart - _synHold) / _synDur;
            if (!Dragging) Begin(_synA0, now);
            if (k < 0f) { Step(_synA0, now); return; }
            Step(FxMath.Lerp(_synA0, _synA1, k), now);
            if (k >= 1f) { _synStart = -1f; End(true); }
            return;
        }
        if (!Pointer(out Vector2 screen, out bool down, out bool began))
        {
            if (Dragging) End(true);
            return;
        }
        if (!Dragging)
        {
            if (!began || OverUi()) return;
        }
        else if (!down)
        {
            End(true);
            return;
        }
        var cam = _cam;
        if (!cam) { _cam = cam = Camera.main; if (!cam) return; }
        Vector3 w = cam.ScreenToWorldPoint(FxMath.V3(screen.x, screen.y, 0f));
        Vector3 c = Owner.transform.position;
        float ox = w.x - c.x, oy = w.y - c.y;
        float raw = FxMath.Atan2(oy, ox) * FxMath.Rad2Deg;
        if (!Dragging)
        {
            Begin(raw, now);
            _ox = ox;
            _oy = oy;
            _scale = cam.orthographicSize / 3f;
            return;
        }
        if (Push(ox, oy, now)) return;
        // 角度を前の値から連続にする (-180/180 を跨いでも続ける)
        float d = raw - Wrap(Angle);
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        Step(Angle + d, now);
    }

    // 指/マウスの今の位置。down = 押している・began = このフレームで押した
    private static bool Pointer(out Vector2 screen, out bool down, out bool began)
    {
        screen = default;
        down = began = false;
        if (!Touch)
        {
            down = Input.GetMouseButton(0);
            began = Input.GetMouseButtonDown(0);
            if (!down && !Dragging) return false;
            Vector3 m = Input.mousePosition;
            screen = FxMath.V2(m.x, m.y);
            if (began && !Dragging && TouchBlocked != null && TouchBlocked(screen)) return false;
            return true;
        }
        int n = Input.touchCount;
        for (int i = 0; i < n; i++)
        {
            var t = Input.GetTouch(i);
            if (_finger >= 0 ? t.fingerId != _finger : t.phase != TouchPhase.Began || t.position.x < Screen.width * 0.5f) continue;
            if (_finger < 0 && TouchBlocked != null && TouchBlocked(t.position)) continue;
            screen = t.position;
            began = t.phase == TouchPhase.Began;
            down = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
            if (began) _finger = t.fingerId;
            return true;
        }
        return false;
    }

    // 押し始めがボタン・パネルの上なら振らない
    private static bool OverUi() =>
        PassiveButtonManager.InstanceExists && PassiveButtonManager.Instance.currentOver;

    // 外へ押し出す動き (突き)。突いたら true (なぞりはそこで終わる)。
    // 押し出しの速さが接線の速さより十分大きい間・体に近すぎる間は、角度の一振りを始めさせない
    private static bool Push(float ox, float oy, float now)
    {
        float dt = now - _prevT;
        if (dt <= 1e-4f) return false;
        float r0 = FxMath.Sqrt(_ox * _ox + _oy * _oy), r1 = FxMath.Sqrt(ox * ox + oy * oy);
        float vt = r0 > 1e-3f ? FxMath.Abs(_ox * oy - _oy * ox) / (r0 * dt) : 0f;
        _vr = _vr * 0.35f + (r1 - r0) / dt * 0.65f;
        float start = ThrustSpeed * _scale;
        _noStart = r1 < NearRadius * _scale || _vr > start * 0.5f && _vr > vt * 1.5f;
        float px = _ox, py = _oy;
        _ox = ox;
        _oy = oy;
        if (InStroke || NoThrust) { _thOn = false; return false; }
        if (!_thOn)
        {
            if (_vr < start) return false;
            _thOn = true;
            _thX0 = px;
            _thY0 = py;
            _thSlowT = -1f;
        }
        float dx = ox - _thX0, dy = oy - _thY0, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d >= ThrustDist * _scale && r1 > 1e-3f && (dx * ox + dy * oy) / r1 >= ThrustAlign * d)
        {
            Fire(FxMath.Atan2(dy, dx) * FxMath.Rad2Deg);
            return true;
        }
        if (_vr >= start * 0.4f) _thSlowT = -1f;
        else if (_thSlowT < 0f) _thSlowT = now;
        else if (now - _thSlowT >= EndHold) _thOn = false;
        return false;
    }

    // 突いた: なぞりはここで終わる (押したままでも、次は押し直してから)
    private static void Fire(float angle)
    {
        End(false);
        Thrusts++;
        Last = $"thrust a={Wrap(angle):0}";
        try { OnThrust?.Invoke(Wrap(angle)); }
        catch (Exception e) { Plugin.Logger.LogError($"swipe thrust: {e}"); }
    }

    private static void Begin(float angle, float now)
    {
        Dragging = true;
        DragId++;
        Angle = angle;
        PressedAt = now;
        _prevT = now;
        InStroke = false;
        Speed = 0f;
        _vr = 0f;
        _thOn = _noStart = false;
    }

    private static void Step(float angle, float now)
    {
        float dt = now - _prevT;
        if (dt <= 1e-4f) { Angle = angle; return; }
        float w = (angle - Angle) / dt;
        // 1 フレームのぶれで始まらないよう少しならす
        Speed = Speed * 0.35f + w * 0.65f;
        float prev = Angle;
        Angle = angle;
        _prevT = now;
        float abs = FxMath.Abs(Speed);
        int sign = Speed >= 0f ? 1 : -1;
        if (!InStroke)
        {
            if (abs < StartSpeed || _noStart) return;
            InStroke = true;
            _strokeSign = sign;
            _strokeStartA = prev;
            _strokeStartT = now - dt;
            _slowT = -1f;
            StrokeStarted();
            return;
        }
        float sweep = Angle - _strokeStartA;
        bool reversed = sign != _strokeSign && abs >= StartSpeed;
        if (abs < EndSpeed) { if (_slowT < 0f) _slowT = now; }
        else _slowT = -1f;
        if (reversed || _slowT >= 0f && now - _slowT >= EndHold || FxMath.Abs(sweep) >= MaxSweep || now - _strokeStartT >= MaxSeconds)
        {
            Finish(now);
            // 切り返しは次の一振りの始まり
            if (reversed)
            {
                InStroke = true;
                _strokeSign = sign;
                _strokeStartA = prev;
                _strokeStartT = now - dt;
                _slowT = -1f;
                StrokeStarted();
            }
        }
    }

    private static void StrokeStarted()
    {
        try { OnStrokeStart?.Invoke(); }
        catch (Exception e) { Plugin.Logger.LogError($"swipe stroke start: {e}"); }
    }

    private static void Finish(float now)
    {
        if (!InStroke) return;
        InStroke = false;
        float sweep = Angle - _strokeStartA;
        float secs = now - _strokeStartT;
        if (FxMath.Abs(sweep) < MinSweep) return;
        if (sweep > MaxSweep) sweep = MaxSweep;
        else if (sweep < -MaxSweep) sweep = -MaxSweep;
        Strokes++;
        Last = $"a0={Wrap(_strokeStartA):0} sweep={sweep:0} s={secs:0.00}";
        try { OnStroke?.Invoke(Wrap(_strokeStartA), sweep, secs); }
        catch (Exception e) { Plugin.Logger.LogError($"swipe stroke: {e}"); }
    }

    private static void End(bool emit)
    {
        if (emit) Finish(Time.time);
        Dragging = InStroke = false;
        Speed = 0f;
        _finger = -1;
    }

    internal static float Wrap(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        else if (a <= -180f) a += 360f;
        return a;
    }

    internal static string Describe() =>
        $"armed={Armed} drag={Dragging} stroke={InStroke} angle={Wrap(Angle):0} speed={Speed:0} strokes={Strokes} thrusts={Thrusts} last[{Last}] moveBlocked={MoveBlock.Hits}";
}

// 構えている間は PC のマウスで歩く操作を止める (キーで歩くのは残す)
internal static class MoveBlock
{
    internal static int Hits;
    private static bool _logged;

    internal static bool Blocks()
    {
        if (!SwipeInput.Armed || OperatingSystem.IsAndroid()) return false;
        Hits++;
        if (!_logged) { _logged = true; Plugin.Logger.LogInfo("swipe: mouse movement blocked while armed"); }
        return true;
    }
}

[HarmonyPatch(typeof(KeyboardJoystick), "UpdateTouchMovement")]
internal static class SwipeKeyboardMouseMovePatch
{
    public static bool Prefix(KeyboardJoystick __instance) => __instance is null || !MoveBlock.Blocks();
}

[HarmonyPatch(typeof(ScreenJoystick), "FixedUpdate")]
internal static class SwipeScreenJoystickPatch
{
    public static bool Prefix(ScreenJoystick __instance)
    {
        if (__instance is null) return false;
        if (!MoveBlock.Blocks()) return true;
        __instance.DeltaL = Vector2.zero;
        return false;
    }
}
