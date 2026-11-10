using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// チキンの死に際と冷や汗 (見た目だけ)。
//   Tremble = 死ぬと決まったチキンがその場で固まって小刻みに震え、汗の滴が飛ぶ (全員の端末)。震えは次第に強くなり、OmenTime 後に倒れる。
//   Sweat   = 危ない所を無事に通った本人の画面にだけ、汗の滴が数粒はじける。
// 体の絵 (BodyForms) と着せ替え (Cosmetics) を動かすので、元の位置を覚えて書き戻さず、前のフレームに足した分だけを差し替える
// (同じ絵を動かす演出と重なっても元の姿を汚さない)。透明の間の滴は体と同じ濃さ
internal static class ChickenFx
{
    internal const float OmenTime = 1.7f;                       // 震え始めてから死ぬまで (秒・体の動きは下の BodyKeys)
    private const float DeathWait = 1.5f;                         // 死ぬ時刻の後も倒れた姿で待つ長さ (ホストを経た死の知らせの遅れ)
    // 手 (エモートの手の絵を本人の色で)。苦しむ間は片手で胸を押さえ、もう片手で宙をつかむ。倒れる時は前の手を頭の先へ伸ばし、胸の手で床をつく
    private const float HandSize = 0.17f, FistSize = 0.15f, HandZ = -0.002f;
    private const int Open = 0, Fist = 1;
    private const float BodyHalf = 0.38f;                         // 体の中心から足元まで (倒れる・縮む軸)
    private const float HeadH = 0.3f;                             // 体の中心から着せ替え (帽子・顔) の高さ
    private const float AmpTo = 0.06f, TiltTo = 7f;               // 震えの強さ 1 の時の左右の揺れ (単位)・傾き (度)
    private const float Hz = 26f;                                 // 揺れの速さ (往復/秒)
    private const float QuiverAmp = 0.022f;                       // 本編の絵を流している間の左右の震え (単位)
    // 流す絵の段の切り替わり (秒・連番の表と合わせる) と、手を組んでいる絵の頭の動き (体の絵の単位・右向き)
    private const float FlipClaspAt = 0.375f, FlipProneAt = 1.21f;
    private const float ClaspHeadX = 0.48f, ClaspHeadY = -0.64f;
    private const float DropGap = 0.11f;                          // 震えている間に滴が飛ぶ間隔 (秒)
    private const float DropLife = 0.5f, Gravity = 5.5f;          // 滴の寿命 (秒)・重力 (単位/秒²)
    private const float HeadUp = 0.2f, HeadSide = 0.24f;         // 体の中心から滴の出る所 (頭の左右)
    private const float DropW = 0.09f, DropH = 0.13f;            // 滴の大きさ (単位)

    private sealed class Shake
    {
        public PlayerControl P;
        public Transform Body, Cos;
        public bool Flip;                                          // 本編の変身の絵を流している (体は位置の震えだけ足す)
        public float ArtScale = 0.5f;                              // 体の絵の 1 単位が世界の何単位か (流す絵の頭の位置を世界へ直す)
        public float Start, NextDrop;
        public float Bx, By, Cx, Cy, Ang, Sx = 1f, Sy = 1f;       // 今足している分 (体の絵の位置・着せ替えの位置・回転・倍率)
        public bool Side;
        public float Face, Inv = 1f;                               // 顔の向き (+1 = 右)・人の根の倍率の逆数
        public Vector3 Bp;                                         // 震え始めた時の体の絵の位置 (手を体に合わせて回す軸)
        public GameObject Ha, Hb;                                  // a = 宙をつかむ手 (顔の側)・b = 胸を押さえる手
        public Transform At, Ac, Bt, Bc;
        public SpriteRenderer Ar, Br;
        public int Asp = -1, Bsp = -1;
    }

    private struct Drop
    {
        public GameObject Go;
        public SpriteRenderer Sr;
        public byte Id;
        public float X, Y, Z, Vx, Vy, Born;
    }

    private static readonly List<Shake> Shakes = new();
    private static readonly List<Drop> Drops = new();
    private static Sprite _drop;
    private static int _gen = -1;

    // 本人の端末: 震えている人 (動けない・PortFreezePatch が読む)。255 = いない
    internal static byte LocalOmen = 255;
    internal static float LocalOmenAt;

