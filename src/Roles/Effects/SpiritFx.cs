using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 死者や霊に触れている人の周りに出る、控えめな現象 (全員の端末・世界に置くので視界の外や壁の向こうは本編の影が隠す)。
//   Mist   = ポラリスが死体を見つめている間、死体から薄い白いもやが立ち、ゆるく弧を描いて見つめる人の顔へ流れる
//   Breath = シャーマンが降霊している間、霊を降ろした場の冷えで吐く息が白く見える (息のたびに口の前へ広がって消える)
// 使う人の端末が始めと終わりを送る。使う人が見えない (ベントの中・透明) 間は出さない。誰も使っていない間は Unity に触らない
internal static class SpiritFx
{
    private const byte KindMist = 0, KindBreath = 1, KindOff = 255;
    private const float MaxTime = 60f;                       // 終わりの知らせが届かなかった時の上限 (秒)
    // もや: 出す間隔・寿命・大きさ (始め→終わり)・一番濃い時・弧の高さ・出る所のばらつき
    // 弧は死体と人が隣り合っていても読めるよう、体 1 つ分ほど上へ立ち昇ってから顔へ降りる
    private const float MistGap = 0.06f, MistLife = 1.9f, MistS0 = 0.18f, MistS1 = 0.46f, MistA = 0.5f, MistArc = 0.6f, MistSpread = 0.16f;
    private const float FaceUp = 0.28f;                      // 足元から顔の高さ (単位)
    // 息: 1 呼吸の長さ・吐く間・吐く間に出す粒の数・寿命・大きさ・濃さ・前へ出る速さ・止まる強さ
    private const float BreathCycle = 1.35f, BreathOut = 0.28f, BreathLife = 1.05f, BreathS0 = 0.09f, BreathS1 = 0.4f, BreathA = 0.42f;
    private const int BreathPuffs = 4;
    private const float BreathSpeed = 0.42f, BreathDrag = 2.4f, MouthSide = 0.2f, MouthUp = 0.2f;

    private sealed class Source
    {
        public byte Id, Kind;
        public PlayerControl Pc;
        public float Bx, By, Bz;                             // もやの出る所 (死体の絵の真ん中)
        public float Start, Next;
        public int Puffed;
    }

