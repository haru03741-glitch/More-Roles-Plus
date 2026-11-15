using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 透明で走っているコメットの通った後に残る、かすかな光の粒 (全員の端末)。
// 体から離れた粒は青白く光って生まれ、冷めながら縮み、色がコメットの青へ沈んで消える。進む向きには流さない (その場で冷める)。
// 世界に置くので、視界の外と壁の向こうは本編の影が隠す。走っていない間・誰も透明でない間は Unity に触らない
internal static class CometTrail
{
    private const float Spacing = 0.07f;      // この距離を進むごとに 1 粒
    private const float Jitter = 0.035f;      // 出る所のばらつき (1 本の線に見せない)
    private const float Rise = 0.14f;         // 足元から胴の高さ
    private const float S0 = 0.15f, S1 = 0.05f, PeakA = 0.32f;
    private const float MaxStep = 1f;         // 1 フレームでこれより飛んだら TP として繋がない

    private sealed class Source
    {
        public PlayerControl Pc;
        public byte Id;
        public bool Has;
        public float X, Y, Carry;
    }

    private struct Grain
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public float Born, Life, Size;
        public bool Live;
    }

    private static readonly List<Source> Sources = new();
    private static readonly Grain[] Grains = new Grain[72];
    private static int _live;
    private static Sprite _soft;
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
        float now = Time.time;
        for (int i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            if (life <= 0f || !Cloak.IsOn(s.Id) || !s.Pc || s.Pc.inVent || !PortKit.Alive(s.Pc)) { s.Has = false; continue; }
            var p = s.Pc.transform.position;
            if (!s.Has) { s.Has = _tracking = true; s.X = p.x; s.Y = p.y; s.Carry = 0f; continue; }
            float dx = p.x - s.X, dy = p.y - s.Y, d2 = dx * dx + dy * dy;
            if (d2 > MaxStep * MaxStep) { s.X = p.x; s.Y = p.y; s.Carry = 0f; continue; }
            if (d2 < 1e-6f) continue;
            float d = FxMath.Sqrt(d2), walked = s.Carry + d;
            // 前のフレームの位置から今の位置まで、間隔ごとに置く (速く走っても途切れない)
            float t = Spacing - s.Carry;
            while (t <= d)
            {
                float k = t / d;
                Emit(s.X + dx * k, s.Y + dy * k, p.z, life, now);
                t += Spacing;
            }
            s.Carry = walked % Spacing;
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
            // 生まれてすぐ一番明るく、あとは冷めるほど縮んで暗くなる
            float a = PeakA * (u < 0.08f ? u / 0.08f : 1f - (u - 0.08f) / 0.92f);
            float size = g.Size * (S0 + (S1 - S0) * u) / S0;
            g.Tf.localScale = FxMath.V3(size, size, 1f);
            // 青白 (0.86,0.95,1) → コメットの青 (0.47,0.69,0.81)
            g.Sr.color = FxMath.Rgba(0.86f - 0.39f * u, 0.95f - 0.26f * u, 1f - 0.19f * u, a);
        }
    }

    private static void Emit(float x, float y, float z, float life, float now)
    {
        int slot = -1;
        for (int i = 0; i < Grains.Length; i++) if (!Grains[i].Live) { slot = i; break; }
        if (slot < 0) return; // 上限 (古い粒が消えるまで足さない)
        ref var g = ref Grains[slot];
        if (!g.Sr)
        {
            _soft ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpCometSoft");
            var go = FxRoot.New("MrpCometTrail");
            g.Sr = go.AddComponent<SpriteRenderer>();
            g.Sr.sprite = _soft;
            g.Tf = go.transform;
        }
        else g.Sr.gameObject.SetActive(true);
        float jx = (Random.value - 0.5f) * 2f * Jitter, jy = (Random.value - 0.5f) * 2f * Jitter;
        // 体の少し奥 (通った後に残る物なので、戻ってきた本人の体より手前に出さない)
        g.Tf.position = FxMath.V3(x + jx, y + Rise + jy, z + 0.002f);
        g.Size = S0 * (0.8f + 0.4f * Random.value);
        g.Tf.localScale = FxMath.V3(g.Size, g.Size, 1f);
        g.Sr.color = FxMath.Rgba(0.86f, 0.95f, 1f, 0f);
        g.Born = now;
        g.Life = life * (0.8f + 0.4f * Random.value);
        g.Live = true;
        _live++;
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

}
