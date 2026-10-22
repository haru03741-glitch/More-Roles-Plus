using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 時が戻った時に全員の画面に出す演出 (誰が戻したかは出さない)。画面に貼り付けて 1.5 秒:
// 紺の幕 → 画面の真ん中に金の細い線の大きな時計盤 (暗い下地 → にじみ → 盤 → 針) の針が反時計回りに速く回る →
// ビデオの巻き戻しのような横の走査帯が上へ流れ、金と水色の細い光の筋が下から上へ逆さに降る → 上に ◀◀ が点滅。
// 頭は水色の短い閃光。全画面の発動の演出を見た本人は真っ白の画面から始まって引いていく (閃光を二重にしない)。
// 絵は最初の 1 回だけ焼き、部品は船ごとに 1 組作って使い回す。出ていない時は何もしない
internal static class RewindWorldFx
{
    private const float DefaultSeconds = 1.5f;
    private const int Bands = 7, NoiseBands = 2, Rain = 48;
    private const float ClockFrac = 0.7f;   // 時計盤の直径 (画面の高さに対して)
    private const float MinuteSpeed = -900f, HourSpeed = -75f, FaceSpeed = -20f; // 度/秒 (負 = 反時計回り)

    private static readonly Color Navy = FxMath.Rgba(0.047f, 0.07f, 0.21f, 1f);
    private static readonly Color Gold = FxMath.Rgba(0.94f, 0.75f, 0.38f, 1f);
    private static readonly Color HandCol = FxMath.Rgba(1f, 0.93f, 0.72f, 1f);
    private static readonly Color Aqua = FxMath.Rgba(0.55f, 0.95f, 1f, 1f);
    private static readonly Color White = FxMath.Rgba(1f, 1f, 1f, 1f);

    private static Sprite _solid, _face, _halo, _disc, _hand, _streak, _noise, _ff;
    private static GameObject _root;
    private static Transform _clockTf, _hourTf, _minTf, _ffTf;
    private static SpriteRenderer _tint, _flash, _discSr, _haloSr, _faceSr, _hourSr, _minSr, _ffSr;
    private static readonly Transform[] BandTf = new Transform[Bands], RainTf = new Transform[Rain];
    private static readonly SpriteRenderer[] BandSr = new SpriteRenderer[Bands], RainSr = new SpriteRenderer[Rain];
    private static readonly float[] BandY = new float[Bands], BandSpeed = new float[Bands], BandH = new float[Bands], BandA = new float[Bands];
    private static readonly float[] RainX = new float[Rain], RainDelay = new float[Rain], RainSpeed = new float[Rain], RainLen = new float[Rain];
    private static int _shipGen = -1, _frame;
    private static bool _on, _white;
    private static float _t, _w, _h, _jitter, _dur = DefaultSeconds;

    internal static bool Playing => _on;
    // HUD の中の奥行き。本編のボタンと文字 (負の z) より奥に置き、ボタンを隠さない。閃光だけは暗転の板と同じ一番手前
    internal static float RootZ = 10f;

    private static long _whiteUntil;

    // 全画面の発動の演出が真っ白で終わる時に、時戻しを頼む直前に呼ぶ。次に戻った時の演出を真っ白から始める
    // (頼みが断られて戻らなかった時のために 3 秒で忘れる)
    internal static void StartFromWhiteNext() => _whiteUntil = Environment.TickCount64 + 3000;

    // 盤などの絵を先に焼く (512px の盤は焼くのに時間がかかるので戻った瞬間に止まらないように)
    internal static void Prewarm()
    {
        try { Bake(); }
        catch (Exception e) { Plugin.Logger.LogError($"[RewindWorldFx] prewarm: {e}"); }
    }

