using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 死体が食べられる / 錬金される時の姿 (全員の画面)。本物の死体は消し、写した絵だけを動かす。
// Devour: 食べる人の頭が割れて口になり (本編の舌のキルの絵)、伸びた舌が死体に巻き付いて口へ引き寄せ、
// 押し込んで噛む。巻き付いた所から血が飛び、床に暗い染みが残って薄れる。
// Transmute: 色が抜けて灰になり、上から崩れて沈む。崩れた粉は錬金した人の手元へ流れ、届くと小さく光る
internal static class BodyConsume
{
    internal enum Kind : byte { Devour, Transmute }

    // Devour (秒・ワールドの長さ)
    private const float TongueAt = 0.18f;                                       // 口が開きかけた所で舌を撃つ
    private const float ReachSpeed = 12f, ReachMin = 0.08f, ReachMax = 0.25f;  // 舌が伸びる
    private const float Grip = 0.1f;                                            // 舌の先が死体に巻き付く
    private const float DragSpeed = 6f, DragMin = 0.22f, DragMax = 0.6f;        // 舌を巻き戻して口まで引き寄せる
    private const float Stuff = 0.12f;                                          // 口へ押し込む
    private const float DragShrink = 0.62f;                                     // 口に着いた時の死体の大きさ
    // 食べる姿が終わるまでの長さ (遠い死体を巻き戻しきって噛み、口が閉じるまで)。勝ちの確定をこの後にする
    internal const float DevourSeconds = TongueAt + ReachMax + Grip + DragMax + Stuff + 0.35f;
    private const float StainA = 0.62f, StainIn = 0.6f, StainHold = 15f, StainOut = 5f;
    private const float StainW = 0.72f, StainH = 0.34f;

    // Transmute
    private const float AshT = 0.35f;         // 灰になるまで
    private const float CrumbleT = 0.85f;     // 上から崩れきるまで
    private const int Grains = 45;
    private const float GrainSpeed = 2.6f;    // 手元へ流れる速さ
    private const float GrainSteer = 5f;      // 流れへ向きを合わせる速さ (1/秒)
    private const float GrainLife = 1.6f;
    private const float HandUp = 0.35f;       // 足元から手元までの高さ
    private const float FlashT = 0.28f, FlashSize = 0.55f;

    private static readonly Color Ash = new(0.74f, 0.72f, 0.69f, 1f);
    private static readonly Color Blood = new(0.3f, 0.03f, 0.03f, 1f);

    private sealed class Corpse
    {
        public Kind Kind;
        public GameObject Go;
        public Transform Tf;
        public readonly List<SpriteRenderer> Parts = new();
        public readonly List<bool> Body = new();
        public Color BodyColor;
        public float T, X0, Y0, Z, Bottom, H, Ex, Ey, Ux, Uy, Width;
        public int NextTug, Emitted, Arrived;
        public SpriteRenderer Stain, Flash;
        public float StainT = -1f, FlashAt = -1f;
        public bool Gone;
        // Devour
        public byte WhoId;
        public bool Maw;
        public float Reach, DragT, Gx, Gy, Ox, Oy;
        public Grain[] Grain;
    }

    private struct Grain
    {
        public SpriteRenderer Sr;
        public float X, Y, Vx, Vy, Age, Size, Phase;
        public bool Live, Glow;
    }

    private static readonly List<Corpse> Live = new();
    private static int _gen = -1;
    private static Sprite _dot, _soft;

    internal static int Count => Live.Count;

