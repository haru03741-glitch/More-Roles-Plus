using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// ゲッサーが会議の名札を撃ち抜く演出 (全員の画面)。撃たれた人の名札に照準が縮み → 銃声と閃光 → 名札が弾かれて弾痕とひびが残る。
// 撃った人は出さない。倒れる (名札の ✕) のはホストが HitAt の少し後に起こす (GuessKit)。
// 物は全部その名札の子に置く (会議の画面の層で描かれ、会議が閉じれば一緒に消える)。弾痕だけは会議の終わりまで残す
internal static class GuessShotFx
{
    internal const float HitAt = 0.3f;              // 照準が縮み切って撃つ時刻
    private const float End = 1.1f;                 // 弾痕以外が消える時刻
    private const float ShakeTime = 0.25f, ShakeAmp = 0.06f;
    private const float RecoilDist = 0.08f, RecoilDeg = 4f, RecoilTau = 0.12f, RecoilPeriod = 0.28f;
    private const int Streaks = 7, Chips = 5;
    private const float Gravity = -5f;
    private const float DimAlpha = 0.5f;
    private const float StreakAspect = 8f;          // 破片の線の絵は 128×16 = 横 8 単位で焼かれる

    private sealed class Piece
    {
        public Transform Tf;
        public SpriteRenderer R;
        public float Vx, Vy, Spin, Life, X, Y, Rot, Size;
        public Color C;
    }

    private sealed class Shot
    {
        public byte Id;
        public float Start;
        public bool Fired;
        public Transform Card;
        public Vector3 CardPos;
        public Quaternion CardRot;
        public float Fx, Fy;                       // 撃ち抜く所 (名札の中の位置)
        public float Dx, Dy;                       // 弾の進む向き (名札が弾かれる向き)
        public GameObject Root;
        public SpriteRenderer Dim, Reticle, Under, Glow, Core;
        public readonly List<Piece> Streak = new(), Chip = new();
    }

    private static readonly List<Shot> Active = new();
    private static Sprite _reticle, _glow, _core, _streak, _hole, _chip, _square;

    // 揺らしている会議の画面と元の位置 (2 発が重なっても元の位置を取り違えない)
    private static Transform _shakeTf;
    private static Vector3 _shakeBase;
    private static float _shakeUntil;

    internal static int Started, Fired;

    // 全員の端末で: その人の名札で演出を始める
    internal static void Begin(byte id)
    {
        var mh = MeetingHud.Instance;
        if (!mh) return;
        PlayerVoteArea card = null;
        foreach (var s in mh.playerStates)
            if (s && s.PlayerId.Value == id) { card = s; break; }
        if (!card) return;
        EnsureSprites();

        var tf = card.transform;
        var shot = new Shot { Id = id, Start = Time.time, Card = tf, CardPos = tf.localPosition, CardRot = tf.localRotation };
        // 顔の絵の少し右上を撃つ
        var face = card.PlayerIcon ? card.PlayerIcon.transform.localPosition : FxMath.V3(-0.95f, 0f, 0f);
        shot.Fx = face.x + 0.18f;
        shot.Fy = face.y + 0.12f;
        float a = FxMath.Range(-0.5f, 0.15f);
        shot.Dx = FxMath.Cos(a);
        shot.Dy = FxMath.Sin(a);

        int layer = card.gameObject.layer;
        var root = new GameObject("MrpGuessShot") { layer = layer };
        root.transform.SetParent(tf, false);
        shot.Root = root;

        // 名札の地を少し暗く (撃たれる名札に目を集める)
        float w = 2.9f, h = 0.75f;
        if (card.Background)
        {
            var b = card.Background.bounds.size;
            var ls = tf.lossyScale;
            if (ls.x > 0.0001f && ls.y > 0.0001f) { w = b.x / ls.x; h = b.y / ls.y; }
        }
        shot.Dim = Make(root, "Dim", _square, 0f, 0f, -0.3f, w, h, FxMath.Rgba(0.05f, 0.02f, 0.04f, 0f), layer);
        shot.Reticle = Make(root, "Reticle", _reticle, shot.Fx, shot.Fy, -2.3f, 0.55f, 0.55f, FxMath.Rgba(1f, 0.18f, 0.16f, 0f), layer);
        Started++;
        Active.Add(shot);
        BreakNoise.PlayMeeting("noise_guess_aim", 0.7f);
    }

