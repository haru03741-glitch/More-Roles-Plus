using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// ノヴァの見た目: 腕のプラズマキャノンが展開して狙いの方を向く → 溜め (砲口に赤いプラズマの球・まわりを走る放電・吸い込まれる粒・狙いの線)
// → 発射 (赤白の光線の帯 4 枚・砲口から光線に直交する輪が 2 つ広がる・反動) → 着弾 (床に巨大な衝撃波の輪 2 つ・爆発・火花)。
// 壁を壊す・床の水を湯気にする・燃える物に火を付けるのは、ホストが起こす地形の爆発と熱が全員の画面で出す
internal static class NovaFx
{
    // ---- 数値 ----
    private const float CannonPpu = 200f;            // 3D の形を描いた時と同じ (長さ約 1.6 単位)
    private const float Squash = 0.58f;              // 床の奥行きが画面で縮む倍率 (形を描いた時と同じ)
    private const float MuzzleAhead = 0.635f;        // 形の真ん中から砲口まで (床の上の長さ)
    private const float HandAhead = 0.3f, HandLift = -0.08f; // 形の真ん中: 体の芯から狙いの方へ・上下
    private const float Deploy = 0.18f, Retract = 0.25f, Hold = 1.3f; // Hold = 撃った後に赤熱が冷めて湯気を吹く間
    private const float Ignite = 0.07f;              // 出始め: 細い先導の光が壁まで走る時間
    private const float Swell = 0.08f;               // その後に光線が太く膨らむ時間
    private const float Thread = 0.35f;              // 出た後: 細い糸になって揺らぐ時間 (光線の終わりの内)
    private const float ScarLife = 2.5f;             // 床に残る焼けた線が冷める時間
    private const float RecoilBack = 0.16f, RecoilTime = 0.22f;
    private const float OrbPpu = 256f / 0.9f;
    private const float BeamPpu = 512f;
    private const float StreakPpu = 128f;
    private const float BoltPpu = 256f / 0.7f;       // 放電 1 本 = 長さ 0.7 単位
    private const float RingPpu = 512f;              // 輪の絵 = 直径 1 単位
    private const float BeamLife = 0.9f;
    private const float BeamWide = 1.25f;             // 見た目の太さ (当たりの太さに掛ける)
    private const float StreakSpeed = 30f;
    private const int MaxPuffs = 200;

    private static Sprite[] _cannon, _orb, _bolt;
    private static Sprite _beam, _streak, _ring, _flash;
    private static bool _searched;

    private static bool Art()
    {
        if (_beam) return true;
        if (_searched) return false;
        _searched = true;
        _cannon = ButtonIcons.Frames("nova_cannon3d", 32, CannonPpu, 4);
        _orb = ButtonIcons.Frames("nova_orb", 4, OrbPpu);
        _bolt = ButtonIcons.Frames("nova_bolt", 6, BoltPpu, 6);
        _beam = ButtonIcons.Load("nova_beam", BeamPpu, FxMath.V2(0f, 0.5f)); // 左端が原点 (撃った所から先へ伸ばす)
        _streak = ButtonIcons.Load("nova_streak", StreakPpu, FxMath.V2(0.5f, 0.5f));
        _ring = ButtonIcons.Load("nova_ring", RingPpu, FxMath.V2(0.5f, 0.5f));
        _flash = ButtonIcons.Load("rl_flash", 200f, FxMath.V2(0.5f, 0.5f));
        return _cannon != null && _orb != null && _bolt != null && _beam && _streak && _ring;
    }