    // 本物の死体を消す前に呼ぶ。eater = 食べる / 錬金する人の足元。who = 食べる人 (Devour の口と舌)
    internal static void Start(DeadBody body, Kind kind, Vector2 eater, PlayerControl who = null)
    {
        if (!body || !ShipStatus.Instance || body.bodyRenderers == null || body.bodyRenderers.Length == 0) return;
        var main = body.bodyRenderers[0];
        if (!main || !main.sprite) return;
        Check();
        var b = main.bounds;
        Vector3 c = b.center;
        float pz = body.transform.position.z;
        int layer = body.gameObject.layer;
        var go = new GameObject("MrpBodyConsume") { layer = layer };
        FxRoot.Attach(go);
        go.transform.position = FxMath.V3(c.x, c.y, pz);
        float dx = eater.x - c.x, dy = eater.y - b.min.y;
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(body.ParentId) : null;
        int color = info != null && info.DefaultOutfit != null ? info.DefaultOutfit.ColorId : -1;
        var k = new Corpse
        {
            Kind = kind, Go = go, Tf = go.transform, X0 = c.x, Y0 = c.y, Z = pz,
            Bottom = b.min.y, H = b.size.y, Width = b.size.x, Ex = eater.x, Ey = eater.y,
            Ux = d > 0.01f ? dx / d : 1f, Uy = d > 0.01f ? dy / d : 0f,
            BodyColor = color >= 0 && color < Palette.PlayerColors.Length ? Palette.PlayerColors[color] : Ash,
        };
        // 死体の絵 (体・着せ替え) を全部写す。体は本編の死体と同じ材質で色を塗り直す
        var mat = FxHands.PlayerMat();
        foreach (var src in body.GetComponentsInChildren<SpriteRenderer>())
        {
            if (!src || !src.enabled || !src.sprite || !src.gameObject.activeInHierarchy) continue;
            bool isBody = false;
            foreach (var r in body.bodyRenderers) if (r && r.Pointer == src.Pointer) { isBody = true; break; }
            var p = new GameObject("Part") { layer = layer };
            p.transform.SetParent(k.Tf, false);
            Vector3 at = src.transform.position;
            p.transform.localPosition = FxMath.V3(at.x - c.x, at.y - c.y, at.z - pz);
            p.transform.localRotation = src.transform.rotation;
            p.transform.localScale = src.transform.lossyScale;
            var sr = p.AddComponent<SpriteRenderer>();
            sr.sprite = src.sprite;
            sr.flipX = src.flipX;
            sr.sortingOrder = src.sortingOrder;
            sr.sharedMaterial = isBody && mat ? mat : src.sharedMaterial;
            if (isBody && color >= 0 && color < Palette.PlayerColors.Length) PlayerMaterial.SetColors(color, sr);
            sr.color = src.color;
            k.Parts.Add(sr);
            k.Body.Add(isBody);
        }
        if (k.Parts.Count == 0) { Object.Destroy(go); return; }

        if (kind == Kind.Devour)
        {
            BeginDevour(k, who);
            k.Stain = Make("Stain", Soft(), FxMath.V3(c.x, b.min.y + 0.06f, (b.min.y + 0.06f) / 1000f + 0.0009f), layer, null);
            k.Stain.transform.localScale = FxMath.V3(StainW, StainH, 1f);
            k.Stain.color = FxMath.Rgba(Blood.r, Blood.g, Blood.b, 0f);
            k.StainT = 0f;
            // 口が開く音 (口が出せない時は噛む音だけ)。巻き付く音と噛む音は舌の段取りに合わせて鳴らす
            PortSound.Play(k.Maw ? "vulture_eat" : "vulture_bite", null, 0.9f, FxMath.V2(c.x, c.y), 6f);
        }
        else
        {
            k.Grain = new Grain[Grains];
            PortSound.Play("alchemist_transmute", null, 0.85f, FxMath.V2(c.x, c.y), 6f);
        }
        Live.Add(k);
        // 始まる前へ戻ると演出ごと消える (逆再生の間は Tick を止めるので、戻した後に続きから再開させない)
        Rewind.Undo(_ =>
        {
            if (!Live.Remove(k)) return;
            if (k.Maw) ImpostorMaw.DevourTongue(k.WhoId, 0f, 0f);
            Drop(k);
        });
    }

