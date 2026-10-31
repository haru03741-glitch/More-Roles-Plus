using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// トリガーハッピーの見た目 (全員の端末・電文は増やさない)。
// 機関銃: 3D の形から描いた向きごとのコマ (Resources/Icons/th_gatling3d.png・1 コマ 144 四方・8 列・32 方向 × 銃身の回り 4 段)。
//         右手の側に持ち (本人の色の握った手で上の取っ手と下の握りを持つ)、真下の床に影を落とす。
//         撃つと銃身の回りが上がっていき (離すと緩んで止まる)、撃っている間は 1 発ごとに軸に沿って後ろへ跳ね、
//         銃口から撃つ向きへ伸びた炎が出て、横へ薬莢を弾き出す (床で 1 回跳ねる)。撃ち終わっても少しの間は銃口から煙が立つ。
// 弾: 黄白の光の筋。毎フレームここで進める (撃った人の当たりは TriggerHappy が tick ごとに決める)。
// 着弾: 壁に当たった所に火花・小さな煙・黒い弾痕 (しばらくして薄れる)。
// 物は全部使い回す。毎フレームの算術は FxMath
internal static class GatlingFx
{
    // ---- 数値 ----
    private const int Cell = 144, Cols = 8, Yaws = 32, Phases = 4;
    private const float Ppu = 100f, Squash = 0.58f;
    private const float SpinTop = 40f, SpinUp = 160f, SpinDown = 60f; // 銃身の回り (段/秒)・上がる速さ・緩む速さ (段/秒²)
    private const float Kick = 0.022f, Jitter = 0.005f;               // 撃っている間の後ろへの跳ね (軸に沿って)・横の震え
    private const float HandScale = 0.2f;                             // 握った手の絵 (1.4 単位) の倍率
    // 煙: 撃つたびに銃口から前へ噴き出して急に止まり、ふくらみながらゆっくり昇って残る (撃ち続けると銃の周りに溜まる)。
    // 撃ち終わると熱い銃身から細い煙が揺れながら立つ (撃った長さで濃く長く)
    private const float BlastEvery = 0.045f;                          // 噴き出す煙の間 (秒)
    private const float HeatMax = 4f, HeatCool = 0.6f;                 // 銃身の熱 (撃った秒で溜まる・1 秒に冷める量)
    private const float WispEvery = 0.07f;                             // 熱い時の銃身の煙の間 (熱が 1 の時)
    private const float Height = 0.82f;                                // 立体の高さが画面で縮む倍率 (絵と同じ)
    private const float BodyToFeet = 0.3636f;                          // 人の体の中心から足元まで
    private const float CaseEvery = 0.08f, CaseScale = 1f;          // 薬莢を出す間 (秒)・絵 (0.1 × 0.04) の倍率
    private const float HoldOut = 0.22f, HoldSide = 0.24f, HoldDown = 0.08f; // 体の中心から銃の握りまで (狙いの向きへ・右手の側へ・下へ)
    private const float MuzzleLen = 0.63f;                   // 握りから銃口まで (銃に沿って・絵の銃身の先)
    private const float FlashLen = 0.34f, FlashWidth = 0.15f; // 銃口の炎の長さと太さ (炎の絵は 0.96 × 0.48)
    private const float TracerLen = 0.62f, TracerWidth = 0.05f;
    private const float HoldSeconds = 2.5f, FadeSeconds = 1.5f; // 弾痕が濃いままの秒・薄れる秒
    private const int MaxParts = 360, MaxHoles = 80, MaxTracers = 256;

    // ---- 絵 ----
    private static readonly Sprite[] Frames = new Sprite[Yaws * Phases];
    private static Sprite _flash, _tracer, _hole, _case;
    private static bool _searched;

    // ---- 機関銃 (人ごと) ----
    internal sealed class Gun
    {
        public PlayerControl Owner;
        public float Angle;          // 画面の上の狙いの向き (度)
        public float FiredAt = -9f;  // 最後に撃った時刻 (銃身の回りと閃光)
        public bool Hidden;          // ベントの中・会議
        internal GameObject Go;
        internal Transform Tf, FlashTf, ShadowTf, HandRear, HandTop;
        internal SpriteRenderer Sr, FlashSr, HandRearSr, HandTopSr;
        internal float Spin, SpinSpeed, NextCase, NextSmoke, NextWisp, Heat;
        internal int HandFlip = -1, HandYaw = -1;
        internal int Frame = -1;
        internal bool FlashOn, Shown = true;
        internal float Px, Py, Pz;   // 握りの位置 (跳ねる前)
        internal float Gx, Gy;       // 今置いてある銃の位置 (跳ね・震えを含む)
        internal float YawRad;       // 今出しているコマの床の上の向き (絵の銃身の向き)
    }

