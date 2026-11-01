using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// ノヴァの見た目: 溜め (体の前に光の球が育ち、まわりから光の粒が吸い込まれる・撃つ向きに細い線) → 光線。
// 光線は円柱の陰影の帯を 4 枚重ねる (暗い下地 → にじみ → 本体 → 白い芯) + 帯の上を流れる光の筋 + 銃口と壁の閃光。
// 壁を焼く・床の水を湯気にする・燃える物に火を付けるのは、ホストが起こす地形の焼けと熱が全員の画面で出す
internal static class NovaFx
{
    private const float OrbPpu = 256f / 0.9f;       // 球のコマ (256 画素) = 0.9 単位
    private const float BeamPpu = 512f;              // 帯の絵: 幅 1 単位・高さ 0.25 単位 (縦は size で伸ばす)
    private const float StreakPpu = 128f;
    private const float Ahead = 0.42f, OrbLift = 0.32f; // 球の位置: 体の芯から撃つ向きへ・上へ
    private const float BeamLife = 0.6f;
    private const float StreakSpeed = 26f;
    private const int MaxPuffs = 140;

    private static Sprite[] _orb;
    private static Sprite _beam, _streak, _flash;
    private static bool _searched;

    private static bool Art()
    {
        if (_beam) return true;
        if (_searched) return false;
        _searched = true;
        _orb = ButtonIcons.Frames("nova_orb", 4, OrbPpu);
        _beam = ButtonIcons.Load("nova_beam", BeamPpu, FxMath.V2(0f, 0.5f)); // 左端が原点 (撃った所から先へ伸ばす)
        _streak = ButtonIcons.Load("nova_streak", StreakPpu, FxMath.V2(0.5f, 0.5f));
        _flash = ButtonIcons.Load("rl_flash", 200f, FxMath.V2(0.5f, 0.5f));
        return _orb != null && _beam && _streak;
    }