    // Plugin.Update から。演出が無い間は何もしない
    internal static void Tick()
    {
        GuessKit.HostTick();
        if (Active.Count == 0) return;
        float now = Time.time;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var s = Active[i];
            if (!s.Card || !s.Root) { Active.RemoveAt(i); continue; }
            float t = now - s.Start;
            if (!s.Fired && t >= HitAt) Fire(s);
            Animate(s, t);
            if (t >= End)
            {
                s.Card.localPosition = s.CardPos;
                s.Card.localRotation = s.CardRot;
                Clear(s);
                Active.RemoveAt(i);
            }
        }
        TickShake(now);
    }

    private static void Fire(Shot s)
    {
        s.Fired = true;
        Fired++;
        int layer = s.Root.layer;
        // 光の重ね: 暗い下地 → 橙のにじみ → 白い核
        s.Under = Make(s.Root, "Under", _glow, s.Fx, s.Fy, -2.25f, 1.0f, 1.0f, FxMath.Rgba(0.1f, 0.02f, 0f, 0.5f), layer);
        s.Glow = Make(s.Root, "Glow", _glow, s.Fx, s.Fy, -2.35f, 1.1f, 1.1f, FxMath.Rgba(1f, 0.55f, 0.15f, 0.85f), layer);
        s.Core = Make(s.Root, "Core", _core, s.Fx, s.Fy, -2.45f, 0.45f, 0.45f, FxMath.Rgba(1f, 1f, 0.95f, 1f), layer);
        // 弾痕とひび (会議の終わりまで残す)
        // 名札の ✕ (z -2) より手前に置く (撃ち抜いた跡が倒れた印に隠れないように)
        var hole = Make(s.Root, "Hole", _hole, s.Fx, s.Fy, -2.2f, 0.5f, 0.5f, FxMath.Rgba(0.08f, 0.08f, 0.1f, 0.92f), layer);
        hole.transform.localRotation = FxMath.RotZ(FxMath.Range(0f, 360f));
        hole.transform.SetParent(s.Card, true);

        // 破片の飛ぶ線 (弾の抜ける向きへ寄せて散らす)
        float baseDeg = FxMath.Atan2(s.Dy, s.Dx) * (180f / System.MathF.PI);
        for (int i = 0; i < Streaks; i++)
        {
            float deg = baseDeg + FxMath.Range(-80f, 80f) + (i % 3 == 0 ? 180f : 0f);
            float r = deg * (System.MathF.PI / 180f);
            float len = FxMath.Range(0.3f, 0.55f);
            var p = new Piece { X = s.Fx, Y = s.Fy, Vx = FxMath.Cos(r) * 2.3f, Vy = FxMath.Sin(r) * 2.3f, Rot = deg, Size = len, Life = FxMath.Range(0.12f, 0.18f), C = FxMath.Rgba(1f, 0.9f, 0.7f, 1f) };
            p.R = Make(s.Root, "Streak", _streak, p.X, p.Y, -2.4f, len / StreakAspect, 0.035f, p.C, layer);
            p.Tf = p.R.transform;
            p.Tf.localRotation = FxMath.RotZ(deg);
            s.Streak.Add(p);
        }
        // 名札のかけら (名札の地の色で落ちる)
        for (int i = 0; i < Chips; i++)
        {
            float sz = FxMath.Range(0.05f, 0.09f);
            var p = new Piece { X = s.Fx, Y = s.Fy, Vx = s.Dx * FxMath.Range(0.6f, 1.4f) + FxMath.Range(-0.9f, 0.9f), Vy = FxMath.Range(0.9f, 1.6f), Spin = FxMath.Range(360f, 900f) * (i % 2 == 0 ? 1f : -1f), Size = sz, Life = FxMath.Range(0.5f, 0.8f), C = FxMath.Rgba(0.72f, 0.74f, 0.8f, 1f) };
            p.R = Make(s.Root, "Chip", _chip, p.X, p.Y, -2.42f, sz, sz, p.C, layer);
            p.Tf = p.R.transform;
            s.Chip.Add(p);
        }
        StartShake();
        BreakNoise.PlayMeeting("noise_guess_shot", 1f);
    }

    private static void Animate(Shot s, float t)
    {
        // 照準: 1.6 倍から縮み、少し回りながら止まる → 撃った瞬間に白く光って消える
        if (s.Reticle)
        {
            if (t < HitAt)
            {
                float k = t / HitAt, e = 1f - (1f - k) * (1f - k);
                float sc = 0.55f * (1.6f - 0.6f * e);
                s.Reticle.transform.localScale = FxMath.V3(sc, sc, 1f);
                s.Reticle.transform.localRotation = FxMath.RotZ(25f * (1f - e));
                s.Reticle.color = FxMath.Rgba(1f, 0.18f, 0.16f, FxMath.Clamp01(t / 0.1f));
            }
            else
            {
                float k = FxMath.Clamp01((t - HitAt) / 0.15f);
                s.Reticle.color = FxMath.Rgba(1f, 1f - 0.8f * k, 1f - 0.8f * k, 1f - k);
            }
        }
        if (s.Dim)
        {
            float a = t < HitAt ? DimAlpha * t / HitAt : t < 0.6f ? DimAlpha : DimAlpha * FxMath.Clamp01((1f - t) / 0.4f);
            s.Dim.color = FxMath.Rgba(0.05f, 0.02f, 0.04f, a);
        }
        if (!s.Fired) return;
        float f = t - HitAt;
        if (s.Core) s.Core.color = FxMath.Rgba(1f, 1f, 0.95f, FxMath.Clamp01(1f - f / 0.08f));
        if (s.Glow)
        {
            float k = FxMath.Clamp01(f / 0.25f), sc = 1.1f + 0.2f * k;
            s.Glow.transform.localScale = FxMath.V3(sc, sc * 0.8f, 1f);
            s.Glow.color = FxMath.Rgba(1f, 0.55f, 0.15f, 0.85f * (1f - k));
        }
        if (s.Under) s.Under.color = FxMath.Rgba(0.1f, 0.02f, 0f, 0.5f * FxMath.Clamp01(1f - f / 0.4f));
        foreach (var p in s.Streak)
        {
            if (!p.Tf) continue;
            float k = FxMath.Clamp01(f / p.Life);
            p.Tf.localPosition = FxMath.V3(p.X + p.Vx * f * 0.15f / p.Life, p.Y + p.Vy * f * 0.15f / p.Life, -2.4f);
            p.Tf.localScale = FxMath.V3(p.Size * (1f - 0.5f * k) / StreakAspect, 0.035f, 1f);
            p.R.color = FxMath.Rgba(p.C.r, p.C.g, p.C.b, 1f - k);
        }
        foreach (var p in s.Chip)
        {
            if (!p.Tf) continue;
            float x = p.X + p.Vx * f, y = p.Y + p.Vy * f + 0.5f * Gravity * f * f;
            p.Tf.localPosition = FxMath.V3(x, y, -2.42f);
            p.Tf.localRotation = FxMath.RotZ(p.Spin * f);
            p.R.color = FxMath.Rgba(p.C.r, p.C.g, p.C.b, FxMath.Clamp01((p.Life - f) / 0.2f));
        }
        // 名札が弾の向きへ弾かれ、ばねで戻る
        float d = System.MathF.Exp(-f / RecoilTau) * FxMath.Cos(f * (2f * System.MathF.PI / RecoilPeriod));
        if (f > 0.7f) d = 0f;
        s.Card.localPosition = FxMath.V3(s.CardPos.x + s.Dx * RecoilDist * d, s.CardPos.y + s.Dy * RecoilDist * d, s.CardPos.z);
        s.Card.localRotation = FxMath.RotZ(-RecoilDeg * d * (s.Dx >= 0f ? 1f : -1f));
    }

    private static void StartShake()
    {
        var mh = MeetingHud.Instance;
        if (!mh) return;
        if (_shakeTf == null || !_shakeTf)
        {
            _shakeTf = mh.transform;
            _shakeBase = _shakeTf.localPosition;
        }
        _shakeUntil = Time.time + ShakeTime;
    }

    private static void TickShake(float now)
    {
        if (_shakeTf == null) return;
        if (!_shakeTf) { _shakeTf = null; return; }
        if (now >= _shakeUntil)
        {
            _shakeTf.localPosition = _shakeBase;
            _shakeTf = null;
            return;
        }
        float amp = ShakeAmp * (_shakeUntil - now) / ShakeTime;
        _shakeTf.localPosition = FxMath.V3(_shakeBase.x + FxMath.Range(-amp, amp), _shakeBase.y + FxMath.Range(-amp, amp), _shakeBase.z);
    }

    private static void Clear(Shot s)
    {
        if (s.Root) Object.Destroy(s.Root);
    }

    // 会議が閉じる時: 揺らしている画面と弾いている名札を元の位置へ戻し、参照を捨てる (物は名札と一緒に消える)
    internal static void Reset()
    {
        foreach (var s in Active)
        {
            if (!s.Card) continue;
            s.Card.localPosition = s.CardPos;
            s.Card.localRotation = s.CardRot;
        }
        Active.Clear();
        if (_shakeTf != null && _shakeTf) _shakeTf.localPosition = _shakeBase;
        _shakeTf = null;
    }

    private static SpriteRenderer Make(GameObject parent, string name, Sprite sp, float x, float y, float z, float sx, float sy, Color c, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(sx, sy, 1f);
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = sp;
        r.color = c;
        return r;
    }

    internal static string Describe() => $"active={Active.Count} started={Started} fired={Fired} shaking={(_shakeTf != null)}";

    // ── 絵 (式から 1 回だけ焼く) ─────────────────────────────

    private static void EnsureSprites()
    {
        if (_reticle) return;
        _square = FxSprites.Rect(8, 8, (_, _) => 1f, "mrp_guess_square");
        _glow = FxSprites.Rect(128, 128, FxSprites.Soft, "mrp_guess_glow");
        _core = FxSprites.Rect(128, 128, FxSprites.Dot, "mrp_guess_core");
        _reticle = FxSprites.Rect(256, 256, Reticle, "mrp_guess_reticle");
        _streak = FxSprites.Rect(128, 16, (x, y) =>
        {
            float across = FxMath.Clamp01(1f - FxMath.Abs(y) * 2.2f);
            return across * across * FxMath.Clamp01(x + 0.5f);
        }, "mrp_guess_streak");
        _chip = FxSprites.Rect(32, 32, (x, y) => y > -0.42f && y < 0.38f - FxMath.Abs(x) * 1.6f + (x > 0f ? 0.1f : 0f) ? 1f : 0f, "mrp_guess_chip");
        BuildCracks();
        _hole = FxSprites.Rect(256, 256, Hole, "mrp_guess_hole");
    }

    // 十字 (中心を空けた 4 本の腕) + 腕の間だけの細い輪の切れ端 + 中心の小さな点
    private static float Reticle(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y);
        float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
        float arm = 0f;
        if (r > 0.1f && r < 0.46f) arm = FxMath.Max(FxMath.Clamp01(1.5f - ay / 0.012f) * (ax > ay ? 1f : 0f), FxMath.Clamp01(1.5f - ax / 0.012f) * (ay > ax ? 1f : 0f));
        float ring = FxMath.Clamp01(1.5f - FxMath.Abs(r - 0.32f) / 0.009f);
        float ang = FxMath.Atan2(ay, ax);
        if (ang < 0.35f || ang > 1.22f) ring = 0f;
        float dot = FxMath.Clamp01(1f - r / 0.02f);
        return FxMath.Max(arm, FxMath.Max(ring, dot));
    }

    // ひびの線分 (中心から 6 本・3 節で曲がる・先ほど細い)
    private static readonly List<(float x0, float y0, float x1, float y1, float w)> Cracks = new();

    private static void BuildCracks()
    {
        Cracks.Clear();
        var rng = new System.Random(20261101);
        const int n = 6;
        for (int i = 0; i < n; i++)
        {
            float ang = (i + (float)rng.NextDouble() * 0.6f) * (2f * System.MathF.PI / n);
            float x = 0f, y = 0f, reach = 0.28f + (float)rng.NextDouble() * 0.12f;
            for (int s = 0; s < 3; s++)
            {
                float a = ang + ((float)rng.NextDouble() - 0.5f) * 0.7f;
                float len = reach / 3f;
                float nx = x + System.MathF.Cos(a) * len, ny = y + System.MathF.Sin(a) * len;
                Cracks.Add((x, y, nx, ny, 0.007f - s * 0.0018f));
                x = nx; y = ny;
            }
        }
    }

    // 黒い穴 (縁は少し明るく抜ける) + ひび
    private static float Hole(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y);
        float hole = FxMath.Clamp01((0.11f - r) / 0.015f);
        float rim = FxMath.Clamp01(1f - FxMath.Abs(r - 0.13f) / 0.014f) * 0.55f;
        float crack = 0f;
        foreach (var c in Cracks)
        {
            float dx = c.x1 - c.x0, dy = c.y1 - c.y0;
            float t = FxMath.Clamp01(((x - c.x0) * dx + (y - c.y0) * dy) / (dx * dx + dy * dy));
            float px = c.x0 + dx * t - x, py = c.y0 + dy * t - y;
            float d = FxMath.Sqrt(px * px + py * py);
            crack = FxMath.Max(crack, FxMath.Clamp01(1f - d / c.w));
        }
        return FxMath.Max(hole, FxMath.Max(rim, crack * 0.9f));
    }
}

[HarmonyLib.HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
internal static class GuessShotFxClosePatch
{
    public static void Prefix() => GuessShotFx.Reset();
}
