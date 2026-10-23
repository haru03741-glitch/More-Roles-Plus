using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 時戻しのボタンを長押しして溜めている間、自分の体に時の力が集まる演出 (押している本人の画面だけ。
// 他の人に見せると誰が時戻しか分かってしまう)。溜まるほど強くなる:
// 足元に金の時計盤が広がって反時計回りに回る → 金と水色の光の筋が渦を巻いて体へ吸い込まれる →
// 体を水色のにじみが包み、胸に白い核が灯る → 1 秒増えるごとに金の輪が 1 つ広がる → 上限まで溜まると全体が脈打つ。
// 溜めているかはボタン (ModButton.Holding) を毎フレーム見て決めるので、離した・会議・死亡で自然に止まる。
// 出ていない時は Unity に触らない
internal static class RewindChargeFx
{
    private const int Motes = 64, Rings = 3;
    private const float FeetY = -0.36f, ChestY = 0.02f;
    private const float DialMin = 0.95f, DialMax = 1.6f, DialSquash = 0.42f;
    private const float DialSpin = 25f, DialSpinMax = 110f; // 度/秒 (正 = 反時計回り)
    private const float MoteR0 = 1.15f, MoteR1 = 1.75f, MoteEnd = 0.1f;
    private const float RateMin = 22f, RateMax = 75f; // 1 秒に生まれる筋の数
    private const float RingTime = 0.45f;

    private static readonly Color Gold = FxMath.Rgba(0.94f, 0.75f, 0.38f, 1f);
    private static readonly Color Aqua = FxMath.Rgba(0.55f, 0.95f, 1f, 1f);
    private static readonly Color Deep = FxMath.Rgba(0.1f, 0.2f, 0.5f, 1f);
    private static readonly Color White = FxMath.Rgba(1f, 1f, 1f, 1f);

    private static Sprite _glow, _ring;
    private static GameObject _root;
    private static Transform _dialTf, _faceTf, _backTf, _auraTf, _coreTf;
    private static SpriteRenderer _shadeSr, _dialHaloSr, _faceSr, _backSr, _auraSr, _coreSr;
    private static readonly Transform[] MoteTf = new Transform[Motes], RingTf = new Transform[Rings];
    private static readonly SpriteRenderer[] MoteSr = new SpriteRenderer[Motes], RingSr = new SpriteRenderer[Rings];
    private static readonly float[] MoteAge = new float[Motes], MoteLife = new float[Motes], MoteR = new float[Motes],
        MoteAng = new float[Motes], MoteW = new float[Motes], MoteSpin = new float[Motes], MoteX = new float[Motes], MoteY = new float[Motes];
    private static readonly float[] RingAge = new float[Rings];
    private static bool _on;
    private static float _t, _spawn, _faceRot;
    private static int _sec, _nextRing;

    // テスト用: 0〜1 で溜めを固定して出す (負 = 使わない)
    internal static float TestCharge = -1f;

    internal static bool Showing => _on;

    internal static void Tick()
    {
        float c;
        int sec;
        if (TestCharge >= 0f)
        {
            c = FxMath.Clamp01(TestCharge);
            sec = (int)(c * 10f);
        }
        else
        {
            var b = RoleState.Local is Crew.Rewinder r ? r.ChargeButton : null;
            if (b == null || !b.Holding)
            {
                if (_on) Stop();
                return;
            }
            float min = Crew.Rewinder.MinSeconds, max = Math.Max(min, (float)Crew.Rewinder.Seconds);
            c = max > min ? FxMath.Clamp01((b.HoldValue - min) / (max - min)) : 1f;
            sec = (int)b.HoldValue;
        }
        try
        {
            if (!_on && !Start(sec)) return;
            if (!_root) { _on = false; return; }
            Apply(c, sec, Time.deltaTime);
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RewindChargeFx] {e}"); Stop(); }
    }

    private static bool Start(int sec)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return false;
        Ensure(lp);
        _root.transform.SetParent(lp.transform, false);
        _root.transform.localPosition = FxMath.V3(0f, 0f, 0f);
        for (int i = 0; i < Motes; i++) { MoteAge[i] = MoteLife[i] = 0f; SetA(MoteSr[i], White, 0f); }
        for (int i = 0; i < Rings; i++) { RingAge[i] = RingTime; SetA(RingSr[i], Gold, 0f); }
        _t = _spawn = 0f;
        _sec = sec;
        _on = true;
        _root.SetActive(true);
        return true;
    }

    private static void Stop()
    {
        _on = false;
        if (_root) _root.SetActive(false);
    }

    private static void SetA(SpriteRenderer sr, Color c, float a)
    {
        c.a = a;
        sr.color = c;
    }

    private static void Apply(float c, int sec, float dt)
    {
        _t += dt;
        // 立ち上がり 0.25 秒・上限では 2 回/秒の脈
        float rise = FxMath.Clamp01(_t / 0.25f);
        float pulse = c >= 1f ? 0.5f + 0.5f * FxMath.Cos(_t * 12.6f) : 0f;
        float k = rise * (0.45f + 0.55f * c);

        // 足元の時計盤: 外の暗い下地 → にじみ → 盤。溜まるほど広く速く回る
        float d = FxMath.Lerp(DialMin, DialMax, c) * (1f + 0.04f * pulse);
        _dialTf.localScale = FxMath.V3(d, d * DialSquash, 1f);
        _faceRot += (DialSpin + (DialSpinMax - DialSpin) * c) * dt;
        _faceTf.localRotation = FxMath.RotZ(_faceRot);
        SetA(_shadeSr, Deep, 0.28f * k);
        SetA(_dialHaloSr, Gold, (0.18f + 0.22f * c + 0.1f * pulse) * rise);
        SetA(_faceSr, Gold, (0.35f + 0.35f * c + 0.15f * pulse) * rise);

        // 体を包むにじみと胸の核。明るい床でも浮くように体の奥に紺の下地を敷く
        float back = 2.1f + 0.8f * c + 0.1f * pulse;
        _backTf.localScale = FxMath.V3(back, back * 1.1f, 1f);
        SetA(_backSr, Deep, (0.4f + 0.25f * c) * rise);
        float aura = 1.05f + 0.45f * c + 0.08f * pulse;
        _auraTf.localScale = FxMath.V3(aura, aura * 1.15f, 1f);
        SetA(_auraSr, Aqua, (0.3f + 0.4f * c + 0.15f * pulse) * rise);
        float core = 0.22f + 0.22f * c + 0.06f * pulse;
        _coreTf.localScale = FxMath.V3(core, core, 1f);
        SetA(_coreSr, White, (0.15f + 0.6f * c * c + 0.2f * pulse) * rise);

        // 1 秒増えるごとに輪を 1 つ
        if (sec > _sec)
        {
            RingAge[_nextRing] = 0f;
            _nextRing = (_nextRing + 1) % Rings;
        }
        _sec = sec;
        for (int i = 0; i < Rings; i++)
        {
            if (RingAge[i] >= RingTime) continue;
            RingAge[i] += dt;
            float u = FxMath.Clamp01(RingAge[i] / RingTime);
            float s = 0.5f + 1.7f * (1f - (1f - u) * (1f - u));
            RingTf[i].localScale = FxMath.V3(s, s, 1f);
            SetA(RingSr[i], i % 2 == 0 ? Gold : Aqua, 0.75f * (1f - u));
        }

        // 渦を巻いて吸い込まれる筋
        _spawn += (RateMin + (RateMax - RateMin) * c) * rise * dt;
        for (int i = 0; i < Motes && _spawn >= 1f; i++)
        {
            if (MoteAge[i] < MoteLife[i]) continue;
            _spawn -= 1f;
            MoteAge[i] = 0f;
            MoteLife[i] = FxMath.Range(0.55f, 0.85f) * (1f - 0.25f * c);
            MoteR[i] = FxMath.Range(MoteR0, MoteR1);
            MoteAng[i] = FxMath.Range(0f, FxMath.PI * 2f);
            MoteSpin[i] = FxMath.Range(2.2f, 3.4f);
            MoteX[i] = MoteR[i] * FxMath.Cos(MoteAng[i]);
            MoteY[i] = ChestY + MoteR[i] * FxMath.Sin(MoteAng[i]) * 0.8f;
            MoteW[i] = FxMath.Range(0.55f, 0.85f);
        }
        if (_spawn > 1f) _spawn = 1f; // 空きが無い時は持ち越さない
        for (int i = 0; i < Motes; i++)
        {
            if (MoteAge[i] >= MoteLife[i]) continue;
            MoteAge[i] += dt;
            float u = MoteAge[i] / MoteLife[i];
            if (u >= 1f) { SetA(MoteSr[i], White, 0f); continue; }
            float ease = (1f - u) * (1f - u);
            float r = MoteEnd + (MoteR[i] - MoteEnd) * ease;
            float ang = MoteAng[i] + MoteSpin[i] * u * (1.5f - 0.5f * u);
            float x = r * FxMath.Cos(ang), y = ChestY + r * FxMath.Sin(ang) * 0.8f;
            float dx = x - MoteX[i], dy = y - MoteY[i];
            MoteX[i] = x;
            MoteY[i] = y;
            // 進む向きに筋を向ける (絵の頭は上)。速くなるほど長く
            float speed = FxMath.Sqrt(dx * dx + dy * dy) / (dt > 0f ? dt : 0.016f);
            MoteTf[i].localPosition = FxMath.V3(x, y, -0.003f);
            MoteTf[i].localRotation = FxMath.RotZ(FxMath.Atan2(dy, dx) * FxMath.Rad2Deg - 90f);
            float len = FxMath.Clamp(0.12f + speed * 0.06f, 0.12f, 0.45f);
            MoteTf[i].localScale = FxMath.V3(MoteW[i], len, 1f);
            float a = u < 0.2f ? u / 0.2f : u > 0.85f ? (1f - u) / 0.15f : 1f;
            SetA(MoteSr[i], (i & 1) == 0 ? Gold : Aqua, a * rise);
        }
    }

    // ── 部品 ───────────────────────────────────────────────────────────

    private static void Ensure(PlayerControl lp)
    {
        if (_root) return;
        _glow ??= FxSprites.Rect(64, 64, (x, y) => FxMath.Exp(-(x * x + y * y) / (2f * 0.16f * 0.16f)), "MrpChargeGlow");
        _ring ??= FxSprites.Rect(128, 128, (x, y) =>
        {
            float r = FxMath.Sqrt(x * x + y * y);
            float e = (r - 0.46f) / 0.018f;
            return FxMath.Exp(-e * e) + 0.35f * FxMath.Exp(-(r - 0.44f) * (r - 0.44f) / (2f * 0.04f * 0.04f));
        }, "MrpChargeRing");
        int layer = lp.gameObject.layer;
        _root = new GameObject("MrpRewindCharge") { layer = layer };
        // 時計盤は体のすぐ奥 (床より手前)・にじみと筋は体の手前
        var dial = new GameObject("Dial") { layer = layer };
        _dialTf = dial.transform;
        _dialTf.SetParent(_root.transform, false);
        _dialTf.localPosition = FxMath.V3(0f, FeetY, 0.0006f);
        _shadeSr = Part(_dialTf, "Shade", _glow, 0f, layer);
        _shadeSr.transform.localScale = FxMath.V3(1.5f, 1.5f, 1f);
        _dialHaloSr = Part(_dialTf, "Halo", RewindWorldFx.HaloSprite, -0.0001f, layer);
        _dialHaloSr.transform.localScale = FxMath.V3(1.08f, 1.08f, 1f);
        _faceSr = Part(_dialTf, "Face", RewindWorldFx.FaceSprite, -0.0002f, layer);
        _faceTf = _faceSr.transform;
        _backSr = Part(_root.transform, "Back", _glow, 0.0004f, layer);
        _backTf = _backSr.transform;
        _backTf.localPosition = FxMath.V3(0f, ChestY, 0.0004f);
        _auraSr = Part(_root.transform, "Aura", _glow, -0.002f, layer);
        _auraTf = _auraSr.transform;
        _auraTf.localPosition = FxMath.V3(0f, ChestY, -0.002f);
        _coreSr = Part(_root.transform, "Core", _glow, -0.0035f, layer);
        _coreTf = _coreSr.transform;
        _coreTf.localPosition = FxMath.V3(0f, ChestY, -0.0035f);
        for (int i = 0; i < Rings; i++)
        {
            RingSr[i] = Part(_root.transform, "Ring", _ring, -0.0025f, layer);
            RingTf[i] = RingSr[i].transform;
            RingTf[i].localPosition = FxMath.V3(0f, ChestY, -0.0025f);
        }
        var streak = RewindWorldFx.StreakSprite;
        for (int i = 0; i < Motes; i++)
        {
            MoteSr[i] = Part(_root.transform, "Mote", streak, -0.003f, layer);
            MoteTf[i] = MoteSr[i].transform;
        }
        _root.SetActive(false);
    }

    private static SpriteRenderer Part(Transform parent, string name, Sprite sp, float z, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        return sr;
    }
}
