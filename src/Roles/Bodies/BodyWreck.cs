using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 壊れ方 (どの一撃で壊れたか)。残骸の見た目 (焦げ・煙・火花・水) が変わる
internal enum WreckFate : byte
{
    Break = 0, // 撃たれた・叩かれた・爆発 = 煙と、ときどき火花
    Cut = 1,   // 斬られた = 切り口から火花 (煙は出ない)
    Burn = 2,  // 焼けた = 黒く焦げ、濃い煙が長く出る
    Douse = 3, // 水に浸かった = 火も煙も出ない
}

// 残骸の片 1 つの絵。yaw = 床の上の向きの番号 (0〜15)・landed = 一度床に着いたか・age = 壊れてからの秒
internal delegate Sprite WreckLook(int yaw, bool landed, float age, out bool flip);

// 残骸の片 1 つ。位置は床の点 (X, Y) と、床に置いた時からの高さ H で持つ (画面では H だけ上に描く)
internal sealed class WreckPart
{
    public WreckLook Look;
    public float Lift;               // 床に置いた時の、絵の原点の床からの高さ
    public float X, Y, H, Vh;        // 床の点・高さ・上下の速さ (上 = 正)
    public float Vx, Vy;             // 床の上の速さ
    public float Yaw, Spin;          // 向き (番号・小数) と回る速さ (番号/秒・床に着くと止まる)
    public Sprite ShadowArt;         // 床の影の絵 (黒で塗る)
    public float Shadow = 0.3f;      // 影の絵の倍率
    public float Pitch = 1f;         // 床に当たる音の高さ
    public bool Sparks, Smoke;       // この片から火花・煙を出すか
    public Action<BodyWreck.Wreck, WreckPart> OnLand; // 最初に床に当たった時 (外れる部品を足す等)

    internal GameObject Go;
    internal Transform Tf, ArtTf, ShadowTf;
    internal SpriteRenderer Art, ShadowSr;
    internal Sprite Shown;
    internal bool Flip, Landed, Resting, Wet;
}

// 壊れた物の残骸: 高さを持って落ち、床で跳ねて滑って止まり、影を落とす。煙・火花・焦げ・水のしぶきは壊れ方と素材で決まる。
// 残る時間はサムライが斬った扉・空間の裂け目と同じ戻り方 (会議まで / 秒数 / 戻らない) に揃える。
// 絵は FxRoot の下に置く (時戻しの逆再生がそのまま流す・その間はここも止める)
internal static class BodyWreck
{
    private const float Gravity = 9f;
    private const float Bounce = 0.25f;       // 跳ね返りの速さの割合
    private const float BounceMin = 1.1f;     // これより遅く当たったら跳ねずに着く
    private const float Friction = 4f;        // 床を滑る時の減速 (/秒²)
    private const float FadeTime = 0.6f;
    private const int MaxWrecks = 10, MaxSmoke = 28;
    private const float SmokeEvery = 0.16f, SmokeLife = 1.5f;
    private const float LandRange = 9f, LandVolume = 0.8f;

    internal sealed class Wreck
    {
        public WreckFate Fate;
        public string LandSound, SplashSound;
        public readonly List<WreckPart> Parts = new(3);
        internal float Born, Until, SmokeUntil, SparkUntil, NextSmoke, NextSpark, FadeFrom = -1f;
        internal bool Sounded;

        // 片を足す (落ちた時に外れる部品など)
        public void Add(WreckPart p)
        {
            if (Make(p)) Parts.Add(p);
        }
    }

    private enum PuffKind : byte { Smoke, Drop, Ring }

    private sealed class Puff
    {
        public PuffKind Kind;
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public float X, Y, Z, Vx, Vy, H, Vh, Age, Life, S0, S1, Dark;
    }

    private static readonly List<Wreck> Live = new();
    private static readonly List<Puff> Smoke = new();
    private static int _shipGen = -1;
    private static float _nextCheck;
    internal static int Made { get; private set; }

    // 残骸を出す。parts の位置・速さ・向き・絵は呼ぶ側が決める
    internal static Wreck Add(WreckFate fate, string landSound, string splashSound, params WreckPart[] parts)
    {
        if (!FxRoot.Get()) return null;
        if (_shipGen != GameClock.ShipGen) Clear();
        _shipGen = GameClock.ShipGen;
        if (Live.Count >= MaxWrecks) { Remove(Live[0]); Live.RemoveAt(0); }
        float now = Time.time;
        var w = new Wreck { Fate = fate, LandSound = landSound, SplashSound = splashSound, Born = now };
        int restore = Neutral.Samurai.CutRestore.Value;
        w.Until = restore == 1 ? now + Neutral.Samurai.CutRestoreTime.Value : 0f;
        w.SmokeUntil = fate switch { WreckFate.Break => now + 3f, WreckFate.Burn => now + 5f, _ => 0f };
        w.SparkUntil = fate switch { WreckFate.Break => now + 2f, WreckFate.Cut => now + 0.8f, _ => 0f };
        foreach (var p in parts) w.Add(p);
        if (w.Parts.Count == 0) return null;
        Live.Add(w);
        Made++;
        return w;
    }