    private static readonly List<Gun> Guns = new();

    // ---- 弾の筋と小物 ----
    internal sealed class Tracer
    {
        internal GameObject Go;
        internal Transform Tf;
        internal SpriteRenderer Sr;
        internal float X, Y, Dx, Dy, Speed, Max, Traveled, Wait, Size;
        internal bool Wall;          // 飛び切ったら壁に当たった所 (着弾を出す)
        internal bool Live;
        internal int Gen;
    }

    private enum Kind : byte { Spark, Smoke, Flash, Hole, Case, Haze, Wisp }

    private sealed class Part
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public Kind Kind;
        public float Start, Life, X, Y, Z, Vx, Vy, S0, S1, Floor, Rot, Turn;
        public float Drag, Rise, Alpha, Seed; // 煙: 速さの減り (1/秒)・浮く速さ・濃さ・揺れの位相
        public bool Bounced;
        public Sprite Art;
    }

    private static readonly List<Tracer> Tracers = new();
    private static readonly Stack<Tracer> TracerPool = new();
    private static readonly List<Part> Parts = new();
    private static readonly Stack<Part> PartPool = new();
    private static int _holes, _shipGen = -1;
    private static readonly float[] SparkAngles = { -0.9f, 0f, 0.9f };

    // ---- 機関銃 ----

    internal static Gun AddGun(PlayerControl owner)
    {
        if (!owner || !Ready()) return null;
        var g = new Gun { Owner = owner };
        g.Go = new GameObject("MrpGatlingGun");
        Under(g.Go);
        g.Tf = g.Go.transform;
        g.Sr = g.Go.AddComponent<SpriteRenderer>();
        var fl = new GameObject("Flash");
        g.FlashTf = fl.transform;
        g.FlashTf.SetParent(g.Tf, false);
        g.FlashSr = fl.AddComponent<SpriteRenderer>();
        g.FlashSr.sprite = _flash;
        g.FlashSr.enabled = false;
        // 床の影 (足元の高さに置く。銃の子だが位置は毎フレーム書く)
        var sh = new GameObject("Shadow");
        sh.transform.SetParent(g.Tf, false);
        g.ShadowTf = sh.transform;
        var ssr = sh.AddComponent<SpriteRenderer>();
        ssr.sprite = Terrain.DebrisArt.SoftShadow;
        ssr.color = FxMath.Rgba(0f, 0f, 0f, 0.32f);
        // 握った手 (本人の色)。手の絵が無ければ手だけ出さない
        int color = owner.Data != null ? owner.Data.DefaultOutfit.ColorId : 0;
        g.HandRear = NewHand(g, "HandRear", color, out g.HandRearSr);
        g.HandTop = NewHand(g, "HandTop", color, out g.HandTopSr);
        Guns.Add(g);
        Place(g, Time.time, 0f);
        return g;
    }

    private static Transform NewHand(Gun g, string name, int color, out SpriteRenderer sr)
    {
        sr = null;
        var mat = FxHands.PlayerMat();
        var grip = FxHands.GripSprite();
        if (!mat || !grip) return null;
        var go = new GameObject(name);
        go.transform.SetParent(g.Tf, false);
        go.transform.localScale = FxMath.V3(HandScale, HandScale, 1f);
        sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sprite = grip;
        if (color < 0 || color >= Palette.PlayerColors.Length) color = 0;
        PlayerMaterial.SetColors(color, sr);
        return go.transform;
    }

    // 銃の立体の点 (x = 銃口の向き・z = 上) を、握りの軸から見た画面のずれへ
    private static Vector2 Project(float yawRad, float x, float z) =>
        FxMath.V2(FxMath.Cos(yawRad) * x, FxMath.Sin(yawRad) * x * Squash + z * Height);

    internal static void RemoveGun(Gun g)
    {
        if (g == null) return;
        Guns.Remove(g);
        if (g.Go) Object.Destroy(g.Go);
        g.Go = null;
    }

    // 銃口 (今の握りと向きから)。弾を出す所
    // 絵の銃身の先 (今出しているコマの向きと、今置いてある銃の位置から)
    internal static Vector2 Muzzle(Gun g) =>
        FxMath.V2(g.Gx + FxMath.Cos(g.YawRad) * MuzzleLen, g.Gy + FxMath.Sin(g.YawRad) * MuzzleLen * Squash);

    private static void Place(Gun g, float now, float dt)
    {
        if (!g.Owner) return;
        bool show = !g.Hidden;
        if (show != g.Shown) { g.Shown = show; g.Go.SetActive(show); }
        if (!show) return;
        var c = g.Owner.transform.position;
        float r = g.Angle * (MathF.PI / 180f);
        float cs = FxMath.Cos(r), sn = FxMath.Sin(r);
        // 右手の側 (狙いの向きから見て右) に持つ: 上を狙っても体の横から銃が見える
        g.Px = c.x + cs * HoldOut + sn * HoldSide;
        g.Py = c.y + sn * HoldOut * Squash - cs * HoldSide * Squash - HoldDown;
        // 上を狙う時は体の後ろ・それ以外は前
        g.Pz = c.z + (sn > 0.3f ? 0.0008f : -0.0008f);
        bool firing = now - g.FiredAt < 0.12f;
        // 銃身の回り: 撃つと上がっていき、離すと緩んで止まる
        float want = firing ? SpinTop : 0f;
        g.SpinSpeed = g.SpinSpeed < want ? FxMath.Min(want, g.SpinSpeed + SpinUp * dt) : FxMath.Max(want, g.SpinSpeed - SpinDown * dt);
        g.Spin += g.SpinSpeed * dt;
        if (g.Spin > 4096f) g.Spin -= 4096f;
        // 撃っている間は 1 フレームおきに軸に沿って後ろへ跳ね、横にわずかに震える
        float kick = firing ? ((Time.frameCount & 1) == 0 ? Kick : Kick * 0.35f) : 0f;
        float jit = firing ? FxMath.Range(-Jitter, Jitter) : 0f;
        g.Gx = g.Px - cs * kick + sn * jit;
        g.Gy = g.Py - sn * kick * Squash - cs * jit;
        g.Tf.position = FxMath.V3(g.Gx, g.Gy, g.Pz);
        // 床の影: 足元の高さ・銃の画面の上の長さに合わせて横に伸ばす (影の絵は 0.32 四方)
        float feet = c.y - BodyToFeet;
        float sw = (0.3f + FxMath.Abs(cs) * 0.75f) / 0.32f, shh = (0.16f + FxMath.Abs(sn) * 0.2f) / 0.32f;
        g.ShadowTf.position = FxMath.V3(g.Px + cs * 0.12f, feet + 0.02f, feet / 1000f + 0.0005f);
        g.ShadowTf.localScale = FxMath.V3(sw, shh, 1f);
        int yi = (int)MathF.Round(YawOf(g.Angle) / (360f / Yaws));
        yi = ((yi % Yaws) + Yaws) % Yaws;
        int frame = yi * Phases + ((int)g.Spin & (Phases - 1));
        g.YawRad = yi * (2f * MathF.PI / Yaws);
        // 手は銃の子なので、向きのコマか左右が替わった時だけ置き直す
        int hk = yi * 2 + (cs < 0f ? 1 : 0);
        if (hk != g.HandYaw) { g.HandYaw = hk; PlaceHands(g, g.YawRad, cs); }
        if (frame != g.Frame && Frames[frame])
        {
            g.Frame = frame;
            g.Sr.sprite = Frames[frame];
        }
        // 銃口の炎: 撃っている間だけ 1 フレームおきに、撃つ向きへ伸びた形で瞬く (大きさと傾きを少し変える)
        bool flash = firing && (Time.frameCount & 1) == 0;
        if (flash)
        {
            var m = Muzzle(g);
            // 炎の絵の根元が銃口に来る。向きは絵の銃身の画面の上の向き
            float dir = FxMath.Atan2(FxMath.Sin(g.YawRad) * Squash, FxMath.Cos(g.YawRad)) * (180f / MathF.PI);
            g.FlashTf.position = FxMath.V3(m.x, m.y, g.Pz - 0.0002f);
            g.FlashTf.localScale = FxMath.V3(FlashLen * FxMath.Range(0.75f, 1.2f) / 0.96f, FlashWidth * FxMath.Range(0.8f, 1.1f) / 0.48f, 1f);
            g.FlashTf.rotation = FxMath.RotZ(dir + FxMath.Range(-5f, 5f));
        }
        Smoke(g, now, dt, firing, cs, sn);
        if (flash != g.FlashOn) { g.FlashOn = flash; g.FlashSr.enabled = flash; }
        // 薬莢: 銃の右手の側へ弾き出し、足元の床で 1 回跳ねて止まる
        if (firing && now >= g.NextCase && Parts.Count < MaxParts)
        {
            g.NextCase = now + CaseEvery;
            float v = FxMath.Range(1.1f, 1.6f);
            var p = Spawn(Kind.Case, g.Px - cs * 0.05f, g.Py + 0.05f, g.Pz - 0.0001f, sn * v * 0.6f + FxMath.Range(-0.2f, 0.2f), -cs * v * 0.6f * Squash + FxMath.Range(0.5f, 0.9f),
                CaseScale, CaseScale, FxMath.Range(0.7f, 0.9f), _case, FxMath.Range(0f, 360f));
            p.Floor = FxMath.Min(feet + FxMath.Range(-0.1f, 0.05f), g.Py - 0.15f);
            p.Bounced = false;
            p.Turn = FxMath.Range(-900f, 900f);
            p.Rot = FxMath.Range(0f, 360f);
        }
    }

    // 銃口の煙と銃身の熱の煙
    private static void Smoke(Gun g, float now, float dt, bool firing, float cs, float sn)
    {
        g.Heat = firing ? FxMath.Min(HeatMax, g.Heat + dt) : FxMath.Max(0f, g.Heat - HeatCool * dt);
        if (Parts.Count >= MaxParts - 20) return;
        var m = Muzzle(g);
        // 画面の上の銃身の向き
        float bx = FxMath.Cos(g.YawRad), by = FxMath.Sin(g.YawRad) * Squash;
        float bl = FxMath.Sqrt(bx * bx + by * by);
        bx /= bl; by /= bl;
        if (firing && now >= g.NextSmoke)
        {
            g.NextSmoke = now + BlastEvery;
            // 前へ噴き出す (すぐ止まる)。撃ち続けるほど煙は濃く大きくなる
            float hot = g.Heat / HeatMax;
            float v = FxMath.Range(1.8f, 2.8f), side = FxMath.Range(-0.45f, 0.45f);
            var p = Spawn(Kind.Haze, m.x + bx * 0.04f, m.y + by * 0.04f, g.Pz - 0.0003f,
                bx * v - by * side, by * v + bx * side, FxMath.Range(0.09f, 0.13f), FxMath.Range(0.5f, 0.7f) + 0.25f * hot,
                FxMath.Range(1.3f, 2.1f) + 0.6f * hot, Terrain.DebrisArt.DustBlob, FxMath.Range(0f, 360f));
            p.Drag = 6.5f;
            p.Rise = FxMath.Range(0.1f, 0.2f);
            p.Alpha = 0.34f + 0.14f * hot;
            p.Turn = FxMath.Range(-35f, 35f);
            p.Seed = FxMath.Range(0f, 6.28f);
            p.Sr.color = FxMath.Rgba(0.5f, 0.5f, 0.5f, 0f); // 色を書くのは 1 フレームおきなので、使い回した粒の前の色を出さない
        }
        if (!firing && g.Heat > 0.15f && now >= g.NextWisp)
        {
            // 熱い銃身の煙: 銃身の先の方のどこかから、細く揺れながら昇る
            g.NextWisp = now + WispEvery / FxMath.Min(1f, g.Heat);
            float along = FxMath.Range(-0.25f, 0f);
            var p = Spawn(Kind.Wisp, m.x + bx * along, m.y + by * along + 0.02f, g.Pz - 0.0002f,
                FxMath.Range(-0.04f, 0.04f), FxMath.Range(0.32f, 0.45f), 0.04f, FxMath.Range(0.13f, 0.19f),
                FxMath.Range(1.1f, 1.5f), Terrain.DebrisArt.DustBlob, FxMath.Range(0f, 360f));
            p.Drag = 0f;
            p.Rise = 0.05f;
            p.Alpha = 0.3f * FxMath.Min(1f, g.Heat / 1.5f);
            p.Turn = FxMath.Range(-20f, 20f);
            p.Seed = FxMath.Range(0f, 6.28f);
            p.Sr.color = FxMath.Rgba(0.5f, 0.5f, 0.5f, 0f); // 色を書くのは 1 フレームおきなので、使い回した粒の前の色を出さない
        }
    }

    // 握った手: 下の握り (後ろ寄りの下) と上の取っ手。拳の指は銃口の側。左を向く時は絵を左右に反転する
    private static void PlaceHands(Gun g, float yaw, float cs)
    {
        if (!g.HandRear) return;
        bool left = cs < 0f;
        int flip = left ? 1 : 0;
        if (flip != g.HandFlip)
        {
            g.HandFlip = flip;
            g.HandRearSr.flipX = g.HandTopSr.flipX = left;
        }
        float rot = left ? g.Angle - 180f : g.Angle;
        var r = Project(yaw, -0.09f, -0.13f);
        var t = Project(yaw, -0.12f, 0.15f);
        g.HandRear.localPosition = FxMath.V3(r.x, r.y, -0.0003f);
        g.HandTop.localPosition = FxMath.V3(t.x, t.y, -0.0003f);
        // 真上・真下を狙っても拳が裏返らないよう、傾きは ±50° まで
        if (rot > 180f) rot -= 360f;
        if (rot < -180f) rot += 360f;
        rot = FxMath.Clamp(rot, -50f, 50f);
        g.HandRear.rotation = FxMath.RotZ(rot);
        g.HandTop.rotation = FxMath.RotZ(rot + (left ? 25f : -25f));
    }

    // ---- 弾の筋 ----

    // 弾を 1 発出す。wait 秒たってから飛び始める (まとめて届いた他の人の弾をばらして見せる)
    internal static Tracer Shoot(float x, float y, float dx, float dy, float speed, float max, bool wall, float size, float wait = 0f)
    {
        if (!Ready() || Tracers.Count >= MaxTracers) return null;
        var t = TracerPool.Count > 0 ? TracerPool.Pop() : null;
        if (t == null)
        {
            var go = new GameObject("MrpGatlingTracer");
            Under(go);
            t = new Tracer { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
            t.Sr.sprite = _tracer;
            t.Sr.color = FxMath.Rgba(1f, 0.93f, 0.6f, 1f);
        }
        // 6 本の銃身のどれかから出たように、筋だけ横へ少しずらす (当たりの計算は TriggerHappy の位置のまま)
        float j = FxMath.Range(-0.03f, 0.03f);
        t.X = x - dy * j; t.Y = y + dx * j; t.Dx = dx; t.Dy = dy; t.Speed = speed; t.Max = max; t.Wall = wall; t.Wait = wait; t.Size = size;
        t.Traveled = 0f;
        t.Live = true;
        t.Gen++;
        t.Tf.rotation = FxMath.RotZ(FxMath.Atan2(dy, dx) * (180f / MathF.PI));
        t.Tf.localScale = FxMath.V3(TracerLen * size, TracerWidth * size / 0.125f, 1f);
        t.Go.SetActive(wait <= 0f);
        if (wait <= 0f) t.Tf.position = FxMath.V3(t.X, t.Y, -5f);
        Tracers.Add(t);
        return t;
    }

    // 人に当たった弾を消す (着弾は出さない)
    internal static void Stop(Tracer t, int gen)
    {
        if (t == null || !t.Live || t.Gen != gen) return;
        t.Live = false;
    }

    // ---- 着弾 ----

    // 壁に当たった (全員の端末)。火花 3 つ・煙 1 つ・小さな閃光・弾痕
    internal static void Impact(float x, float y, float dx, float dy)
    {
        if (!Ready()) return;
        float z = y / 1000f - 0.02f;
        float back = FxMath.Atan2(-dy, -dx);
        if (Parts.Count < MaxParts)
        {
            for (int i = 0; i < SparkAngles.Length; i++)
            {
                float a = back + SparkAngles[i] + FxMath.Range(-0.35f, 0.35f);
                float v = FxMath.Range(2.5f, 4f);
                Spawn(Kind.Spark, x, y, z - 0.0001f, FxMath.Cos(a) * v, FxMath.Sin(a) * v, 0.11f, 0.05f, FxMath.Range(0.14f, 0.24f), Terrain.DebrisArt.Spark, a * (180f / MathF.PI));
            }
            Spawn(Kind.Flash, x, y, z - 0.0002f, 0f, 0f, 0.12f, 0.2f, 0.06f, _flash, back * (180f / MathF.PI) + FxMath.Range(-25f, 25f));
            Spawn(Kind.Smoke, x, y, z, -dx * 0.3f, -dy * 0.3f + 0.25f, 0.1f, 0.32f, 0.45f, Terrain.DebrisArt.Puff, FxMath.Range(0f, 360f));
        }
        if (_holes >= MaxHoles) RecycleOldestHole();
        Spawn(Kind.Hole, x - dx * 0.04f, y - dy * 0.04f, z + 0.0003f, 0f, 0f, FxMath.Range(0.07f, 0.1f), 0f, HoldSeconds + FadeSeconds, _hole, FxMath.Range(0f, 360f));
        _holes++;
    }

    private static Part Spawn(Kind kind, float x, float y, float z, float vx, float vy, float s0, float s1, float life, Sprite art, float rot)
    {
        var p = PartPool.Count > 0 ? PartPool.Pop() : null;
        if (p == null)
        {
            var go = new GameObject("MrpGatlingBit");
            Under(go);
            p = new Part { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        if (!ReferenceEquals(p.Art, art)) { p.Art = art; p.Sr.sprite = art; }
        p.Kind = kind;
        p.Rot = 0f;
        p.Start = Time.time;
        p.Life = life;
        p.X = x; p.Y = y; p.Z = z; p.Vx = vx; p.Vy = vy; p.S0 = s0; p.S1 = s1;
        p.Tf.rotation = FxMath.RotZ(rot);
        p.Tf.position = FxMath.V3(x, y, z);
        p.Tf.localScale = FxMath.V3(s0, s0, 1f);
        p.Go.SetActive(true);
        Parts.Add(p);
        return p;
    }

    private static void RecycleOldestHole()
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            if (Parts[i].Kind != Kind.Hole) continue;
            Free(i);
            return;
        }
    }

    private static void Free(int i)
    {
        var p = Parts[i];
        if (p.Kind == Kind.Hole) _holes--;
        p.Go.SetActive(false);
        PartPool.Push(p);
        Parts.RemoveAt(i);
    }

    // ---- 毎フレーム ----

    internal static void Tick()
    {
        if (Guns.Count == 0 && Tracers.Count == 0 && Parts.Count == 0) return;
        if (GameClock.ShipGen != _shipGen) { Forget(); return; }
        float now = Time.time, dt = Time.deltaTime;
        for (int i = Guns.Count - 1; i >= 0; i--)
        {
            var g = Guns[i];
            if (!g.Owner) { RemoveGun(g); continue; }
            Place(g, now, dt);
        }
        TickTracers(dt);
        TickParts(now, dt);
    }

    private static void TickTracers(float dt)
    {
        for (int i = Tracers.Count - 1; i >= 0; i--)
        {
            var t = Tracers[i];
            if (!t.Live) { Recycle(i); continue; }
            if (t.Wait > 0f)
            {
                t.Wait -= dt;
                if (t.Wait > 0f) continue;
                t.Go.SetActive(true);
            }
            float step = t.Speed * dt;
            t.Traveled += step;
            if (t.Traveled >= t.Max)
            {
                if (t.Wall) Impact(t.X + t.Dx * (t.Max - t.Traveled + step), t.Y + t.Dy * (t.Max - t.Traveled + step), t.Dx, t.Dy);
                Recycle(i);
                continue;
            }
            t.X += t.Dx * step;
            t.Y += t.Dy * step;
            t.Tf.position = FxMath.V3(t.X, t.Y, -5f);
        }
    }

    private static void Recycle(int i)
    {
        var t = Tracers[i];
        t.Live = false;
        t.Gen++;
        t.Go.SetActive(false);
        TracerPool.Push(t);
        Tracers.RemoveAt(i);
    }

    private static void TickParts(float now, float dt)
    {
        for (int i = Parts.Count - 1; i >= 0; i--)
        {
            var p = Parts[i];
            float f = (now - p.Start) / p.Life;
            if (f >= 1f) { Free(i); continue; }
            switch (p.Kind)
            {
                case Kind.Spark:
                {
                    float drag = 1f - f * 0.5f;
                    p.X += p.Vx * dt * drag;
                    p.Y += p.Vy * dt * drag;
                    float s = p.S0 + (p.S1 - p.S0) * f;
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.9f - 0.4f * f, 0.45f - 0.3f * f, 1f - f * f);
                    break;
                }
                case Kind.Smoke:
                {
                    p.X += p.Vx * dt;
                    p.Y += p.Vy * dt;
                    float e = 1f - (1f - f) * (1f - f);
                    float s = p.S0 + (p.S1 - p.S0) * e;
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    float dense = p.Rot > 0f ? p.Rot : 0.55f; // 銃口の煙は薄い (Rot に濃さを入れてある)
                    p.Sr.color = FxMath.Rgba(0.8f, 0.8f, 0.8f, dense * (1f - f));
                    break;
                }
                case Kind.Haze:
                case Kind.Wisp:
                {
                    float age = now - p.Start;
                    // 噴き出しは空気で急に止まり、そのあと浮いて横に揺れる
                    if (p.Drag > 0f)
                    {
                        float k = FxMath.Exp(-p.Drag * dt);
                        p.Vx *= k; p.Vy *= k;
                    }
                    float sway = FxMath.Sin(age * (p.Kind == Kind.Wisp ? 5f : 1.6f) + p.Seed) * (p.Kind == Kind.Wisp ? 0.12f : 0.05f);
                    p.X += (p.Vx + sway) * dt;
                    p.Y += (p.Vy + p.Rise * FxMath.Min(1f, age * 2f)) * dt;
                    p.Rot += p.Turn * dt;
                    float e = 1f - (1f - f) * (1f - f) * (1f - f);
                    float s = p.S0 + (p.S1 - p.S0) * e;
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s * 0.9f, 1f);
                    // 回りと色はゆっくり変わるので、粒ごとに 1 フレームおきに書く
                    if (((Time.frameCount + i) & 1) != 0) break;
                    p.Tf.rotation = FxMath.RotZ(p.Rot);
                    // 生まれた直後は火の光で暖かい色・すぐ灰色へ。すっと濃くなり、ゆっくり薄れる。
                    // 明るい床でも見えるよう中くらいの灰色 (広がるほど明るく薄く)
                    float warm = FxMath.Max(0f, 1f - age * 12f);
                    float a = p.Alpha * FxMath.Min(1f, f * 14f) * (1f - f) * (1f - f * 0.5f);
                    float gr = 0.46f + 0.16f * f;
                    p.Sr.color = FxMath.Rgba(gr + 0.24f * warm, gr + 0.14f * warm, gr + 0.02f, a);
                    break;
                }
                case Kind.Flash:
                {
                    float s = p.S0 + (p.S1 - p.S0) * f;
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.95f, 0.7f, 1f - f);
                    break;
                }
                case Kind.Case:
                {
                    // 重さで落ち、足元の床で 1 回だけ跳ねて止まる・最後に薄れる
                    p.Vy -= 7f * dt;
                    p.X += p.Vx * dt;
                    p.Y += p.Vy * dt;
                    if (p.Y < p.Floor && p.Vy < 0f)
                    {
                        p.Y = p.Floor;
                        if (p.Bounced) { p.Vx = p.Vy = 0f; p.Turn = 0f; }
                        else { p.Bounced = true; p.Vy = -p.Vy * 0.35f; p.Vx *= 0.5f; p.Turn *= 0.4f; }
                    }
                    p.Rot += p.Turn * dt;
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Bounced ? p.Floor / 1000f + 0.0004f : p.Z);
                    p.Tf.rotation = FxMath.RotZ(p.Rot);
                    if (f > 0.7f) p.Sr.color = FxMath.Rgba(1f, 1f, 1f, (1f - f) / 0.3f);
                    else if (now - p.Start < dt * 1.5f) p.Sr.color = FxMath.Rgba(1f, 1f, 1f, 1f);
                    break;
                }
                default:
                {
                    // 弾痕は濃いまましばらく残ってから薄れる (色は薄れ始めてから書く)
                    float t = now - p.Start;
                    if (t < HoldSeconds)
                    {
                        if (t < dt * 1.5f) p.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0.9f);
                        break;
                    }
                    p.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0.9f * (1f - (t - HoldSeconds) / FadeSeconds));
                    break;
                }
            }
        }
    }

    // 船が替わった: 物は船と一緒に消えているので、一覧だけ捨てる
    private static void Forget()
    {
        _shipGen = GameClock.ShipGen;
        Guns.Clear();
        Tracers.Clear();
        TracerPool.Clear();
        Parts.Clear();
        PartPool.Clear();
        _holes = 0;
    }

    // ---- 小物 ----

    private static void Under(GameObject go)
    {
        var ship = ShipStatus.Instance;
        if (ship) go.transform.SetParent(ship.transform, true);
    }

    // 画面の上の向き (度) → 床の上の向き (度)。画面の縦は床の奥行きが縮んだ物なので伸ばして戻す
    private static float YawOf(float screenDeg)
    {
        float r = screenDeg * (MathF.PI / 180f);
        return FxMath.Atan2(FxMath.Sin(r) / Squash, FxMath.Cos(r)) * (180f / MathF.PI);
    }

    private static bool Ready()
    {
        if (GameClock.ShipGen != _shipGen) Forget();
        if (Frames[0] && _flash) return true;
        if (_searched) return false;
        _searched = true;
        _flash = MakeMuzzleFlash();
        var atlas = ButtonIcons.Load("th_gatling3d", Ppu, FxMath.V2(0.5f, 0.5f));
        var tex = atlas ? atlas.texture : null;
        if (tex)
        {
            int rows = tex.height / Cell;
            for (int k = 0; k < Frames.Length; k++)
            {
                float x = (k % Cols) * Cell, y = (rows - 1 - k / Cols) * Cell; // 絵の行は上から・テクスチャの y は下から
                Frames[k] = Sprite.Create(tex, new Rect(x, y, Cell, Cell), FxMath.V2(0.5f, 0.5f), Ppu);
                Frames[k].name = "MrpGatling" + k;
            }
        }
        _tracer = MakeTracer();
        _hole = MakeHole();
        _case = MakeCase();
        if (!Frames[0] || !_flash) Plugin.Logger.LogWarning("gatling fx: art not found");
        return Frames[0] && _flash;
    }

    // 弾の筋: 横 64 × 縦 8。後ろ (左) は透明・先 (右) ほど明るい白い芯と黄色の縁。幅 1 単位 = 64 px (縦 0.125)・中心が先
    private static unsafe Sprite MakeTracer()
    {
        const int w = 64, h = 8;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x + 0.5f) / w, v = MathF.Abs((y + 0.5f) / h * 2f - 1f);
            float a = u * u * MathF.Max(0f, 1f - v * v);
            float core = MathF.Max(0f, 1f - v * 2.2f);
            int i = (y * w + x) * 4;
            px[i] = 255;
            px[i + 1] = (byte)(200 + 55 * core);
            px[i + 2] = (byte)(90 + 165 * core);
            px[i + 3] = (byte)(255 * a);
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = "MrpGatlingTracer", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(1f, 0.5f), w);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 銃口の炎: 撃つ向き (右) へ細く伸びる炎。根元は白く熱い芯、先へ黄から橙へ抜けて消える。
    // 横 96 × 縦 48・1 単位 = 100 px・原点は根元 (左の端の真ん中)
    private static unsafe Sprite MakeMuzzleFlash()
    {
        const int w = 96, h = 48;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x + 0.5f) / w, v = MathF.Abs((y + 0.5f) / h * 2f - 1f);
            // 太さ: 根元で少し膨らみ、先へ細る
            float wid = 0.16f + 0.62f * MathF.Sqrt(u) * MathF.Pow(1f - u, 0.9f);
            float d = v / wid;
            if (d >= 1f) continue;
            float body = MathF.Pow(1f - d, 1.4f) * MathF.Pow(1f - u, 0.55f);
            float core = MathF.Max(0f, 1f - d * 1.9f) * MathF.Max(0f, 1f - u * 2.4f);
            // 根元の丸い光
            float bx = u * 4f, glow = MathF.Max(0f, 1f - MathF.Sqrt(bx * bx + v * v * 1.2f));
            float a = MathF.Min(1f, body + glow * 0.8f);
            float hot = MathF.Min(1f, core + glow * 0.6f);
            int i = (y * w + x) * 4;
            px[i] = 255;
            px[i + 1] = (byte)(140 + 115 * MathF.Min(1f, hot + (1f - u) * 0.35f));
            px[i + 2] = (byte)(40 + 215 * hot);
            px[i + 3] = (byte)(255 * a);
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = "MrpGatlingFlash", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(0f, 0.5f), 100f);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 薬莢: 真鍮の小さな筒 (横 10 × 縦 4・縁は暗い真鍮・上に照り)。1 単位 = 100 px
    private static unsafe Sprite MakeCase()
    {
        const int w = 10, h = 4;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool edge = x == 0 || x == w - 1 || y == 0 || y == h - 1;
            int i = (y * w + x) * 4;
            byte r = 222, g = 174, b = 72;
            if (edge) { r = 120; g = 84; b = 28; }
            else if (y == h - 2) { r = 255; g = 226; b = 140; }
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = "MrpGatlingCase", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 弾痕: 黒い穴と、焦げた縁 (16 四方・1 単位 = 16 px)
    private static unsafe Sprite MakeHole()
    {
        const int n = 16;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float d = MathF.Sqrt(u * u + v * v);
            if (d > 1f) continue;
            int i = (y * n + x) * 4;
            byte g = d < 0.45f ? (byte)18 : (byte)52;
            px[i] = g; px[i + 1] = g; px[i + 2] = (byte)(g + 4);
            px[i + 3] = (byte)(d < 0.45f ? 255 : 255 * (1f - (d - 0.45f) / 0.55f) * 0.8f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            name = "MrpGatlingHole", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }
}
