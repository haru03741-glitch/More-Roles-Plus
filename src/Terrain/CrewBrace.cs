using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 強い流れに逆らって歩くクルーの踏ん張り (全員の端末で): 上流へ体を傾けて腰を落とし、走りのアニメを遅くして 1 歩ずつ沈み込みながら進む。
// 上流側のすねに白い水の盛り上がり、踏み込む度に水しぶき、頭から汗が飛ぶ。絵は本編の絵柄に合わせた平塗り + 濃い輪郭線。
// 自分は CrewSwim が入力から決める。他人は入力が分からないので、強い流れの中で上流へ動いているかを 0.1 秒ごとの位置の差で見る。
// 体の傾きは足元を軸に回す (体の絵と帽子・服は別の根なので、それぞれの原点から足元までの距離で回し戻す)。
// 毎フレーム触るのは踏ん張っている人だけ。出ていない時は Unity に触らない
internal static class CrewBrace
{
    private const long ScanMs = 100;
    private const float StepHz = 1.7f;      // 1 秒の歩数
    private const float AnimSpeed = 0.55f;  // 走りのアニメの速さ
    private const float LeanMax = 11f;      // 体の傾き (度・流れの強さ 1.5 倍以上で最大)
    private const float LeanSide = 0.35f;   // 縦の流れでは向いている側へこれだけ傾ける
    private const float Rock = 2.5f;        // 1 歩ごとの揺れ (度)
    private const float Crouch = 0.04f;     // 腰を落とした分の縦の縮み
    private const float StepSquash = 0.035f;
    private const float StepDip = 0.03f;    // 踏み込みで沈む深さ
    private const float FadeIn = 5f, FadeOut = 6f; // 1 秒あたりの出入り
    private const float FeetZ = -0.00014f;  // 水の板 (-0.00012) より手前
    private const int Drops = 3, Splashes = 4;
    private const float DropLife = 0.6f, DropGravity = 5.5f, DropSize = 0.2f;
    private const float SplashLife = 0.4f, SplashGravity = 6f, SplashSize = 0.085f;
    private const float BackZ = 0.0001f;    // 体の奥 (盛り上がりの向こう側の半分)
    private const float RingX = 0.34f, RingY = 0.1f; // 体のまわりの水面の輪 (水のシェーダの体の半幅と輪の縦の半径に合わせる)
    private const float WaveSpanW = 0.84f, WaveSpanH = 0.5f; // 盛り上がりの絵の大きさ (ワールド)
    private const float RemoteMove = 0.2f;  // 他人: これより速く上流へ動いている時
    private const int RemoteOn = 2, RemoteOff = 3;

