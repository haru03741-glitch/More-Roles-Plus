using System.Collections.Generic;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 溺れる: 水面に顔を出して喘いでは沈むのを 3 回 (だんだん弱く)、最後に後ろへ倒れて沈み、水の底に横たわる。
// 体の絵は CrewFlip の "drown" (本編の撃たれた人の連番を左右返し・その場の動きにした物)。
// 顔を出す/沈むは体の絵を動かさず、体の水面の覆い (WaterWade) の深さを揺らして見せる。顔を出すたびに水しぶきと音、沈むたびに泡。
// 横たわった体は、覆いの形 (立った体) に合わないので、体の絵を型にした水の板 (BodyCover) を重ねて水越しに見せる。
// 死体の水の板は BodyCover が死因によらず付ける。ここは溺れた死体から泡を昇らせるだけ。
// 誰も溺れておらず沈んだ死体も無い間は Unity に触らない
internal static class DrownFx
{
    // 揺れの深さ (足元の深さに足す・単位) の要の点 (秒, 深さ)。正 = 沈む・負 = 顔を出す。CrewFlip "drown" のコマと合わせてある
    private static readonly float[] KeyT = { 0f, 0.12f, 0.25f, 0.5f, 0.62f, 0.78f, 0.88f, 1.12f, 1.25f, 1.4f, 1.5f, 1.68f, 1.95f };
    private static readonly float[] KeyD = { 0.3f, 0.3f, -0.3f, -0.25f, 0.35f, 0.35f, -0.3f, -0.22f, 0.4f, 0.4f, -0.12f, -0.05f, 0.9f };
    private static readonly float[] SplashT = { 0.2f, 0.85f, 1.47f };   // 顔を出す時
    private static readonly float[] SplashK = { 0.7f, 0.6f, 0.35f };
    private static readonly float[] GulpT = { 0.6f, 1.3f, 1.8f, 2.2f };  // 沈んで息が漏れる時
    private static readonly int[] GulpN = { 3, 4, 7, 9 };
    private const float LieAt = 1.96f;          // 横たわる絵になる時 (立った体の覆いをやめて写しにする)
    private const float SinkFrom = 1.72f, SinkTime = 0.5f;
    private const float CorpseCheck = 0.5f;     // 死体の足元の水を見る間隔
    private const float BubbleDepth = 0.25f;    // 死体の足元の水がこれより深ければ泡が昇る
    private const float DepthCheck = 0.25f;     // 倒れた体の足元の水を見る間隔
    private const float SplashRange = 10f, SplashMuffle = 5f, SplashVolume = 0.45f;

    private sealed class Sinker
    {
        public byte Id;
        public PlayerControl Pc;
        public SpriteRenderer Body;
        public BodyCover.Cover Cov;
        public float Start;
        public int NextSplash, NextGulp;
        public bool Corpse;
        public float NextCheck, NextBubble, DiedAt, Depth;
    }

    private static readonly List<Sinker> Live = new();
    private static int _gen = -1;

    // 溺れると決まった (全員の端末で・連番を流せた時だけ)
    internal static void Start(PlayerControl pc)
    {
        var body = pc && pc.cosmetics && pc.cosmetics.currentBodySprite != null ? pc.cosmetics.currentBodySprite.BodySprite : null;
        if (!body) return;
        Gen();
        Stop(pc.PlayerId);
        Live.Add(new Sinker { Id = pc.PlayerId, Pc = pc, Body = body, Start = Time.time });
        WaterWade.Bob(pc.PlayerId, KeyD[0], false);
    }

    // 溺れて死んだ人の死体 (倒れた姿の絵を差し替えた本編の死体の描画物)
    internal static void Corpse(SpriteRenderer main, byte id)
    {
        if (!main) return;
        Gen();
        Stop(id);
        Live.Add(new Sinker { Id = id, Body = main, Corpse = true, DiedAt = Time.time, Start = Time.time, Depth = 1f });
    }