    private static SpriteRenderer Make(string name, Sprite sp, Vector3 pos, int layer, Transform parent)
    {
        var go = new GameObject(name) { layer = layer };
        if (parent) go.transform.SetParent(parent, false);
        else FxRoot.Attach(go);
        go.transform.position = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    private static Sprite Dot() => _dot ? _dot : _dot = FxSprites.Rect(32, 32, FxSprites.Dot, "MrpConsumeDot");
    private static Sprite Soft() => _soft ? _soft : _soft = FxSprites.Rect(64, 64, FxSprites.Soft, "MrpConsumeSoft");

    internal static void Tick()
    {
        if (Live.Count == 0 || RewindPlayback.Active) return; // 逆再生の間は止める (見えるのは録画の逆再生)
        // 船が替わったら全部捨てる (絵は船の子なので船と一緒に消えている)。会議では本編が死体を片付けるので染みも消す
        if (_gen != GameClock.ShipGen || !GameClock.ShipAlive || MeetingHud.Instance) { Clear(); return; }
        float dt = Time.deltaTime;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var k = Live[i];
            k.T += dt;
            bool more = k.Kind == Kind.Devour ? StepDevour(k, dt) : StepTransmute(k, dt);
            if (!more) { Drop(k); Live.RemoveAt(i); }
        }
    }

    // ---- Devour ----

    // 口が出せる時だけ舌で運ぶ。食べる人が見えない (透明・通気口) なら口を出さずにその場で薄れる (居場所を漏らさない)
    private static void BeginDevour(Corpse k, PlayerControl who)
    {
        k.Gx = k.X0;
        k.Gy = k.Y0 + k.H * 0.12f;
        if (!who || !who.Visible) return;
        Vector2 me = who.GetTruePosition();
        float dx = k.Gx - me.x, dy = k.Gy - me.y;
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        k.Reach = FxMath.Clamp(d / ReachSpeed, ReachMin, ReachMax);
        k.DragT = FxMath.Clamp(d / DragSpeed, DragMin, DragMax);
        float bite = TongueAt + k.Reach + Grip + k.DragT + Stuff * 0.5f;
        if (!ImpostorMaw.Devour(who, FxMath.Atan2(dy, dx) * FxMath.Rad2Deg, bite)) return;
        k.WhoId = who.PlayerId;
        k.Maw = true;
        // 巻き付く所 (死体の真ん中より少し上) から死体の真ん中まで
        k.Ox = k.X0 - k.Gx;
        k.Oy = k.Y0 - k.Gy;
    }

    // 段取りの節目の音と血 (フレームが飛んで段を跨いでも 1 回ずつ・順に)。1 = 舌が巻き付く・2 = 口に押し込んで噛む
    private static void Cue(Corpse k, int step, float mx, float my)
    {
        if (k.NextTug < 1 && step >= 1)
        {
            k.NextTug = 1;
            Spray(k, 0f);
            PortSound.Play("vulture_grab", null, 0.85f, FxMath.V2(k.Gx, k.Gy), 6f);
        }
        if (k.NextTug < 2 && step >= 2)
        {
            k.NextTug = 2;
            PortSound.Play("vulture_bite", null, 0.95f, FxMath.V2(mx, my), 6f);
        }
    }

    private static float EaseOut(float u) => 1f - (1f - u) * (1f - u);
    private static float EaseInOut(float u) => u * u * (3f - 2f * u);

    private static bool StepDevour(Corpse k, float dt)
    {
        float t = k.T;
        if (!k.Gone)
        {
            if (k.Maw) DevourByTongue(k, t);
            else
            {
                float g = FxMath.Clamp01(t / 0.35f);
                for (int j = 0; j < k.Parts.Count; j++) if (k.Parts[j]) k.Parts[j].color = FxMath.Rgba(1f, 1f, 1f, 1f - g);
                if (g >= 1f) Hide(k);
            }
        }
        StepSpray(k, dt);
        // 染み: 広がって残り、ゆっくり乾いて消える
        if (k.StainT >= 0f)
        {
            k.StainT += dt;
            float s = k.StainT;
            float a = s < StainIn ? StainA * s / StainIn
                : s < StainIn + StainHold ? StainA
                : StainA * (1f - FxMath.Clamp01((s - StainIn - StainHold) / StainOut));
            if (s < StainIn + dt || s > StainIn + StainHold) k.Stain.color = FxMath.Rgba(Blood.r, Blood.g, Blood.b, a);
            if (s >= StainIn + StainHold + StainOut) return false;
            return true;
        }
        return !k.Gone;
    }

    private static void Hide(Corpse k)
    {
        k.Gone = true;
        for (int j = 0; j < k.Parts.Count; j++) if (k.Parts[j]) k.Parts[j].enabled = false;
        if (k.Maw) ImpostorMaw.DevourTongue(k.WhoId, 0f, 0f);
    }

