using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// エクリプスの見た目: 印のビーコン (自分の画面だけ) と、隕石が落ちるまでの影・照準・落下・着弾。
// 段取り = 溜め (影が広がって範囲が暗くなる・照準が脈打つ) → 山場 (燃える岩が斜めに落ちて閃光) → 持続 (煙・火の粉) → 締め (影が引く)。
// 壁が壊れる・焦げる・爆発の音は、ホストが起こす地形の爆発が全員の画面で出す
internal static class EclipseFx
{
    // ---- 数値 ----
    // 絵は 3D の形から描いたコマ (tools/make-eclipse-3d.py)。1 単位あたりの画素は描いた時と同じにして大きさを合わせる
    private const float BeaconPpu = 220f;
    private const float BeaconFeet = (80f - 22f) / BeaconPpu; // コマの中心から足の接地点まで
    private const float RockPpu = 150f;              // 岩の直径 ≈ 1 単位
    private const float HeadStep = 0.16f;            // ビーコンの皿が 45° 回る間隔
    private const float RockTurns = 1.6f;            // 落ちる間の岩の転がり (回/秒)
    private const float FallTime = 0.6f;             // 空から着弾まで
    private const float FallFromX = 3f, FallFromY = 5.5f; // 落ち始めの位置 (着弾点から右上・画面の中から見える所)
    private const float ShadowMax = 0.62f;           // 影の濃さ (着弾の直前)
    private const float RimPulseHz = 5f;
    private const float FadeOut = 0.35f;             // 着弾の後に影と照準が引く時間
    private const float TrailEvery = 0.016f;
    private const float ShakeReach = 10f;
    private const int MaxPuffs = 160;

    private static Sprite[] _beacon, _rock;
    private static Sprite _reticle, _shadow, _flash;
    private static bool _searched;

    private static bool Art()
    {
        if (_rock != null && _reticle) return true;
        if (_searched) return false;
        _searched = true;
        _beacon = ButtonIcons.Frames("eclipse_beacon3d", 16, BeaconPpu, 2);
        _rock = ButtonIcons.Frames("eclipse_rock3d", 16, RockPpu, 2);
        _reticle = ButtonIcons.Load("eclipse_reticle", 512f, FxMath.V2(0.5f, 0.5f));
        _shadow = ButtonIcons.Load("eclipse_shadow", 256f, FxMath.V2(0.5f, 0.5f));
        _flash = ButtonIcons.Load("rl_flash", 200f, FxMath.V2(0.5f, 0.5f));
        return _rock != null && _reticle && _shadow;
    }