    // 溺れる動きを流している (立って喘ぐ間の水面も、倒れた体の水の板もここが描く)
    internal static bool Sinking(byte id)
    {
        for (int i = 0; i < Live.Count; i++) if (!Live[i].Corpse && Live[i].Id == id) return true;
        return false;
    }

    internal static void Stop(byte id)
    {
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var s = Live[i];
            if (s.Corpse || s.Id != id) continue;
            End(s);
            Live.RemoveAt(i);
        }
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;
        if (_gen != GameClock.ShipGen) { EndAll(); return; }
        float now = Time.time;
        bool rewinding = Roles.RewindPlayback.Active;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var s = Live[i];
            if (!s.Body) { End(s); Live.RemoveAt(i); continue; }
            if (s.Corpse) { if (!rewinding) TickCorpse(s, now); continue; }
            // 死んだ (死体へ移る)・助かった (会議・時戻し)・崩れの外へ出た
            bool dead = !s.Pc || s.Pc.Data == null || s.Pc.Data.IsDead || s.Pc.Data.Disconnected;
            if (rewinding || dead || !Roles.TerrainDeath.IsDoomed(s.Id) || MeetingHud.Instance)
            {
                // 死んだ直後は体に倒れた最後の絵が残っているので、幽霊の絵に替わるまで水の板を残す
                if (dead && !rewinding && s.Cov != null) { BodyCover.Linger(s.Cov); s.Cov = null; }
                End(s); Live.RemoveAt(i); continue;
            }
            float t = now - s.Start;
            WaterWade.Bob(s.Id, Depth(t), t >= LieAt);
            var feet = s.Pc.GetTruePosition();
            while (s.NextSplash < SplashT.Length && t >= SplashT[s.NextSplash])
            {
                float k = SplashK[s.NextSplash++];
                Roles.BodyWreck.Splash(feet.x, feet.y + 0.05f, k);
                BreakNoise.PlayAt("noise_splash_" + (1 + s.NextSplash % 3), feet.x, feet.y, SplashRange, SplashMuffle, SplashVolume * k, 1.1f);
            }
            while (s.NextGulp < GulpT.Length && t >= GulpT[s.NextGulp])
                Bubbles(feet.x, feet.y + 0.35f, GulpN[s.NextGulp++]);
            // 後ろへ倒れて沈む: 体の上の水を厚くしていく (足元の深さまで)
            if (t >= SinkFrom)
            {
                s.Cov ??= BodyCover.Make(s.Body);
                if (now >= s.NextCheck) { s.NextCheck = now + DepthCheck; s.Depth = WaterSim.CurrentAt(feet, out _, out _); }
                float k = FxMath.Clamp01((t - SinkFrom) / SinkTime);
                BodyCover.SetLevel(s.Cov, BodyCover.Thick + (s.Depth - BodyCover.Thick) * k);
            }
        }
    }

    // 本編のアニメが体の絵を書いた後 (CrewFlip が連番の絵を差した後) に水の板へ同じ絵を写す
    internal static void LateTick()
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var s = Live[i];
            if (s.Corpse || s.Cov == null || !s.Cov.Shown) continue;
            BodyCover.Sync(s.Cov);
        }
    }

    // 溺れた死体: 水がある間は時々泡が昇る (水の板は BodyCover)
    private static void TickCorpse(Sinker s, float now)
    {
        if (now >= s.NextCheck)
        {
            s.NextCheck = now + CorpseCheck;
            var at = s.Body.transform.position;
            s.Depth = WaterSim.Running && WaterSim.Ready ? WaterSim.CurrentAt(FxMath.V2(at.x, at.y - 0.1f), out _, out _) : 0f;
        }
        // 沈んでしばらくは息が残っていて泡が昇る。だんだん間が空く
        if (s.Depth > BubbleDepth && now >= s.NextBubble)
        {
            float age = now - s.DiedAt;
            s.NextBubble = now + (age < 20f ? FxMath.Range(1.2f, 3f) : FxMath.Range(6f, 11f));
            var p = s.Body.transform.position;
            Bubbles(p.x, p.y, age < 20f ? FxMath.Range(1, 4) : 1);
        }
    }

    private static float Depth(float t)
    {
        if (t <= KeyT[0]) return KeyD[0];
        for (int i = 1; i < KeyT.Length; i++)
        {
            if (t > KeyT[i]) continue;
            float u = (t - KeyT[i - 1]) / (KeyT[i] - KeyT[i - 1]);
            u = u * u * (3f - 2f * u);
            return KeyD[i - 1] + (KeyD[i] - KeyD[i - 1]) * u;
        }
        return KeyD[KeyD.Length - 1];
    }

    // 息の泡: 小さな泡が揺れながら昇って消える
    private static void Bubbles(float x, float y, int n)
    {
        var sp = AirGauge.BubbleSprite;
        if (!sp) return;
        for (int i = 0; i < n; i++)
        {
            if (Rising.Count >= MaxRising) return;
            var go = Roles.FxRoot.New("MrpDrownBubble");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            float size = FxMath.Range(0.035f, 0.08f);
            Rising.Add(new Bubble
            {
                Tf = go.transform, Sr = sr, X = x + FxMath.Range(-0.12f, 0.12f), Y = y + FxMath.Range(-0.05f, 0.1f),
                Born = Time.time + FxMath.Range(0f, 0.35f), Life = FxMath.Range(0.6f, 1.1f), Size = size,
                Rise = FxMath.Range(0.35f, 0.6f), Phase = FxMath.Range(0f, 6.28f),
            });
            go.transform.localScale = FxMath.V3(0f, 0f, 1f);
        }
    }

    private sealed class Bubble
    {
        public Transform Tf;
        public SpriteRenderer Sr;
        public float X, Y, Born, Life, Size, Rise, Phase;
    }

    private const int MaxRising = 48;
    private static readonly List<Bubble> Rising = new();

    // 泡を動かす (毎フレーム・泡がある間だけ)
    internal static void TickBubbles()
    {
        if (Rising.Count == 0) return;
        if (Roles.RewindPlayback.Active) return;
        float now = Time.time;
        for (int i = Rising.Count - 1; i >= 0; i--)
        {
            var b = Rising[i];
            if (!b.Tf) { Rising.RemoveAt(i); continue; }
            float t = now - b.Born;
            if (t < 0f) continue;
            if (t >= b.Life) { Object.Destroy(b.Tf.gameObject); Rising.RemoveAt(i); continue; }
            float u = t / b.Life;
            float y = b.Y + b.Rise * t, x = b.X + 0.025f * FxMath.Sin(b.Phase + t * 11f);
            // 前後は昇る前の高さで (人と同じ y/1000)。人の手前
            b.Tf.position = FxMath.V3(x, y, b.Y / 1000f - 0.002f);
            float s = b.Size * (u < 0.15f ? u / 0.15f : 1f + 0.25f * u);
            b.Tf.localScale = FxMath.V3(s, s, 1f);
            b.Sr.color = FxMath.Rgba(1f, 1f, 1f, u > 0.8f ? (1f - u) / 0.2f : 1f);
        }
    }

    private static void End(Sinker s)
    {
        if (!s.Corpse) WaterWade.EndBob(s.Id);
        BodyCover.Destroy(s.Cov);
        s.Cov = null;
    }

    private static void Gen()
    {
        if (_gen == GameClock.ShipGen) return;
        _gen = GameClock.ShipGen;
        EndAll();
        Rising.Clear();
    }

    // 試合が替わった: 人は次の試合へ残るので、体に付けた写しと水面の揺れを外す
    private static void EndAll()
    {
        foreach (var s in Live) End(s);
        Live.Clear();
    }

    internal static string Describe() => $"drown live={Live.Count} bubbles={Rising.Count}";
}