    private struct Grain
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public byte Kind;
        public float X0, Y0, X1, Y1, X, Y, Z, Vx, Vy, Born, Life, Spin;
        public bool Live;
    }

    private static readonly List<Source> Sources = new();
    private static Grain[] _grains = new Grain[64];
    private static int _live;
    private static Sprite _soft;
    private static int _gen = -1;
    internal static int Made;

    // 使う人の端末: 始め (Mist は見つめる死体) / 終わり
    internal static void LocalMist(PlayerControl pc, DeadBody body)
    {
        if (!pc || !body) return;
        Start(pc, KindMist, body.ParentId);
        Call.Send((KindMist, body.ParentId));
    }

    internal static void LocalBreath(PlayerControl pc)
    {
        if (!pc) return;
        Start(pc, KindBreath, 255);
        Call.Send((KindBreath, 255));
    }

    internal static void LocalStop(PlayerControl pc)
    {
        if (!pc) return;
        Stop(pc.PlayerId);
        Call.Send((KindOff, 255));
    }

    private static readonly Net.RemoteCall<(byte kind, byte body)> Call = new("Spirit.Fx", Net.Route.Anyone,
        (w, m) => { w.Write(m.kind); w.Write(m.body); }, r => (r.ReadByte(), r.ReadByte()),
        (sender, m) =>
        {
            if (!sender) return;
            if (m.kind == KindOff) Stop(sender.PlayerId);
            else if (!MeetingHud.Instance) Start(sender, m.kind, m.body);
        }, perSecond: 4);

    private static void Start(PlayerControl pc, byte kind, byte bodyId)
    {
        if (!ShipStatus.Instance || !PortKit.Alive(pc)) return;
        if (_gen != Terrain.GameClock.ShipGen) { Clear(); _gen = Terrain.GameClock.ShipGen; }
        Stop(pc.PlayerId);
        var s = new Source { Id = pc.PlayerId, Kind = kind, Pc = pc, Start = Time.time, Next = Time.time };
        if (kind == KindMist)
        {
            DeadBody body = null;
            foreach (var b in Object.FindObjectsOfType<DeadBody>()) if (b && b.ParentId == bodyId) { body = b; break; }
            var rs = body ? body.bodyRenderers : null;
            if (rs == null || rs.Length == 0 || !rs[0]) return;
            var c = rs[0].bounds.center;
            s.Bx = c.x;
            s.By = c.y;
            s.Bz = body.transform.position.z;
        }
        Sources.Add(s);
    }

    private static void Stop(byte id)
    {
        for (int i = Sources.Count - 1; i >= 0; i--) if (Sources[i].Id == id) Sources.RemoveAt(i);
    }

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        if (Sources.Count == 0 && _live == 0) return;
        if (_gen != Terrain.GameClock.ShipGen || !ShipStatus.Instance || RewindPlayback.Active || MeetingHud.Instance) { Clear(); return; }
        float now = Time.time, dt = Time.deltaTime;
        for (int i = Sources.Count - 1; i >= 0; i--)
        {
            var s = Sources[i];
            if (!PortKit.Alive(s.Pc) || now - s.Start > MaxTime) { Sources.RemoveAt(i); continue; }
            // 見えない人の周りに出すと居場所が漏れる
            if (s.Pc.inVent || !s.Pc.Visible || Cloak.AlphaOf(s.Id) < 0.99f || UchuKit.Hidden.AlphaOf(s.Id) < 0.99f) continue;
            if (s.Kind == KindMist) Mist(s, now);
            else Breath(s, now);
        }
        for (int i = 0; i < _grains.Length; i++)
        {
            ref var g = ref _grains[i];
            if (!g.Live) continue;
            float u = (now - g.Born) / g.Life;
            if (u >= 1f || !g.Sr)
            {
                g.Live = false;
                _live--;
                if (g.Sr) g.Sr.gameObject.SetActive(false);
                continue;
            }
            float size, a;
            if (g.Kind == KindMist)
            {
                // 死体から顔へ、上へ膨らむ弧で寄っていく (終わりに近いほど速く吸い寄せられる)
                float k = u * u * (3f - 2f * u);
                g.X = g.X0 + (g.X1 - g.X0) * k;
                g.Y = g.Y0 + (g.Y1 - g.Y0) * k + MistArc * 4f * k * (1f - k);
                size = MistS0 + (MistS1 - MistS0) * u;
                a = MistA * FxMath.Sin(u * 3.1415927f);
            }
            else
            {
                // 吐いた息: 前へ出て止まり、少し昇りながら広がって消える
                float drag = 1f - FxMath.Min(1f, BreathDrag * dt);
                g.Vx *= drag;
                g.Vy = g.Vy * drag + 0.12f * dt;
                g.X += g.Vx * dt;
                g.Y += g.Vy * dt;
                size = BreathS0 + (BreathS1 - BreathS0) * FxMath.Sqrt(u);
                a = BreathA * (u < 0.12f ? u / 0.12f : 1f - (u - 0.12f) / 0.88f);
            }
            g.Tf.position = FxMath.V3(g.X, g.Y, g.Z);
            g.Tf.localScale = FxMath.V3(size, size * 0.8f, 1f);
            g.Tf.rotation = FxMath.RotZ(g.Spin * u);
            // 明るい床でも消えないよう、少し青みのある白
            g.Sr.color = FxMath.Rgba(0.84f, 0.9f, 1f, a);
        }
    }

    private static void Mist(Source s, float now)
    {
        if (now < s.Next) return;
        s.Next = now + MistGap;
        var p = s.Pc.transform.position;
        float x0 = s.Bx + (Random.value - 0.5f) * 2f * MistSpread, y0 = s.By + (Random.value - 0.5f) * MistSpread;
        // 顔の高さへ (少しばらして 1 本の線に見せない)
        float x1 = p.x + (Random.value - 0.5f) * 0.1f, y1 = p.y + FaceUp + (Random.value - 0.5f) * 0.08f;
        // 死体と見つめる人のどちらよりも手前
        float z = FxMath.Min(s.Bz, p.z) - 0.004f;
        Emit(KindMist, x0, y0, z, 0f, 0f, MistLife * (0.85f + 0.3f * Random.value), x1, y1);
    }

    private static void Breath(Source s, float now)
    {
        float t = now - s.Start;
        float phase = t % BreathCycle;
        int cycle = (int)(t / BreathCycle);
        // 吐く間に BreathPuffs 粒を等間隔で
        int want = phase < BreathOut ? (int)(phase / BreathOut * BreathPuffs) + 1 : BreathPuffs;
        if (s.Next != cycle) { s.Next = cycle; s.Puffed = 0; }
        if (s.Puffed >= want) return;
        s.Puffed++;
        var p = s.Pc.transform.position;
        float face = s.Pc.cosmetics && s.Pc.cosmetics.FlipX ? -1f : 1f;
        float x = p.x + face * MouthSide, y = p.y + MouthUp;
        float sp = BreathSpeed * (0.8f + 0.4f * Random.value);
        Emit(KindBreath, x, y, p.z - 0.004f, face * sp, (Random.value - 0.3f) * 0.12f, BreathLife * (0.85f + 0.3f * Random.value), 0f, 0f);
    }

    private static void Emit(byte kind, float x, float y, float z, float vx, float vy, float life, float x1, float y1)
    {
        int slot = -1;
        for (int i = 0; i < _grains.Length; i++) if (!_grains[i].Live) { slot = i; break; }
        if (slot < 0) return; // 上限 (古い粒が消えるまで足さない)
        ref var g = ref _grains[slot];
        if (!g.Sr)
        {
            _soft ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpSpiritSoft");
            var go = FxRoot.New("MrpSpirit");
            g.Sr = go.AddComponent<SpriteRenderer>();
            g.Sr.sprite = _soft;
            g.Tf = go.transform;
            Made++;
        }
        else g.Sr.gameObject.SetActive(true);
        g.Kind = kind;
        g.X = g.X0 = x;
        g.Y = g.Y0 = y;
        g.X1 = x1;
        g.Y1 = y1;
        g.Z = z;
        g.Vx = vx;
        g.Vy = vy;
        g.Born = Time.time;
        g.Life = life;
        g.Spin = (Random.value - 0.5f) * 90f;
        g.Live = true;
        g.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        _live++;
    }

    private static void Clear()
    {
        Sources.Clear();
        for (int i = 0; i < _grains.Length; i++)
        {
            if (_grains[i].Sr) Object.Destroy(_grains[i].Sr.gameObject);
            _grains[i] = default;
        }
        _live = 0;
    }

    internal static string Describe() => $"sources={Sources.Count} grains={_live} made={Made}";
}