    private static void DevourByTongue(Corpse k, float t)
    {
        // 口は食べる人と一緒に動く。姿が消えたら (会議・死亡・通気口) 死体の写しもそこで消す
        if (!ImpostorMaw.MouthAt(k.WhoId, out float mx, out float my, out float mz, out bool flip, out _, out _, out _)) { Hide(k); return; }
        float fx = flip ? -1f : 1f;
        float t1 = TongueAt + k.Reach, t2 = t1 + Grip, t3 = t2 + k.DragT, t4 = t3 + Stuff;
        float px, py; // 舌の先
        float cx = k.X0, cy = k.Y0, scale = 1f, alpha = 1f, tilt = 0f, cz = k.Z;
        bool held = false;
        if (t < TongueAt)
        {
            px = mx;
            py = my;
        }
        else if (t < t1)
        {
            float u = EaseOut((t - TongueAt) / k.Reach);
            px = FxMath.Lerp(mx, k.Gx, u);
            py = FxMath.Lerp(my, k.Gy, u);
        }
        else if (t < t2)
        {
            // 巻き付いて締める: 先が死体に食い込み、死体が口の方へ小さく引かれる
            Cue(k, 1, mx, my);
            float u = (t - t1) / Grip;
            float pull = 0.05f * FxMath.Sin(u * FxMath.PI);
            float dx = mx - k.Gx, dy = my - k.Gy, d = FxMath.Sqrt(dx * dx + dy * dy) + 0.001f;
            px = k.Gx + dx / d * pull;
            py = k.Gy + dy / d * pull;
            held = true;
        }
        else if (t < t3)
        {
            Cue(k, 1, mx, my);
            float u = EaseInOut((t - t2) / k.DragT);
            px = FxMath.Lerp(k.Gx, mx, u);
            py = FxMath.Lerp(k.Gy, my, u);
            scale = FxMath.Lerp(1f, DragShrink, u);
            // 引かれる勢いで少し傾く
            tilt = -fx * 12f * FxMath.Sin(u * FxMath.PI);
            cz = FxMath.Min(k.Z, mz - 0.0004f);
            held = true;
        }
        else if (t < t4)
        {
            // 口の奥へ押し込む: 頭の絵の後ろへ回して縮める
            Cue(k, 2, mx, my);
            float u = (t - t3) / Stuff;
            px = mx + fx * 0.06f * u;
            py = my;
            scale = DragShrink * (1f - 0.75f * u);
            alpha = 1f - u * u;
            cz = mz + 0.0004f;
            held = true;
        }
        else
        {
            Hide(k);
            return;
        }

        // 舌: 口から先まで (押し込んだ後は口の中)
        float tx = px - mx, ty = py - my;
        float len = t < t3 ? FxMath.Sqrt(tx * tx + ty * ty) : 0f;
        ImpostorMaw.DevourTongue(k.WhoId, len, FxMath.Atan2(ty, tx) * FxMath.Rad2Deg);

        // 死体の写し: 舌の先に付いて動く
        if (held)
        {
            cx = px + k.Ox * scale;
            cy = py + k.Oy * scale;
            k.Tf.position = FxMath.V3(cx, cy, cz);
            k.Tf.localRotation = FxMath.RotZ(tilt);
            k.Tf.localScale = FxMath.V3(scale, scale, 1f);
            if (alpha < 1f)
                for (int j = 0; j < k.Parts.Count; j++)
                {
                    var sr = k.Parts[j];
                    if (!sr) continue;
                    sr.enabled = alpha > 0.01f;
                    if (alpha > 0.01f) sr.color = FxMath.Rgba(1f, 1f, 1f, alpha);
                }
        }
    }