    private static SpriteRenderer Make(string name, Sprite sp, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // ================= 印 (自分の画面だけ) =================

    private static GameObject _mark;
    private static Transform _markTf, _markRetTf;
    private static SpriteRenderer _markBeacon, _markRet, _markGlow;
    private static float _markT0, _markR;
    private static int _markFrame = -1;

    internal static void ShowMark(Vector2 at, float r)
    {
        HideMark();
        if (!Art()) return;
        _mark = new GameObject("MrpEclipseMark");
        FxRoot.Attach(_mark);
        _markTf = _mark.transform;
        _markTf.position = FxMath.V3(at.x, at.y, 0f);
        _markRet = Make("Reticle", _reticle, _markTf);
        _markRetTf = _markRet.transform;
        _markRetTf.localPosition = FxMath.V3(0f, 0f, at.y / 1000f + 0.0009f); // 床のすぐ手前 (人の下)
        _markRetTf.localScale = FxMath.V3(r * 2f, r * 2f, 1f);
        _markRet.color = FxMath.Rgba(1f, 0.25f, 0.22f, 0.3f);
        _markGlow = Make("Glow", Terrain.DebrisArt.Puff, _markTf);
        _markGlow.transform.localPosition = FxMath.V3(0f, 0.05f, at.y / 1000f + 0.0005f);
        _markGlow.transform.localScale = FxMath.V3(0.7f, 0.45f, 1f);
        if (_beacon != null)
        {
            _markBeacon = Make("Beacon", _beacon[0], _markTf);
            _markBeacon.transform.localPosition = FxMath.V3(0f, BeaconFeet, (at.y + 0.3636f) / 1000f);
        }
        _markT0 = Time.time;
        _markR = r;
        _markFrame = -1;
    }

    internal static void HideMark()
    {
        if (_mark) Object.Destroy(_mark);
        _mark = null;
        _markBeacon = _markRet = _markGlow = null;
    }

    private static void TickMark(float now)
    {
        if (!_mark) { _mark = null; return; }
        float t = now - _markT0;
        // ランプは 1 秒に 1 回: 0.15 秒で点いて 0.5 秒かけて消える。皿は回りながら空を探す
        float ph = t - (int)t;
        float lamp = ph < 0.15f ? ph / 0.15f : FxMath.Clamp01(1f - (ph - 0.15f) / 0.5f);
        int f = (lamp > 0.5f ? 8 : 0) + (int)(t / HeadStep) % 8;
        if (_markBeacon && f != _markFrame) { _markFrame = f; _markBeacon.sprite = _beacon[f]; }
        if (_markGlow) _markGlow.color = FxMath.Rgba(1f, 0.2f, 0.18f, 0.08f + 0.32f * lamp);
        if (_markRetTf)
        {
            _markRetTf.localRotation = FxMath.RotZ(t * 20f);
            _markRet.color = FxMath.Rgba(1f, 0.25f, 0.22f, 0.22f + 0.12f * lamp);
        }
    }

    // ================= 隕石 (全員の画面) =================

    private sealed class Strike
    {
        public GameObject Root;
        public SpriteRenderer Shadow, Rim, Rock, RockGlow, Light;
        public Transform ShadowTf, RimTf, RockTf, RockGlowTf, LightTf;
        public float X, Y, R, T0, Impact, NextTrail;
        public int RockFrame = -1;
        public bool Hit;
    }

    private enum PuffKind : byte { Smoke, Core, Glow, Spark, Ember }

    private sealed class Puff
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public float Start, Life, Size0, Size1, X, Y, Vx, Vy, Z, Fall;
        public PuffKind Kind;
        public Sprite Art;
    }

    private static readonly List<Strike> Live = new();
    private static readonly List<Puff> Puffs = new();
    private static readonly Stack<Puff> Pool = new();

    // 警告を出してから着弾までの秒 (落ちる時間より短ければ落ちる時間)。倒す時刻もこれに合わせる
    internal static float ImpactDelay(float warn) => warn < FallTime ? FallTime : warn;

    internal static void Begin(Vector2 at, float r, float warn)
    {
        if (!Art()) return;
        var root = new GameObject("MrpEclipseStrike");
        FxRoot.Attach(root);
        float zFloor = at.y / 1000f;
        var s = new Strike { Root = root, X = at.x, Y = at.y, R = r, T0 = Time.time, Impact = ImpactDelay(warn) };
        s.Shadow = Make("Shadow", _shadow, root.transform);
        s.ShadowTf = s.Shadow.transform;
        s.ShadowTf.position = FxMath.V3(at.x, at.y, zFloor + 0.0010f);
        s.Shadow.color = FxMath.Rgba(0f, 0f, 0.02f, 0f);
        s.Rim = Make("Rim", _reticle, root.transform);
        s.RimTf = s.Rim.transform;
        s.RimTf.position = FxMath.V3(at.x, at.y, zFloor + 0.0008f);
        s.RimTf.localScale = FxMath.V3(r * 2f, r * 2f, 1f);
        s.Rim.color = FxMath.Rgba(1f, 0.2f, 0.18f, 0f);
        // 近づく火の玉に照らされる床の光だまり (影の上・人の下)
        s.Light = Make("Light", Terrain.DebrisArt.Puff, root.transform);
        s.LightTf = s.Light.transform;
        s.LightTf.position = FxMath.V3(at.x, at.y, zFloor + 0.0006f);
        s.Light.enabled = false;
        s.RockGlow = Make("RockGlow", Terrain.DebrisArt.Puff, root.transform);
        s.RockGlowTf = s.RockGlow.transform;
        s.RockGlow.enabled = false;
        s.Rock = Make("Rock", _rock[0], root.transform);
        s.RockTf = s.Rock.transform;
        s.Rock.enabled = false;
        Live.Add(s);
    }