    internal static void Tremble(PlayerControl p)
    {
        if (!PortKit.Alive(p) || !Ready()) return;
        for (int i = 0; i < Shakes.Count; i++) if (Shakes[i].P && Shakes[i].P.PlayerId == p.PlayerId) return;
        // 全身の絵は本編のかくれんぼの変身の連番 (立つ → 前のめり → 手を組んで震える → 縮む → 床に突っ伏してひくつく) を流す。
        // 絵が読めない・他の演出が体を使っている・はしごや昇降機で運ばれている (床に伏せる絵が宙に浮く) 時は、
        // 体の絵を回して縮める動きと手の絵で代わりに見せる
        bool carried = p.inMovingPlat || p.onLadder || p.MyPhysics && p.MyPhysics.Animations != null && p.MyPhysics.Animations.IsPlayingAnyLadderAnimation();
        // 倒れる流し方: 帽子や服は周りへ落ち、伏せた姿のまま死体になる (自決の知らせが届くまで伏せたまま待つ)
        bool flip = !carried && CrewFlip.Play(p, "chicken", OmenTime + DeathWait, fallen: true);
        var s = new Shake { P = p, Body = p.transform.Find("BodyForms"), Cos = p.cosmetics ? p.cosmetics.transform : null, Start = Time.time, Flip = flip };
        var bodyArt = p.cosmetics && p.cosmetics.currentBodySprite != null ? p.cosmetics.currentBodySprite.BodySprite : null;
        if (bodyArt) s.ArtScale = bodyArt.transform.lossyScale.y;
        if (s.Body) s.Bp = s.Body.localPosition;
        s.Face = p.cosmetics && p.cosmetics.FlipX ? -1f : 1f;
        float scale = p.transform.localScale.x;
        s.Inv = scale > 0.01f ? 1f / scale : 1f;
        Material mat = FxHands.PlayerMat();
        if (!flip && mat && EmoteArt.HandArt(Open, out _))
        {
            int color = p.Data != null ? p.Data.DefaultOutfit.ColorId : 0;
            int layer = p.gameObject.layer;
            s.Ha = EmoteArt.NewHand(p.transform, mat, color, layer, out s.At, out s.Ac, out s.Ar);
            s.Hb = EmoteArt.NewHand(p.transform, mat, color, layer, out s.Bt, out s.Bc, out s.Br);
        }
        Shakes.Add(s);
    }

    internal static void Sweat(PlayerControl p)
    {
        if (!PortKit.Alive(p) || p.inVent || !Ready()) return;
        var c = p.transform.position;
        Spawn(p.PlayerId, c, true, 0.9f);
        Spawn(p.PlayerId, c, false, 1f);
        Spawn(p.PlayerId, c, Random.value < 0.5f, 0.7f);
    }

    private static bool Ready()
    {
        if (!ShipStatus.Instance) return false;
        if (_gen != GameClock.ShipGen) { Forget(); _gen = GameClock.ShipGen; }
        return true;
    }

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        // 震えの途中で役職が外れた等で後始末が来なくても、動けないままにしない
        if (LocalOmen != 255 && Time.time - LocalOmenAt > OmenTime + DeathWait) LocalOmen = 255;
        if (Shakes.Count == 0 && Drops.Count == 0) return;
        if (_gen != GameClock.ShipGen) { Forget(); return; }
        if (RewindPlayback.Active) { ReleaseAll(); return; } // 逆再生の間は録画が見える
        float now = Time.time, dt = Time.deltaTime;
        bool meeting = MeetingHud.Instance || ExileController.Instance;