    // 舌が巻き付いた所から、引く向きの反対側へ小さな飛沫
    private static void Spray(Corpse k, float pull)
    {
        k.Grain ??= new Grain[9];
        int made = 0;
        for (int i = 0; i < k.Grain.Length && made < 3; i++)
        {
            ref var g = ref k.Grain[i];
            if (g.Live) continue;
            if (!g.Sr) g.Sr = Make("Spray", Dot(), FxMath.V3(0f, 0f, k.Z - 0.001f), k.Go.layer, k.Tf.parent);
            g.Sr.enabled = true;
            float side = FxMath.Range(-1f, 1f);
            g.X = k.Gx + k.Ux * pull - k.Uy * side * 0.1f;
            g.Y = k.Gy + k.Uy * pull + k.Ux * side * 0.1f;
            float sp = FxMath.Range(0.6f, 1.1f);
            g.Vx = (-k.Ux + FxMath.Range(-0.6f, 0.6f)) * sp;
            g.Vy = (-k.Uy + FxMath.Range(-0.6f, 0.6f)) * sp + 0.4f;
            g.Age = 0f;
            g.Size = FxMath.Range(0.05f, 0.08f);
            g.Phase = FxMath.Range(0.12f, 0.2f); // 宙にある秒
            g.Live = true;
            made++;
        }
    }

    private static void StepSpray(Corpse k, float dt)
    {
        if (k.Grain == null) return;
        for (int i = 0; i < k.Grain.Length; i++)
        {
            ref var g = ref k.Grain[i];
            if (!g.Live || !g.Sr) continue;
            g.Age += dt;
            if (g.Age < g.Phase)
            {
                g.Vy -= 6f * dt;
                g.X += g.Vx * dt;
                g.Y += g.Vy * dt;
            }
            // 床に落ちた滴は少し残って染みに紛れる
            float a = g.Age < g.Phase ? 0.95f : 0.95f * (1f - FxMath.Clamp01((g.Age - g.Phase) / 1.2f));
            if (a <= 0f) { g.Live = false; g.Sr.enabled = false; continue; }
            g.Sr.transform.position = FxMath.V3(g.X, g.Y, k.Z - 0.001f);
            g.Sr.transform.localScale = FxMath.V3(g.Size, g.Size * 0.8f, 1f);
            g.Sr.color = FxMath.Rgba(Blood.r * 1.4f, Blood.g, Blood.b, a);
        }
    }

    // ---- Transmute ----

    private static bool StepTransmute(Corpse k, float dt)
    {
        float t = k.T;
        // 色が抜けて灰になる
        if (t <= AshT + dt)
        {
            float a = FxMath.Clamp01(t / AshT);
            var bc = k.BodyColor;
            var col = FxMath.Rgba(FxMath.Lerp(bc.r, Ash.r, a), FxMath.Lerp(bc.g, Ash.g, a), FxMath.Lerp(bc.b, Ash.b, a), 1f);
            float m = FxMath.Lerp(1f, 0.72f, a);
            for (int j = 0; j < k.Parts.Count; j++)
            {
                var sr = k.Parts[j];
                if (!sr) continue;
                if (k.Body[j]) PlayerMaterial.SetColors(col, sr);
                else sr.color = FxMath.Rgba(m, m, m, 1f);
            }
        }
        // 上から崩れて沈む (足元を残して縦に縮む)。崩れた所から粉が出る
        float c = FxMath.Clamp01((t - AshT) / CrumbleT);
        if (t > AshT && !k.Gone)
        {
            float e = c * c * (3f - 2f * c);
            float sy = 1f - e;
            k.Tf.localScale = FxMath.V3(1f + 0.08f * e, FxMath.Max(sy, 0.001f), 1f);
            k.Tf.position = FxMath.V3(k.X0, k.Bottom + (k.Y0 - k.Bottom) * sy, k.Z);
            int want = (int)(Grains * c);
            float top = k.Bottom + k.H * sy;
            while (k.Emitted < want) Emit(k, top);
            if (c >= 1f)
            {
                k.Gone = true;
                k.Go.SetActive(false);
            }
        }
        // 粉が手元へ流れる
        float hx = k.Ex, hy = k.Ey + HandUp;
        bool any = false;
        for (int i = 0; i < k.Emitted; i++)
        {
            ref var g = ref k.Grain[i];
            if (!g.Live) continue;
            any = true;
            g.Age += dt;
            float dx = hx - g.X, dy = hy - g.Y;
            float d = FxMath.Sqrt(dx * dx + dy * dy);
            if (d < 0.12f || g.Age > GrainLife)
            {
                g.Live = false;
                if (g.Sr) g.Sr.enabled = false;
                if (++k.Arrived >= Grains * 0.6f && k.FlashAt < 0f) Flash(k, hx, hy);
                continue;
            }
            // 崩れた直後は落ちかけ、すぐに手元への流れに乗る (揺らぎは粒ごとの位相)
            float steer = g.Age < 0.12f ? 0f : FxMath.Clamp01(GrainSteer * dt);
            float wob = 0.35f * FxMath.Sin(g.Age * 9f + g.Phase);
            float tx = (dx / d - dy / d * wob) * GrainSpeed, ty = (dy / d + dx / d * wob) * GrainSpeed;
            if (g.Age < 0.12f) g.Vy -= 3f * dt;
            g.Vx += (tx - g.Vx) * steer;
            g.Vy += (ty - g.Vy) * steer;
            g.X += g.Vx * dt;
            g.Y += g.Vy * dt;
            float fade = FxMath.Clamp01(d / 0.4f);
            float a = (g.Glow ? 0.3f : 0.9f) * FxMath.Min(1f, 0.4f + fade);
            g.Sr.transform.position = FxMath.V3(g.X, g.Y, k.Z - 0.002f);
            g.Sr.color = g.Glow ? FxMath.Rgba(0.55f, 0.56f, 1f, a) : FxMath.Rgba(0.6f, 0.57f, 0.64f, a);
        }
        if (k.Flash)
        {
            float f = (t - k.FlashAt) / FlashT;
            if (f >= 1f) { k.Flash.enabled = false; }
            else
            {
                float s = FlashSize * (0.6f + 0.4f * f);
                k.Flash.transform.localScale = FxMath.V3(s, s, 1f);
                k.Flash.color = FxMath.Rgba(0.62f, 0.64f, 1f, 0.65f * (1f - f) * (1f - f));
            }
        }
        if (k.Gone && !any && k.FlashAt < 0f) Flash(k, hx, hy);
        return !k.Gone || any || (k.Flash && k.Flash.enabled);
    }