    private static bool Make(WreckPart p)
    {
        if (p.Look == null) return false;
        var sp = p.Look(Idx(p.Yaw), false, 0f, out bool flip);
        if (!sp) return false;
        var go = new GameObject("MrpWreck");
        FxRoot.Attach(go);
        var tf = go.transform;
        var art = new GameObject("Art");
        art.transform.SetParent(tf, false);
        var sr = art.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.flipX = flip;
        var sh = new GameObject("Shadow");
        sh.transform.SetParent(tf, false);
        var ssr = sh.AddComponent<SpriteRenderer>();
        ssr.sprite = p.ShadowArt ? p.ShadowArt : DebrisArt.SoftShadow;
        ssr.color = new Color(0f, 0f, 0f, 0f);
        p.Go = go; p.Tf = tf; p.Art = sr; p.ArtTf = art.transform; p.ShadowSr = ssr; p.ShadowTf = sh.transform;
        p.Shown = sp; p.Flip = flip;
        Place(p);
        return true;
    }

    private static int Idx(float yaw)
    {
        int k = (int)FxMath.Floor(yaw + 0.5f) % 16;
        return k < 0 ? k + 16 : k;
    }

    internal static void Tick()
    {
        if (Live.Count == 0 && Smoke.Count == 0) return;
        if (RewindPlayback.Active) return;
        if (_shipGen != GameClock.ShipGen) { Clear(); return; }
        float now = Time.time, dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;
        bool check = now >= _nextCheck;
        if (check) _nextCheck = now + 0.25f;
        bool meetingGone = check && Neutral.Samurai.CutRestore.Value == 0 && MeetingHud.Instance;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var w = Live[i];
            if (meetingGone) { Remove(w); Live.RemoveAt(i); continue; }
            if (w.Until > 0f && now >= w.Until - FadeTime && w.FadeFrom < 0f) w.FadeFrom = now;
            if (w.FadeFrom >= 0f)
            {
                float a = 1f - (now - w.FadeFrom) / FadeTime;
                if (a <= 0f) { Remove(w); Live.RemoveAt(i); continue; }
                foreach (var p in w.Parts) Fade(p, a);
                continue;
            }
            float age = now - w.Born;
            for (int k = 0; k < w.Parts.Count; k++)
            {
                var p = w.Parts[k];
                if (p.Resting || !p.Go) continue;
                Step(w, p, dt, age);
            }
            Emit(w, now);
        }
        TickSmoke(dt);
    }

    private static void Step(Wreck w, WreckPart p, float dt, float age)
    {
        // 空中: 重さで落ちる・床の上は滑って止まる
        if (p.H > 0f || p.Vh > 0f)
        {
            p.Vh -= Gravity * dt;
            p.H += p.Vh * dt;
            p.Yaw += p.Spin * dt;
            if (p.H <= 0f)
            {
                p.H = 0f;
                float hit = -p.Vh;
                bool first = !p.Landed;
                p.Landed = true;
                bool water = InWater(p.X, p.Y);
                if (first) OnFirstLand(w, p, water, hit);
                if (hit > BounceMin && !water)
                {
                    p.Vh = hit * Bounce;
                    p.Vx *= 0.6f; p.Vy *= 0.6f;
                    p.Spin *= 0.4f;
                }
                else
                {
                    p.Vh = 0f;
                    p.Spin = 0f;
                    if (water) { p.Vx *= 0.3f; p.Vy *= 0.3f; }
                }
            }
        }
        else
        {
            float sp = FxMath.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            float dec = Friction * dt;
            if (sp <= dec) { p.Vx = p.Vy = 0f; p.Resting = true; }
            else { float k = (sp - dec) / sp; p.Vx *= k; p.Vy *= k; }
        }
        float nx = p.X + p.Vx * dt, ny = p.Y + p.Vy * dt;
        // 壁へ入る動きは止める (床の点で調べる)
        if (SolidMap.Valid && SolidMap.Solid(nx, ny)) { p.Vx = p.Vy = 0f; }
        else { p.X = nx; p.Y = ny; }
        var sprite = p.Look(Idx(p.Yaw), p.Landed, age, out bool flip);
        if (sprite && !ReferenceEquals(sprite, p.Shown)) { p.Art.sprite = sprite; p.Shown = sprite; }
        if (flip != p.Flip) { p.Art.flipX = flip; p.Flip = flip; }
        Place(p);
    }

    private static void OnFirstLand(Wreck w, WreckPart p, bool water, float hit)
    {
        if (water)
        {
            p.Wet = true;
            // 水の中に沈んで見える (水の色が乗り、少し透ける)
            p.Art.color = new Color(0.62f, 0.76f, 0.86f, 0.82f);
            p.Lift -= 0.03f;
            Splash(p.X, p.Y);
            if (w.SplashSound != null) Hearing.At(w.SplashSound, p.X, p.Y, LandRange, LandVolume, null, Ears.All, p.Pitch);
            w.SmokeUntil = 0f;
            w.SparkUntil = FxMath.Min(w.SparkUntil, Time.time + 0.3f);
        }
        else if (w.LandSound != null && hit > 0.4f)
            Hearing.At(w.LandSound, p.X, p.Y, LandRange, LandVolume * FxMath.Min(1f, hit / 3f + 0.4f), null, Ears.All, p.Pitch);
        if (w.Fate == WreckFate.Burn) p.Art.color = new Color(0.40f, 0.37f, 0.35f, 1f);
        p.OnLand?.Invoke(w, p);
    }

    private static bool InWater(float x, float y) => WaterSim.DepthAt(FxMath.V2(x, y)) >= WaterSim.Full / 12;

    // 位置を書く: 根は床の点 (奥行きは床の y)・絵は高さの分だけ上・影は床で、高いほど小さく薄い
    private static void Place(WreckPart p)
    {
        p.Tf.position = FxMath.V3(p.X, p.Y, p.Y / 1000f);
        p.ArtTf.localPosition = FxMath.V3(0f, p.H + p.Lift, 0f);
        float up = FxMath.Clamp01(p.H / 0.6f);
        float s = p.Shadow * (1f - 0.45f * up);
        p.ShadowTf.localPosition = FxMath.V3(0f, 0f, 0.0005f);
        p.ShadowTf.localScale = FxMath.V3(s, s, 1f);
        p.ShadowSr.color = new Color(0f, 0f, 0f, p.Wet ? 0.12f : 0.4f * (1f - 0.6f * up));
    }

    private static void Fade(WreckPart p, float a)
    {
        if (!p.Go) return;
        var c = p.Art.color;
        p.Art.color = new Color(c.r, c.g, c.b, (p.Wet ? 0.82f : 1f) * a);
        p.ShadowSr.color = new Color(0f, 0f, 0f, 0.4f * a);
    }

    // ---- 煙と火花 ----

    private static void Emit(Wreck w, float now)
    {
        bool smoke = now < w.SmokeUntil, sparks = now < w.SparkUntil;
        if (!smoke && !sparks) return;
        foreach (var p in w.Parts)
        {
            if (!p.Go) continue;
            float x = p.X, y = p.Y + p.H + p.Lift;
            if (smoke && p.Smoke && p.Landed && now >= w.NextSmoke)
            {
                bool burn = w.Fate == WreckFate.Burn;
                // 終わりに近いほど細く薄く
                float left = (w.SmokeUntil - now) / (burn ? 5f : 3f);
                SpawnSmoke(x + FxMath.Range(-0.05f, 0.05f), y + 0.02f, p.Y / 1000f - 0.002f, burn ? 0.85f : 0.55f, left);
            }
            if (sparks && p.Sparks && now >= w.NextSpark)
            {
                float a = FxMath.Range(0f, FxMath.PI * 2f);
                GatlingFx.Deflect(x + FxMath.Range(-0.06f, 0.06f), y, FxMath.Cos(a), FxMath.Sin(a));
            }
        }
        if (now >= w.NextSmoke) w.NextSmoke = now + SmokeEvery;
        if (now >= w.NextSpark) w.NextSpark = now + (w.Fate == WreckFate.Cut ? FxMath.Range(0.08f, 0.22f) : FxMath.Range(0.25f, 0.7f));
    }

    private static Puff NewPuff(PuffKind kind, Sprite sprite, float x, float y, float z)
    {
        if (Smoke.Count >= MaxSmoke) return null;
        var go = new GameObject("MrpWreckPuff");
        FxRoot.Attach(go);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        var p = new Puff { Kind = kind, Go = go, Tf = go.transform, Sr = sr, X = x, Y = y, Z = z };
        Smoke.Add(p);
        return p;
    }

    private static void SpawnSmoke(float x, float y, float z, float dark, float left)
    {
        var p = NewPuff(PuffKind.Smoke, DebrisArt.Puff, x, y, z);
        if (p == null) return;
        float k = 0.5f + 0.5f * FxMath.Clamp01(left);
        p.Vx = FxMath.Range(-0.06f, 0.06f); p.Vy = FxMath.Range(0.28f, 0.42f);
        p.Life = SmokeLife * k; p.S0 = 0.10f * k; p.S1 = 0.42f * k; p.Dark = dark;
        Draw(p);
    }

    private static void TickSmoke(float dt)
    {
        for (int i = Smoke.Count - 1; i >= 0; i--)
        {
            var p = Smoke[i];
            p.Age += dt;
            if (p.Age >= p.Life || !p.Go)
            {
                if (p.Go) UnityEngine.Object.Destroy(p.Go);
                Smoke.RemoveAt(i);
                continue;
            }
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            if (p.Kind == PuffKind.Smoke) p.Vy *= 1f - 0.6f * dt; // 上るほど遅くなり横へ流れる
            else if (p.Kind == PuffKind.Drop)
            {
                p.Vh -= Gravity * dt;
                p.H += p.Vh * dt;
                if (p.H < 0f) { p.Age = p.Life; continue; } // 水面へ落ちた
            }
            Draw(p);
        }
    }

    private static void Draw(Puff p)
    {
        float t = p.Age / p.Life;
        if (p.Kind == PuffKind.Drop)
        {
            p.Tf.position = FxMath.V3(p.X, p.Y + p.H, p.Z);
            p.Tf.localScale = FxMath.V3(p.S0, p.S0, 1f);
            p.Sr.color = new Color(0.78f, 0.88f, 0.95f, 0.85f * (1f - t * t));
            return;
        }
        if (p.Kind == PuffKind.Ring)
        {
            // 水面に広がる輪 (床の上なので縦に潰れる)
            float r = p.S0 + (p.S1 - p.S0) * FxMath.Sqrt(t);
            p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
            p.Tf.localScale = FxMath.V3(r, r * 0.42f, 1f);
            p.Sr.color = new Color(0.85f, 0.93f, 1f, 0.55f * (1f - t));
            return;
        }
        float s = p.S0 + (p.S1 - p.S0) * FxMath.Sqrt(t);
        p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
        p.Tf.localScale = FxMath.V3(s, s, 1f);
        // 出てすぐは濃く、広がるほど薄い。煤の灰色
        float a = (t < 0.15f ? t / 0.15f : 1f - (t - 0.15f) / 0.85f) * 0.42f * p.Dark;
        float g = 0.22f - 0.1f * p.Dark;
        p.Sr.color = new Color(g, g, g * 1.04f, a);
    }

    // 水へ落ちた: 跳ね上がる水滴と、水面に広がる輪
    private static void Splash(float x, float y)
    {
        float z = y / 1000f - 0.003f;
        for (int i = 0; i < 10; i++)
        {
            var p = NewPuff(PuffKind.Drop, DebrisArt.WaterDrop, x, y, z);
            if (p == null) break;
            float a = FxMath.Range(0f, FxMath.PI * 2f), v = FxMath.Range(0.4f, 1.1f);
            p.Vx = FxMath.Cos(a) * v; p.Vy = FxMath.Sin(a) * v * 0.45f;
            p.Vh = FxMath.Range(1.4f, 2.4f); p.H = 0.02f;
            p.Life = 0.7f; p.S0 = FxMath.Range(0.05f, 0.09f);
            Draw(p);
        }
        for (int i = 0; i < 2; i++)
        {
            var p = NewPuff(PuffKind.Ring, DebrisArt.WaveRing, x, y, z + 0.001f);
            if (p == null) break;
            p.Life = 0.9f + 0.4f * i; p.S0 = 0.15f; p.S1 = 0.9f + 0.4f * i;
            Draw(p);
        }
    }

    // ---- 片付け ----

    private static void Remove(Wreck w)
    {
        foreach (var p in w.Parts) if (p.Go) UnityEngine.Object.Destroy(p.Go);
    }

    internal static void Clear()
    {
        foreach (var w in Live) Remove(w);
        Live.Clear();
        foreach (var p in Smoke) if (p.Go) UnityEngine.Object.Destroy(p.Go);
        Smoke.Clear();
        _shipGen = GameClock.ShipGen;
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"wrecks n={Live.Count} made={Made} smoke={Smoke.Count}");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var w in Live)
        {
            sb.Append($" | {w.Fate} parts={w.Parts.Count}");
            foreach (var p in w.Parts)
                sb.Append($" [{p.X.ToString("F2", inv)},{p.Y.ToString("F2", inv)} h={p.H.ToString("F2", inv)} land={(p.Landed ? 1 : 0)} rest={(p.Resting ? 1 : 0)} wet={(p.Wet ? 1 : 0)}]");
        }
        return sb.ToString();
    }
}