    // white = 全画面の発動の演出の直後 (真っ白から引く)・seconds = 演出の長さ (巻き戻しを見せる長さに合わせる)
    internal static void Play(bool white, float seconds = DefaultSeconds)
    {
        _dur = FxMath.Clamp(seconds, 0.8f, 4f);
        if (Environment.TickCount64 < _whiteUntil) white = true;
        _whiteUntil = 0;
        try { PlayCore(white); }
        catch (Exception e) { Plugin.Logger.LogError($"[RewindWorldFx] play: {e}"); Stop(); }
    }

    private static void PlayCore(bool white)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.UICamera || MeetingHud.Instance) return;
        if (!Ensure(hud)) return;
        var cam = hud.UICamera;
        _h = cam.orthographicSize * 2f;
        _w = _h * cam.aspect;
        float fsZ = hud.FullScreen ? hud.FullScreen.transform.localPosition.z : -500f;
        _root.transform.localPosition = FxMath.V3(0f, 0f, RootZ);
        _flash.transform.localPosition = FxMath.V3(0f, 0f, fsZ + 1f - RootZ);
        _tint.transform.localScale = FxMath.V3(_w + 1f, _h + 1f, 1f);
        _flash.transform.localScale = FxMath.V3(_w + 1f, _h + 1f, 1f);
        float d = _h * ClockFrac;
        _clockTf.localScale = FxMath.V3(d, d, 1f);
        _ffTf.localPosition = FxMath.V3(0f, _h * 0.5f - 0.75f, -0.07f);
        for (int i = 0; i < Bands; i++)
        {
            BandY[i] = FxMath.Range(-_h * 0.5f, _h * 0.5f);
            BandSpeed[i] = FxMath.Range(6f, 14f);
            bool thick = i < NoiseBands;
            BandH[i] = thick ? FxMath.Range(0.35f, 0.6f) : FxMath.Range(0.03f, 0.22f);
            BandA[i] = thick ? FxMath.Range(0.25f, 0.4f) : FxMath.Range(0.12f, 0.4f);
            BandTf[i].localScale = FxMath.V3((_w + 1f) * (thick ? 1f / 8f : 1f), BandH[i], 1f);
        }
        for (int i = 0; i < Rain; i++)
        {
            RainX[i] = FxMath.Range(-_w * 0.5f, _w * 0.5f);
            RainDelay[i] = 0.05f + (_dur - 0.5f) * i / Rain + FxMath.Range(0f, 0.05f);
            RainSpeed[i] = FxMath.Range(8f, 16f);
            RainLen[i] = FxMath.Range(0.4f, 1.2f);
            RainTf[i].localScale = FxMath.V3(FxMath.Range(0.5f, 0.9f), RainLen[i], 1f);
            var c = (i & 1) == 0 ? Gold : Aqua;
            c.a = 0f;
            RainSr[i].color = c;
        }
        _white = white;
        _t = 0f;
        _jitter = 0f;
        _on = true;
        _root.SetActive(true);
        Apply();
        BreakNoise.PlayFlat("noise_rewind_world", 0.9f);
    }

    internal static void Tick()
    {
        if (!_on) return;
        try
        {
            if (!_root || MeetingHud.Instance) { Stop(); return; }
            _t += Time.deltaTime;
            if (_t >= _dur) { Stop(); return; }
            Apply();
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RewindWorldFx] tick: {e}"); Stop(); }
    }

    private static void Stop()
    {
        _on = false;
        if (_root) _root.SetActive(false);
    }

    // 立ち上がり a 秒・保ち・終わりの b 秒で消える 0..1
    private static float Env(float t, float a, float hold, float b)
    {
        if (t < a) { float u = t / a; return u * (2f - u); }
        if (t < hold) return 1f;
        return FxMath.Clamp01(1f - (t - hold) / b);
    }

    private static void SetA(SpriteRenderer sr, Color c, float a)
    {
        c.a = a;
        sr.color = c;
    }

    private static void Apply()
    {
        float t = _t;
        float env = Env(t, 0.12f, _dur - 0.65f, 0.65f);
        SetA(_tint, Navy, 0.32f * env);
        if (_white)
        {
            float u = FxMath.Clamp01(t / 0.4f);
            SetA(_flash, White, (1f - u) * (1f - u));
        }
        else SetA(_flash, Aqua, 0.45f * FxMath.Exp(-t / 0.12f));

        float ce = Env(t, 0.15f, _dur - 0.5f, 0.45f);
        float grow = 0.96f + 0.04f * FxMath.Clamp01(t / 0.3f);
        float d = _h * ClockFrac * grow;
        _clockTf.localScale = FxMath.V3(d, d, 1f);
        SetA(_discSr, Navy, 0.45f * ce);
        SetA(_haloSr, Gold, 0.35f * ce);
        SetA(_faceSr, Gold, 0.6f * ce);
        SetA(_hourSr, HandCol, 0.85f * ce);
        SetA(_minSr, HandCol, 0.9f * ce);
        _faceSr.transform.localRotation = FxMath.RotZ(FaceSpeed * t);
        _hourTf.localRotation = FxMath.RotZ(40f + HourSpeed * t);
        _minTf.localRotation = FxMath.RotZ(MinuteSpeed * t);

        // 走査帯: 上へ流れて下から戻る。3 フレームに 1 回だけ横に揺らす
        if (++_frame % 3 == 0) _jitter = FxMath.Range(-0.06f, 0.06f);
        float span = _h + 1f;
        for (int i = 0; i < Bands; i++)
        {
            float y = FxMath.Repeat(BandY[i] + BandSpeed[i] * t + span * 0.5f, span) - span * 0.5f;
            BandTf[i].localPosition = FxMath.V3(i % 2 == 0 ? _jitter : -_jitter, y, -0.05f);
            SetA(BandSr[i], i < NoiseBands ? Aqua : White, BandA[i] * env);
        }

        // 逆さの雨: 下の端から順に昇って上へ抜ける
        for (int i = 0; i < Rain; i++)
        {
            float lt = t - RainDelay[i];
            float y = -_h * 0.5f - RainLen[i] * 0.5f + lt * RainSpeed[i];
            bool live = lt >= 0f && y - RainLen[i] * 0.5f < _h * 0.5f;
            var c = RainSr[i].color;
            float a = live ? env : 0f;
            if (c.a == a && !live) continue;
            RainTf[i].localPosition = FxMath.V3(RainX[i], y, -0.06f);
            c.a = a;
            RainSr[i].color = c;
        }

        // ◀◀: 0.25 秒点いて 0.15 秒消える
        bool blink = t > 0.05f && t < _dur - 0.2f && FxMath.Repeat(t - 0.05f, 0.4f) < 0.25f;
        SetA(_ffSr, White, blink ? 0.85f * env : 0f);
    }

    // ── 部品 ───────────────────────────────────────────────────────────

    private static bool Ensure(HudManager hud)
    {
        if (_root && _shipGen == GameClock.ShipGen) return true;
        _shipGen = GameClock.ShipGen;
        Bake();
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpRewindWorld") { layer = layer };
        _root.transform.SetParent(hud.transform, false);
        GameClock.Ship.Bind(_root);
        _tint = Part(_root.transform, "Tint", _solid, 0f, layer);
        var clock = new GameObject("Clock") { layer = layer };
        _clockTf = clock.transform;
        _clockTf.SetParent(_root.transform, false);
        _discSr = Part(_clockTf, "Dark", _disc, -0.01f, layer);
        _discSr.transform.localScale = FxMath.V3(1.25f, 1.25f, 1f);
        _haloSr = Part(_clockTf, "Halo", _halo, -0.02f, layer);
        _haloSr.transform.localScale = FxMath.V3(1.08f, 1.08f, 1f);
        _faceSr = Part(_clockTf, "Face", _face, -0.03f, layer);
        _hourSr = Part(_clockTf, "Hour", _hand, -0.04f, layer);
        _hourTf = _hourSr.transform;
        _hourTf.localScale = FxMath.V3(1.3f, 0.62f, 1f);
        _minSr = Part(_clockTf, "Minute", _hand, -0.045f, layer);
        _minTf = _minSr.transform;
        for (int i = 0; i < Bands; i++)
        {
            BandSr[i] = Part(_root.transform, "Band", i < NoiseBands ? _noise : _solid, -0.05f, layer);
            BandTf[i] = BandSr[i].transform;
        }
        for (int i = 0; i < Rain; i++)
        {
            RainSr[i] = Part(_root.transform, "Rain", _streak, -0.06f, layer);
            RainTf[i] = RainSr[i].transform;
        }
        _ffSr = Part(_root.transform, "Rewind", _ff, -0.07f, layer);
        _ffTf = _ffSr.transform;
        _ffTf.localScale = FxMath.V3(0.55f, 0.55f, 1f);
        _flash = Part(_root.transform, "Flash", _solid, -0.08f, layer);
        _root.SetActive(false);
        return true;
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

    private static void Bake()
    {
        if (_solid) return;
        _solid = BakeRect(8, 8, (_, _) => 1f, "MrpRewindSolid");
        _face = BakeRect(512, 512, Face, "MrpRewindFace");
        _halo = BakeRect(256, 256, Halo, "MrpRewindHalo");
        _disc = BakeRect(128, 128, (x, y) => { float r = FxMath.Sqrt(x * x + y * y) * 2f; return r >= 1f ? 0f : FxMath.Pow(1f - r, 1.2f); }, "MrpRewindDisc");
        _hand = BakeRect(256, 256, Hand, "MrpRewindHand");
        _streak = BakeRect(32, 256, Streak, "MrpRewindStreak");
        var rnd = new System.Random(1023);
        var cols = new float[256];
        for (int i = 0; i < cols.Length; i++) cols[i] = (float)rnd.NextDouble();
        _noise = BakeRect(256, 32, (x, y) =>
        {
            int c = (int)((x + 0.5f) * 255f);
            float v = (cols[c] + cols[(c + 1) & 255] + cols[(c + 255) & 255]) / 3f;
            return (0.25f + 0.75f * v * v) * FxMath.Exp(-y * y / (2f * 0.22f * 0.22f));
        }, "MrpRewindNoise");
        _ff = BakeRect(128, 128, FastBack, "MrpRewindFf");
    }

    // 1 本の線 (中心からの距離 d・半幅 w・縁のにじみ 1.5px 相当)
    private static float Line(float d, float w, float soft) => FxMath.Clamp01(1f - (d - w) / soft);

    // 時計盤 (1 単位・中心からの位置 -0.5〜0.5): 縁の二重の輪と歯・60 の目盛 (12 は長く太い)・時の菱形・内側の輪・放射の細い筋・軸
    private static float Face(float x, float y)
    {
        const float px = 1f / 512f;
        float r = FxMath.Sqrt(x * x + y * y);
        float ang = FxMath.Atan2(y, x);
        float a = 0f;
        a = FxMath.Max(a, Line(FxMath.Abs(r - 0.47f), 0.004f, 1.5f * px));
        a = FxMath.Max(a, Line(FxMath.Abs(r - 0.452f), 0.0015f, 1.5f * px) * 0.8f);
        // 縁の外の歯 48 枚
        if (r > 0.474f && r < 0.492f)
        {
            float k = ang / (MathF.PI * 2f) * 48f;
            float f = k - FxMath.Floor(k);
            if (f > 0.25f && f < 0.75f) a = FxMath.Max(a, 0.85f * Line(FxMath.Abs(r - 0.483f), 0.008f, 1.5f * px));
        }
        // 目盛: 一番近い目盛の線までの垂直の距離
        float m = ang / (MathF.PI * 2f) * 60f;
        float mi = MathF.Round(m);
        float perp = FxMath.Abs(m - mi) * (MathF.PI * 2f / 60f) * r;
        bool hour = ((int)mi % 5 + 5) % 5 == 0;
        if (hour ? r > 0.385f && r < 0.445f : r > 0.42f && r < 0.445f)
            a = FxMath.Max(a, Line(perp, hour ? 0.004f : 0.0016f, 1.5f * px));
        // 時の菱形
        if (hour && r > 0.33f && r < 0.37f)
        {
            float dr = FxMath.Abs(r - 0.35f) / 0.02f, dp = perp / 0.008f;
            if (dr + dp < 1f) a = FxMath.Max(a, 0.9f);
        }
        a = FxMath.Max(a, Line(FxMath.Abs(r - 0.3f), 0.0012f, 1.5f * px) * 0.8f);
        // 放射の細い筋 (180 本・淡く)
        if (r > 0.06f && r < 0.29f)
        {
            float s = ang / (MathF.PI * 2f) * 180f;
            float sp = FxMath.Abs(s - MathF.Round(s)) * (MathF.PI * 2f / 180f) * r;
            a = FxMath.Max(a, 0.22f * Line(sp, 0.0008f, 1.2f * px));
        }
        a = FxMath.Max(a, Line(FxMath.Abs(r - 0.045f), 0.003f, 1.5f * px));
        if (r < 0.018f) a = 1f;
        return a;
    }

    // にじみ: 縁と内側の輪のぼかし + 真ん中の淡い光
    private static float Halo(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y);
        float a = 0.8f * FxMath.Exp(-(r - 0.44f) * (r - 0.44f) / (2f * 0.03f * 0.03f));
        a += 0.35f * FxMath.Exp(-(r - 0.28f) * (r - 0.28f) / (2f * 0.02f * 0.02f));
        a += 0.25f * FxMath.Exp(-r * r / (2f * 0.08f * 0.08f));
        return a;
    }

    // 針 (中心が回る軸・上へ伸びる): 先へ細くなる芯 + にじみ + 短い尾
    private static float Hand(float x, float y)
    {
        if (y < -0.08f || y > 0.42f) return 0f;
        float along = FxMath.Clamp01(y / 0.42f);
        float w = y < 0f ? 0.008f : 0.012f * (1f - 0.65f * along);
        float d = FxMath.Abs(x);
        float core = FxMath.Clamp01(1f - (d - w) / (1.5f / 256f));
        float glow = 0.35f * FxMath.Exp(-d * d / (2f * 0.025f * 0.025f));
        float tip = y > 0.4f ? FxMath.Clamp01((0.42f - y) / 0.02f) : 1f;
        return FxMath.Max(core, glow) * tip;
    }

    // 光の筋 (縦長): 頭 (上) が明るく尾 (下) へ消える
    private static float Streak(float x, float y)
    {
        float across = FxMath.Exp(-x * x / (2f * 0.16f * 0.16f));
        float head = FxMath.Pow(FxMath.Clamp01(y + 0.5f), 1.6f);
        return across * head;
    }

    // ◀◀ (巻き戻し): 左向きの三角 2 つ
    private static float FastBack(float x, float y)
    {
        float a = 0f;
        for (int k = 0; k < 2; k++)
        {
            float tipX = -0.36f + k * 0.34f; // 三角の左の先
            float u = (x - tipX) / 0.34f;     // 0 = 先・1 = 底
            if (u < 0f || u > 1f) continue;
            float half = 0.3f * u;
            float edge = FxMath.Clamp01((half - FxMath.Abs(y)) * 128f);
            a = FxMath.Max(a, edge);
        }
        return a;
    }

    // 白い絵 (w×h・高さ 1 単位)。f(x, y) は中心からの位置 (x は -0.5〜0.5 を幅で・y は -0.5〜0.5) → 不透明度
    private static unsafe Sprite BakeRect(int w, int h, Func<float, float, float> f, string name)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float a = FxMath.Clamp01(f((x + 0.5f) / w - 0.5f, (y + 0.5f) / h - 0.5f));
            int i = (y * w + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), h);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }
}