    private static bool _rewound;
    private static int _tick;

    internal static void Tick()
    {
        if (_mark == null && Live.Count == 0 && Puffs.Count == 0) return;
        // 時戻しの再生中は止める。明けた時に途中の隕石と粒が一度に進まないよう、捨てる
        if (RewindPlayback.Active) { _rewound = true; return; }
        if (_rewound) { _rewound = false; DropStrikes(); }
        float now = Time.time;
        if (_mark != null && ++_tick % 3 == 0) TickMark(now); // 小さな印のランプと照準は 3 フレームに 1 回で足りる
        if (Live.Count > 0)
        {
            // 落ちる途中で会議が始まったら捨てる (会議の後に見た目だけ着弾しないように。倒すのと音は会議で止まる)
            if (MeetingHud.Instance) DropStrikes();
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var s = Live[i];
                if (!s.Root) { Live.RemoveAt(i); continue; }
                if (!TickStrike(s, now)) { Object.Destroy(s.Root); Live.RemoveAt(i); }
            }
        }
        if (Puffs.Count > 0) TickPuffs(now);
    }

    private static void DropStrikes()
    {
        foreach (var s in Live) if (s.Root) Object.Destroy(s.Root);
        Live.Clear();
        foreach (var p in Puffs) if (p.Go) { p.Go.SetActive(false); Pool.Push(p); }
        Puffs.Clear();
    }

    private static bool TickStrike(Strike s, float now)
    {
        float t = now - s.T0;
        if (t < s.Impact)
        {
            // 溜め: 影が広がって暗くなり、照準が速く脈打つ
            float u = t / s.Impact;
            float e = u * u;
            float d = s.R * 2f * (0.3f + 0.7f * e);
            s.ShadowTf.localScale = FxMath.V3(d, d, 1f);
            s.Shadow.color = FxMath.Rgba(0f, 0f, 0.02f, ShadowMax * (0.15f + 0.85f * e));
            float pulse = 0.5f + 0.5f * FxMath.Sin(t * RimPulseHz * 2f * System.MathF.PI);
            s.Rim.color = FxMath.Rgba(1f, 0.2f + 0.15f * pulse, 0.18f, FxMath.Clamp01(u * 4f) * (0.35f + 0.4f * pulse));
            s.RimTf.localRotation = FxMath.RotZ(-t * 40f);
            float fall0 = s.Impact - FallTime;
            if (t >= fall0) Fall(s, now, (t - fall0) / FallTime);
            return true;
        }
        if (!s.Hit)
        {
            s.Hit = true;
            s.Rock.enabled = s.RockGlow.enabled = s.Light.enabled = false;
            Impact(s.X, s.Y, s.R);
        }
        // 締め: 影と照準が引く
        float k = 1f - (t - s.Impact) / FadeOut;
        if (k <= 0f) return false;
        s.Shadow.color = FxMath.Rgba(0f, 0f, 0.02f, ShadowMax * k * k);
        s.Rim.color = FxMath.Rgba(1f, 0.35f, 0.2f, 0.5f * k);
        return true;
    }

    // 山場の手前: 燃える岩が右上から斜めに落ちてくる (落ちるほど速い)。後ろに火の粉と煙の尾
    private static void Fall(Strike s, float now, float u)
    {
        if (!s.Rock.enabled) { s.Rock.enabled = s.RockGlow.enabled = s.Light.enabled = true; s.NextTrail = now; }
        float e = u * u;
        float ld = s.R * (1.2f + 1.4f * e);
        s.LightTf.localScale = FxMath.V3(ld, ld * 0.75f, 1f);
        // 明るい床で茶色い濁りにならないよう、黄に寄せて薄く (AU に加算の合成が無い)
        s.Light.color = FxMath.Rgba(1f, 0.82f + 0.1f * e, 0.45f + 0.15f * e, 0.05f + 0.17f * e);
        float x = s.X + FallFromX * (1f - e), y = s.Y + FallFromY * (1f - e);
        const float zSky = -6f; // 空の物はどの人や壁よりも手前
        s.RockTf.position = FxMath.V3(x, y, zSky);
        int rf = (int)((now - s.T0) * RockTurns * 16f) & 15;
        if (rf != s.RockFrame) { s.RockFrame = rf; s.Rock.sprite = _rock[rf]; }
        float g = 1.5f + 0.6f * FxMath.Sin(now * 37f);
        s.RockGlowTf.position = FxMath.V3(x + 0.12f, y + 0.18f, zSky + 0.001f);
        s.RockGlowTf.localScale = FxMath.V3(g, g, 1f);
        s.RockGlow.color = FxMath.Rgba(1f, 0.55f, 0.18f, 0.55f);
        // 落ちる向き (右上から左下)
        const float len = 9.43f; // sqrt(5^2 + 8^2)
        float dx = -FallFromX / len, dy = -FallFromY / len;
        while (s.NextTrail <= now)
        {
            s.NextTrail += TrailEvery;
            float jx = FxMath.Range(-0.12f, 0.12f), jy = FxMath.Range(-0.12f, 0.12f);
            SpawnPuff(x + jx - dx * 0.35f, y + jy - dy * 0.35f, -dx * 0.6f, -dy * 0.6f, 0.35f, 1.0f, FxMath.Range(0.45f, 0.7f), zSky + 0.002f, PuffKind.Smoke);
            if (FxMath.Range(0f, 1f) < 0.5f)
                SpawnPuff(x + jx, y + jy, -dx * FxMath.Range(1f, 2.5f) + FxMath.Range(-0.8f, 0.8f), -dy * FxMath.Range(1f, 2.5f), 0.1f, 0.05f, FxMath.Range(0.25f, 0.45f), zSky - 0.001f, PuffKind.Ember);
        }
    }

    // 山場: 白い核 → 橙のにじみ → 煙の輪 → 火花と火の粉 → 画面の揺れ
    private static void Impact(float x, float y, float r)
    {
        float z = (y - 1.5f) / 1000f; // そばに立つ人より手前
        float rr = r < 0.6f ? 0.6f : r > 4f ? 4f : r;
        SpawnPuff(x, y, 0f, 0f, rr * 1.2f, rr * 2.8f, 0.4f, z + 0.0003f, PuffKind.Glow);
        if (_flash) SpawnPuff(x, y, 0f, 0f, rr * 1.0f, rr * 1.6f, 0.18f, z, PuffKind.Core);
        int ring = 16;
        for (int i = 0; i < ring; i++)
        {
            float a = (i + FxMath.Range(-0.3f, 0.3f)) * (2f * System.MathF.PI / ring);
            float v = rr * FxMath.Range(1.4f, 2.2f);
            SpawnPuff(x, y, FxMath.Cos(a) * v, FxMath.Sin(a) * v * 0.75f, 0.4f, rr * 0.6f + 0.35f, FxMath.Range(0.9f, 1.3f), z + 0.0005f, PuffKind.Smoke);
        }
        for (int i = 0; i < 14; i++)
        {
            float a = FxMath.Range(0f, 2f * System.MathF.PI);
            float v = rr * FxMath.Range(3f, 5.5f);
            SpawnPuff(x, y, FxMath.Cos(a) * v, FxMath.Sin(a) * v, 0.18f, 0.08f, FxMath.Range(0.25f, 0.45f), z - 0.0002f, PuffKind.Spark, a * (180f / System.MathF.PI));
        }
        for (int i = 0; i < 18; i++)
        {
            float a = FxMath.Range(0f, 2f * System.MathF.PI);
            float v = FxMath.Range(1.2f, 3.2f);
            SpawnPuff(x, y + 0.2f, FxMath.Cos(a) * v, FxMath.Sin(a) * v * 0.6f + FxMath.Range(1.5f, 3f), 0.09f, 0.05f, FxMath.Range(0.7f, 1.2f), z - 0.0001f, PuffKind.Ember);
        }
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (!lp || !hud || !hud.PlayerCam) return;
        var me = lp.transform.position;
        float ddx = me.x - x, ddy = me.y - y, dd = FxMath.Sqrt(ddx * ddx + ddy * ddy);
        if (dd >= ShakeReach) return;
        float k = 1f - dd / ShakeReach;
        hud.PlayerCam.ShakeScreen(0.25f + 0.3f * k, 0.6f + 3f * k * k);
    }

    // ---- 粒 ----

    private static void SpawnPuff(float x, float y, float vx, float vy, float s0, float s1, float life, float z, PuffKind kind, float rot = float.NaN)
    {
        if (Puffs.Count >= MaxPuffs) return;
        var p = Pool.Count > 0 ? Pool.Pop() : null;
        if (p == null || !p.Go)
        {
            var go = new GameObject("MrpEclipsePuff");
            FxRoot.Attach(go);
            p = new Puff { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        var art = kind == PuffKind.Core ? _flash : kind is PuffKind.Spark or PuffKind.Ember ? Terrain.DebrisArt.Spark : Terrain.DebrisArt.Puff;
        if (!ReferenceEquals(p.Art, art)) { p.Art = art; p.Sr.sprite = art; }
        p.Kind = kind;
        p.Go.SetActive(true);
        p.Start = Time.time;
        p.Life = life;
        p.Size0 = s0;
        p.Size1 = s1;
        p.X = x; p.Y = y; p.Vx = vx; p.Vy = vy; p.Z = z;
        p.Fall = kind == PuffKind.Ember ? 4.5f : 0f;
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
                if (p.Go) { p.Go.SetActive(false); Pool.Push(p); }
                Puffs.RemoveAt(i);
                continue;
            }
            float t = now - p.Start;
            float drag = p.Kind == PuffKind.Spark ? 1f - f * 0.3f : 1f - f * 0.6f;
            float s = p.Size0 + (p.Size1 - p.Size0) * (1f - (1f - f) * (1f - f));
            p.Tf.position = FxMath.V3(p.X + p.Vx * t * drag, p.Y + p.Vy * t * drag - 0.5f * p.Fall * t * t, p.Z);
            p.Tf.localScale = FxMath.V3(s, s, 1f);
            switch (p.Kind)
            {
                case PuffKind.Core:
                    p.Sr.color = FxMath.Rgba(1f, 1f, 0.92f, FxMath.Clamp01((1f - f) * 1.6f));
                    break;
                case PuffKind.Glow: // 黄から赤橙へ (濃いと明るい床で茶色の円盤に見えるので薄く)
                    p.Sr.color = FxMath.Rgba(1f, 0.9f - 0.45f * f, 0.55f - 0.4f * f, (1f - f) * (1f - f) * 0.55f);
                    break;
                case PuffKind.Spark:
                    p.Sr.color = FxMath.Rgba(1f, 0.9f - 0.4f * f, 0.45f - 0.3f * f, 1f - f * f);
                    break;
                case PuffKind.Ember: // 火の粉: 明るい黄から暗い赤へ
                    p.Sr.color = FxMath.Rgba(1f, 0.85f - 0.6f * f, 0.35f - 0.3f * f, 1f - f * f * f);
                    break;
                default:
                {
                    // 煙: 生まれたては火の色・すぐに暗い灰へ (隕石の煙は黒っぽい)
                    float w = FxMath.Clamp01(1f - f * 5f);
                    float g = 0.35f + 0.25f * f;
                    p.Sr.color = FxMath.Rgba(g + (1f - g) * w, g + (0.55f - g) * w, g + (0.2f - g) * w, (1f - f) * 0.75f);
                    break;
                }
            }
        }
    }

    internal static void ClearAll()
    {
        HideMark();
        foreach (var s in Live) if (s.Root) Object.Destroy(s.Root);
        Live.Clear();
        foreach (var p in Puffs) if (p.Go) Object.Destroy(p.Go);
        Puffs.Clear();
        foreach (var p in Pool) if (p.Go) Object.Destroy(p.Go);
        Pool.Clear();
    }
}
