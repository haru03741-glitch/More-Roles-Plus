using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// ノヴァの見た目 (腕のプラズマキャノンの光線)。光・熱・煙・火花といった実際に起きる現象だけで見せる:
//  構え   = キャノンを下げた位置から狙いの方へ持ち上げる。握りは体の脇の手の位置で、狙いが変わるとそこを軸に回る。細い照準の光
//  溜め   = 筒の光る所が灯っていき、砲口に熱い点が育つ。筒の上の空気が熱で揺らぎ、終わり近くは砲口で小さく放電し溶けた粒が垂れる
//  光線   = 光をよく吸う暗い太い柱 + 長さ方向に流れる明るい筋 (色の組は設定で選ぶ) (左上からの光で円柱の明暗)・真下の床に影。砲口に短い閃光。通り道の空気が焼けて薄い煙が昇り、真下の床に火花が落ちる
//  出た後 = 光線は細って消え、床に焼けた線が残って冷める。砲口は赤熱して湯気を吹き、キャノンは下げて消える
//  着弾   = 当たった所が白熱して冷めていき、火花が放物線を描いて散り、煙が立つ
// 壁を壊す・床の水を湯気にする・燃える物に火を付けるのは、ホストが起こす地形の爆発と熱が全員の画面で出す
internal static partial class NovaFx
{
    // ---- 数値 ----
    private const float CannonPpu = 200f;            // 3D の形を描いた時と同じ (長さ約 1.6 単位)
    private const float GlowPpu = CannonPpu / 2f;    // 光の絵は半分の解像度
    private const float Squash = 0.58f;              // 床の奥行きが画面で縮む倍率 (形を描いた時と同じ)
    private const float MuzzleAhead = 0.635f;        // 形の真ん中から砲口まで (床の上の長さ)
    private const float ApexGap = 0.28f;             // 砲口から光線の頂点 (溜めの玉) まで。爪の先より少し前
    // 握り (腕の留め具) は形の真ん中から筒の後ろへ 0.53・軸の下 0.15 (画面)。そこを体の脇の手に合わせる
    private const float GripBack = 0.53f, GripLift = 0.15f;
    private const float HandOut = 0.4f, HandSide = 0.2f, HandY = -0.24f; // 手: 体の芯から狙いの方へ・上下を狙う時は利き手の側へ
    private const float Deploy = 0.3f, Retract = 0.35f, Hold = 1.3f; // Hold = 撃った後に赤熱が冷めて湯気を吹く間
    private const float RaiseFrom = 55f;             // 構える前は狙いからこれだけ下げた向き
    private const float SpringW = 16f, SpringZeta = 0.62f; // 砲身の重さ: 狙いへ 0.2 秒ほどで追い付き、数度だけ行き過ぎる
    private const float BobRate = 11f, BobLift = 0.022f, BobSway = 1.5f; // 歩く揺れ: 歩みの速さ (rad/s)・上下の幅・振れの度
    private const float VentOpenTime = 0.3f;         // 排熱口の蓋が開く/閉じる時間
    private const float RecoilBack = 0.18f, RecoilTime = 0.22f;
    private const float Ignite = 0.05f;              // 出始め: 芯が砲口から壁まで伸びる時間
    private const float Settle = 0.12f;              // 出始めの太さの山から落ち着くまで
    private const float Thread = 0.25f;              // 終わり: 細って消える時間 (光線の終わりの内)
    private const float ScarLife = 2.5f;             // 床に残る焼けた線が冷める時間
    private const float BeamPpu = 512f;
    private const float StreakPpu = 128f;
    private const float BoltPpu = 256f / 0.7f;       // 放電 1 本 = 長さ 0.7 単位
    private const float BeamLife = 1.2f;
    private const float Gravity = 7f;                // 火花が床へ落ちる速さ (画面の下向き)
    private const int MaxPuffs = 260;

    // 光線の層の太さ (光線の太さの設定 1 あたりの単位)。当たりの太さ (1 + 体の幅) と本体のにじみが合う
    private static readonly float[] LayerW = { 1.5f, 1.0f, 0.55f, 0.22f };
    private static readonly Color[] LayerC =
    {
        new(0.05f, 0.01f, 0.16f, 0.6f),  // 暗い下地 (色は UsePalette が色の組から入れる)
        new(0.2f, 0.13f, 0.5f, 0.6f),    // 藍のにじみ
        new(0.33f, 0.26f, 0.95f, 0.9f),  // 本体
        new(0.95f, 0.84f, 1f, 1f),       // 芯
    };

    private static Sprite[] _cannon, _glow, _bolt;
    private static Sprite _beam, _streak, _cone;
    private const float ConeOpen = 0.6f, ConeExtra = 1.4f; // 円錐が開ききるまで・満タンの光線が長く照らす分
    // 円錐の層: 外の暗い地 → にじみ → 明るい本体 (広がりの割合・色)
    private static readonly float[] ConeW = { 1.1f, 0.9f, 0.55f };
    private static readonly Color[] ConeC =
    {
        new(0.05f, 0.01f, 0.16f, 0.6f),
        new(0.18f, 0.12f, 0.45f, 0.7f),
        new(0.45f, 0.36f, 1f, 0.9f),
    };
    private static bool _searched;

    private static bool Art()
    {
        if (_beam) return true;
        if (_searched) return false;
        _searched = true;
        _cannon = ButtonIcons.Frames("nova_cannon3d", 32, CannonPpu, 4);
        _glow = ButtonIcons.Frames("nova_cannon_glow", 32, GlowPpu, 4);
        _glowSets[0] = _glow;
        _bolt = ButtonIcons.Frames("nova_bolt", 6, BoltPpu, 6);
        _beam = ButtonIcons.Load("nova_beam", BeamPpu, FxMath.V2(0f, 0.5f)); // 左端が原点 (撃った所から先へ伸ばす)
        _streak = ButtonIcons.Load("nova_streak", StreakPpu, FxMath.V2(0.5f, 0.5f));
        _cone = ButtonIcons.Load("nova_cone", 512f, FxMath.V2(0f, 0.5f));
        return _cannon != null && _glow != null && _bolt != null && _beam && _streak && PartsArt() && TankArt();
    }