    private static SpriteRenderer Make(string name, Sprite sp, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // 画面の上の向き (度) → 床の上の向き (度)。画面の縦は床の奥行きが縮んだ物なので伸ばして戻す
    private static float YawOf(float screenDeg)
    {
        float r = screenDeg * (System.MathF.PI / 180f);
        return FxMath.Atan2(FxMath.Sin(r) / Squash, FxMath.Cos(r)) * (180f / System.MathF.PI);
    }

    // ================= キャノンと溜め =================

    private sealed class Shot
    {
        public GameObject Root;
        public PlayerControl P;
        public SpriteRenderer Cannon, Orb, Light, Aim, Heat;
        public SpriteRenderer[] Bolts;
        public Transform CannonTf, OrbTf, LightTf, AimTf;
        public float T0, Dur, Ax, Ay, Size, AimLen, NextSpark, NextBolt, FiredAt = -1f, MuzzleLen, NextSteam;
        public float Mx, My, Z;            // 今の砲口 (画面) と奥行き
        public int Frame = -1, Check;
        public bool Shown = true;
    }

    private static readonly List<Shot> Shots = new();

    // 撃つ人・向き (画面の度)・溜めの秒・光線の太さ・狙いの線の長さ
    internal static void BeginCharge(PlayerControl p, float angle, float dur, float size, float aimLen)
    {
        if (!p || RewindPlayback.Active || !Art()) return;
        CancelCharge(p);
        var root = new GameObject("MrpNovaShot");
        FxRoot.Attach(root);
        float r = angle * (System.MathF.PI / 180f);
        var s = new Shot { Root = root, P = p, T0 = Time.time, Dur = dur, Ax = FxMath.Cos(r), Ay = FxMath.Sin(r), Size = size, AimLen = aimLen };
        // 床の上の向きのコマと、そのコマでの砲口までの画面の長さ
        float yaw = YawOf(angle);
        int k = (int)System.MathF.Round(yaw / 11.25f);
        s.Frame = ((k % 32) + 32) % 32;
        float yr = yaw * (System.MathF.PI / 180f);
        float cy = FxMath.Cos(yr), sy = FxMath.Sin(yr) * Squash;
        s.MuzzleLen = MuzzleAhead * FxMath.Sqrt(cy * cy + sy * sy);
        s.Light = Make("Light", Terrain.DebrisArt.Puff, root.transform);
        s.LightTf = s.Light.transform;
        s.Aim = Make("Aim", _beam, root.transform);
        s.AimTf = s.Aim.transform;
        s.AimTf.rotation = FxMath.RotZ(angle);
        s.Cannon = Make("Cannon", _cannon[s.Frame], root.transform);
        s.CannonTf = s.Cannon.transform;
        s.Bolts = new SpriteRenderer[3];
        for (int i = 0; i < 3; i++) s.Bolts[i] = Make("Bolt" + i, _bolt[i], root.transform);
        s.Orb = Make("Orb", _orb[0], root.transform);
        s.OrbTf = s.Orb.transform;
        s.Heat = Make("Heat", Terrain.DebrisArt.Puff, root.transform);
        s.Heat.enabled = false;
        Shots.Add(s);
    }

    private static bool TickShot(Shot s, float now)
    {
        if (!s.P) return false;
        float t = now - s.T0;
        bool firing = s.FiredAt >= 0f;
        float tf = firing ? now - s.FiredAt : 0f;
        if (firing && tf > BeamLife + Hold + Retract) return false;
        if (!firing && t > s.Dur + 1.5f) return false; // 撃たれずに終わった (会議・死)
        // 生死と見え方は数フレームに 1 回: 死んだら消す・撃つ人が自分の視界に無い時は出さない (壁越しに居場所が漏れない)
        if (++s.Check % 6 == 1)
        {
            if (!firing && !PortKit.Alive(s.P)) return false;
            bool vis = s.P.Visible;
            if (vis != s.Shown) { s.Shown = vis; s.Cannon.enabled = vis; }
        }
        // キャノン: 展開 (0 → 1.1 → 1) → 撃った時に反動で後ろへ → 引っ込む
        float scale = t < Deploy ? Back(t / Deploy) : 1f;
        if (firing && tf > BeamLife + Hold) scale = 1f - (tf - BeamLife - Hold) / Retract;
        float recoil = firing && tf < RecoilTime ? RecoilBack * (1f - tf / RecoilTime) * (1f - tf / RecoilTime) : 0f;
        var pos = s.P.transform.position;
        float cx = pos.x + s.Ax * (HandAhead - recoil), cyy = pos.y + HandLift + s.Ay * (HandAhead - recoil) * 0.8f;
        // 上を狙う時は体の後ろ、それ以外は体の前
        s.Z = pos.z + (s.Ay > 0.35f ? 0.0004f : -0.0004f);
        s.CannonTf.position = FxMath.V3(cx, cyy, s.Z);
        s.CannonTf.localScale = FxMath.V3(scale, scale, 1f);
        s.Mx = cx + s.Ax * s.MuzzleLen * scale;
        s.My = cyy + s.Ay * s.MuzzleLen * scale;
        if (!firing) TickCharge(s, now, t);
        else TickCooling(s, now, tf, cx, cyy);
        return true;
    }

    // 撃った後: 砲口が赤熱して冷め、キャノンの上から湯気が昇る
    private static void TickCooling(Shot s, float now, float tf, float cx, float cy)
    {
        if (!s.Shown) { s.Heat.enabled = false; return; }
        float cool = FxMath.Clamp01((tf - BeamLife * 0.5f) / (BeamLife * 0.5f + Hold));
        s.Heat.enabled = cool < 1f;
        if (s.Heat.enabled)
        {
            float hs = 0.9f - 0.4f * cool;
            s.Heat.transform.position = FxMath.V3(s.Mx, s.My, s.Z - 0.0003f);
            s.Heat.transform.localScale = FxMath.V3(hs, hs, 1f);
            s.Heat.color = FxMath.Rgba(1f, 0.25f + 0.2f * (1f - cool), 0.15f, 0.75f * (1f - cool));
        }
        if (tf > 0.25f && tf < BeamLife + Hold && now >= s.NextSteam)
        {
            s.NextSteam = now + 0.07f;
            float k = FxMath.Range(-0.35f, 0.25f);
            float sx = cx + s.Ax * k * 1.2f, sy = cy + s.Ay * k + 0.12f;
            SpawnPuff(sx, sy, FxMath.Range(-0.15f, 0.15f), FxMath.Range(0.5f, 0.9f), 0.15f, 0.55f, FxMath.Range(0.6f, 0.9f), s.Z - 0.0006f, PuffKind.Steam);
        }
    }

    private static float Back(float u) { u = FxMath.Clamp01(u); float c = 1.7f; return 1f + (c + 1f) * (u - 1f) * (u - 1f) * (u - 1f) + c * (u - 1f) * (u - 1f); }

    private static void TickCharge(Shot s, float now, float t)
    {
        bool show = s.Shown;
        s.Orb.enabled = s.Light.enabled = s.Aim.enabled = show;
        for (int i = 0; i < 3; i++) s.Bolts[i].enabled = show;
        if (!show) return;
        float u = s.Dur > 0f ? FxMath.Clamp01(t / s.Dur) : 1f;
        float z = s.Z - 0.0003f;
        float ox = s.Mx + s.Ax * 0.08f, oy = s.My + s.Ay * 0.08f;
        s.OrbTf.position = FxMath.V3(ox, oy, z);
        float os = (0.35f + 0.9f * u * u) * (0.8f + 0.2f * s.Size) * (1f + 0.1f * FxMath.Sin(now * 45f));
        s.OrbTf.localScale = FxMath.V3(os, os, 1f);
        s.Orb.sprite = _orb[(int)(now * 16f) & 3];
        // 放電: 3 本が 0.05 秒ごとに向きと長さを変えて砲口のまわりを走る (溜めの終わりほど長く明るい)
        if (now >= s.NextBolt)
        {
            s.NextBolt = now + 0.05f;
            for (int i = 0; i < 3; i++)
            {
                var b = s.Bolts[i];
                b.sprite = _bolt[(int)FxMath.Range(0f, 5.999f)];
                float a = FxMath.Range(0f, 360f), len = FxMath.Range(0.5f, 1f) * (0.5f + 0.9f * u);
                float ar = a * (System.MathF.PI / 180f);
                var tf = b.transform;
                tf.position = FxMath.V3(ox + FxMath.Cos(ar) * len * 0.45f, oy + FxMath.Sin(ar) * len * 0.45f, z - 0.0001f);
                tf.rotation = FxMath.RotZ(a);
                tf.localScale = FxMath.V3(len, 0.6f + 0.5f * u, 1f);
                b.color = FxMath.Rgba(1f, 0.35f + 0.3f * u, 0.32f + 0.3f * u, 0.5f + 0.5f * u);
            }
        }
        // 床の赤い光だまり (明るい床で濁らないよう薄く)
        var pos = s.P.transform.position;
        float fy = pos.y - 0.36f;
        float ld = 1.0f + 2.2f * u;
        s.LightTf.position = FxMath.V3(ox, fy + 0.05f, fy / 1000f + 0.0006f);
        s.LightTf.localScale = FxMath.V3(ld, ld * 0.6f, 1f);
        s.Light.color = FxMath.Rgba(1f, 0.45f, 0.4f, 0.06f + 0.2f * u);
        // 狙いの線
        s.AimTf.position = FxMath.V3(ox, oy, z + 0.0002f);
        s.AimTf.localScale = FxMath.V3(s.AimLen, 0.2f + 0.25f * u, 1f);
        s.Aim.color = FxMath.Rgba(1f, 0.3f, 0.25f, (0.25f + 0.4f * u) * (0.75f + 0.25f * FxMath.Sin(now * 31f)));
        // 光の粒が砲口へ吸い込まれる
        while (s.NextSpark <= now)
        {
            s.NextSpark = (s.NextSpark <= 0f ? now : s.NextSpark) + (0.04f - 0.03f * u);
            float a = FxMath.Range(0f, 2f * System.MathF.PI);
            float rad = FxMath.Range(0.8f, 1.5f);
            float life = 0.22f;
            float sx = ox + FxMath.Cos(a) * rad, sy = oy + FxMath.Sin(a) * rad * 0.8f;
            SpawnPuff(sx, sy, (ox - sx) / life, (oy - sy) / life, 0.07f, 0.14f, life, z - 0.0001f, PuffKind.Gather, a * (180f / System.MathF.PI));
        }
        // 溜めの終わり 0.3 秒は近くの画面を細かく揺らす
        if (t > s.Dur - 0.3f && ((int)(now * 20f) & 1) == 0) Shake(ox, oy, 6f, 0.04f, 0.3f);
    }

    // 撃たずに終わった (光線に替わらなかった) 溜めを消す
    internal static void CancelCharge(PlayerControl p)
    {
        for (int i = Shots.Count - 1; i >= 0; i--)
            if (Shots[i].P == p && Shots[i].FiredAt < 0f) { if (Shots[i].Root) Object.Destroy(Shots[i].Root); Shots.RemoveAt(i); }
    }

    // ================= 光線 =================

    private sealed class Beam
    {
        public GameObject Root;
        public SpriteRenderer[] Layers;
        public Transform[] LayerTf;
        public float T0, X, Y, Dx, Dy, Len, Size, Z, NextStreak, Angle;
        public SpriteRenderer Scar;
        public bool Embered;
    }

    private static readonly float[] LayerW = { 2.8f, 1.9f, 1.15f, 0.62f };
    private static readonly Color[] LayerC =
    {
        new(0.3f, 0.02f, 0.05f, 0.38f),  // 暗い下地 (明るい床で沈まないように)
        new(1f, 0.1f, 0.08f, 0.45f),     // 赤いにじみ
        new(1f, 0.18f, 0.14f, 0.95f),    // 本体 (濃い赤)
        new(1f, 0.97f, 0.95f, 1f),       // 白い芯
    };

    private static readonly List<Beam> Beams = new();

    // from = 当たりの線の起点 (足元)。見た目はキャノンの砲口から出す
    internal static void Fire(PlayerControl p, Vector2 from, float angle, float length, float size, bool hitWall)
    {
        if (RewindPlayback.Active || !Art()) return;
        float r = angle * (System.MathF.PI / 180f);
        float dx = FxMath.Cos(r), dy = FxMath.Sin(r);
        Shot shot = null;
        foreach (var s in Shots) if (s.P == p && s.FiredAt < 0f) { shot = s; break; }
        float mx, my;
        if (shot != null)
        {
            shot.FiredAt = Time.time;
            shot.Orb.enabled = shot.Light.enabled = shot.Aim.enabled = false;
            for (int i = 0; i < 3; i++) shot.Bolts[i].enabled = false;
            mx = shot.Mx; my = shot.My;
        }
        else
        {
            mx = from.x + dx * 0.9f; my = from.y + 0.28f + dy * 0.7f;
        }
        // 足元の線の長さを砲口から測り直す (砲口の分だけ短い)
        float back = (mx - from.x) * dx + (my - from.y) * dy;
        float len = length - back > 0.05f ? length - back : 0.05f;
        var b = new Beam { T0 = Time.time, X = mx, Y = my, Dx = dx, Dy = dy, Len = len, Size = size * BeamWide, Angle = angle, Z = (from.y - 2f) / 1000f };
        b.Root = new GameObject("MrpNovaBeam");
        FxRoot.Attach(b.Root);
        b.Layers = new SpriteRenderer[4];
        b.LayerTf = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            var sr = Make("Layer" + i, _beam, b.Root.transform);
            var tf = sr.transform;
            tf.position = FxMath.V3(b.X, b.Y, b.Z - i * 0.0001f);
            tf.rotation = FxMath.RotZ(angle);
            b.Layers[i] = sr;
            b.LayerTf[i] = tf;
        }
        // 床に残る焼けた線 (光線の真下の床・人の下)。光線が細り始めたら出して冷めていく
        b.Scar = Make("Scar", _beam, b.Root.transform);
        var scarTf = b.Scar.transform;
        scarTf.position = FxMath.V3(mx, my - 0.3f, from.y / 1000f + 0.0007f);
        scarTf.rotation = FxMath.RotZ(angle);
        scarTf.localScale = FxMath.V3(len, 0.9f * size, 1f);
        b.Scar.enabled = false;
        Beams.Add(b);
        // 後ろの放熱口から噴気と火花 (反動)
        float rx = mx - dx * 1.2f, ry = my - dy * 1.0f;
        for (int i = 0; i < 9; i++)
        {
            float a = angle + 180f + FxMath.Range(-35f, 35f), ar = a * (System.MathF.PI / 180f), v = FxMath.Range(1.5f, 3.5f);
            SpawnPuff(rx, ry, FxMath.Cos(ar) * v, FxMath.Sin(ar) * v, 0.25f, 0.9f, FxMath.Range(0.5f, 0.8f), b.Z + 0.0002f, PuffKind.Steam);
            SpawnPuff(rx, ry, FxMath.Cos(ar) * v * 2f, FxMath.Sin(ar) * v * 2f, 0.14f, 0.06f, FxMath.Range(0.2f, 0.4f), b.Z - 0.0004f, PuffKind.Spark, a);
        }
        // 砲口: 閃光・大きなにじみ・光線に直交するプラズマの輪 2 つ
        if (_flash) SpawnPuff(mx, my, 0f, 0f, 0.8f * size + 0.6f, 1.6f * size + 1f, 0.22f, b.Z - 0.0008f, PuffKind.Flash, angle);
        SpawnPuff(mx, my, 0f, 0f, 2f, 6f, 0.5f, b.Z + 0.0004f, PuffKind.Glow);
        SpawnPuff(mx, my, 0f, 0f, 0.6f, 4.2f, 0.5f, b.Z - 0.0007f, PuffKind.RingCross, angle);
        SpawnPuff(mx + dx * 0.5f, my + dy * 0.5f, dx * 3f, dy * 3f, 0.4f, 3.0f, 0.45f, b.Z - 0.0007f, PuffKind.RingCross, angle, 0.1f);
        Shake(mx, my, 12f, 0.35f, 1.6f);
        if (hitWall) Impact(mx + dx * len, my + dy * len, angle, size, b.Z);
    }