    private struct Bit
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public float X, Y, Vx, Vy, Age, Life;
        public bool Live;
    }

    private sealed class Crew
    {
        public PlayerControl Pc;
        public byte Id;
        public bool Slowed;
        public Transform Root, Body, Cos;
        public Vector3 Bp, Bs, Cp, Cs;
        public Quaternion Br, Cr;
        public float Inv;                  // ワールドの長さ → 体の根の中の長さ
        public float FootBy, FootCy;       // 体の絵/帽子・服の原点から足元まで (根の中の長さ)
        public float FootX, FootY;         // 根から足元まで (根の中の長さ)
        public bool FlipX;
        public PowerTools.SpriteAnim Anim, Skin;
        public float AnimBase = 1f, SkinBase = 1f;
        public GameObject Fx;
        public SpriteRenderer WaveF, WaveB;
        public Transform WaveFt, WaveBt;
        public int WaveDir = -1;
        public Bit[] Drop = new Bit[Drops], Splash = new Bit[Splashes];
        public float W, Lean, Ux, Uy, Str, Depth, PrevStep, NextDrop;
        public bool On, Posed;
        public int Hits, Misses;
        public float LastX, LastY;
        public long LastMs;
    }

    private static readonly Dictionary<byte, Crew> Crews = new();
    private static readonly List<byte> Gone = new();
    private static Sprite _drop, _splash;
    private static readonly Sprite[] WaveArt = new Sprite[16]; // 8 方向 × 手前/奥
    private static int _shipGen = -1;
    private static long _nextMs, _lastMs;
    private static int _posed;
    private static int _localId = -1;

    // 足を踏み出す勢い (-1..1): 踏み込んだ直後に強く、次の踏み込みの前は踏みしめて弱い。自分の進む速さも同じ時計で揺らす
    internal static float StepPush()
    {
        return _localId >= 0 ? MathF.Sin(MathF.PI * 2f * StepOf((byte)_localId)) : 0f;
    }

    private static float StepOf(byte id)
    {
        double t = Environment.TickCount64 / 1000.0 * StepHz + id * 0.37;
        return (float)(t - Math.Floor(t));
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[CrewBrace] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (Crews.Count == 0 && !CrewSwim.Wet) return;
        long now = Environment.TickCount64;
        float dt = FxMath.Clamp((now - _lastMs) / 1000f, 0f, 0.1f);
        _lastMs = now;
        if (GameClock.ShipGen != _shipGen) { Clear(); _shipGen = GameClock.ShipGen; }
        if (now >= _nextMs)
        {
            _nextMs = now + ScanMs;
            Scan(now);
        }
        if (_posed == 0) return;
        foreach (var c in Crews.Values) if (c.Posed) Animate(c, dt);
    }

    private static void Scan(long now)
    {
        // 抜けた人・壊れた体は手放す (毎フレームの方では生きているかを見ない)
        foreach (var kv in Crews)
        {
            var c = kv.Value;
            if (c.Pc && (c.Body || c.Cos || !c.Posed) && (c.Fx || !c.Posed)) continue;
            if (c.Posed) End(c);
            if (c.Fx) UnityEngine.Object.Destroy(c.Fx);
            Gone.Add(kv.Key);
        }
        foreach (byte id in Gone) Crews.Remove(id);
        Gone.Clear();
        if (!CrewSwim.Wet)
        {
            foreach (var c in Crews.Values) { StopAnim(c); c.On = false; }
            return;
        }
        var lp = PlayerControl.LocalPlayer;
        if (lp) _localId = lp.PlayerId;
        float walk = lp && lp.MyPhysics ? lp.MyPhysics.TrueSpeed : 2.5f;
        float need = CrewSwim.BraceCur * walk;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            Crews.TryGetValue(pc.PlayerId, out var c);
            var data = pc.Data;
            Vector2 p = pc.GetTruePosition();
            float d = pc.AmOwner ? CrewSwim.FlowAt(p, out float cx, out float cy) : WaterSim.CurrentAt(p, out cx, out cy);
            float c2 = cx * cx + cy * cy;
            bool cand = d >= 0.12f && d < CrewSwim.BraceMaxDepth && c2 >= need * need;
            bool hidden = (cand || (c != null && c.Posed)) && (data == null || data.IsDead || data.Disconnected || pc.inVent || !pc.Visible || pc.shouldAppearInvisible || !BodyShown(pc));
            if (c != null && c.Posed && hidden) { End(c); continue; }
            if (hidden) cand = false;
            if (!cand && c == null) continue;
            c ??= Crews[pc.PlayerId] = new Crew { Pc = pc, Id = pc.PlayerId, LastX = p.x, LastY = p.y, LastMs = now };
            float mag = MathF.Sqrt(c2);
            bool on;
            if (pc.AmOwner) on = cand && CrewSwim.Bracing;
            else
            {
                float sec = FxMath.Max((now - c.LastMs) / 1000f, 0.05f);
                float vx = (p.x - c.LastX) / sec, vy = (p.y - c.LastY) / sec;
                bool up = cand && -(vx * cx + vy * cy) / mag >= RemoteMove;
                if (up) { c.Hits++; c.Misses = 0; } else { c.Misses++; c.Hits = 0; }
                on = c.On ? c.Misses < RemoteOff : c.Hits >= RemoteOn;
            }
            c.LastX = p.x; c.LastY = p.y; c.LastMs = now;
            if (on)
            {
                c.Ux = -cx / mag; c.Uy = -cy / mag;
                c.Str = FxMath.Clamp01((mag / walk - CrewSwim.BraceCur) / (1.5f - CrewSwim.BraceCur));
                c.Depth = d;
                if (!c.Posed && !Begin(c)) continue;
                // 本編は走り始める度にアニメを速さ 1 で流し直すので、踏ん張っている間は毎回戻す
                if (c.Anim) c.Anim.Speed = AnimSpeed;
                if (c.Skin) c.Skin.Speed = AnimSpeed;
                c.Slowed = true;
                if (!c.On) c.FlipX = pc.cosmetics && pc.cosmetics.FlipX;
            }
            else if (c.On) StopAnim(c);
            c.On = on;
        }
    }

    // 透明になる役職は体の絵を薄くして消える (消え際も)。消えている体に踏ん張りを出すと居場所が分かる
    private static bool BodyShown(PlayerControl pc)
    {
        var body = pc.cosmetics ? pc.cosmetics.currentBodySprite : null;
        var sr = body != null ? body.BodySprite : null;
        return sr && sr.enabled && sr.color.a > 0.5f;
    }

    private static bool Begin(Crew c)
    {
        var pc = c.Pc;
        var root = pc.transform;
        c.Root = root;
        c.Body = root.Find("BodyForms");
        c.Cos = pc.cosmetics ? pc.cosmetics.transform : null;
        if (!c.Body && !c.Cos) return false;
        float s = root.localScale.x;
        c.Inv = s > 0.01f ? 1f / s : 1f;
        if (c.Body) { c.Bp = c.Body.localPosition; c.Br = c.Body.localRotation; c.Bs = c.Body.localScale; }
        if (c.Cos) { c.Cp = c.Cos.localPosition; c.Cr = c.Cos.localRotation; c.Cs = c.Cos.localScale; }
        Vector3 at = root.position;
        Vector2 feet = pc.GetTruePosition();
        c.FootX = (feet.x - at.x) * c.Inv;
        c.FootY = (feet.y - at.y) * c.Inv;
        c.FootBy = c.FootY - c.Bp.y;
        c.FootCy = c.FootY - c.Cp.y;
        var body = pc.cosmetics ? pc.cosmetics.currentBodySprite : null;
        var bsr = body != null ? body.BodySprite : null;
        c.Anim = bsr ? bsr.GetComponent<PowerTools.SpriteAnim>() : null;
        var skin = pc.cosmetics ? pc.cosmetics.skin : null;
        c.Skin = skin ? skin.animator : null;
        if (c.Anim) c.AnimBase = c.Anim.Speed;
        if (c.Skin) c.SkinBase = c.Skin.Speed;
        if (!c.Fx) MakeFx(c);
        c.Fx.SetActive(true);
        c.W = 0f;
        c.Lean = 0f;
        c.PrevStep = StepOf(c.Id);
        c.NextDrop = 0.2f;
        c.Posed = true;
        _posed++;
        return true;
    }

    private static void StopAnim(Crew c)
    {
        if (!c.Slowed) return;
        c.Slowed = false;
        if (c.Anim) c.Anim.Speed = c.AnimBase;
        if (c.Skin) c.Skin.Speed = c.SkinBase;
    }

    private static void End(Crew c)
    {
        if (c.Body) { c.Body.localPosition = c.Bp; c.Body.localRotation = c.Br; c.Body.localScale = c.Bs; }
        if (c.Cos) { c.Cos.localPosition = c.Cp; c.Cos.localRotation = c.Cr; c.Cos.localScale = c.Cs; }
        StopAnim(c);
        c.On = false;
        if (c.Fx) c.Fx.SetActive(false);
        for (int i = 0; i < Drops; i++) c.Drop[i].Live = false;
        for (int i = 0; i < Splashes; i++) c.Splash[i].Live = false;
        c.Posed = false;
        _posed--;
    }

    private static void Animate(Crew c, float dt)
    {
        c.W = c.On ? FxMath.Min(1f, c.W + dt * FadeIn) : c.W - dt * FadeOut;
        if (c.W <= 0f) { End(c); return; }
        float w = c.W * c.W * (3f - 2f * c.W);
        float face = c.FlipX ? -1f : 1f;
        float lx = c.Ux + LeanSide * face * FxMath.Abs(c.Uy);
        float target = -FxMath.Clamp(lx, -1f, 1f) * LeanMax * (0.6f + 0.4f * c.Str);
        c.Lean += (target - c.Lean) * FxMath.Min(1f, dt * 6f);

        float phase = StepOf(c.Id);
        float step = phase * 2f; step -= MathF.Floor(step);
        float rise = MathF.Sin(MathF.PI * step);           // 0 = 踏み込み・1 = 足を上げている
        float land = 1f - rise;
        float deg = (c.Lean + Rock * MathF.Sin(MathF.PI * 2f * phase)) * w;
        float sy = 1f - (Crouch + StepSquash * land * land) * w;
        float sx = 1f + 0.5f * (1f - sy);
        float dip = -StepDip * land * land * w * c.Inv;
        float rad = deg * (MathF.PI / 180f), sin = MathF.Sin(rad), cos = MathF.Cos(rad);
        var rot = FxMath.RotZ(deg);
        // 足元 (原点から (0, f)) を縮めて回した先が元の足元に戻るように原点をずらす
        if (c.Body)
        {
            float f = c.FootBy * sy;
            c.Body.localPosition = FxMath.V3(c.Bp.x + f * sin, c.Bp.y + c.FootBy - f * cos + dip, c.Bp.z);
            c.Body.localRotation = rot;
            c.Body.localScale = FxMath.V3(c.Bs.x * sx, c.Bs.y * sy, c.Bs.z);
        }
        if (c.Cos)
        {
            float f = c.FootCy * sy;
            c.Cos.localPosition = FxMath.V3(c.Cp.x + f * sin, c.Cp.y + c.FootCy - f * cos + dip, c.Cp.z);
            c.Cos.localRotation = rot;
            c.Cos.localScale = FxMath.V3(c.Cs.x * sx, c.Cs.y * sy, c.Cs.z);
        }

        bool landed = step < c.PrevStep;
        c.PrevStep = step;
        AnimateWave(c, w, land);
        if (c.On && landed) Kick(c);
        if (c.On)
        {
            c.NextDrop -= dt;
            if (c.NextDrop <= 0f) { Sweat(c); c.NextDrop = FxMath.Range(0.5f, 0.85f); }
        }
        StepBits(c.Drop, dt, DropGravity, DropSize, c.Inv, true);
        StepBits(c.Splash, dt, SplashGravity, SplashSize, c.Inv, false);
    }

    // 上流側のすねに当たって盛り上がる水: 体のまわりの水面の輪に沿う泡の三日月。輪の手前の半分は体の前、奥の半分は体の後ろに置く。
    // 踏み込みで大きく、流れが強いほど高い
    private static void AnimateWave(Crew c, float w, float land)
    {
        int dir = (int)MathF.Round(MathF.Atan2(c.Uy, c.Ux) / (MathF.PI / 4f)) & 7;
        if (dir != c.WaveDir)
        {
            c.WaveDir = dir;
            c.WaveF.sprite = WaveSprite(dir, true);
            c.WaveB.sprite = WaveSprite(dir, false);
        }
        float k = w * (0.7f + 0.3f * c.Str);
        float sx = k * c.Inv * (0.92f + 0.12f * land), sy = k * c.Inv * (0.8f + 0.35f * land);
        float y = c.FootY + c.Depth * c.Inv;
        var at = FxMath.V3(c.FootX, y, FeetZ);
        c.WaveFt.localPosition = at;
        c.WaveBt.localPosition = FxMath.V3(c.FootX, y, BackZ);
        var sc = FxMath.V3(sx, sy, 1f);
        c.WaveFt.localScale = sc;
        c.WaveBt.localScale = sc;
        var col = FxMath.Rgba(1f, 1f, 1f, w);
        c.WaveF.color = col;
        c.WaveB.color = col;
    }

    // 踏み込んだ足から下流へ跳ねる水しぶき
    private static void Kick(Crew c)
    {
        int n = 2 + (c.Str > 0.5f ? 1 : 0);
        for (int i = 0; i < Splashes && n > 0; i++)
        {
            ref var b = ref c.Splash[i];
            if (b.Live) continue;
            n--;
            float side = FxMath.Range(-1f, 1f);
            // 上流側の輪の縁から、上へ跳ねて上流側と横へ散る
            b.X = c.FootX / c.Inv + c.Ux * RingX - c.Uy * side * 0.15f;
            b.Y = c.FootY / c.Inv + c.Depth + c.Uy * RingY + 0.05f;
            b.Vx = c.Ux * FxMath.Range(0.3f, 0.7f) - c.Uy * side * 0.6f;
            b.Vy = FxMath.Range(1.3f, 1.9f);
            b.Age = 0f;
            b.Life = SplashLife * FxMath.Range(0.8f, 1.2f);
            b.Live = true;
            b.Sr.enabled = true;
        }
    }

    // 頭から下流側へ飛ぶ汗
    private static void Sweat(Crew c)
    {
        for (int i = 0; i < Drops; i++)
        {
            ref var b = ref c.Drop[i];
            if (b.Live) continue;
            float back = FxMath.Abs(c.Ux) > 0.3f ? (c.Ux > 0f ? -1f : 1f) : (c.FlipX ? 1f : -1f);
            b.X = c.FootX / c.Inv + back * 0.16f;
            b.Y = c.FootY / c.Inv + 0.66f;
            b.Vx = back * FxMath.Range(0.6f, 1.0f);
            b.Vy = FxMath.Range(1.1f, 1.5f);
            b.Age = 0f;
            b.Life = DropLife;
            b.Live = true;
            b.Sr.enabled = true;
            return;
        }
    }

    // 粒はワールドの長さで動かし、根の中へ直して置く。汗は飛ぶ向きへ尖りを向ける
    private static void StepBits(Bit[] bits, float dt, float g, float size, float inv, bool aim)
    {
        for (int i = 0; i < bits.Length; i++)
        {
            ref var b = ref bits[i];
            if (!b.Live) continue;
            b.Age += dt;
            if (b.Age >= b.Life) { b.Live = false; b.Sr.enabled = false; continue; }
            b.Vy -= g * dt;
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;
            float t = b.Age / b.Life;
            float s = size * inv * (t < 0.15f ? 0.5f + t / 0.3f : 1f - 0.4f * (t - 0.15f));
            b.Tf.localPosition = FxMath.V3(b.X * inv, b.Y * inv, FeetZ);
            b.Tf.localScale = FxMath.V3(s, s, 1f);
            if (aim) b.Tf.localRotation = FxMath.RotZ(MathF.Atan2(b.Vy, b.Vx) * (180f / MathF.PI) - 90f);
            b.Sr.color = FxMath.Rgba(1f, 1f, 1f, t < 0.7f ? 1f : (1f - t) / 0.3f);
        }
    }

    private static void MakeFx(Crew c)
    {
        var go = new GameObject("MrpBrace") { layer = c.Pc.gameObject.layer };
        go.transform.SetParent(c.Root, false);
        c.Fx = go;
        c.WaveF = Part(go, "WaveFront", null, out c.WaveFt);
        c.WaveB = Part(go, "WaveBack", null, out c.WaveBt);
        c.WaveDir = -1;
        for (int i = 0; i < Drops; i++) { c.Drop[i].Sr = Part(go, "Drop", Drop(), out c.Drop[i].Tf); c.Drop[i].Sr.enabled = false; }
        for (int i = 0; i < Splashes; i++) { c.Splash[i].Sr = Part(go, "Splash", Splash(), out c.Splash[i].Tf); c.Splash[i].Sr.enabled = false; }
    }

    private static SpriteRenderer Part(GameObject parent, string name, Sprite sp, out Transform tf)
    {
        var go = new GameObject(name) { layer = parent.layer };
        tf = go.transform;
        tf.SetParent(parent.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }

    // ── 絵 (平塗り + 濃い輪郭線・4×4 の副標本で縁をなめらかに) ─────────────────

    private const int LineR = 38, LineG = 62, LineB = 104;

    // 汗: 上が尖った雫。水色の本体・左上に白い艶
    private static Sprite Drop() => _drop ??= Bake(40, 40, (x, y) =>
    {
        float outer = Tear(x, y, 0f), inner = Tear(x, y, 0.075f);
        if (outer <= 0f) return (0, 0, 0, 0);
        bool shine = (x + 0.1f) * (x + 0.1f) + (y + 0.02f) * (y + 0.02f) < 0.06f * 0.06f;
        return inner > 0f ? (shine ? (255, 255, 255, 255) : (y < -0.2f ? (118, 190, 240, 255) : (160, 220, 255, 255))) : (LineR, LineG, LineB, 255);
    }, "MrpBraceDrop", new Vector2(0.5f, 0.5f));

    // 中心 (0, -0.12) 半径 0.27 の円と、上の尖り (0, 0.42) へ細る形。t = 内側へ縮める幅
    private static float Tear(float x, float y, float t)
    {
        const float cy = -0.12f, r = 0.27f, tip = 0.42f;
        float rr = r - t;
        if (x * x + (y - cy) * (y - cy) <= rr * rr) return 1f;
        if (y < cy || y > tip - t * 1.6f) return 0f;
        float u = (y - cy) / (tip - t * 1.6f - cy);
        return MathF.Abs(x) <= rr * MathF.Pow(1f - u, 1.15f) ? 1f : 0f;
    }

    // しぶき: 白い丸に薄い水色の影と輪郭
    private static Sprite Splash() => _splash ??= Bake(20, 20, (x, y) =>
    {
        float r = MathF.Sqrt(x * x + y * y);
        if (r > 0.46f) return (0, 0, 0, 0);
        if (r > 0.33f) return (70, 120, 170, 255);
        return y < -0.1f ? (200, 230, 248, 255) : (255, 255, 255, 255);
    }, "MrpBraceSplash", new Vector2(0.5f, 0.5f));

    // すねの水の盛り上がり: 水面の輪 (RingX × RingY) に沿って泡の玉を並べ、上流 (dir × 45°) ほど大きく積む。
    // front = 輪の手前の半分 (体の前に置く) / 奥の半分。玉は白・下側に水色の影・まとまりの外周に濃い輪郭線。
    // 玉ごとに周りの画素だけを見て (2×2 の副標本で) 一番内側の玉を残す
    private static unsafe Sprite WaveSprite(int dir, bool front)
    {
        int idx = dir * 2 + (front ? 0 : 1);
        if (WaveArt[idx]) return WaveArt[idx];
        const float ppu = 200f, line = 0.012f;
        const int ss = 2;
        int w = (int)(WaveSpanW * ppu), h = (int)(WaveSpanH * ppu);
        int sw = w * ss, sh = h * ss;
        float step = 1f / (ppu * ss), x0 = -WaveSpanW * 0.5f, y0 = -WaveSpanH * 0.5f;
        var margin = new float[sw * sh];
        var rel = new float[sw * sh];
        Array.Fill(margin, -1f);
        float au = dir * (MathF.PI / 4f);
        const int n = 40;
        for (int i = 0; i < n; i++)
        {
            float a = i * (MathF.PI * 2f / n);
            float wt = MathF.Cos(a - au);
            if (wt < 0.5f) continue;
            float sa = MathF.Sin(a);
            if (front ? sa > 0.08f : sa < -0.08f) continue;
            float jit = 0.8f + 0.4f * (0.5f + 0.5f * MathF.Sin(i * 12.9898f + dir * 4.1f));
            float r = (0.024f + 0.04f * wt * wt) * jit;
            float cx = RingX * MathF.Cos(a), cy = RingY * sa + r * 0.3f;
            int ix0 = Math.Max(0, (int)((cx - r - x0) / step)), ix1 = Math.Min(sw - 1, (int)((cx + r - x0) / step) + 1);
            int iy0 = Math.Max(0, (int)((cy - r - y0) / step)), iy1 = Math.Min(sh - 1, (int)((cy + r - y0) / step) + 1);
            for (int y = iy0; y <= iy1; y++)
            {
                float dy = y0 + (y + 0.5f) * step - cy;
                for (int x = ix0; x <= ix1; x++)
                {
                    float dx = x0 + (x + 0.5f) * step - cx;
                    float m = r - MathF.Sqrt(dx * dx + dy * dy);
                    int k = y * sw + x;
                    if (m > margin[k]) { margin[k] = m; rel[k] = dy / r + (m > 0.45f * r ? 2f : 0f); }
                }
            }
        }
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int cr = 0, cg = 0, cb = 0, ca = 0;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                int k = (y * ss + sy) * sw + x * ss + sx;
                float m = margin[k];
                if (m <= 0f) continue;
                float rv = rel[k];
                bool deep = rv > 1f;
                if (deep) rv -= 2f;
                var (r, g, b) = m < line ? (LineR, LineG, LineB) : rv < -0.3f ? (186, 222, 246) : deep && rv > 0.2f ? (255, 255, 255) : (236, 246, 255);
                cr += r; cg += g; cb += b; ca++;
            }
            if (ca == 0) continue;
            int o = (y * w + x) * 4;
            px[o] = (byte)(cr / ca); px[o + 1] = (byte)(cg / ca); px[o + 2] = (byte)(cb / ca);
            px[o + 3] = (byte)(ca * 255 / (ss * ss));
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = front ? "MrpBraceWaveF" : "MrpBraceWaveB" };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return WaveArt[idx] = sp;
    }

    private static unsafe Sprite Bake(int w, int h, Func<float, float, (int, int, int, int)> f, string name, Vector2 pivot)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int r = 0, g = 0, b = 0, a = 0;
            for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                var (cr, cg, cb, ca) = f((x + (sx + 0.5f) / 4f) / w - 0.5f, (y + (sy + 0.5f) / 4f) / h - 0.5f);
                r += cr * ca; g += cg * ca; b += cb * ca; a += ca;
            }
            int i = (y * w + x) * 4;
            if (a > 0) { px[i] = (byte)(r / a); px[i + 1] = (byte)(g / a); px[i + 2] = (byte)(b / a); }
            px[i + 3] = (byte)(a / 16);
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w, 0, SpriteMeshType.FullRect);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    internal static void Clear()
    {
        foreach (var c in Crews.Values)
        {
            if (c.Posed) End(c);
            if (c.Fx) UnityEngine.Object.Destroy(c.Fx);
        }
        Crews.Clear();
        _posed = 0;
    }

    // ── 確認用 ─────────────────────────────────────────────────────────

    internal static void Register()
    {
        TestBridge.Register("brace", "[flow <cx> <cy> [depth] | off] 流れに踏ん張る人: 状態の一覧 (flow = 自分の足元の流れと深さをこの値に固定・off = 戻す)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length >= 3 && a[0] == "flow" && float.TryParse(a[1], out float fx) && float.TryParse(a[2], out float fy))
            {
                CrewSwim.ForceCx = fx; CrewSwim.ForceCy = fy;
                CrewSwim.ForceDepth = a.Length >= 4 && float.TryParse(a[3], out float fd) ? fd : 0.25f;
                CrewSwim.Forced = true;
                reply($"OK brace flow=({fx:0.00},{fy:0.00}) depth={CrewSwim.ForceDepth:0.00}");
                return;
            }
            if (a.Length >= 1 && a[0] == "off")
            {
                CrewSwim.Forced = false;
                reply("OK brace flow off");
                return;
            }
            var sb = new System.Text.StringBuilder($"OK brace posed={_posed} self={CrewSwim.Bracing}");
            foreach (var kv in Crews)
            {
                var c = kv.Value;
                sb.Append($" | id={kv.Key} on={c.On} w={c.W:0.00} lean={c.Lean:0.0} up=({c.Ux:0.00},{c.Uy:0.00}) str={c.Str:0.00} depth={c.Depth:0.00} anim={(c.Anim ? c.Anim.Speed : -1f):0.00}");
            }
            reply(sb.ToString());
        });
    }
}