    private static SpriteRenderer Make(string name, Sprite sp, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    private const float Deg = System.MathF.PI / 180f;

    // 画面の上の向き (度) → 床の上の向き (度)。画面の縦は床の奥行きが縮んだ物なので伸ばして戻す
    private static float YawOf(float screenDeg)
    {
        float r = screenDeg * Deg;
        return FxMath.Atan2(FxMath.Sin(r) / Squash, FxMath.Cos(r)) / Deg;
    }

    private static int FrameOf(float yaw)
    {
        int k = (int)System.MathF.Round(yaw / 11.25f);
        return ((k % 32) + 32) % 32;
    }

    // -180..180 へ畳む
    private static float Wrap(float d)
    {
        d %= 360f;
        return d > 180f ? d - 360f : d < -180f ? d + 360f : d;
    }

    // 床の上の向き (度) → 画面の上の向き (度)
    private static float ScreenOf(float yaw)
    {
        float r = yaw * Deg;
        return FxMath.Atan2(FxMath.Sin(r) * Squash, FxMath.Cos(r)) / Deg;
    }

    private static float EaseOut(float u) { u = 1f - FxMath.Clamp01(u); return 1f - u * u * u; }

    // ================= キャノン (構え・溜め・冷め) =================

    private sealed class Shot
    {
        public GameObject Root;
        public PlayerControl P;
        public Rig Rig;
        public Tank Tank;
        public SpriteRenderer Cannon, Glow, Core, Halo, Light, Aim, Dot, Heat, Bolt;
        public Transform CannonTf, GlowTf, CoreTf, HaloTf, LightTf, AimTf, DotTf, BoltTf;
        public float T0, C0, Dur, Limit, Open, OpenFire, LevelFire, Ax, Ay, Angle, Size, AimLen, AimYaw, FiredAt = -1f, EndAt = -1f;
        public float NextHaze, NextBolt, BoltOff, NextDrip, NextSteam;
        public float Mx, My, Px, Py, Z, Dx, Dy;   // 今の砲口 (画面)・光線の頂点 (画面)・奥行き・今のキャノンの画面の向き
        public float Side = 1f;           // キャノンを持つ手 (右 = 1・左 = -1)
        public float Vx = 1f, Vy, VisYaw, VisVel, LastNow, Bob, BobPhase, Rot; // 見えている向き (狙いに重さで遅れて付いてくる)・歩く揺れ・絵を回した角度
        public int Frame = -1, Check, KeyCannon = -1, KeyGlow = -1;
        public bool Shown = true, Stance, Moving;
    }

    private static readonly List<Shot> Shots = new();

    private static Shot Find(PlayerControl p, bool stance)
    {
        foreach (var s in Shots) if (s.P == p && s.FiredAt < 0f && s.EndAt < 0f && s.Stance == stance) return s;
        return null;
    }

    private static Shot NewShot(PlayerControl p, float angle)
    {
        UsePalette();
        var root = new GameObject("MrpNovaShot");
        FxRoot.Attach(root);
        var s = new Shot { Root = root, P = p, T0 = Time.time };
        s.Side = p.cosmetics && p.cosmetics.FlipX ? -1f : 1f;
        SetAngle(s, angle);
        s.VisYaw = s.AimYaw;
        s.Vx = s.Ax; s.Vy = s.Ay;
        if (s.Ax > 0.25f) s.Side = 1f; else if (s.Ax < -0.25f) s.Side = -1f;
        s.LastNow = s.T0;
        var rt = root.transform;
        s.Light = Make("Light", Terrain.DebrisArt.SoftGlow, rt); s.LightTf = s.Light.transform;
        s.Aim = Make("Aim", _beam, rt); s.AimTf = s.Aim.transform;
        s.Dot = Make("Dot", Terrain.DebrisArt.SoftGlow, rt); s.DotTf = s.Dot.transform;
        s.Rig = MakeRig(rt);
        s.Cannon = Make("Cannon", _cannon[0], s.Rig.Tf); s.CannonTf = s.Cannon.transform;
        s.Glow = Make("Glow", _glow[0], s.Rig.Tf); s.GlowTf = s.Glow.transform;
        s.GlowTf.localPosition = FxMath.V3(0f, 0f, -0.00005f);
        s.Halo = Make("Halo", Terrain.DebrisArt.SoftGlow, rt); s.HaloTf = s.Halo.transform;
        s.Core = Make("Core", Terrain.DebrisArt.SoftGlow, rt); s.CoreTf = s.Core.transform;
        s.Bolt = Make("Bolt", _bolt[0], rt); s.BoltTf = s.Bolt.transform;
        s.Heat = Make("Heat", Terrain.DebrisArt.SoftGlow, rt);
        s.Core.enabled = s.Halo.enabled = s.Bolt.enabled = s.Heat.enabled = s.Light.enabled = false;
        s.Cannon.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        Shots.Add(s);
        return s;
    }

    private static void SetAngle(Shot s, float angle)
    {
        s.Angle = angle;
        s.Ax = FxMath.Cos(angle * Deg); s.Ay = FxMath.Sin(angle * Deg);
        s.AimYaw = YawOf(angle);
        if (s.AimTf) s.AimTf.rotation = FxMath.RotZ(angle);
    }

    // 自分の端末だけ: 構えて狙っている (撃つまで電文は出ない)。毎フレーム今の狙いを渡す
    internal static void Stance(PlayerControl p, float angle, float aimLen)
    {
        if (!p || RewindPlayback.Active || !Art()) return;
        var s = Find(p, true) ?? NewShot(p, angle);
        s.Stance = true;
        if (angle != s.Angle) SetAngle(s, angle);
        s.AimLen = aimLen;
    }

    // 構えをやめた: 下げて消す
    internal static void EndStance(PlayerControl p)
    {
        var s = Find(p, true);
        if (s != null) s.EndAt = Time.time;
    }

    // 撃つ人・向き (画面の度)・溜めの秒・光線の太さ・狙いの線の長さ。構えていればそのキャノンから溜める
    // limit = これより長く撃たれなければ溜めを捨てる (会議・死・抜けた)
    internal static void BeginCharge(PlayerControl p, float angle, float dur, float size, float aimLen, float limit)
    {
        if (!p || RewindPlayback.Active || !Art()) return;
        CancelCharge(p);
        var s = Find(p, true);
        if (s == null) s = NewShot(p, angle);
        else if (angle != s.Angle) SetAngle(s, angle);
        s.Stance = false;
        s.C0 = Time.time;
        s.Dur = dur;
        s.Limit = limit;
        s.Size = size;
        s.AimLen = aimLen;
    }

    // 溜めている間の狙いの向き (自分は毎フレーム・他の人は時々届く)
    internal static void ChargeAim(PlayerControl p, float angle, float aimLen)
    {
        if (!p) return;
        foreach (var s in Shots)
        {
            if (s.P != p || s.Stance || s.FiredAt >= 0f || s.EndAt >= 0f) continue;
            if (angle != s.Angle) SetAngle(s, angle);
            s.AimLen = aimLen;
            return;
        }
    }

    private static bool TickShot(Shot s, float now)
    {
        if (!s.P) return false;
        float td = now - s.T0;
        bool firing = s.FiredAt >= 0f, ending = s.EndAt >= 0f;
        float tf = firing ? now - s.FiredAt : 0f;
        float tc = s.Stance ? 0f : now - s.C0;
        float end = BeamLife + Hold;
        if (firing && tf > end + Retract) return false;
        if (ending && now - s.EndAt > Retract) return false;
        if (!firing && !s.Stance && !ending && tc > s.Limit) return false; // 撃たれずに終わった (会議・死)
        // 生死・見え方・体の向きは数フレームに 1 回: 撃つ人が自分の視界に無い時は出さない (壁越しに居場所が漏れない)
        if (++s.Check % 6 == 1)
        {
            if (!firing && !PortKit.Alive(s.P)) return false;
            bool vis = s.P.Visible;
            if (vis != s.Shown) { s.Shown = vis; s.Rig.Go.SetActive(vis); }
            // 体を狙いの側へ向ける (キャノンを持つ手が体の前に来る)。構えて歩いている間は歩く向きのまま
            s.Moving = s.Stance && !Still(s.P);
            if ((s.Vx > 0.25f || s.Vx < -0.25f) && !s.Moving)
            {
                var cos = s.P.cosmetics;
                bool left = s.Vx < 0f;
                if (cos && cos.FlipX != left) cos.SetFlipX(left);
            }
        }
        // 見えている向き: 重い砲身は狙いへ少し遅れて回り、わずかに行き過ぎて止まる (減衰の強いばね)。撃つ時は狙いと一致させる
        float dt = now - s.LastNow;
        s.LastNow = now;
        if (dt > 0.05f) dt = 0.05f;
        if (firing) { s.VisYaw = s.AimYaw; s.VisVel = 0f; }
        else
        {
            float h = dt * 0.5f;
            for (int i = 0; i < 2; i++)
            {
                float err = Wrap(s.AimYaw - s.VisYaw);
                s.VisVel += (SpringW * SpringW * err - 2f * SpringZeta * SpringW * s.VisVel) * h;
                s.VisYaw += s.VisVel * h;
            }
        }
        // 手の位置・持つ手・体の向きは見えている砲身の向きに合わせる (狙いが先に回っても、砲身が回り込むまで持ち替えない)
        float vs = ScreenOf(s.VisYaw) * Deg;
        s.Vx = FxMath.Cos(vs); s.Vy = FxMath.Sin(vs);
        if (s.Vx > 0.25f) s.Side = 1f; else if (s.Vx < -0.25f) s.Side = -1f;
        // 構えて歩くと、歩みに合わせて上下に揺れ、わずかに振れる。止まると落ち着く
        s.Bob += ((s.Moving ? 1f : 0f) - s.Bob) * FxMath.Clamp01(dt * 6f);
        if (s.Bob > 0.01f) s.BobPhase += dt * BobRate;
        // 向き: 構える時は下げた向きから持ち上げる・しまう時は下げながら消える
        float yaw = s.VisYaw + s.Bob * BobSway * FxMath.Sin(s.BobPhase * 0.5f), lift = 0f, alpha = 1f;
        if (s.Bob > 0.01f) lift += s.Bob * BobLift * FxMath.Abs(FxMath.Sin(s.BobPhase));
        if (td < Deploy)
        {
            float e = EaseOut(td / Deploy);
            yaw -= s.Side * RaiseFrom * (1f - e);
            lift = -0.12f * (1f - e);
            alpha = FxMath.Clamp01(td / 0.12f);
        }
        float rt = ending ? now - s.EndAt : firing && tf > end ? tf - end : -1f;
        if (rt >= 0f)
        {
            float u = FxMath.Clamp01(rt / Retract);
            yaw -= s.Side * RaiseFrom * u * u;
            lift = -0.12f * u;
            alpha = 1f - u;
        }
        // 絵は 11.25° 刻みの 32 コマ。いちばん近いコマを選び (境目で行き来しないよう少し粘る)、残りの角度は画面の上で絵を回して埋める
        int f = s.Frame;
        if (f < 0 || FxMath.Abs(Wrap(yaw - f * 11.25f)) > 6.6f) f = FrameOf(yaw);
        if (f != s.Frame) { s.Frame = f; s.Cannon.sprite = _cannon[f]; s.Glow.sprite = _glow[f]; }
        float scr = ScreenOf(yaw);
        float rot = (int)System.MathF.Round(Wrap(scr - ScreenOf(f * 11.25f)) * 4f) * 0.25f;
        if (rot != s.Rot)
        {
            s.Rot = rot;
            s.Rig.Tf.rotation = FxMath.RotZ(rot);
        }
        // 今の画面の向き (握りから砲口へ) と、床の上の長さが画面で縮む倍率
        float yr = yaw * Deg;
        float cyaw = FxMath.Cos(yr), syaw = FxMath.Sin(yr) * Squash;
        float dl = FxMath.Sqrt(cyaw * cyaw + syaw * syaw);
        s.Dx = cyaw / dl; s.Dy = syaw / dl;
        // 反動 (撃った瞬間に握りごと後ろへ押されて戻る)・溜めの終わりほど強くなる細かい震え
        float kick = 0f, jx = 0f, jy = 0f;
        if (firing && tf < RecoilTime) { float r = 1f - tf / RecoilTime; kick = RecoilBack * r * r; }
        if (!firing && !s.Stance && td > Deploy)
        {
            float u = FxMath.Clamp01(tc / s.Dur);
            jx = FxMath.Range(-0.012f, 0.012f) * u * u; jy = FxMath.Range(-0.012f, 0.012f) * u * u;
        }
        if (s.Stance) lift += 0.006f * FxMath.Sin(now * 2.2f);
        // 手の位置 → 握り → 形の真ん中
        var pos = s.P.transform.position;
        float ax = FxMath.Abs(s.Vx);
        float hx = pos.x + s.Vx * HandOut + s.Side * HandSide * (1f - ax) - s.Dx * kick + jx;
        float hy = pos.y + HandY + s.Vy * 0.05f + lift - s.Dy * kick + jy;
        float cx = hx + s.Dx * GripBack * dl, cy = hy + s.Dy * GripBack * dl + GripLift;
        // 上へ構える時は体の後ろ、それ以外は体の前
        s.Z = pos.z + (s.Vy > 0.35f ? 0.0004f : -0.0004f);
        int kc = (int)(alpha * 64f);
        if (kc != s.KeyCannon) { s.KeyCannon = kc; s.Cannon.color = FxMath.Rgba(1f, 1f, 1f, kc / 64f); }
        s.Mx = cx + s.Dx * MuzzleAhead * dl;
        s.My = cy + s.Dy * MuzzleAhead * dl;
        s.Px = s.Mx + s.Dx * ApexGap * dl;
        s.Py = s.My + s.Dy * ApexGap * dl;
        // 光る所: 溜めで灯っていく・撃った瞬間は最大・その後は冷めて消える
        float g;
        if (s.Stance || td < Deploy) g = 0f;
        else if (!firing) g = FxMath.Clamp01(tc / (s.Dur + 0.001f)) * FxMath.Range(0.9f, 1f);
        else g = tf < BeamLife ? 1f : FxMath.Clamp01(1f - (tf - BeamLife) / Hold);
        int kg = (int)(g * alpha * 64f);
        if (kg != s.KeyGlow) { s.KeyGlow = kg; s.Glow.color = FxMath.Rgba(1f, 1f, 1f, kg / 64f); }
        // 爪の開き: 構えている間はゆっくり息をするように開閉・溜めで少しずつ開く・撃つと一気に開いて戻る・しまう時は閉じる
        float open;
        if (firing)
        {
            float peak = FxMath.Clamp01(s.OpenFire + 0.2f);
            open = tf < 0.08f ? s.OpenFire + (peak - s.OpenFire) * tf / 0.08f : 0.1f + (peak - 0.1f) * FxMath.Clamp01(1f - (tf - 0.08f) / BeamLife);
        }
        else if (s.Stance) open = 0.08f + 0.05f * FxMath.Sin((now - s.T0) * 1.7f);
        else open = 0.1f + 0.9f * EaseOut(FxMath.Clamp01(tc / (s.Dur + 0.001f)));
        open *= alpha;
        if (!firing) s.Open = open;
        // 排熱口: 光線が細り始めた頃に開いて熱を吐き、冷めたら閉じる
        float vent = 0f, heat = 0f;
        if (firing)
        {
            float t0 = BeamLife * 0.5f, t1 = end - 0.5f;
            vent = tf < t0 ? 0f : tf < t0 + VentOpenTime ? EaseOut((tf - t0) / VentOpenTime) : tf < t1 ? 1f : 1f - FxMath.Clamp01((tf - t1) / VentOpenTime);
            heat = FxMath.Clamp01(1f - (tf - t0) / (end - t0));
        }
        TickRig(s, cx, cy, now, open, vent, heat, g, alpha, s.Shown && !firing && td > Deploy);
        // 溜まり具合の筒 (撃ったら 0.4 秒で薄れて消える)
        if (!s.Stance || s.Tank != null)
        {
            float lv = firing ? s.LevelFire : FxMath.Clamp01(tc / (s.Dur + 0.001f));
            if (!firing) s.LevelFire = lv;
            bool tankOn = s.Shown && !s.Stance && !ending && (!firing || tf < 0.4f);
            TickTank(s, pos, now, lv, tankOn, firing ? 1f - tf / 0.4f : FxMath.Clamp01(tc / 0.15f));
        }
        if (firing) TickCooling(s, now, tf, cx, cy, dl);
        else TickAim(s, now, td, tc, alpha, dl);
        return true;
    }

    private static bool Still(PlayerControl p)
    {
        var ph = p.MyPhysics;
        if (!ph || !ph.body) return true;
        var v = ph.body.velocity;
        return v.x * v.x + v.y * v.y < 0.01f;
    }

    // 構えと溜め: 照準の光・砲口の熱い点・揺らぐ空気・小さな放電・垂れる溶けた粒
    private static void TickAim(Shot s, float now, float td, float tc, float alpha, float dl)
    {
        bool show = s.Shown;
        bool up = td > Deploy * 0.7f;
        float u = s.Stance ? 0f : s.Dur > 0f ? FxMath.Clamp01(tc / s.Dur) : 1f;
        bool charging = !s.Stance && up;
        float z = s.Z - 0.0003f;
        float ox = s.Px, oy = s.Py;   // 照準と溜めの玉は光線の頂点から
        // 照準: 細い光と壁の上の点 (溜めで少し濃く)
        s.Aim.enabled = s.Dot.enabled = show && up;
        if (s.Aim.enabled)
        {
            float a = (0.28f + 0.3f * u) * alpha;
            s.AimTf.position = FxMath.V3(ox, oy, z + 0.0002f);
            s.AimTf.localScale = FxMath.V3(s.AimLen, 0.14f, 1f);
            s.Aim.color = Pm(2, 0.6f, a);
            float ex = ox + s.Ax * s.AimLen, ey = oy + s.Ay * s.AimLen;
            s.DotTf.position = FxMath.V3(ex, ey, z);
            float ds = 0.16f * FxMath.Range(0.9f, 1.1f);
            s.DotTf.localScale = FxMath.V3(ds, ds, 1f);
            s.Dot.color = Pc(3, a * 1.8f);
        }
        s.Core.enabled = s.Halo.enabled = s.Light.enabled = show && charging;
        if (!show || !charging) { s.Bolt.enabled = false; return; }
        // 砲口の光る点 (芯 + 本体の色のにじみ)。わずかに揺らぐ
        float fl = FxMath.Range(0.92f, 1.08f);
        float cs = (0.05f + 0.22f * u) * (0.8f + 0.2f * s.Size) * fl;
        s.CoreTf.position = FxMath.V3(ox, oy, z - 0.0002f);
        s.CoreTf.localScale = FxMath.V3(cs, cs, 1f);
        s.Core.color = Pc(4, u);
        float hs = (0.25f + 1.1f * u * u) * fl;
        s.HaloTf.position = FxMath.V3(ox, oy, z - 0.0001f);
        s.HaloTf.localScale = FxMath.V3(hs, hs, 1f);
        s.Halo.color = Pc(2, 0.12f + 0.4f * u);
        // 床に落ちる光 (明るい床で濁らないよう薄く)
        var pos = s.P.transform.position;
        float fy = pos.y - 0.36f;
        float ld = 1.2f + 2.4f * u;
        s.LightTf.position = FxMath.V3(ox, fy + 0.05f, fy / 1000f + 0.0006f);
        s.LightTf.localScale = FxMath.V3(ld, ld * Squash, 1f);
        s.Light.color = Pm(2, 0.15f, (0.05f + 0.16f * u) * fl);
        // 筒の上の空気が熱で揺らぐ (薄い透明なもやが昇る)
        if (u > 0.25f && now >= s.NextHaze)
        {
            s.NextHaze = now + 0.07f;
            float k = FxMath.Range(-0.4f, 0.5f);
            SpawnPuff(s.Mx - s.Dx * dl * (0.6f - k), s.My - s.Dy * dl * (0.6f - k) + 0.1f, FxMath.Range(-0.08f, 0.08f), FxMath.Range(0.3f, 0.5f),
                0.2f, 0.55f, FxMath.Range(0.5f, 0.8f), s.Z - 0.0005f, PuffKind.Haze);
        }
        // 終わり近く: 砲口で短い放電が不規則に走る (いつも出ているのではなく、時々)
        s.Bolt.enabled = now < s.BoltOff;
        if (u > 0.5f && now >= s.NextBolt)
        {
            s.NextBolt = now + FxMath.Range(0.08f, 0.3f) * (1.4f - u);
            s.BoltOff = now + 0.045f;
            s.Bolt.enabled = true;
            s.Bolt.sprite = _bolt[FxMath.Range(0, 6)];
            float a = s.Angle + FxMath.Range(-70f, 70f), len = FxMath.Range(0.35f, 0.6f);
            s.BoltTf.position = FxMath.V3(ox + FxMath.Cos(a * Deg) * len * 0.45f, oy + FxMath.Sin(a * Deg) * len * 0.45f, z - 0.0003f);
            s.BoltTf.rotation = FxMath.RotZ(a);
            s.BoltTf.localScale = FxMath.V3(len, 0.5f, 1f);
            s.Bolt.color = Pc(3, 0.75f);
        }
        // 溶けた粒が砲口から垂れる
        if (u > 0.55f && now >= s.NextDrip)
        {
            s.NextDrip = now + FxMath.Range(0.08f, 0.18f);
            SpawnPuff(s.Mx, s.My - 0.03f, FxMath.Range(-0.4f, 0.4f) + s.Dx * 0.4f, FxMath.Range(0.2f, 0.8f), 0.09f, 0.06f, FxMath.Range(0.3f, 0.45f), z - 0.0004f, PuffKind.Spark);
        }
        if (tc > s.Dur - 0.3f && tc < s.Dur + 0.3f && ((int)(now * 20f) & 1) == 0) Shake(ox, oy, 6f, 0.025f, 0.2f);
    }

    // 撃った後: 砲口が赤熱して冷め、キャノンの上から湯気が昇る
    private static void TickCooling(Shot s, float now, float tf, float cx, float cy, float dl)
    {
        if (!s.Shown) { s.Heat.enabled = false; return; }
        float cool = FxMath.Clamp01((tf - BeamLife * 0.5f) / (BeamLife * 0.5f + Hold));
        s.Heat.enabled = cool < 1f;
        if (s.Heat.enabled)
        {
            float hs = 0.55f - 0.25f * cool;
            var ht = s.Heat.transform;
            ht.position = FxMath.V3(s.Mx, s.My, s.Z - 0.0003f);
            ht.localScale = FxMath.V3(hs, hs, 1f);
            s.Heat.color = FxMath.Rgba(1f, 0.3f + 0.3f * (1f - cool), 0.12f, 0.7f * (1f - cool));
        }
        // 開いた排熱口から熱い空気と湯気を外の上へ吐く (蓋が開いている間)
        var r = s.Rig;
        if (r.KeyVent > 40 && now >= s.NextSteam)
        {
            s.NextSteam = now + 0.05f;
            float hot = FxMath.Clamp01(1f - tf / (BeamLife + Hold));
            float c = FxMath.Cos(s.Rot * Deg), sn = FxMath.Sin(s.Rot * Deg);
            for (int i = 0; i < 2; i++)
            {
                float vx = r.VentX[i], vy = r.VentY[i];
                float wx = cx + c * vx - sn * vy, wy = cy + sn * vx + c * vy;
                float nx = c * r.VentNx[i] - sn * r.VentNy[i], ny = sn * r.VentNx[i] + c * r.VentNy[i];
                float v = FxMath.Range(0.5f, 0.9f);
                SpawnPuff(wx, wy, nx * v + FxMath.Range(-0.08f, 0.08f), ny * v + 0.25f, 0.08f, 0.45f + 0.2f * hot, FxMath.Range(0.6f, 0.9f), s.Z - 0.0006f, hot > 0.5f ? PuffKind.Haze : PuffKind.Steam);
            }
        }
    }

    // 撃たずに終わった (光線に替わらなかった) 溜めを消す
    internal static void CancelCharge(PlayerControl p)
    {
        for (int i = Shots.Count - 1; i >= 0; i--)
        {
            var s = Shots[i];
            if (s.P != p || s.FiredAt >= 0f || s.Stance || s.EndAt >= 0f) continue;
            if (s.Root) Object.Destroy(s.Root);
            Shots.RemoveAt(i);
        }
    }

    // ================= 光線 =================

    private sealed class Beam
    {
        public GameObject Root;
        public SpriteRenderer[] Layers;
        public Transform[] LayerTf;
        public SpriteRenderer Scar;
        public float T0, X, Y, Dx, Dy, Len, Size, Z, NextSmoke, NextFloor, Angle, FloorY, Spread, Lit, Life = BeamLife;
        public SpriteRenderer[] Cone;
        public Transform[] ConeTf;
        public SpriteRenderer Plasma, FloorGlow;   // 式で描くプラズマの筒 (シェーダがある時)・真下の床の照り返し
        public Transform PlasmaTf, FloorTf;
        public float Height, FloorZ;               // 光線の床からの高さ (画面)・床の奥行き
        public MaterialPropertyBlock Mpb;
        public float MpbLen = -1f;
        public float NextShake, NextHit, NextDust;
        public bool Embered, HitWall;
    }

    private static readonly List<Beam> Beams = new();

    // from = 当たりの線の起点 (足元)。見た目はキャノンの砲口の前の頂点から出す
    // half = 光線の片側の幅 (世界の単位。頂点 1 点から開いて、先へ行くほどこの幅に近づく。0 = まっすぐな帯)
    internal static void Fire(PlayerControl p, Vector2 from, float angle, float length, float size, bool hitWall, float half = 0f)
    {
        if (RewindPlayback.Active || !Art()) return;
        UsePalette();
        float dx = FxMath.Cos(angle * Deg), dy = FxMath.Sin(angle * Deg);
        Shot shot = null;
        foreach (var s in Shots) if (s.P == p && s.FiredAt < 0f && !s.Stance && s.EndAt < 0f) { shot = s; break; }
        float mx, my;
        if (shot != null)
        {
            shot.FiredAt = Time.time;
            shot.OpenFire = shot.Open;
            shot.Core.enabled = shot.Halo.enabled = shot.Light.enabled = shot.Aim.enabled = shot.Dot.enabled = shot.Bolt.enabled = false;
            mx = shot.Px; my = shot.Py;
        }
        else
        {
            mx = from.x + dx * 1.8f; my = from.y + 0.45f + dy * 1.2f;
        }
        // 足元の線の長さを頂点から測り直す (頂点の分だけ短い)
        float back = (mx - from.x) * dx + (my - from.y) * dy;
        float len = length - back > 0.05f ? length - back : 0.05f;
        var b = new Beam { T0 = Time.time, X = mx, Y = my, Dx = dx, Dy = dy, Len = len, Size = size, Angle = angle, Z = (from.y - 2f) / 1000f, FloorY = from.y };
        // 光線の横向き (板の +y = 画面の (-dy, dx)) と左上からの光 (-0.45, 0.89) の内積。光の当たる側の縁を明るくする
        b.Lit = dy * 0.45f + dx * 0.89f;
        b.Root = new GameObject("MrpNovaBeam");
        FxRoot.Attach(b.Root);
        var rt = b.Root.transform;
        b.Layers = new SpriteRenderer[4];
        b.LayerTf = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            var sr = Make("Layer" + i, _beam, rt);
            var tf = sr.transform;
            tf.position = FxMath.V3(b.X, b.Y, b.Z - i * 0.0001f);
            tf.rotation = FxMath.RotZ(angle);
            sr.enabled = false;
            b.Layers[i] = sr;
            b.LayerTf[i] = tf;
        }
        // シェーダがあれば、帯の層の代わりに式で描くプラズマの筒 1 枚 (根元から先へラッパに広がり、中をエネルギーが流れる)。
        // 真下の床には同じ形で広がる照り返しを置き、光線が手の高さに浮いて見えるようにする
        b.Height = my - from.y;
        b.HitWall = hitWall;
        if (MrpBundle.NovaBeamMaterial)
        {
            _plasmaSprite ??= FxSprites.Rect(4, 4, (_, _) => 1f, "MrpNovaPlasma");
            b.Plasma = Make("Plasma", _plasmaSprite, rt);
            b.Plasma.sharedMaterial = MrpBundle.NovaBeamMaterial;
            b.PlasmaTf = b.Plasma.transform;
            b.PlasmaTf.rotation = FxMath.RotZ(angle);
            b.Plasma.enabled = false;
            {
                b.FloorGlow = Make("FloorGlow", _plasmaSprite, rt);
                b.FloorGlow.sharedMaterial = MrpBundle.NovaBeamMaterial;
                b.FloorTf = b.FloorGlow.transform;
                b.FloorZ = from.y / 1000f + 0.0006f;
                b.FloorTf.rotation = FxMath.RotZ(angle);
                b.FloorGlow.enabled = false;
            }
            for (int i = 0; i < 4; i++) b.Layers[i].gameObject.SetActive(false);
        }
        // 溜めた光線は砲口から先へ円錐に広がる (帯の層の外側に重ねる熱の円錐。シェーダが無い時)
        b.Spread = half;
        if (half > 0f) b.Life = BeamLife + ConeExtra * FxMath.Clamp01((half - Impostor.Nova.HalfMin) / Impostor.Nova.HalfGain);
        if (half > 0f && _cone && b.Plasma == null)
        {
            b.Cone = new SpriteRenderer[3];
            b.ConeTf = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                var sr = Make("Cone" + i, _cone, rt);
                var tf = sr.transform;
                tf.position = FxMath.V3(b.X, b.Y, b.Z + 0.00005f - i * 0.0001f);
                tf.rotation = FxMath.RotZ(angle);
                sr.enabled = false;
                b.Cone[i] = sr;
                b.ConeTf[i] = tf;
            }
        }
        // 床に残る焼けた線 (光線の真下の床・人の下)。光線が細り始めたら出して冷めていく
        b.Scar = Make("Scar", _beam, rt);
        var scarTf = b.Scar.transform;
        scarTf.position = FxMath.V3(mx, from.y + dy * back, from.y / 1000f + 0.0007f);
        scarTf.rotation = FxMath.RotZ(angle);
        scarTf.localScale = FxMath.V3(len, 0.6f * size, 1f);
        b.Scar.enabled = false;
        Beams.Add(b);
        // 後ろの放熱口から噴気と火花 (反動)
        float rx = mx - dx * (1.2f + ApexGap), ry = my - dy * (1.0f + ApexGap);
        for (int i = 0; i < 6; i++)
        {
            float a = angle + 180f + FxMath.Range(-35f, 35f), v = FxMath.Range(1.2f, 2.6f);
            SpawnPuff(rx, ry, FxMath.Cos(a * Deg) * v, FxMath.Sin(a * Deg) * v, 0.25f, 0.9f, FxMath.Range(0.6f, 0.9f), b.Z + 0.0002f, PuffKind.Steam);
            SpawnPuff(rx, ry, FxMath.Cos(a * Deg) * v * 1.6f, FxMath.Sin(a * Deg) * v * 1.6f + 1f, 0.12f, 0.08f, FxMath.Range(0.3f, 0.5f), b.Z - 0.0004f, PuffKind.Spark);
        }
        // 砲口: ごく短い閃光と、まわりを照らす光
        SpawnPuff(mx, my, 0f, 0f, 0.8f * size + 0.5f, 1.4f * size + 0.8f, 0.1f, b.Z - 0.0008f, PuffKind.Flash, angle);
        SpawnPuff(mx, my, 0f, 0f, 2.5f, 4.5f, 0.45f, b.Z + 0.0004f, PuffKind.Glow);
        Shake(mx, my, 10f, 0.18f, 0.7f);
        Overlay(mx, my, 0.6f);
        if (hitWall) Impact(mx + dx * len, my + dy * len, angle, size, b.Z);
    }

    // 着弾: 当たった所が白熱して冷めていく・火花が放物線を描いて散る・煙が立つ
    private static void Impact(float x, float y, float angle, float size, float z)
    {
        SpawnPuff(x, y, 0f, 0f, 0.9f * size + 0.6f, 1.6f * size + 1f, 0.14f, z - 0.0008f, PuffKind.Flash);
        SpawnPuff(x, y, 0f, 0f, 0.5f * size + 0.3f, 0.7f * size + 0.4f, BeamLife + 1.6f, z - 0.0006f, PuffKind.Molten);
        SpawnPuff(x, y, 0f, 0f, 2f, 3.5f, 0.6f, z + 0.0004f, PuffKind.Glow);
        for (int i = 0; i < 24; i++)
        {
            float a = angle + 180f + FxMath.Range(-75f, 75f), v = FxMath.Range(2.5f, 6.5f);
            SpawnPuff(x, y, FxMath.Cos(a * Deg) * v, FxMath.Sin(a * Deg) * v + 1.5f, 0.14f, 0.1f, FxMath.Range(0.35f, 0.7f), z - 0.0005f, PuffKind.Spark, float.NaN, FxMath.Range(0f, 0.6f));
        }
        for (int i = 0; i < 8; i++)
        {
            float v = FxMath.Range(0.3f, 0.8f);
            SpawnPuff(x + FxMath.Range(-0.2f, 0.2f), y + FxMath.Range(-0.1f, 0.2f), FxMath.Range(-0.25f, 0.25f), v, 0.4f, 1.6f, FxMath.Range(1.4f, 2.2f), z + 0.0002f, PuffKind.Smoke, float.NaN, FxMath.Range(0.1f, 1f));
        }
        Shake(x, y, 10f, 0.15f, 0.6f);
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
        if (t >= b.Life - Thread)
        {
            float st = (t - (b.Life - Thread)) / ScarLife;
            if (st >= 1f) return false;
            if (!b.Scar.enabled) b.Scar.enabled = true;
            float sc = 1f - st;
            b.Scar.color = FxMath.Rgba(0.2f + 0.8f * sc * sc * sc, 0.05f + 0.15f * sc * sc * sc, 0.04f, 0.5f * sc);
        }
        if (t >= b.Life)
        {
            if (!b.Embered)
            {
                b.Embered = true;
                for (int i = 0; i < 4; i++) b.Layers[i].enabled = false;
                if (b.Cone != null) for (int i = 0; i < 3; i++) b.Cone[i].enabled = false;
                if (b.Plasma != null) { b.Plasma.enabled = false; if (b.FloorGlow) b.FloorGlow.enabled = false; }
                Embers(b);
            }
            return true;
        }
        // 長さ: 出始めは芯が砲口から壁まで伸びる
        float len = t < Ignite ? b.Len * (t / Ignite) : b.Len;
        // 太さ: 出た瞬間が少し太く、すぐ落ち着く。終わりは細って消える
        float k = t < Ignite ? 0.3f : t < Ignite + Settle ? 1.2f - 0.2f * ((t - Ignite) / Settle) : 1f;
        float fade = 1f;
        if (t > b.Life - Thread)
        {
            float u = (t - (b.Life - Thread)) / Thread;
            k *= (1f - u) * (1f - u) * 0.85f + 0.15f;
            fade = 1f - u * u;
        }
        float flick = FxMath.Range(0.96f, 1.04f);
        if (b.Plasma != null) TickPlasma(b, t, len, k, fade);
        else for (int i = 0; i < 4; i++)
        {
            // 伸びている間は芯だけ
            bool on = t >= Ignite || i == 3;
            if (b.Layers[i].enabled != on) b.Layers[i].enabled = on;
            if (!on) continue;
            float w = b.Size * LayerW[i] * k * (i == 3 ? 1f : flick) * 4f; // 帯の絵の高さ 0.25 単位 → 1 単位に
            b.LayerTf[i].localScale = FxMath.V3(len, w, 1f);
            var c = LayerC[i];
            b.Layers[i].color = FxMath.Rgba(c.r, c.g, c.b, c.a * fade);
        }
        // 円錐: 芯が届いてから 0.25 秒で開ききる。外ほど暗い地・内は明るい本体
        if (b.Cone != null)
        {
            float open = t < Ignite ? 0f : EaseOut((t - Ignite) / ConeOpen);
            for (int i = 0; i < 3; i++)
            {
                bool on = open > 0.01f;
                if (b.Cone[i].enabled != on) b.Cone[i].enabled = on;
                if (!on) continue;
                float far = (2f * b.Spread * ConeW[i] + b.Size * LayerW[i]) * open * k * flick;
                b.ConeTf[i].localScale = FxMath.V3(len, far * 2f, 1f); // 絵の高さ 0.5 単位 → 1 単位に
                var c = ConeC[i];
                b.Cone[i].color = FxMath.Rgba(c.r, c.g, c.b, c.a * fade);
            }
        }
        if (t < Ignite) return true;
        float px = -b.Dy, py = b.Dx;
        // 通り道の空気が焼けて薄い煙が昇る (光線が消えた後もしばらく漂う)
        if (now >= b.NextSmoke && fade > 0.3f)
        {
            b.NextSmoke = now + 0.035f;
            float d = FxMath.Range(0.2f, len);
            float side = FxMath.Range(-0.25f, 0.25f) * b.Size;
            SpawnPuff(b.X + b.Dx * d + px * side, b.Y + b.Dy * d + py * side, FxMath.Range(-0.1f, 0.1f), FxMath.Range(0.15f, 0.35f),
                0.3f, 0.9f, FxMath.Range(1.2f, 1.8f), b.Z + 0.0003f, PuffKind.Smoke);
        }
        // 照射の間ずっと、近くの人の画面が細かく揺れ続ける (撃った人ほど強い)
        if (now >= b.NextShake && fade > 0.3f)
        {
            b.NextShake = now + 0.1f;
            Shake(b.X, b.Y, 14f, 0.07f + 0.05f * b.Spread, 0.16f);
        }
        // 壁に当たり続けている所: 白熱の閃光と溶けた火花が絶えず弾ける
        if (b.HitWall && now >= b.NextHit && fade > 0.3f)
        {
            b.NextHit = now + 0.07f;
            float hx = b.X + b.Dx * len, hy = b.Y + b.Dy * len;
            SpawnPuff(hx, hy, 0f, 0f, 0.5f * b.Spread + 0.4f, 0.9f * b.Spread + 0.7f, 0.09f, b.Z - 0.0008f, PuffKind.Flash);
            for (int i = 0; i < 5; i++)
            {
                float a = b.Angle + 180f + FxMath.Range(-80f, 80f), v = FxMath.Range(2.5f, 7f);
                SpawnPuff(hx, hy, FxMath.Cos(a * Deg) * v, FxMath.Sin(a * Deg) * v + 1.5f, 0.13f, 0.09f, FxMath.Range(0.3f, 0.6f), b.Z - 0.0005f, PuffKind.Spark);
            }
        }
        // 柱の両脇の床: 吹き付ける流れで粉塵が外へ押し出される
        if (now >= b.NextDust && fade > 0.3f && b.Spread > 0f)
        {
            b.NextDust = now + 0.025f;
            float d = FxMath.Range(0.6f, len);
            float sgn = FxMath.Range(0f, 1f) < 0.5f ? -1f : 1f;
            float w = b.Spread * FxMath.Range(0.8f, 1.05f) * sgn;
            float v = FxMath.Range(1.5f, 3.2f) * sgn;
            float fy = b.FloorY + b.Dy * d;
            SpawnPuff(b.X + b.Dx * d + px * w, fy + py * w * Squash, px * v, py * v * Squash, 0.25f, 0.9f, FxMath.Range(0.5f, 0.8f), fy / 1000f - 0.0001f, PuffKind.Smoke);
        }
        // 光線の真下の床に火花が落ちて跳ねる
        if (now >= b.NextFloor && fade > 0.3f)
        {
            b.NextFloor = now + 0.025f;
            float d = FxMath.Range(0.3f, len);
            float fx = b.X + b.Dx * d, fy = b.FloorY + b.Dy * d;
            SpawnPuff(fx, fy, FxMath.Range(-1.2f, 1.2f), FxMath.Range(0.8f, 1.8f), 0.1f, 0.06f, FxMath.Range(0.25f, 0.4f), fy / 1000f - 0.0002f, PuffKind.Spark);
        }
        return true;
    }

    private static Sprite _plasmaSprite;
    private const float PlasmaBack = 0.5f, PlasmaOpen = 0.12f;

    // プラズマの筒: 長さは芯が伸びるのに合わせ、開き具合は芯が届いてからすぐ開く。値は色で渡す (r = 開き・g = 熱・b = 芯の太さ・a = 濃さ)
    private static void TickPlasma(Beam b, float t, float len, float k, float fade)
    {
        float coreHalf = b.Size * 0.5f * 0.4f;
        if (b.Spread > 0f && coreHalf > b.Spread * 0.45f) coreHalf = b.Spread * 0.45f;
        float farHalf = b.Spread > 0f ? b.Spread : coreHalf * 2.2f;
        float open = b.Spread > 0f && t > Ignite ? EaseOut((t - Ignite) / PlasmaOpen) : 0f;
        if (!b.Plasma.enabled) b.Plasma.enabled = true;
        // 板は頂点の PlasmaBack だけ後ろから (頂点の光のにじみの分)
        float full = len + PlasmaBack, mid = (len - PlasmaBack) * 0.5f;
        b.PlasmaTf.position = FxMath.V3(b.X + b.Dx * mid, b.Y + b.Dy * mid, b.Z);
        // 照射中は流れの勢いで柱の太さが不規則に脈打つ (板の縦の拡大なので形ごと太る)
        float surge = 1f + (0.05f * FxMath.Sin(t * 37f) + 0.035f * FxMath.Sin(t * 23f + 1.3f) + 0.025f * FxMath.Sin(t * 61f + 0.4f)) * open;
        b.PlasmaTf.localScale = FxMath.V3(full, farHalf * 2f * 1.3f * surge, 1f);
        // 大きさは絵ごとの値で (同じ材質の絵はまとめて描かれ、シェーダから拡大の倍率が読めないため)。変わった時だけ
        if (len != b.MpbLen)
        {
            b.MpbLen = len;
            b.Mpb ??= new MaterialPropertyBlock();
            b.Mpb.SetFloat("_Len", full);
            b.Mpb.SetFloat("_Back", PlasmaBack);
            b.Mpb.SetFloat("_Half", farHalf);
            b.Mpb.SetFloat("_Core", coreHalf * (0.6f + 0.4f * k));
            b.Mpb.SetFloat("_Lit", b.Lit);
            b.Plasma.SetPropertyBlock(b.Mpb);
            if (b.FloorGlow) b.FloorGlow.SetPropertyBlock(b.Mpb);
        }
        b.Plasma.color = FxMath.Rgba(open, fade, 0f, FxMath.Clamp01(fade * 1.2f));
        // 床: 同じ柱を真下の床へ平たく写した影と、筋の光の淡い照り返し (床の奥行きの分だけ縦に縮める)
        if (b.FloorGlow)
        {
            if (!b.FloorGlow.enabled) b.FloorGlow.enabled = true;
            b.FloorTf.position = FxMath.V3(b.X + b.Dx * mid, b.Y - b.Height + b.Dy * mid, b.FloorZ);
            float flat = Squash + (1f - Squash) * FxMath.Abs(b.Dy);
            b.FloorTf.localScale = FxMath.V3(full, farHalf * 2f * 1.3f * flat * 1.15f * surge, 1f);
            b.FloorGlow.color = FxMath.Rgba(open, 0.5f * fade, 1f, 0.5f * fade);
        }
    }

    // 光線の通った所に、熱で光る細かい塵が漂って消える
    private static void Embers(Beam b)
    {
        int n = System.Math.Clamp((int)(b.Len * 1.5f), 6, 20);
        for (int i = 0; i < n; i++)
        {
            float d = FxMath.Range(0f, b.Len);
            float side = FxMath.Range(-0.15f, 0.15f);
            float x = b.X + b.Dx * d - b.Dy * side, y = b.Y + b.Dy * d + b.Dx * side;
            SpawnPuff(x, y, FxMath.Range(-0.15f, 0.15f), FxMath.Range(0.1f, 0.4f), 0.07f, 0.03f, FxMath.Range(0.6f, 1.2f), b.Z - 0.0004f, PuffKind.Ember);
        }
    }

    // ================= 近くの画面: 光線の光で一瞬明るくなる =================

    private static GameObject _ov;
    private static SpriteRenderer _ovFlash;
    private static float _ovT0 = -99f, _ovK;
    private const float OvFlash = 0.18f, OvReach = 11f;

    // 自分の画面がこの点から近いほど明るくなる (ゲームのカメラの子・HUD は別のカメラなので覆わない)
    private static void Overlay(float x, float y, float strength)
    {
        var lp = PlayerControl.LocalPlayer;
        var cam = Camera.main;
        if (!lp || !cam) return;
        var me = lp.transform.position;
        float dx = me.x - x, dy = me.y - y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d >= OvReach) return;
        float k = (1f - d / OvReach) * strength;
        if (!_ov)
        {
            _ov = new GameObject("MrpNovaOverlay") { layer = cam.gameObject.layer };
            var tf = _ov.transform;
            tf.SetParent(cam.transform, false);
            tf.localPosition = FxMath.V3(0f, 0f, cam.nearClipPlane + 0.4f);
            float h = cam.orthographicSize * 2.1f;
            _ovFlash = Make("Flash", Terrain.DebrisArt.SoftGlow, tf);
            _ovFlash.transform.localScale = FxMath.V3(h * cam.aspect * 2.2f, h * 2.2f, 1f);
        }
        float now = Time.time;
        bool active = _ov.activeSelf && now - _ovT0 <= OvFlash;
        _ovK = active && _ovK > k ? _ovK : k;
        _ovT0 = now;
        _ov.SetActive(true);
        _ovOn = true;
    }

    private static void TickOverlay(float now)
    {
        float t = now - _ovT0;
        if (t > OvFlash) { _ov.SetActive(false); _ovK = 0f; _ovOn = false; return; }
        float fl = 1f - t / OvFlash;
        _ovFlash.color = Pm(3, 0.6f, 0.5f * fl * fl * _ovK);
    }

    // ================= 粒 =================

    private enum PuffKind : byte { Spark, Flash, Glow, Smoke, Steam, Ember, Haze, Molten }

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
            PuffKind.Spark => _streak,
            _ => Terrain.DebrisArt.SoftGlow,
        };
        if (!ReferenceEquals(p.Art, art)) { p.Art = art; p.Sr.sprite = art; }
        p.Kind = kind;
        p.Go.SetActive(true);
        p.Start = Time.time + delay;
        p.Life = life;
        p.Size0 = s0;
        p.Size1 = s1;
        p.X = x; p.Y = y; p.Vx = vx; p.Vy = vy; p.Z = z;
        p.Tf.rotation = FxMath.RotZ(float.IsNaN(rot) ? (kind == PuffKind.Spark ? FxMath.Atan2(vy, vx) / Deg : 0f) : rot);
        p.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
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
            float e = 1f - (1f - f) * (1f - f);
            float s = p.Size0 + (p.Size1 - p.Size0) * e;
            switch (p.Kind)
            {
                case PuffKind.Spark: // 溶けた粒: 放物線で落ちる・進む向きに伸びた白熱の筋 → 橙 → 暗い赤
                {
                    float vy = p.Vy - Gravity * t;
                    p.Tf.position = FxMath.V3(p.X + p.Vx * t, p.Y + (p.Vy - 0.5f * Gravity * t) * t, p.Z);
                    p.Tf.rotation = FxMath.RotZ(FxMath.Atan2(vy, p.Vx) / Deg);
                    float sp = FxMath.Sqrt(p.Vx * p.Vx + vy * vy);
                    p.Tf.localScale = FxMath.V3(s * (1f + sp * 0.5f), s * 0.6f, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.95f - 0.6f * f, 0.8f - 0.75f * f, 1f - f * f);
                    break;
                }
                case PuffKind.Flash:
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = Pc(4, (1f - f) * (1f - f));
                    break;
                case PuffKind.Glow: // まわりを照らす光
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s * 0.75f, 1f);
                    p.Sr.color = Pm(2, 0.1f, (1f - f) * (1f - f) * 0.35f);
                    break;
                case PuffKind.Molten: // 当たった所の白熱: 白 → 橙 → 赤 → 暗くなって消える
                {
                    p.Tf.position = FxMath.V3(p.X, p.Y, p.Z);
                    float h = 1f - f;
                    p.Tf.localScale = FxMath.V3(p.Size0 * (0.6f + 0.4f * h), p.Size0 * (0.6f + 0.4f * h), 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.15f + 0.8f * h * h * h, 0.08f + 0.7f * h * h * h * h, h * (0.6f + 0.4f * FxMath.Range(0.9f, 1f)));
                    break;
                }
                case PuffKind.Smoke: // 焼けた煙: 灰色・ゆっくり昇って広がり薄れる
                {
                    float drag = 1f - f * 0.5f;
                    p.Tf.position = FxMath.V3(p.X + p.Vx * t * drag, p.Y + p.Vy * t * drag, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s * 0.85f, 1f);
                    float a = f < 0.15f ? f / 0.15f : (1f - f) / 0.85f;
                    p.Sr.color = FxMath.Rgba(0.32f, 0.3f, 0.3f, a * 0.32f);
                    break;
                }
                case PuffKind.Steam: // 湯気: 白く薄く広がって消える
                {
                    float drag = 1f - f * 0.6f;
                    p.Tf.position = FxMath.V3(p.X + p.Vx * t * drag, p.Y + p.Vy * t * drag, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    float a = f < 0.1f ? f / 0.1f : (1f - f) / 0.9f;
                    p.Sr.color = FxMath.Rgba(0.92f, 0.92f, 0.95f, a * 0.35f);
                    break;
                }
                case PuffKind.Haze: // 熱で揺らぐ空気: ごく薄い透明のもや
                {
                    p.Tf.position = FxMath.V3(p.X + p.Vx * t + 0.03f * FxMath.Sin(t * 13f + p.X * 5f), p.Y + p.Vy * t, p.Z);
                    p.Tf.localScale = FxMath.V3(s * 0.8f, s, 1f);
                    p.Sr.color = FxMath.Rgba(1f, 0.85f, 0.8f, (1f - f) * f * 0.35f);
                    break;
                }
                default: // Ember: 光線の名残で光る塵 (明るい筋の色 → 暗い地へ・ちらつく)
                    p.Tf.position = FxMath.V3(p.X + p.Vx * t, p.Y + p.Vy * t, p.Z);
                    p.Tf.localScale = FxMath.V3(s, s, 1f);
                    p.Sr.color = Pm(1, 0.85f - 0.7f * f, (1f - f) * (0.6f + 0.4f * FxMath.Sin(now * 40f + p.X * 7f)));
                    break;
            }
        }
    }

    // ================= 毎フレーム =================

    private static bool _rewound, _ovOn; // _ovOn = 画面の光が出ている (管理側の旗・出ていない時は Unity に触らない)

    internal static void Tick()
    {
        Impostor.Nova.LocalTick();
        if (Shots.Count == 0 && Beams.Count == 0 && Puffs.Count == 0 && !_ovOn && !_surging) return;
        if (RewindPlayback.Active) { _rewound = true; EndSurge(); return; }
        if (_rewound) { _rewound = false; Drop(); return; }
        float now = Time.time;
        if (_ovOn) { if (_ov) TickOverlay(now); else _ovOn = false; }
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
        if (Shots.Count > 0) TickSurge(now);
        else EndSurge();
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
        if (_ov) _ov.SetActive(false);
        _ovT0 = -99f;
        _ovOn = false;
        EndSurge();
    }
}