        for (int i = Shakes.Count - 1; i >= 0; i--)
        {
            var s = Shakes[i];
            float t = now - s.Start;
            if (meeting || t > OmenTime + DeathWait || !PortKit.Alive(s.P))
            {
                End(s);
                Shakes.RemoveAt(i);
                continue;
            }
            byte id = s.P.PlayerId;
            if (s.Flip)
            {
                // 別の演出が体を動かし始めたら絵を返す・連番が止まった (ベントに入った等) 後は本編の絵のまま揺らさない
                if (CrewBrace.Posed(id) || CrewGrip.Flying(id) || DuelTumble.Posing(id) || EmoteArt.IsPlaying(id))
                {
                    CrewFlip.Stop(id);
                    Undo(s);
                }
                else if (CrewFlip.IsPlaying(id)) Quiver(s, t);
                else Undo(s);
            }
            // 体を別の演出が動かしている間は揺らさない (向こうの姿に足さない)
            else if (CrewBrace.Posed(id) || CrewGrip.Flying(id) || DuelTumble.Posing(id) || EmoteArt.IsPlaying(id))
            {
                Undo(s);
                HideHands(s);
                continue;
            }
            else Animate(s, t);
            float k = FxMath.Clamp01(t / 1.2f);
            // 本編の絵を流している時は滴を頭の位置から出す (手を組んでいる間は前へ下がった頭・床に伏せたら出さない)
            bool prone = s.Flip && t >= FlipProneAt;
            if (now >= s.NextDrop && !prone && !s.P.inVent && s.P.Visible) // ベントの中・隠れている間は滴で居場所を漏らさない
            {
                s.NextDrop = now + DropGap;
                s.Side = !s.Side;
                var c = s.P.transform.position;
                if (s.Flip && t >= FlipClaspAt) c = FxMath.V3(c.x + s.Face * ClaspHeadX * s.ArtScale, c.y + ClaspHeadY * s.ArtScale, c.z);
                Spawn(id, c, s.Side, 0.8f + 0.4f * k);
            }
        }

