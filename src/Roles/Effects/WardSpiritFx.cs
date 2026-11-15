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

    // 守り霊に運ばれている体から、淡い光の粒が 1 つこぼれて立ちのぼる (運んでいる間 0.1 秒ごと)
    internal static void Drift(PlayerControl p)
    {
        if (!p || !ShipStatus.Instance) return;
        var pos = p.transform.position;
        Emit(false, pos.x + (Random.value - 0.5f) * 0.45f, pos.y + Random.value * 0.3f, pos.z - 0.004f,
            (Random.value - 0.5f) * 0.08f, MoteRise * (0.4f + 0.4f * Random.value), MoteLife * 0.6f, MoteS * (0.5f + 0.5f * Random.value), Time.time);
    }

    // 守り霊に運ばれて宙に浮いている体を包む淡い光と、真下の床に落ちる体の影 (高いほど小さく薄い)。
    // 運ぶ側は物理の刻みで Hold を呼び続け、光は描画のコマごとに体を追う。呼ばれなくなると薄れて消える
    private struct Aura
    {
        public PlayerControl Pc;
        public byte Id;
        public SpriteRenderer Body, Floor;
        public float A, Lift, Seen;
        public bool On;
    }
    private static readonly Aura[] Auras = new Aura[4];
    private static int _auras;
    private const float AuraA = 0.5f, FloorA = 0.34f, AuraIn = 3f, AuraOut = 4f, AuraHold = 0.12f;

    internal static void Hold(PlayerControl p, float lift)
    {
        if (!p || !ShipStatus.Instance) return;
        byte id = p.PlayerId;
        int slot = -1, free = -1;
        for (int i = 0; i < Auras.Length; i++)
        {
            if (Auras[i].On && Auras[i].Id == id) { slot = i; break; }
            if (free < 0 && !Auras[i].On) free = i;
        }
        if (slot < 0)
        {
            if (free < 0) return;
            slot = free;
            ref var n = ref Auras[slot];
            if (!n.Body)
            {
                n.Body = FxRoot.New("MrpWardAura").AddComponent<SpriteRenderer>();
                n.Floor = FxRoot.New("MrpWardPool").AddComponent<SpriteRenderer>();
                n.Body.sprite = n.Floor.sprite = _soft ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpWardSoft");
            }
            n.Body.gameObject.SetActive(true);
            n.Floor.gameObject.SetActive(true);
            n.Id = id;
            n.A = 0f;
            n.On = true;
            _auras++;
        }
        ref var a = ref Auras[slot];
        a.Pc = p;
        a.Lift = lift;
        a.Seen = Time.time;
    }

    private static void TickAuras(float now, float dt)
    {
        for (int i = 0; i < Auras.Length; i++)
        {
            ref var a = ref Auras[i];
            if (!a.On) continue;
            bool held = now - a.Seen < AuraHold && a.Pc && a.Body;
            a.A = held ? FxMath.Min(1f, a.A + AuraIn * dt) : a.A - AuraOut * dt;
            if (a.A <= 0f || !a.Body || !a.Pc)
            {
                a.On = false;
                a.Pc = null;
                _auras--;
                if (a.Body) { a.Body.gameObject.SetActive(false); a.Floor.gameObject.SetActive(false); }
                continue;
            }
            var pos = a.Pc.transform.position;
            float breathe = 0.92f + 0.08f * FxMath.Sin(now * 2.1f + a.Id);
            // 体は足元から Lift だけ上 (世界の単位)。光は体の中ほどに体より手前、影は足元の床に平たく敷いて人より奥へ
            a.Body.transform.position = FxMath.V3(pos.x, pos.y + 0.05f + a.Lift, pos.z - 0.002f);
            a.Body.transform.localScale = FxMath.V3(1.5f * breathe, 1.15f * breathe, 1f);
            a.Body.color = FxMath.Rgba(1f, 0.93f, 0.72f, AuraA * a.A * breathe);
            a.Floor.transform.position = FxMath.V3(pos.x, pos.y - 0.36f, pos.z + 0.004f);
            float fs = 1f - 1.5f * a.Lift;
            a.Floor.transform.localScale = FxMath.V3(0.75f * fs, 0.26f * fs, 1f);
            a.Floor.color = FxMath.Rgba(0f, 0f, 0f, FloorA * a.A * fs);
        }
    }

    private static void HideAuras()
    {
        for (int i = 0; i < Auras.Length; i++)
        {
            ref var a = ref Auras[i];
            if (!a.On) continue;
            a.On = false;
            a.Pc = null;
            if (a.Body) { a.Body.gameObject.SetActive(false); a.Floor.gameObject.SetActive(false); }
        }
        _auras = 0;
    }

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        if (_live == 0 && _auras == 0) return;
        if (!ShipStatus.Instance || RewindPlayback.Active || MeetingHud.Instance) { Hide(); HideAuras(); return; }
        if (_auras > 0) TickAuras(Time.time, Time.deltaTime);
        if (_live == 0) return;
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