    private static void Emit(Corpse k, float top)
    {
        ref var g = ref k.Grain[k.Emitted++];
        bool glow = k.Emitted % 3 == 0;
        g.Sr = Make("Grain", glow ? Soft() : Dot(), FxMath.V3(0f, 0f, k.Z), k.Go.layer, k.Tf.parent);
        g.X = k.X0 + FxMath.Range(-0.4f, 0.4f) * k.Width;
        g.Y = top + FxMath.Range(-0.04f, 0.02f);
        g.Vx = FxMath.Range(-0.3f, 0.3f);
        g.Vy = FxMath.Range(-0.2f, 0.25f);
        g.Age = 0f;
        g.Phase = FxMath.Range(0f, 6.28f);
        g.Glow = glow;
        g.Size = glow ? FxMath.Range(0.16f, 0.24f) : FxMath.Range(0.045f, 0.075f);
        g.Sr.transform.localScale = FxMath.V3(g.Size, g.Size, 1f);
        g.Live = true;
    }

    private static void Flash(Corpse k, float x, float y)
    {
        k.FlashAt = k.T;
        k.Flash = Make("Flash", Soft(), FxMath.V3(x, y, k.Z - 0.003f), k.Go.layer, k.Tf.parent);
        k.Flash.color = FxMath.Rgba(0.62f, 0.64f, 1f, 0f);
    }

    // ---- 後片付け ----

    private static void Drop(Corpse k)
    {
        if (k.Go) Object.Destroy(k.Go);
        if (k.Stain) Object.Destroy(k.Stain.gameObject);
        if (k.Flash) Object.Destroy(k.Flash.gameObject);
        if (k.Grain != null)
            foreach (var g in k.Grain) if (g.Sr) Object.Destroy(g.Sr.gameObject);
    }

    private static void Check()
    {
        if (_gen == GameClock.ShipGen) return;
        Clear();
        _gen = GameClock.ShipGen;
    }

    private static void Clear()
    {
        foreach (var k in Live) Drop(k);
        Live.Clear();
        _gen = GameClock.ShipGen;
    }
}
