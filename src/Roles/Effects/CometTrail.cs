using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 透明で走っているコメットの通った後に残る光の尾 (全員の端末)。2 つの層で描く:
//   もや   = 道筋に沿う淡い光。青白く生まれ、冷めながら広がって薄紫へ沈む
//   星屑   = 道筋のまわりに散る小さな星の瞬き。粒ごとに違う速さで明滅し、ゆっくり舞い落ちながら消える (もやより長く残る)
// 世界に置くので、視界の外と壁の向こうは本編の影が隠す。走っていない間・誰も透明でない間は Unity に触らない
internal static class CometTrail
{
    private const byte KindHaze = 0, KindStar = 1;
    private const float HazeGap = 0.09f;          // この距離を進むごとにもやを 1 つ
    private const float StarGap = 0.045f;         // この距離を進むごとに星屑を 1 つ (間引きあり)
    private const float StarChance = 0.7f;
    private const float HazeJitter = 0.04f, StarJitter = 0.16f; // 出る所のばらつき (星屑は道筋の外まで散らす)
    private const float Rise = 0.14f;             // 足元から胴の高さ
    private const float HazeS0 = 0.16f, HazeS1 = 0.3f, HazeA = 0.26f;
    private const float StarS0 = 0.13f, StarS1 = 0.02f, StarA = 0.95f, StarLife = 1.8f; // 寿命は設定の何倍か
    private const float StarFall = 0.12f;         // 星屑が舞い落ちる速さ (単位/秒)
    private const float MaxStep = 1f;             // 1 フレームでこれより飛んだら TP として繋がない

    private sealed class Source
    {
        public PlayerControl Pc;
        public byte Id;
        public bool Has;
        public float X, Y, HazeCarry, StarCarry;
    }