        for (int i = Drops.Count - 1; i >= 0; i--)
        {
            var d = Drops[i];
            float age = now - d.Born;
            if (age >= DropLife || !d.Go)
            {
                if (d.Go) Object.Destroy(d.Go);
                Drops.RemoveAt(i);
                continue;
            }
            d.Vy -= Gravity * dt;
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            d.Go.transform.position = FxMath.V3(d.X, d.Y, d.Z);
            // 落ちる向きへ伸びる (速いほど細長い)
            float ang = System.MathF.Atan2(d.Vy, d.Vx) * (180f / System.MathF.PI) + 90f;
            d.Go.transform.rotation = FxMath.RotZ(ang);
            float fade = age < DropLife * 0.6f ? 1f : 1f - (age - DropLife * 0.6f) / (DropLife * 0.4f);
            float a = fade * FxMath.Min(Cloak.AlphaOf(d.Id), UchuKit.Hidden.AlphaOf(d.Id));
            d.Sr.color = FxMath.Rgba(0.6f, 0.82f, 1f, a);
            Drops[i] = d;
        }
    }

    // 頭の左右どちらかから、外へ跳ねて落ちる滴を 1 つ
    private static void Spawn(byte id, Vector3 c, bool left, float power)
    {
        if (!_drop)
        {
            // 縦長の滴 (上が細く下が丸い・縁は柔らかい)
            _drop = FxSprites.Rect(12, 18, (x, y) =>
            {
                float yy = y + 0.5f;                  // 0 = 下・1 = 上
                float half = 0.5f * FxMath.Sqrt(FxMath.Clamp01(1f - yy)) * (yy < 0.35f ? FxMath.Sqrt(yy / 0.35f) : 1f);
                float edge = half - FxMath.Abs(x);
                return FxMath.Clamp01(edge * 9f);
            }, "MrpChickenDrop");
        }
        float side = left ? -1f : 1f;
        var go = FxRoot.New("MrpChickenDrop");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _drop;
        var b = _drop.bounds.size;
        go.transform.localScale = FxMath.V3(DropW / b.x, DropH / b.y, 1f);
        var d = new Drop
        {
            Go = go, Sr = sr, Id = id,
            X = c.x + side * (HeadSide + Random.value * 0.05f),
            Y = c.y + HeadUp + Random.value * 0.06f,
            Z = c.z - 0.002f,
            Vx = side * (0.5f + Random.value * 0.4f) * power,
            Vy = (0.7f + Random.value * 0.5f) * power,
            Born = Time.time,
        };
        go.transform.position = FxMath.V3(d.X, d.Y, d.Z);
        sr.color = FxMath.Rgba(0.82f, 0.93f, 1f, 0f);
        Drops.Add(d);
    }

    // ---- 体の動き (キー) ----
    // 前 = 顔の向き。Lean = 前へ倒れる角度 (度・負は反る)・Sx/Sy = 体の絵の横/縦の倍率 (足元を軸に縮む)・Lift = 浮く高さ・
    // Fwd = 前へずれる長さ・Jit = 小刻みな震えの強さ (0〜1)・Ease = 次のキーへの運び (0 = なめらか・1 = だんだん速く・2 = だんだん遅く)
    private readonly struct BK
    {
        public readonly float T, Lean, Sx, Sy, Lift, Fwd, Jit;
        public readonly byte Ease;
        public BK(float t, float lean, float sx, float sy, float lift = 0f, float fwd = 0f, float jit = 0f, byte ease = 0)
        { T = t; Lean = lean; Sx = sx; Sy = sy; Lift = lift; Fwd = fwd; Jit = jit; Ease = ease; }
    }

    // 手のキー: 体の絵の中の位置 (x は前が正・体と一緒に縮んで回る)・Rot = 指の向き (0 = 上・負 = 前へ)・Sp = 絵 (0 開く・1 握る・-1 握っては開く)
    private readonly struct HK
    {
        public readonly float T, X, Y, Rot, Size;
        public readonly sbyte Sp;
        public HK(float t, float x, float y, float rot, float size, sbyte sp) { T = t; X = x; Y = y; Rot = rot; Size = size; Sp = sp; }
    }

    //   0.00〜0.26 ビクッと跳ねて縮む (驚き) → 0.26〜1.00 胸を押さえて息が上がる (膨らんで縮む・前かがみが深くなる・2 度ひきつる)
    //   1.00〜1.12 最後に大きく息を吸って反る → 1.12〜1.22 膝が抜けて潰れる → 1.22〜1.40 前へ倒れる (だんだん速く・倒れる間は伸びる)
    //   1.40〜1.55 床に打って平たく潰れ、弾んで戻る → 1.55〜 床でひくつく
    private static readonly BK[] BodyKeys =
    {
        new(0.00f,   0f, 1.00f, 1.00f),
        new(0.05f,  -7f, 0.84f, 1.20f, 0.07f),
        new(0.11f,  -3f, 0.92f, 1.10f, 0.05f),
        new(0.17f,   6f, 1.16f, 0.82f),
        new(0.23f,   4f, 0.96f, 1.05f),
        new(0.30f,   9f, 1.04f, 0.97f, 0f, 0f, 0.35f),
        new(0.60f,  13f, 1.03f, 0.95f, 0f, 0.01f, 0.65f),
        new(0.95f,  17f, 1.06f, 0.92f, 0f, 0.02f, 1.00f),
        new(1.00f,  12f, 1.00f, 1.00f, 0f, 0.02f, 0.40f, 2),
        new(1.08f, -11f, 0.86f, 1.16f, 0.04f, 0f, 0.15f),
        new(1.12f, -10f, 0.88f, 1.14f, 0.03f, 0f, 0.10f),
        new(1.18f,  16f, 1.18f, 0.78f, 0f, 0.03f, 0.05f),
        new(1.22f,  22f, 1.10f, 0.86f, 0f, 0.04f, 0f, 1),
        new(1.40f,  95f, 0.94f, 1.08f, 0f, 0.06f),
        new(1.44f,  97f, 0.74f, 1.12f, 0f, 0.07f),
        new(1.49f,  84f, 1.08f, 0.96f, 0f, 0.07f),
        new(1.54f,  90f, 0.96f, 1.02f, 0f, 0.07f),
        new(1.58f,  88f, 1.00f, 1.00f, 0f, 0.07f, 0.2f),
        new(9.00f,  88f, 1.00f, 1.00f, 0f, 0.07f, 0.2f),
    };

    // a = 前の手 (驚いて上げる → 宙をつかむ → 息を吸う時に高く伸ばす → 頭の先へ伸ばして床を這う)
    private static readonly HK[] HandA =
    {
        new(0.00f, 0.28f, -0.05f, -40f, 0.0f, 0),
        new(0.05f, 0.44f,  0.32f, -15f, 1.15f, 0),
        new(0.12f, 0.46f,  0.24f, -30f, 1.05f, 0),
        new(0.20f, 0.42f,  0.06f, -60f, 1.0f, 1),
        new(0.30f, 0.44f,  0.02f, -72f, 1.0f, -1),
        new(0.60f, 0.47f,  0.06f, -64f, 1.0f, -1),
        new(0.95f, 0.49f,  0.00f, -80f, 1.05f, -1),
        new(1.08f, 0.34f,  0.48f, -12f, 1.15f, 0),
        new(1.12f, 0.32f,  0.50f, -10f, 1.15f, 0),
        new(1.18f, 0.44f,  0.24f, -55f, 1.0f, 0),
        new(1.22f, 0.40f,  0.30f, -40f, 1.0f, 0),
        new(1.40f, 0.16f,  0.56f,  -6f, 1.0f, 0),
        new(1.44f, 0.16f,  0.59f,  -4f, 1.25f, 0),
        new(1.50f, 0.15f,  0.57f,  -6f, 1.0f, 0),
        new(1.56f, 0.14f,  0.59f,  -8f, 1.0f, -1),
        new(9.00f, 0.14f,  0.62f,  -8f, 1.0f, -1),
    };

    // b = 後ろの手 (驚いて上げる → 胸を押さえる → 前について体を支える → 床を打つ)
    private static readonly HK[] HandB =
    {
        new(0.00f, 0.05f, -0.05f,  10f, 0.0f, 0),
        new(0.05f, -0.10f, 0.34f,  15f, 1.1f, 0),
        new(0.12f, 0.00f,  0.20f,  10f, 1.0f, 0),
        new(0.20f, 0.13f, -0.08f,  18f, 1.0f, 1),
        new(0.95f, 0.15f, -0.10f,  12f, 1.0f, 1),
        new(1.08f, 0.10f,  0.02f,  20f, 1.0f, 1),
        new(1.18f, 0.38f, -0.08f, -85f, 1.05f, 0),
        new(1.22f, 0.40f, -0.04f, -90f, 1.0f, 0),
        new(1.40f, 0.38f,  0.22f, -100f, 1.0f, 0),
        new(1.44f, 0.40f,  0.22f, -100f, 1.3f, 0),
        new(1.50f, 0.40f,  0.22f, -100f, 1.0f, 0),
        new(9.00f, 0.40f,  0.22f, -100f, 1.0f, 0),
    };

    private static float Ease(float u, byte e) => e == 1 ? u * u : e == 2 ? 1f - (1f - u) * (1f - u) : u * u * (3f - 2f * u);

    private static void BodyAt(float t, out BK a, out BK b, out float u)
    {
        int i = 0;
        while (i < BodyKeys.Length - 2 && t >= BodyKeys[i + 1].T) i++;
        a = BodyKeys[i];
        b = BodyKeys[i + 1];
        u = Ease(FxMath.Clamp01((t - a.T) / (b.T - a.T)), a.Ease);
    }

    private static void HandAt(HK[] keys, float t, out float x, out float y, out float rot, out float size, out int sp)
    {
        int i = 0;
        while (i < keys.Length - 2 && t >= keys[i + 1].T) i++;
        var a = keys[i];
        var b = keys[i + 1];
        float u = Ease(FxMath.Clamp01((t - a.T) / (b.T - a.T)), 0);
        x = a.X + (b.X - a.X) * u;
        y = a.Y + (b.Y - a.Y) * u;
        rot = a.Rot + (b.Rot - a.Rot) * u;
        size = a.Size + (b.Size - a.Size) * u;
        sp = u < 0.5f ? a.Sp : b.Sp;
    }

    // ひきつり (一瞬で伸びて反り、ゆっくり戻る): at = 一番強い時刻
    private static float Spasm(float t, float at)
    {
        float d = (t - at) / 0.05f;
        return d <= -1f || d >= 2f ? 0f : d < 0f ? 1f + d : 1f - d * 0.5f;
    }

    // 今の姿を体の絵・着せ替え・手へ当てる
    private static void Animate(Shake s, float t)
    {
        BodyAt(t, out var a, out var b, out float u);
        float lean = a.Lean + (b.Lean - a.Lean) * u, sx = a.Sx + (b.Sx - a.Sx) * u, sy = a.Sy + (b.Sy - a.Sy) * u;
        float lift = a.Lift + (b.Lift - a.Lift) * u, fwd = a.Fwd + (b.Fwd - a.Fwd) * u, jit = a.Jit + (b.Jit - a.Jit) * u;

        // 息が上がる (胸を押さえている間だけ・だんだん速く浅く)
        float breathe = t > 0.26f && t < 1.0f ? FxMath.Sin((t - 0.26f) * (16f + 10f * t)) * (0.05f - 0.02f * t) : 0f;
        sy += breathe;
        sx -= breathe * 0.8f;
        // 立ったまま 2 度ひきつる・床に倒れてからも 2 度
        float sp = Spasm(t, 0.52f) + Spasm(t, 0.80f);
        sy += sp * 0.1f;
        sx -= sp * 0.08f;
        lean -= sp * 9f;
        float tw = Spasm(t, 1.60f) * 0.7f + Spasm(t, 1.68f) * 0.5f;
        lean -= tw * 5f;
        sx += tw * 0.06f;

        // 小刻みな震え (ずれた 2 つの揺れを重ねて機械に見せない)
        float ph = t * Hz * 6.2831853f;
        float w = FxMath.Sin(ph) * 0.7f + FxMath.Sin(ph * 1.37f + 1.1f) * 0.3f;
        float jx = w * AmpTo * jit, jr = -w * TiltTo * jit;

        float f = s.Face;
        float ang = -f * lean + jr;                                      // 前へ倒れる = 顔の側へ回る
        float rad = ang * (3.1415927f / 180f), cs = FxMath.Cos(rad), sn = FxMath.Sin(rad);
        // 足元を軸に縮めて回す: 体の中心 = 足元 + 回した (0, BodyHalf·sy)
        float h = BodyHalf * sy;
        float bx = -sn * h + f * fwd + jx, by = cs * h - BodyHalf + lift;
        // 着せ替え (帽子・顔) は縮めず、頭の高さの変わりだけ体と一緒に動かす
        float hd = HeadH * (sy - 1f);
        float cx = bx - sn * hd, cy = by + cs * hd;
        Apply(s, bx, by, cx, cy, ang, sx, sy);
        Hands(s, t, bx, by, ang, sx, sy);
    }

    // 本編の絵を流している間: 絵が変わるのは 2 コマごとなので、間を細かい左右の震えで埋める
    // (前のめりの間は弱く・手を組んでいる間はだんだん強く・縮んで倒れる所は止め・床では弱くひくつく)
    private static void Quiver(Shake s, float t)
    {
        float jit = t < 0.37f ? 0.35f : t < 1.05f ? 0.45f + 0.55f * (t - 0.37f) / 0.68f : t < 1.45f ? 0f : 0.2f;
        float ph = t * Hz * 6.2831853f;
        float w = FxMath.Sin(ph) * 0.7f + FxMath.Sin(ph * 1.37f + 1.1f) * 0.3f;
        float jx = w * QuiverAmp * jit;
        Apply(s, jx, 0f, jx, 0f, 0f, 1f, 1f);
    }

    private static void Hands(Shake s, float t, float bx, float by, float ang, float sx, float sy)
    {
        if (!s.Ha) return;
        float a = s.P.Visible && !s.P.inVent ? FxMath.Min(Cloak.AlphaOf(s.P.PlayerId), UchuKit.Hidden.AlphaOf(s.P.PlayerId)) : 0f;
        // 握っては開く (苦しい間はだんだん速く・床では速く)
        float rate = t > 1.5f ? 12f : 6f + 5f * t;
        int clench = (int)(t * rate) % 3 == 2 ? Fist : Open;
        float jr = FxMath.Sin(t * 39f + 0.7f) * (t < 1.22f ? 7f + 6f * t : 3f);
        float jy = FxMath.Sin(t * 53f) * (t < 1.22f ? 0.012f : 0.004f);

        HandAt(HandA, t, out float x, out float y, out float rot, out float size, out int sp);
        if (sp < 0) sp = clench;
        Hand(s, s.At, s.Ac, s.Ar, ref s.Asp, sp, x * sx, (y + jy) * sy, rot + jr, (sp == Fist ? FistSize : HandSize) * size, a, bx, by, ang);

        HandAt(HandB, t, out x, out y, out rot, out size, out sp);
        if (sp < 0) sp = clench;
        // 胸を押さえる手は息に合わせて握り直す
        if (t > 0.2f && t < 1.08f) size *= 1f + 0.08f * FxMath.Sin((t - 0.26f) * (16f + 10f * t));
        Hand(s, s.Bt, s.Bc, s.Br, ref s.Bsp, sp, x * sx, (y - jy) * sy, rot - jr * 0.5f, (sp == Fist ? FistSize : HandSize) * size, a, bx, by, ang);
    }

    private static void Hand(Shake s, Transform tf, Transform art, SpriteRenderer sr, ref int cur, int sprite, float lx, float ly, float rot, float size, float alpha,
        float bx, float by, float ang)
    {
        if (!tf) return;
        if (cur != sprite)
        {
            cur = sprite;
            sr.sprite = EmoteArt.HandArt(sprite, out var wrist);
            art.localPosition = FxMath.V3(wrist.x, wrist.y, 0f);
        }
        float f = s.Face, rad = ang * (3.1415927f / 180f), cs = FxMath.Cos(rad), sn = FxMath.Sin(rad);
        float x = f * lx, y = ly;
        float px = s.Bp.x + bx + cs * x - sn * y, py = s.Bp.y + by + sn * x + cs * y;
        tf.localPosition = FxMath.V3(px, py, HandZ * s.Inv); // 体の絵と同じ人の根の中の位置
        tf.localRotation = FxMath.RotZ(f * rot + ang);
        float k = size * s.Inv;
        tf.localScale = FxMath.V3(f < 0f ? -k : k, k, 1f);
        sr.color = FxMath.Rgba(1f, 1f, 1f, size > 0.01f ? alpha : 0f);
    }

    private static void HideHands(Shake s)
    {
        if (s.Ar) s.Ar.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        if (s.Br) s.Br.color = FxMath.Rgba(1f, 1f, 1f, 0f);
    }

    private static void DropHands(Shake s)
    {
        if (s.Ha) Object.Destroy(s.Ha);
        if (s.Hb) Object.Destroy(s.Hb);
        s.Ha = s.Hb = null;
    }

    // 足している分を差し替える (体の絵 = 位置・回転・倍率、着せ替え = 位置・回転)。倍率は今掛けている分との比で
    private static void Apply(Shake s, float bx, float by, float cx, float cy, float ang, float sx, float sy)
    {
        float ma = ang - s.Ang;
        var turn = FxMath.RotZ(ma);
        if (s.Body)
        {
            var p = s.Body.localPosition;
            s.Body.localPosition = FxMath.V3(p.x + bx - s.Bx, p.y + by - s.By, p.z);
            if (ma != 0f) s.Body.localRotation = s.Body.localRotation * turn;
            if (sx != s.Sx || sy != s.Sy)
            {
                var l = s.Body.localScale;
                s.Body.localScale = FxMath.V3(l.x * sx / s.Sx, l.y * sy / s.Sy, l.z);
            }
        }
        if (s.Cos)
        {
            var p = s.Cos.localPosition;
            s.Cos.localPosition = FxMath.V3(p.x + cx - s.Cx, p.y + cy - s.Cy, p.z);
            if (ma != 0f) s.Cos.localRotation = s.Cos.localRotation * turn;
        }
        s.Bx = bx;
        s.By = by;
        s.Cx = cx;
        s.Cy = cy;
        s.Ang = ang;
        s.Sx = sx;
        s.Sy = sy;
    }

    private static void Undo(Shake s)
    {
        Apply(s, 0f, 0f, 0f, 0f, 0f, 1f, 1f);
    }

    // 足した分・手・流している絵を外す
    private static void End(Shake s)
    {
        if (s.P)
        {
            Undo(s);
            if (s.Flip) CrewFlip.Stop(s.P.PlayerId);
        }
        DropHands(s);
    }

    // 別の演出がこの人の体を動かし始める直前 (SamuraiBlade.ReleaseBody から): 足した分を外す
    internal static void Release(byte id)
    {
        for (int i = Shakes.Count - 1; i >= 0; i--)
        {
            if (!Shakes[i].P || Shakes[i].P.PlayerId != id) continue;
            End(Shakes[i]);
            Shakes.RemoveAt(i);
        }
    }

    private static void ReleaseAll()
    {
        foreach (var s in Shakes) End(s);
        Shakes.Clear();
    }

    private static void Forget()
    {
        foreach (var s in Shakes) End(s);
        Shakes.Clear();
        foreach (var d in Drops) if (d.Go) Object.Destroy(d.Go);
        Drops.Clear();
        LocalOmen = 255;
    }
}