    // 着弾: 床に巨大な衝撃波の輪 2 つ・爆発のにじみと閃光・跳ね返る火花・煙
    private static void Impact(float x, float y, float angle, float size, float z)
    {
        if (_flash) SpawnPuff(x, y, 0f, 0f, 0.9f * size + 0.6f, 1.8f * size + 1.2f, 0.28f, z - 0.0008f, PuffKind.Flash);
        SpawnPuff(x, y, 0f, 0f, 1.5f, 4.5f, 0.55f, z + 0.0004f, PuffKind.Glow);
        SpawnPuff(x, y, 0f, 0f, 0.5f, 5.5f, 0.65f, z + 0.0006f, PuffKind.RingFloor);
        SpawnPuff(x, y, 0f, 0f, 0.3f, 3.8f, 0.6f, z + 0.0006f, PuffKind.RingFloor, float.NaN, 0.12f);
        for (int i = 0; i < 22; i++)
        {
            float a = angle + 180f + FxMath.Range(-80f, 80f);
            float ar = a * (System.MathF.PI / 180f), v = FxMath.Range(3.5f, 8f);
            SpawnPuff(x, y, FxMath.Cos(ar) * v, FxMath.Sin(ar) * v, 0.18f, 0.08f, FxMath.Range(0.3f, 0.6f), z - 0.0005f, PuffKind.Spark, a);
        }
        for (int i = 0; i < 12; i++)
        {
            float a = FxMath.Range(0f, 2f * System.MathF.PI), v = FxMath.Range(0.8f, 2f);
            SpawnPuff(x, y, FxMath.Cos(a) * v, FxMath.Sin(a) * v * 0.7f, 0.4f, 1.3f, FxMath.Range(0.8f, 1.2f), z + 0.0002f, PuffKind.Smoke);
        }
        Shake(x, y, 12f, 0.3f, 1.4f);
    }