    private struct Grain
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public byte Kind;
        public float X, Y, Z, Vx, Vy, Born, Life, Size, Phase, Rate;
        public bool Live;
    }

    private static readonly List<Source> Sources = new();
    private static readonly Grain[] Grains = new Grain[128];
    private static int _live;
    private static Sprite _soft, _star;
    private static object _match;
    private static bool _tracking; // どれかの Source が位置を持っている

    // 全員の端末: 役職を受け取った時
    internal static void Watch(PlayerControl pc)
    {
        if (!pc) return;
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(() => { Clear(); Sources.Clear(); _match = null; });
        }
        foreach (var s in Sources) if (s.Id == pc.PlayerId) return;
        Sources.Add(new Source { Pc = pc, Id = pc.PlayerId });
    }

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        if (Sources.Count == 0 && _live == 0) return;
        if (!ShipStatus.Instance || RewindPlayback.Active || MeetingHud.Instance)
        {
            // 会議・時戻しの間は残った粒を隠すだけ (作った物は次に使い回す)
            if (_live > 0) Hide();
            if (_tracking) ResetSources();
            return;
        }
        float life = Crew.Comet.TrailLife;
        float now = Time.time, dt = Time.deltaTime;
        for (int i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            if (life <= 0f || !Cloak.IsOn(s.Id) || !s.Pc || s.Pc.inVent || !PortKit.Alive(s.Pc)) { s.Has = false; continue; }
            var p = s.Pc.transform.position;
            if (!s.Has) { s.Has = _tracking = true; s.X = p.x; s.Y = p.y; s.HazeCarry = s.StarCarry = 0f; continue; }
            float dx = p.x - s.X, dy = p.y - s.Y, d2 = dx * dx + dy * dy;
            if (d2 > MaxStep * MaxStep) { s.X = p.x; s.Y = p.y; s.HazeCarry = s.StarCarry = 0f; continue; }
            if (d2 < 1e-6f) continue;
            float d = FxMath.Sqrt(d2);
            // 前のフレームの位置から今の位置まで、間隔ごとに置く (速く走っても途切れない)
            s.HazeCarry = Lay(KindHaze, s, dx, dy, d, p.z, s.HazeCarry, HazeGap, life, now);
            s.StarCarry = Lay(KindStar, s, dx, dy, d, p.z, s.StarCarry, StarGap, life * StarLife, now);
            s.X = p.x;
            s.Y = p.y;
        }
        if (_live == 0) return;
        for (int i = 0; i < Grains.Length; i++)
        {
            ref var g = ref Grains[i];
            if (!g.Live) continue;
            float u = (now - g.Born) / g.Life;
            if (u >= 1f || !g.Sr)
            {
                g.Live = false;
                _live--;
                if (g.Sr) g.Sr.gameObject.SetActive(false);
                continue;
            }
            if (g.Kind == KindHaze)
            {
                float a = HazeA * (u < 0.1f ? u / 0.1f : 1f - (u - 0.1f) / 0.9f);
                float size = g.Size * (1f + (HazeS1 / HazeS0 - 1f) * u);
                g.Tf.localScale = FxMath.V3(size, size, 1f);
                // 青白 (0.8,0.92,1) → 薄紫 (0.66,0.58,0.98)
                g.Sr.color = FxMath.Rgba(0.8f - 0.14f * u, 0.92f - 0.34f * u, 1f - 0.02f * u, a);
            }
            else
            {
                // 舞い落ちながら、横の勢いはすぐに抜ける
                g.Vx *= 1f - FxMath.Min(1f, 3f * dt);
                g.X += g.Vx * dt;
                g.Y += g.Vy * dt;
                g.Tf.position = FxMath.V3(g.X, g.Y, g.Z);
                // 瞬き: 粒ごとの速さで明滅し、全体は終わりへ向けて消える
                float tw = 0.55f + 0.45f * FxMath.Sin(g.Phase + (now - g.Born) * g.Rate);
                float a = StarA * tw * (u < 0.06f ? u / 0.06f : 1f - (u - 0.06f) / 0.94f);
                float size = g.Size * (1f - (1f - StarS1 / StarS0) * u) * (0.85f + 0.15f * tw);
                g.Tf.localScale = FxMath.V3(size, size, 1f);
                // 淡い水色 → 薄紫 (真っ白だと明るい床に溶ける)
                g.Sr.color = FxMath.Rgba(0.82f - 0.1f * u, 0.94f - 0.26f * u, 1f, a);
            }
        }
    }

    // d 進んだ間に gap ごとに粒を置き、次へ持ち越す距離を返す
    private static float Lay(byte kind, Source s, float dx, float dy, float d, float z, float carry, float gap, float life, float now)
    {
        for (float t = gap - carry; t <= d; t += gap)
        {
            if (kind == KindStar && Random.value > StarChance) continue;
            float k = t / d;
            Emit(kind, s.X + dx * k, s.Y + dy * k, z, life, now);
        }
        return (carry + d) % gap;
    }

    private static void Emit(byte kind, float x, float y, float z, float life, float now)
    {
        int slot = -1;
        for (int i = 0; i < Grains.Length; i++) if (!Grains[i].Live) { slot = i; break; }
        if (slot < 0) return; // 上限 (古い粒が消えるまで足さない)
        ref var g = ref Grains[slot];
        if (!g.Sr)
        {
            var go = FxRoot.New("MrpCometTrail");
            g.Sr = go.AddComponent<SpriteRenderer>();
            g.Tf = go.transform;
        }
        else g.Sr.gameObject.SetActive(true);
        g.Kind = kind;
        float j = kind == KindStar ? StarJitter : HazeJitter;
        g.X = x + (Random.value - 0.5f) * 2f * j;
        g.Y = y + Rise + (Random.value - 0.5f) * 2f * j;
        // 体の少し奥 (通った後に残る物なので、戻ってきた本人の体より手前に出さない)。星屑はもやの手前
        g.Z = z + (kind == KindStar ? 0.0015f : 0.002f);
        g.Tf.position = FxMath.V3(g.X, g.Y, g.Z);
        if (kind == KindStar)
        {
            _star ??= FxSprites.Rect(64, 64, Star, "MrpCometStar");
            g.Sr.sprite = _star;
            g.Size = StarS0 * (0.6f + 0.8f * Random.value);
            g.Phase = Random.value * 6.2831855f;
            g.Rate = 14f + 16f * Random.value;
            g.Vx = (Random.value - 0.5f) * 0.25f;
            g.Vy = -StarFall * (0.5f + Random.value);
            // 星の筋を少しずつ傾けて、全部が同じ向きに揃わないようにする
            g.Tf.rotation = FxMath.RotZ((Random.value - 0.5f) * 30f);
        }
        else
        {
            _soft ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpCometSoft");
            g.Sr.sprite = _soft;
            g.Size = HazeS0 * (0.8f + 0.4f * Random.value);
            g.Vx = g.Vy = 0f;
        }
        g.Tf.localScale = FxMath.V3(g.Size, g.Size, 1f);
        g.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        g.Born = now;
        g.Life = life * (0.8f + 0.4f * Random.value);
        g.Live = true;
        _live++;
    }

    // 4 本の細い光の筋 + 明るい芯 + 淡いにじみ (小さく描くと瞬く星に見える)
    private static float Star(float x, float y)
    {
        float ax = FxMath.Abs(x) * 2f, ay = FxMath.Abs(y) * 2f;
        float r2 = ax * ax + ay * ay;
        float ray = FxMath.Exp(-ay * ay * 900f) * FxMath.Max(0f, 1f - ax) + FxMath.Exp(-ax * ax * 900f) * FxMath.Max(0f, 1f - ay);
        float core = FxMath.Exp(-r2 * 60f);
        float glow = 0.35f * FxMath.Exp(-r2 * 9f);
        return ray * 0.9f + core + glow;
    }

    private static void Hide()
    {
        for (int i = 0; i < Grains.Length; i++)
        {
            ref var g = ref Grains[i];
            if (!g.Live) continue;
            g.Live = false;
            if (g.Sr) g.Sr.gameObject.SetActive(false);
        }
        _live = 0;
    }

    private static void ResetSources()
    {
        foreach (var s in Sources) s.Has = false;
        _tracking = false;
    }

    private static void Clear()
    {
        for (int i = 0; i < Grains.Length; i++)
        {
            if (Grains[i].Sr) Object.Destroy(Grains[i].Sr.gameObject);
            Grains[i] = default;
        }
        _live = 0;
        ResetSources();
    }
}
