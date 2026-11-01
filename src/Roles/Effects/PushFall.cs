using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 突き落とされた人の姿 (全員の画面): 消える直前の体と着せ替えを写し、押された向きへよろけて縁を越え、
// 下へ落ちていく。真上から見ているので、落ちるほど遠ざかって小さく暗くなり、勢いで少しずつ回る。
// はしごの上で押された人ははしごに沿って下まで転げ落ち、そこで本編の死体に替わる。
// 動かすのは写した絵の親の transform と色だけ
internal static class PushFall
{
    private const float ShoveT = 0.18f;     // 押されてよろける秒
    private const float ShoveDist = 0.3f;   // よろけて進む長さ
    private const float Lean = 16f;         // よろける間に倒れる角度 (度)
    private const float Gravity = 9.8f;     // 落ちる速さの伸び (単位/秒²)
    private const float CamH = 1.4f;        // 見ている高さ (落ちた深さがこれだけあると半分の大きさに見える)
    // エアシップは斜め横から見た絵なので、落ちる人は画面の下へ動き、遠ざかって縮むのは少しだけ。ほかの船は真上から見ているので
    // 縮んで暗くなるのが主で、画面の下へは少しだけずれる
    private const float DropSide = 0.38f, DropTop = 0.12f;   // 落ちた深さのうち画面の下へずれる割合
    private const float CamSide = 9f;                        // エアシップで半分の大きさに見える深さ
    private const float Drift = 0.25f;      // 縁を越えた後も押された向きへ流れる速さ
    private const float Spin = 120f;        // 落ちる間の回り (度/秒)
    private const float FallLife = 1.7f;    // 縁を越えてから消えきるまで
    private const float FadeT = 0.45f;      // 最後に薄れる秒
    private const float LadderG = 16f;      // はしごを転げ落ちる速さの伸び
    private const float LadderSpin = 300f;

    private sealed class Faller
    {
        public GameObject Go;
        public Transform Tf;
        public readonly List<SpriteRenderer> Parts = new();
        public readonly List<float> Alpha = new();
        public float T, X0, Y0, Z, Dx, Dy, Sign;
        public bool Ladder, Swished, Side;
        public float Lx, Ly, Len;  // はしごの下 (Ladder の時)
    }

    private static readonly List<Faller> Live = new();
    private static int _gen = -1;

    internal static int Count => Live.Count;

    // dx, dy = 押された向き (長さ 1)。ladderTo = はしごの下 (はしごで押された時)
    internal static void Start(PlayerControl pc, float dx, float dy, Vector2? ladderTo)
    {
        if (!pc || !pc.cosmetics || !ShipStatus.Instance) return;
        Check();
        var cos = pc.cosmetics;
        var body = cos.currentBodySprite != null ? cos.currentBodySprite.BodySprite : null;
        if (!body || !body.enabled || !body.sprite) return;
        var b = body.bounds;
        Vector3 c = b.center;
        float pz = pc.transform.position.z;
        int layer = pc.gameObject.layer;
        var go = new GameObject("MrpPushFall") { layer = layer };
        FxRoot.Attach(go);
        go.transform.position = FxMath.V3(c.x, c.y, pz);
        var f = new Faller { Go = go, Tf = go.transform, X0 = c.x, Y0 = c.y, Z = pz, Dx = dx, Dy = dy, Side = ShipStatus.Instance.TryCast<AirshipStatus>() != null };
        // 回る向き: 右へ押されたら時計回り。上下へ押された時は向いている側へ倒れる
        f.Sign = dx > 0.1f ? -1f : dx < -0.1f ? 1f : cos.FlipX ? 1f : -1f;
        int color = cos.ColorId;
        Add(f, body, c, pz, layer, color, true);
        if (cos.skin && cos.skin.layer) Add(f, cos.skin.layer, c, pz, layer, color, false);
        if (cos.visor && cos.visor.Image) Add(f, cos.visor.Image, c, pz, layer, color, false);
        if (cos.hat)
        {
            if (cos.hat.BackLayer) Add(f, cos.hat.BackLayer, c, pz, layer, color, false);
            if (cos.hat.FrontLayer) Add(f, cos.hat.FrontLayer, c, pz, layer, color, false);
        }
        if (ladderTo is Vector2 to)
        {
            f.Ladder = true;
            f.Lx = to.x;
            f.Ly = to.y + (c.y - b.min.y);
            float ex = f.Lx - c.x, ey = f.Ly - c.y;
            f.Len = FxMath.Sqrt(ex * ex + ey * ey);
        }
        Live.Add(f);
        BreakNoise.PlayAt("noise_emote_push", c.x, c.y, 7f, 4f, 0.75f, FxMath.Range(0.85f, 0.95f));
    }

