using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 守り霊が瀕死の人を救った時の光 (全員の端末)。倒れた体を包む淡い白金の光がふくらんでから薄れ、
// 体のまわりから小さな光の粒が瞬きながらゆっくり立ちのぼって消える。世界に置くので視界の外は本編の影が隠す
internal static class WardSpiritFx
{
    private const int Motes = 36;
    private const float GlowLife = 1.2f, MoteLife = 1.7f;
    private const float GlowS0 = 0.55f, GlowS1 = 1.4f, GlowA = 0.62f;
    private const float MoteS = 0.14f, MoteA = 0.95f, MoteRise = 0.55f, MoteSpread = 0.32f;

    private struct Grain
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public bool Glow, Live;
        public float X, Y, Z, Vx, Vy, Born, Life, Size, Phase, Rate;
    }

    private static readonly Grain[] Grains = new Grain[96];
    private static int _live;
    private static Sprite _soft, _star;

    internal static void Rise(PlayerControl p)
    {
        if (!p || !ShipStatus.Instance) return;
        var pos = p.transform.position;
        float now = Time.time;
        float cx = pos.x, cy = pos.y + 0.1f, z = pos.z - 0.003f;
        Emit(true, cx, cy, z, 0f, 0f, GlowLife, GlowS0, now);
        for (int i = 0; i < Motes; i++)
        {
            float ox = (Random.value - 0.5f) * 2f * MoteSpread, oy = (Random.value - 0.5f) * MoteSpread;
            float delay = Random.value * 0.5f; // 一度に出さず、少しずつ立ちのぼる
            Emit(false, cx + ox, cy + oy, z - 0.001f, (Random.value - 0.5f) * 0.1f, MoteRise * (0.6f + 0.8f * Random.value),
                MoteLife * (0.7f + 0.6f * Random.value), MoteS * (0.6f + 0.8f * Random.value), now + delay);
        }
    }

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        if (_live == 0) return;
        if (!ShipStatus.Instance || RewindPlayback.Active || MeetingHud.Instance) { Hide(); return; }
        float now = Time.time, dt = Time.deltaTime;
        for (int i = 0; i < Grains.Length; i++)
        {
            ref var g = ref Grains[i];
            if (!g.Live) continue;
            float age = now - g.Born;
            if (age < 0f) continue; // まだ出ていない
            float u = age / g.Life;
            if (u >= 1f || !g.Sr)
            {
                g.Live = false;
                _live--;
                if (g.Sr) g.Sr.gameObject.SetActive(false);
                continue;
            }
            if (g.Glow)
            {
                // すぐ明るくなり、広がりながら薄れる
                float a = GlowA * (u < 0.15f ? u / 0.15f : 1f - (u - 0.15f) / 0.85f);
                float s = GlowS0 + (GlowS1 - GlowS0) * FxMath.Sqrt(u);
                g.Tf.localScale = FxMath.V3(s, s * 1.25f, 1f);
                g.Sr.color = FxMath.Rgba(1f, 0.93f, 0.72f, a);
                continue;
            }
            g.X += g.Vx * dt;
            g.Y += g.Vy * dt;
            g.Vy *= 1f - FxMath.Min(1f, 0.6f * dt);
            g.Tf.position = FxMath.V3(g.X, g.Y, g.Z);
            float tw = 0.6f + 0.4f * FxMath.Sin(g.Phase + age * g.Rate);
            float ma = MoteA * tw * (u < 0.1f ? u / 0.1f : 1f - (u - 0.1f) / 0.9f);
            float size = g.Size * (1f - 0.5f * u) * (0.85f + 0.15f * tw);
            g.Tf.localScale = FxMath.V3(size, size, 1f);
            // 白金 → 淡い金
            g.Sr.color = FxMath.Rgba(1f, 0.96f - 0.1f * u, 0.82f - 0.2f * u, ma);
        }
    }

    private static void Emit(bool glow, float x, float y, float z, float vx, float vy, float life, float size, float born)
    {
        int slot = -1;
        for (int i = 0; i < Grains.Length; i++) if (!Grains[i].Live) { slot = i; break; }
        if (slot < 0) return;
        ref var g = ref Grains[slot];
        if (!g.Sr)
        {
            var go = FxRoot.New("MrpWardSpirit");
            g.Sr = go.AddComponent<SpriteRenderer>();
            g.Tf = go.transform;
        }
        else g.Sr.gameObject.SetActive(true);
        if (glow) g.Sr.sprite = _soft ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpWardSoft");
        else g.Sr.sprite = _star ??= FxSprites.Rect(64, 64, CometTrail.Star, "MrpWardStar");
        g.Glow = glow;
        g.X = x; g.Y = y; g.Z = z;
        g.Vx = vx; g.Vy = vy;
        g.Born = born;
        g.Life = life;
        g.Size = size;
        g.Phase = Random.value * 6.2831855f;
        g.Rate = 10f + 12f * Random.value;
        g.Tf.position = FxMath.V3(x, y, z);
        g.Tf.localScale = FxMath.V3(size, size, 1f);
        g.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        g.Live = true;
        _live++;
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
}