    private static SpriteRenderer Make(string name, Sprite sp, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // ================= 溜め =================

    private sealed class Charge
    {
        public GameObject Root;
        public PlayerControl P;
        public SpriteRenderer Orb, Light, Aim;
        public Transform OrbTf, LightTf, AimTf;
        public float T0, Dur, Dx, Dy, Size, AimLen, NextSpark;
        public int Frame = -1, Check;
        public bool Shown = true;
    }

    // 撃つ人・向き (度)・溜めの秒・光線の太さ・狙いの線の長さ
    internal static void BeginCharge(PlayerControl p, float angle, float dur, float size, float aimLen)
    {
        if (!p || RewindPlayback.Active || !Art()) return;
        var root = new GameObject("MrpNovaCharge");
        FxRoot.Attach(root);
        float r = angle * (System.MathF.PI / 180f);
        var c = new Charge { Root = root, P = p, T0 = Time.time, Dur = dur, Dx = FxMath.Cos(r), Dy = FxMath.Sin(r), Size = size, AimLen = aimLen };
        c.Light = Make("Light", Terrain.DebrisArt.Puff, root.transform);
        c.LightTf = c.Light.transform;
        c.Aim = Make("Aim", _beam, root.transform);
        c.AimTf = c.Aim.transform;
        c.AimTf.rotation = FxMath.RotZ(angle);
        c.Orb = Make("Orb", _orb[0], root.transform);
        c.OrbTf = c.Orb.transform;
        Charges.Add(c);
    }

    private static readonly List<Charge> Charges = new();

    private static bool TickCharge(Charge c, float now)
    {
        float t = now - c.T0;
        if (t >= c.Dur || !c.P) return false;
        // 生死と見え方は数フレームに 1 回: 死んだら消す・撃つ人が自分の視界に無い時は出さない (壁越しに居場所が漏れない)
        if (++c.Check % 6 == 1)
        {
            if (!PortKit.Alive(c.P)) return false;
            bool vis = c.P.Visible;
            if (vis != c.Shown) { c.Shown = vis; c.Orb.enabled = c.Light.enabled = c.Aim.enabled = vis; }
        }
        if (!c.Shown) return true;
        float u = c.Dur > 0f ? t / c.Dur : 1f;
        var pos = c.P.transform.position;
        float bx = pos.x, by = pos.y - 0.36f; // 足元 (GetTruePosition と同じ所)
        float ox = bx + c.Dx * Ahead, oy = by + c.Dy * Ahead * 0.8f + OrbLift;
        float z = (by - 0.6f) / 1000f;          // 撃つ人より少し手前
        c.OrbTf.position = FxMath.V3(ox, oy, z);
        float s = (0.5f + 0.8f * u * u) * (0.75f + 0.25f * c.Size) * (1f + 0.08f * FxMath.Sin(now * 40f));
        c.OrbTf.localScale = FxMath.V3(s, s, 1f);
        int f = (int)(now * 14f) & 3;
        if (f != c.Frame) { c.Frame = f; c.Orb.sprite = _orb[f]; }
        // 床の光だまり (黄に寄せて薄く・明るい床で濁らない)
        float ld = 0.8f + 1.4f * u;
        c.LightTf.position = FxMath.V3(ox, by + 0.05f, by / 1000f + 0.0006f);
        c.LightTf.localScale = FxMath.V3(ld, ld * 0.6f, 1f);
        c.Light.color = FxMath.Rgba(1f, 0.88f, 0.5f, 0.06f + 0.16f * u);
        // 狙いの線: 撃つ所から先へ細く・ちらつく (撃たれる側が逃げる手掛かり)
        c.AimTf.position = FxMath.V3(ox, oy, z + 0.0002f);
        c.AimTf.localScale = FxMath.V3(c.AimLen, 0.2f + 0.2f * u, 1f);
        c.Aim.color = FxMath.Rgba(1f, 0.8f, 0.35f, (0.3f + 0.35f * u) * (0.75f + 0.25f * FxMath.Sin(now * 31f)));
        // 光の粒が球へ吸い込まれる (溜めの終わりほど多い)
        while (c.NextSpark <= now)
        {
            c.NextSpark = (c.NextSpark <= 0f ? now : c.NextSpark) + (0.05f - 0.035f * u);
            float a = FxMath.Range(0f, 2f * System.MathF.PI);
            float rad = FxMath.Range(0.7f, 1.2f);
            float life = 0.22f;
            float sx = ox + FxMath.Cos(a) * rad, sy = oy + FxMath.Sin(a) * rad * 0.8f;
            SpawnPuff(sx, sy, (ox - sx) / life, (oy - sy) / life, 0.06f, 0.12f, life, z - 0.0001f, PuffKind.Gather, a * (180f / System.MathF.PI));
        }
        return true;
    }

    // ================= 光線 =================

    private sealed class Beam
    {
        public GameObject Root;
        public SpriteRenderer[] Layers;
        public Transform[] LayerTf;
        public float T0, X, Y, Dx, Dy, Len, Size, Z, NextStreak, Angle;
    }

    // 帯 4 枚: 太さ (size に掛ける)・色
    private static readonly float[] LayerW = { 2.6f, 1.7f, 1.0f, 0.35f };
    private static readonly Color[] LayerC =
    {
        new(0.55f, 0.18f, 0.05f, 0.35f), // 暗い下地 (明るい床で沈まないように)
        new(1f, 0.62f, 0.15f, 0.55f),    // にじみ
        new(1f, 0.9f, 0.5f, 0.95f),      // 本体
        new(1f, 1f, 0.96f, 1f),          // 白い芯
    };

    private static readonly List<Beam> Beams = new();

    internal static void Fire(Vector2 from, float angle, float length, float size, bool hitWall)
    {
        if (RewindPlayback.Active || !Art()) return;
        float r = angle * (System.MathF.PI / 180f);
        float dx = FxMath.Cos(r), dy = FxMath.Sin(r);
        // 見た目の光線は溜めの球の所から出る (当たりは足元の線で、球の分だけ先から)
        var b = new Beam { T0 = Time.time, X = from.x + dx * Ahead, Y = from.y + dy * Ahead * 0.8f + OrbLift, Dx = dx, Dy = dy,
            Len = length > Ahead ? length - Ahead : 0.05f, Size = size, Angle = angle };
        length = b.Len;
        b.Root = new GameObject("MrpNovaBeam");
        FxRoot.Attach(b.Root);
        // 光線は人より手前 (撃った人の高さ = 体の上の方を通る)
        b.Z = (from.y - 2f) / 1000f;
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
        Beams.Add(b);
        // 銃口と行き止まりの閃光・火花
        float ex = b.X + b.Dx * length, ey = b.Y + b.Dy * length;
        if (_flash) SpawnPuff(b.X + b.Dx * 0.25f, b.Y + b.Dy * 0.25f, 0f, 0f, 0.5f * size + 0.3f, 0.9f * size + 0.5f, 0.2f, b.Z - 0.0006f, PuffKind.Flash, angle);
        SpawnPuff(b.X, b.Y, 0f, 0f, 1.2f, 2.4f, 0.35f, b.Z + 0.0003f, PuffKind.Glow);
        if (hitWall)
        {
            if (_flash) SpawnPuff(ex, ey, 0f, 0f, 0.6f * size + 0.4f, 1.1f * size + 0.6f, 0.25f, b.Z - 0.0006f, PuffKind.Flash);
            SpawnPuff(ex, ey, 0f, 0f, 1f, 2.2f, 0.45f, b.Z + 0.0003f, PuffKind.Glow);
            for (int i = 0; i < 16; i++)
            {
                // 壁から跳ね返る火花 (来た向きの逆を中心に扇)
                float a = angle + 180f + FxMath.Range(-75f, 75f);
                float ar = a * (System.MathF.PI / 180f), v = FxMath.Range(3f, 7f);
                SpawnPuff(ex, ey, FxMath.Cos(ar) * v, FxMath.Sin(ar) * v, 0.16f, 0.07f, FxMath.Range(0.25f, 0.5f), b.Z - 0.0004f, PuffKind.Spark, a);
            }
        }
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (!lp || !hud || !hud.PlayerCam) return;
        var me = lp.transform.position;
        float ddx = me.x - b.X, ddy = me.y - b.Y;
        if (ddx * ddx + ddy * ddy < 100f) hud.PlayerCam.ShakeScreen(0.18f, 1.2f);
    }

    private static bool TickBeam(Beam b, float now)
    {
        float t = now - b.T0;
        if (t >= BeamLife) return false;
        // 太さ: 出た瞬間に 1.35 倍 → 1 へ落ち着く → 最後の 0.25 秒で細って消える
        float k = t < 0.08f ? 1.35f - 0.35f * (t / 0.08f) : 1f;
        float end = t > BeamLife - 0.25f ? (BeamLife - t) / 0.25f : 1f;
        float flick = 1f + 0.06f * FxMath.Sin(now * 70f);
        for (int i = 0; i < 4; i++)
        {
            float w = b.Size * LayerW[i] * k * end * flick * 4f; // 帯の絵の高さ 0.25 単位 → 1 単位に
            b.LayerTf[i].localScale = FxMath.V3(b.Len, w, 1f);
            var c = LayerC[i];
            b.Layers[i].color = FxMath.Rgba(c.r, c.g, c.b, c.a * (end < 1f ? end : 1f));
        }
        // 帯の上を光の筋が流れる (撃った所から先へ)
        while (b.NextStreak <= now && end > 0.3f)
        {
            b.NextStreak = (b.NextStreak <= 0f ? now : b.NextStreak) + 0.012f;
            float d0 = FxMath.Range(0f, b.Len * 0.7f);
            float side = FxMath.Range(-0.3f, 0.3f) * b.Size;
            float sx = b.X + b.Dx * d0 - b.Dy * side, sy = b.Y + b.Dy * d0 + b.Dx * side;
            float life = FxMath.Range(0.12f, 0.22f);
            SpawnPuff(sx, sy, b.Dx * StreakSpeed, b.Dy * StreakSpeed, 0.5f, 0.9f, life, b.Z - 0.0005f, PuffKind.Streak, b.Angle);
        }
        return true;
    }

    // ================= 粒 =================

    private enum PuffKind : byte { Gather, Streak, Spark, Flash, Glow }

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

    private static void SpawnPuff(float x, float y, float vx, float vy, float s0, float s1, float life, float z, PuffKind kind, float rot = float.NaN)
    {
        if (Puffs.Count >= MaxPuffs) return;
        var p = Pool.Count > 0 ? Pool.Pop() : null;
        if (p == null || !p.Go)
        {
            var go = new GameObject("MrpNovaPuff");
            FxRoot.Attach(go);
            p = new Puff { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        var art = kind == PuffKind.Flash ? _flash : kind == PuffKind.Streak ? _streak : kind == PuffKind.Glow ? Terrain.DebrisArt.Puff : Terrain.DebrisArt.Spark;
        if (!ReferenceEquals(p.Art, art)) { p.Art = art; p.Sr.sprite = art; }
        p.Kind = kind;
        p.Go.SetActive(true);
        p.Start = Time.time;
        p.Life = life;
        p.Size0 = s0;
        p.Size1 = s1;
        p.X = x; p.Y = y; p.Vx = vx; p.Vy = vy; p.Z = z;
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
            float drag = p.Kind == PuffKind.Spark ? 1f - f * 0.35f : 1f;
            float s = p.Size0 + (p.Size1 - p.Size0) * f;
            p.Tf.position = FxMath.V3(p.X + p.Vx * t * drag, p.Y + p.Vy * t * drag, p.Z);
            switch (p.Kind)
            {
                case PuffKind.Streak:
                    p.Tf.localScale = FxMath.V3(s, 0.6f, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 1f, 0.9f, (1f - f) * 0.9f);
                    break;
                case PuffKind.Flash:
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.95f, 0.75f, FxMath.Clamp01((1f - f) * 1.6f));
                    break;
                case PuffKind.Glow:
                    p.Tf.localScale = FxMath.V3(s, s * 0.8f, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.85f - 0.3f * f, 0.45f - 0.25f * f, (1f - f) * (1f - f) * 0.45f);
                    break;
                case PuffKind.Gather: // 吸い込まれる粒: 近づくほど明るく
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.92f, 0.6f, 0.3f + 0.7f * f);
                    break;
                default:
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.9f - 0.4f * f, 0.5f - 0.35f * f, 1f - f * f);
                    break;
            }
        }
    }

    // ================= 毎フレーム =================

    private static bool _rewound;

    internal static void Tick()
    {
        if (Charges.Count == 0 && Beams.Count == 0 && Puffs.Count == 0) return;
        if (RewindPlayback.Active) { _rewound = true; return; }
        if (_rewound) { _rewound = false; Drop(); return; }
        float now = Time.time;
        if (Charges.Count > 0 || Beams.Count > 0)
        {
            if (MeetingHud.Instance) { Drop(); return; }
            for (int i = Charges.Count - 1; i >= 0; i--)
            {
                var c = Charges[i];
                if (!c.Root || !TickCharge(c, now)) { if (c.Root) Object.Destroy(c.Root); Charges.RemoveAt(i); }
            }
            for (int i = Beams.Count - 1; i >= 0; i--)
            {
                var b = Beams[i];
                if (!b.Root || !TickBeam(b, now)) { if (b.Root) Object.Destroy(b.Root); Beams.RemoveAt(i); }
            }
        }
        if (Puffs.Count > 0) TickPuffs(now);
    }

    // 溜めの時間が来た時 (光線に替わる) に消す
    internal static void CancelCharge(PlayerControl p)
    {
        for (int i = Charges.Count - 1; i >= 0; i--)
            if (Charges[i].P == p) { if (Charges[i].Root) Object.Destroy(Charges[i].Root); Charges.RemoveAt(i); }
    }

    private static void Drop()
    {
        foreach (var c in Charges) if (c.Root) Object.Destroy(c.Root);
        Charges.Clear();
        foreach (var b in Beams) if (b.Root) Object.Destroy(b.Root);
        Beams.Clear();
        foreach (var p in Puffs) if (p.Go) { p.Go.SetActive(false); Pool.Push(p); }
        Puffs.Clear();
    }
}