    private static void Add(Faller f, SpriteRenderer src, Vector3 c, float pz, int layer, int color, bool player)
    {
        if (!src.enabled || !src.sprite || !src.gameObject.activeInHierarchy) return;
        var go = new GameObject("Part") { layer = layer };
        go.transform.SetParent(f.Tf, false);
        Vector3 p = src.transform.position;
        go.transform.localPosition = FxMath.V3(p.x - c.x, p.y - c.y, p.z - pz);
        go.transform.localScale = src.transform.lossyScale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = src.sprite;
        sr.flipX = src.flipX;
        // 体は本編の死体と同じ材質 (生きている体の材質は消える時に幽霊の物へ替わる)
        var mat = player ? FxHands.PlayerMat() : null;
        sr.sharedMaterial = mat ? mat : src.sharedMaterial;
        sr.sortingOrder = src.sortingOrder;
        if (color >= 0 && color < Palette.PlayerColors.Length) PlayerMaterial.SetColors(color, sr);
        var col = src.color;
        f.Parts.Add(sr);
        f.Alpha.Add(col.a);
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;
        if (_gen != GameClock.ShipGen || !GameClock.ShipAlive) { Clear(); return; }
        float dt = Time.deltaTime;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var f = Live[i];
            if (!f.Go) { Live.RemoveAt(i); continue; }
            f.T += dt;
            if (f.Ladder ? !StepLadder(f) : !StepEdge(f))
            {
                Object.Destroy(f.Go);
                Live.RemoveAt(i);
            }
        }
    }

    // 縁から落ちる。false = 消えきった
    private static bool StepEdge(Faller f)
    {
        float t = f.T, x, y, rot, k = 1f, a = 1f, shade = 1f;
        if (t < ShoveT)
        {
            float u = t / ShoveT, e = 1f - (1f - u) * (1f - u);
            x = f.X0 + f.Dx * ShoveDist * e;
            y = f.Y0 + f.Dy * ShoveDist * e;
            rot = f.Sign * Lean * e;
        }
        else
        {
            if (!f.Swished)
            {
                f.Swished = true;
                BreakNoise.PlayAt("noise_emote_swish_1", f.X0, f.Y0, 7f, 4f, 0.45f, FxMath.Range(0.7f, 0.78f));
            }
            float s = t - ShoveT;
            if (s >= FallLife) return false;
            float depth = 0.5f * Gravity * s * s;
            float cam = f.Side ? CamSide : CamH;
            k = cam / (cam + depth);
            x = f.X0 + f.Dx * (ShoveDist + Drift * s);
            y = f.Y0 + f.Dy * (ShoveDist + Drift * s) - depth * (f.Side ? DropSide : DropTop);
            rot = f.Sign * (Lean + Spin * s);
            // 船の下の暗がりへ遠ざかる: 明るさは見かけの大きさに合わせて落とす (エアシップの下は明るい空なので少しだけ)
            shade = f.Side ? 1f - 0.3f * (1f - k) : k * k;
            if (s > FallLife - FadeT) a = (FallLife - s) / FadeT;
        }
        Apply(f, x, y, rot, k, shade, a);
        return true;
    }

    // はしごを転げ落ちる。false = 下に着いた (本編の死体に替わる)
    private static bool StepLadder(Faller f)
    {
        float t = f.T;
        float d = 0.5f * LadderG * t * t;
        if (f.Len < 0.01f || d >= f.Len)
        {
            BreakNoise.PlayAt("noise_emote_land_1", f.Lx, f.Ly, 7f, 4f, 0.8f, FxMath.Range(0.8f, 0.9f));
            return false;
        }
        float u = d / f.Len;
        float x = f.X0 + (f.Lx - f.X0) * u, y = f.Y0 + (f.Ly - f.Y0) * u;
        Apply(f, x, y, f.Sign * LadderSpin * t, 1f, 1f, 1f);
        return true;
    }

    private static void Apply(Faller f, float x, float y, float rot, float k, float shade, float a)
    {
        f.Tf.position = FxMath.V3(x, y, f.Z);
        f.Tf.localRotation = FxMath.RotZ(rot);
        f.Tf.localScale = FxMath.V3(k, k, 1f);
        for (int j = 0; j < f.Parts.Count; j++)
        {
            var sr = f.Parts[j];
            if (sr) sr.color = FxMath.Rgba(shade, shade, shade, f.Alpha[j] * a);
        }
    }

    private static void Check()
    {
        if (_gen == GameClock.ShipGen) return;
        Clear();
        _gen = GameClock.ShipGen;
    }

    private static void Clear()
    {
        foreach (var f in Live)
            if (f.Go) Object.Destroy(f.Go);
        Live.Clear();
        _gen = GameClock.ShipGen;
    }
}