    private static void Shake(float x, float y, float reach, float amp, float dur)
    {
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (!lp || !hud || !hud.PlayerCam) return;
        var me = lp.transform.position;
        float dx = me.x - x, dy = me.y - y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d >= reach) return;
        float k = 1f - d / reach;
        hud.PlayerCam.ShakeScreen(amp * k, dur * k);
    }

    private static bool TickBeam(Beam b, float now)
    {
        float t = now - b.T0;
        // 床の焼けた線: 細り始めから冷めていく (赤 → 暗い赤 → 消える)
        if (t >= BeamLife - Thread)
        {
            float st = (t - (BeamLife - Thread)) / ScarLife;
            if (st >= 1f) return false;
            if (!b.Scar.enabled) b.Scar.enabled = true;
            float sc = 1f - st;
            b.Scar.color = FxMath.Rgba(0.35f + 0.65f * sc * sc, 0.06f + 0.2f * sc * sc * sc, 0.05f, 0.55f * sc);
        }
        if (t >= BeamLife)
        {
            if (!b.Embered) { b.Embered = true; for (int i = 0; i < 4; i++) b.Layers[i].enabled = false; Embers(b); }
            return true;
        }
        // 長さ: 出始めは細い先導の光が砲口から壁まで走る
        float len = t < Ignite ? b.Len * (t / Ignite) : b.Len;
        // 太さ: 先導は細い → 一気に 1.5 倍へ膨らむ → 1 へ落ち着く → 終わりは細い糸になって揺らぐ
        float k;
        if (t < Ignite) k = 0.1f;
        else if (t < Ignite + Swell) k = 0.1f + 1.4f * ((t - Ignite) / Swell);
        else if (t < Ignite + Swell + 0.2f) k = 1.5f - 0.5f * ((t - Ignite - Swell) / 0.2f);
        else k = 1f;
        float fade = 1f;
        if (t > BeamLife - Thread)
        {
            float u = (t - (BeamLife - Thread)) / Thread;
            k *= 0.12f + 0.88f * (1f - u) * (1f - u);
            fade = 1f - u * u;
            k *= 0.7f + 0.6f * FxMath.Range(0f, 1f) * u; // 糸が不規則に震える
        }
        float flick = 1f + 0.07f * FxMath.Sin(now * 75f);
        for (int i = 0; i < 4; i++)
        {
            // 先導の間は白い芯だけ
            if (t < Ignite && i < 3) { if (b.Layers[i].enabled) b.Layers[i].enabled = false; continue; }
            if (!b.Layers[i].enabled) b.Layers[i].enabled = true;
            float w = t < Ignite ? 0.35f : b.Size * LayerW[i] * k * flick * 4f; // 帯の絵の高さ 0.25 単位 → 1 単位に
            b.LayerTf[i].localScale = FxMath.V3(len, w, 1f);
            var c = LayerC[i];
            b.Layers[i].color = FxMath.Rgba(c.r, c.g, c.b, c.a * fade);
        }
        // 先導の先端の光
        if (t < Ignite) SpawnPuff(b.X + b.Dx * len, b.Y + b.Dy * len, 0f, 0f, 0.5f, 0.2f, 0.08f, b.Z - 0.0009f, PuffKind.Spark);
        float end = fade;
        while (b.NextStreak <= now && end > 0.3f && t >= Ignite)
        {
            b.NextStreak = (b.NextStreak <= 0f ? now : b.NextStreak) + 0.009f;
            float d0 = FxMath.Range(0f, b.Len * 0.75f);
            float side = FxMath.Range(-0.35f, 0.35f) * b.Size;
            float sx = b.X + b.Dx * d0 - b.Dy * side, sy = b.Y + b.Dy * d0 + b.Dx * side;
            SpawnPuff(sx, sy, b.Dx * StreakSpeed, b.Dy * StreakSpeed, 0.6f, 1.1f, FxMath.Range(0.12f, 0.22f), b.Z - 0.0005f, PuffKind.Streak, b.Angle);
        }
        return true;
    }

    // 光線がほどけて火の粉になり、通った所に漂って消える
    private static void Embers(Beam b)
    {
        int n = System.Math.Clamp((int)(b.Len * 3f), 8, 40);
        for (int i = 0; i < n; i++)
        {
            float d = FxMath.Range(0f, b.Len);
            float side = FxMath.Range(-0.2f, 0.2f);
            float x = b.X + b.Dx * d - b.Dy * side, y = b.Y + b.Dy * d + b.Dx * side;
            SpawnPuff(x, y, FxMath.Range(-0.3f, 0.3f), FxMath.Range(0.2f, 0.7f), 0.1f, 0.03f, FxMath.Range(0.6f, 1.3f), b.Z - 0.0004f, PuffKind.Ember);
        }
    }

    // ================= 粒 =================

    private enum PuffKind : byte { Gather, Streak, Spark, Flash, Glow, Smoke, RingCross, RingFloor, Steam, Ember }

    private sealed class Puff
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public float Start, Life, Size0, Size1, X, Y, Vx, Vy, Z;
        public PuffKind Kind;
        public Sprite Art;
    }

    private static readonly List<Puff> Puffs = new();
    private static readonly Stack<Puff> Pool = new();

    // delay = 出るまでの秒 (その間は見せない)
    private static void SpawnPuff(float x, float y, float vx, float vy, float s0, float s1, float life, float z, PuffKind kind, float rot = float.NaN, float delay = 0f)
    {
        if (Puffs.Count >= MaxPuffs) return;
        var p = Pool.Count > 0 ? Pool.Pop() : null;
        if (p == null || !p.Go)
        {
            var go = new GameObject("MrpNovaPuff");
            FxRoot.Attach(go);
            p = new Puff { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        var art = kind switch
        {
            PuffKind.Flash => _flash,
            PuffKind.Streak => _streak,
            PuffKind.Glow or PuffKind.Smoke or PuffKind.Steam => Terrain.DebrisArt.Puff,
            PuffKind.RingCross or PuffKind.RingFloor => _ring,
            _ => Terrain.DebrisArt.Spark,
        };
        if (!ReferenceEquals(p.Art, art)) { p.Art = art; p.Sr.sprite = art; }
        p.Kind = kind;
        p.Go.SetActive(true);
        p.Start = Time.time + delay;
        p.Life = life;
        p.Size0 = s0;
        p.Size1 = s1;
        p.X = x; p.Y = y; p.Vx = vx; p.Vy = vy; p.Z = z;
        p.Tf.rotation = FxMath.RotZ(float.IsNaN(rot) ? (kind == PuffKind.RingFloor ? 0f : FxMath.Range(0f, 360f)) : rot);
        if (delay > 0f) p.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        Puffs.Add(p);
    }

    private static void TickPuffs(float now)
    {
        for (int i = Puffs.Count - 1; i >= 0; i--)
        {
            var p = Puffs[i];
            if (!p.Go) { Puffs.RemoveAt(i); continue; }
            float t = now - p.Start;
            if (t < 0f) continue;
            float f = t / p.Life;
            if (f >= 1f)
            {
                p.Go.SetActive(false);
                Pool.Push(p);
                Puffs.RemoveAt(i);
                continue;
            }
            float drag = p.Kind == PuffKind.Spark ? 1f - f * 0.35f : p.Kind is PuffKind.Smoke or PuffKind.Steam ? 1f - f * 0.6f : 1f;
            float e = 1f - (1f - f) * (1f - f);
            float s = p.Size0 + (p.Size1 - p.Size0) * (p.Kind is PuffKind.RingCross or PuffKind.RingFloor or PuffKind.Smoke or PuffKind.Glow ? e : f);
            p.Tf.position = FxMath.V3(p.X + p.Vx * t * drag, p.Y + p.Vy * t * drag, p.Z);
            switch (p.Kind)
            {
                case PuffKind.Streak:
                    p.Tf.localScale = FxMath.V3(s, 0.7f, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.9f, 0.88f, (1f - f) * 0.9f);
                    break;
                case PuffKind.Flash:
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.85f, 0.82f, FxMath.Clamp01((1f - f) * 1.6f));
                    break;
                case PuffKind.Glow:
                    p.Tf.localScale = FxMath.V3(s, s * 0.8f, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.4f + 0.2f * (1f - f), 0.35f, (1f - f) * (1f - f) * 0.45f);
                    break;
                case PuffKind.RingCross: // 光線に直交する輪: 光線の向きに潰れた楕円
                    p.Tf.localScale = FxMath.V3(s * 0.32f, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.3f + 0.5f * (1f - f), 0.28f + 0.4f * (1f - f), (1f - f) * (1f - f));
                    break;
                case PuffKind.RingFloor: // 床に広がる衝撃波 (奥行きで潰れた楕円)
                    p.Tf.localScale = FxMath.V3(s, s * Squash, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.3f + 0.5f * (1f - f), 0.28f + 0.4f * (1f - f), (1f - f) * 0.95f);
                    break;
                case PuffKind.Smoke:
                {
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    float w = FxMath.Clamp01(1f - f * 4f);
                    p.Sr.color = FxMath.Rgba(0.4f + 0.6f * w, 0.35f + 0.1f * w, 0.36f, (1f - f) * 0.7f);
                    break;
                }
                case PuffKind.Steam: // 湯気: 白く薄く広がって消える
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(0.92f, 0.92f, 0.95f, (1f - f) * 0.45f);
                    break;
                case PuffKind.Ember: // 火の粉: 明るい赤 → 暗い赤へ・ちらつく
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.5f - 0.4f * f, 0.35f - 0.3f * f, (1f - f) * (0.6f + 0.4f * FxMath.Sin(now * 40f + p.X * 7f)));
                    break;
                case PuffKind.Gather:
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.55f + 0.35f * f, 0.5f + 0.3f * f, 0.3f + 0.7f * f);
                    break;
                default:
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.7f - 0.4f * f, 0.55f - 0.4f * f, 1f - f * f);
                    break;
            }
        }
    }

    // ================= 毎フレーム =================

    private static bool _rewound;

    internal static void Tick()
    {
        if (Shots.Count == 0 && Beams.Count == 0 && Puffs.Count == 0) return;
        if (RewindPlayback.Active) { _rewound = true; return; }
        if (_rewound) { _rewound = false; Drop(); return; }
        float now = Time.time;
        if (Shots.Count > 0 || Beams.Count > 0)
        {
            if (MeetingHud.Instance) { Drop(); return; }
            for (int i = Shots.Count - 1; i >= 0; i--)
            {
                var s = Shots[i];
                if (!s.Root || !TickShot(s, now)) { if (s.Root) Object.Destroy(s.Root); Shots.RemoveAt(i); }
            }
            for (int i = Beams.Count - 1; i >= 0; i--)
            {
                var b = Beams[i];
                if (!b.Root || !TickBeam(b, now)) { if (b.Root) Object.Destroy(b.Root); Beams.RemoveAt(i); }
            }
        }
        if (Puffs.Count > 0) TickPuffs(now);
    }

    private static void Drop()
    {
        foreach (var s in Shots) if (s.Root) Object.Destroy(s.Root);
        Shots.Clear();
        foreach (var b in Beams) if (b.Root) Object.Destroy(b.Root);
        Beams.Clear();
        foreach (var p in Puffs) if (p.Go) { p.Go.SetActive(false); Pool.Push(p); }
        Puffs.Clear();
    }
}
