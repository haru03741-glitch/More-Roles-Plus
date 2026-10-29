using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 武器をなぞりで振る入力 (自分の端末だけ)。PC = 左ボタンを押したままマウスを動かす・スマホ = 画面の右半分をなぞる。
// 武器の向き = 体の中心から指/カーソルへの角度。角度が速く回った区間を一振り (stroke) として切り出し、OnStroke で渡す。
// 構えている間 (Armed) は PC のマウスで歩く操作を止める (歩くのはキーだけ)。
// 毎フレームの問い合わせは、構えている間だけ。なぞっている間だけ画面の座標をワールドへ写す (1 フレーム 1 回)
internal static class SwipeInput
{
    // 一振りと数える角速度 (度/秒)。始め・終わり・最小の掃引角・1 振りの最長
    internal const float StartSpeed = 320f, EndSpeed = 140f, MinSweep = 35f, MaxSweep = 270f, MaxSeconds = 0.6f;
    private const float EndHold = 0.05f; // 遅くなってから終わりと決めるまで

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
    // 一振りが始まった (速さが閾値を越えた所)。押してから始まるまでの溜めは Time.time - PressedAt
    internal static event Action OnStrokeStart;
    internal static float PressedAt { get; private set; }
    // スマホ: 押し始めがここで true を返す所 (役職のボタン) なら、なぞりにしない
    internal static Func<Vector2, bool> TouchBlocked;

    private static int _finger = -1;
    private static float _prevT, _strokeStartA, _strokeStartT, _slowT;
    private static int _strokeSign;
    // 確かめ用の作り物のなぞり (bridge): a0 → a1 を ms で
    private static float _synA0, _synA1, _synStart = -1f, _synDur, _synHold;
    internal static int Strokes;
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
        float raw = FxMath.Atan2(w.y - c.y, w.x - c.x) * FxMath.Rad2Deg;
        if (!Dragging) { Begin(raw, now); return; }
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

    private static void Begin(float angle, float now)
    {
        Dragging = true;
        Angle = angle;
        PressedAt = now;
        _prevT = now;
        InStroke = false;
        Speed = 0f;
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
            if (abs < StartSpeed) return;
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
        $"armed={Armed} drag={Dragging} stroke={InStroke} angle={Wrap(Angle):0} speed={Speed:0} strokes={Strokes} last[{Last}] moveBlocked={MoveBlock.Hits}";
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
