using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 守り (サバイバーのベスト・最初のキルの盾・守護天使の守り) の付いた人に当たった弾は、当たりにならず守りに弾かれる。
// 連続で当たると守りの絵が短い間を空けて次々に出る (守りは割れない) (弾かれ方は SuperNewRoles の賢者の守りと同じ)
public sealed partial class TriggerHappy
{
    private const float GuardSkipDeg = 15f;      // 弾かれる向き = 来た向きと真逆の前後この角度を除いた所から選ぶ
    private const float GuardIgnore = 0.1f;      // 弾かれた弾が同じ人に当たり直さない間 (秒)
    private const float GuardEffectGap = 0.1f;   // 次の守りの絵を出すまでの間 (秒)
    private const int GuardEffectMax = 3;        // 1 人に重ねて出す守りの絵の数 (超えたら古い物から消す)

    private sealed class GuardEffect
    {
        public readonly RoleEffectAnimation[] Anims = new RoleEffectAnimation[GuardEffectMax];
        public readonly float[] Born = new float[GuardEffectMax];
        public readonly bool[] Faded = new bool[GuardEffectMax];
        public AudioSource Sound;
        public float SoundAt;
        public int Head;
        public float Next;
    }
    private static readonly Dictionary<byte, GuardEffect> GuardEffects = new();

    // 弾を止める守りが付いているか (全員の端末で同じに分かる物だけ)
    internal static bool Guarded(PlayerControl p) =>
        p && (Rules.VanillaTweaks.Shielded(p) || Neutral.Survivor.VestOn(p) || p.protectedByGuardianId > -1);

    // 守りに当たった弾を弾く: 来た向きの前後 15° を除いたどこかへ曲げ、残りの射程で飛び直す
    private void GuardBounce(Shot s, PlayerControl p)
    {
        float span = 180f - GuardSkipDeg * 2f;
        float t = FxMath.Range(0f, span * 2f);
        float off = (t <= span ? GuardSkipDeg + t : 180f + GuardSkipDeg + (t - span)) * (Mathf.PI / 180f);
        float c = FxMath.Cos(off), sn = FxMath.Sin(off);
        float dx = s.Dx * c - s.Dy * sn, dy = s.Dx * sn + s.Dy * c;
        s.Dx = dx; s.Dy = dy;
        float push = 0.05f + Radius * BulletSize;
        s.X += dx * push;
        s.Y += dy * push;
        s.GuardId = p.PlayerId;
        s.GuardUntil = Time.time + GuardIgnore;
        float left = s.Left - s.Traveled;
        if (s.Tracer != null) GatlingFx.Stop(s.Tracer, s.Gen);
        s.Tracer = null;
        Segment(s, left > 0.3f ? left : 0.3f, 0f);
        PlayGuard(p);
    }

    // 本編の守護天使の守りの絵と音のうち「守りが出る」所だけを速く流す。本編の絵は 横から切る (1〜7 コマ)→守りが出る→保持→割れる (22 コマ〜) の
    // 30 コマで、弾いただけで守りは割れていないので、切る所と割れる所は出さない (割れる絵は本当に割れた時の本編の流れだけ)
    private const float GuardFrom = 7f / 24f;     // 流し始め = 8 コマ目 (切る線が消え守りが光って出た所)
    private const float GuardTo = 20.5f / 24f;    // 流し終わり = 21 コマ目 (この後が割れる前の光り)
    private const float GuardSpeed = 2.2f;        // 本編の何倍の速さで流すか
    private const float GuardFade = 0.12f;        // 流し終わってから消えるまで (秒)
    private const float GuardSoundFrom = 0.05f;   // 音の頭 (秒)。当たる音は 0.1 秒・割れる音は 0.75 秒から
    private const float GuardSoundEnd = 0.6f;     // 鳴らしてからこの秒で音を絞り終えて止める (割れる音の手前)

    private static void PlayGuard(PlayerControl p)
    {
        if (!GuardEffects.TryGetValue(p.PlayerId, out var g)) GuardEffects[p.PlayerId] = g = new GuardEffect();
        if (Time.time < g.Next || !DestroyableSingleton<RoleManager>.InstanceExists) return;
        var prefab = DestroyableSingleton<RoleManager>.Instance.protectAnim;
        if (!prefab) return;
        var anim = Object.Instantiate(prefab, p.gameObject.transform);
        anim.SetMaskLayerBasedOnWhoShouldSee(true);
        anim.Play(p, null, p.cosmetics.FlipX, RoleEffectAnimation.SoundType.None);
        if (anim.Animator) { anim.Animator.Speed = GuardSpeed; anim.Animator.Time = GuardFrom; }
        var old = g.Anims[g.Head];
        if (old) Object.Destroy(old.gameObject);
        g.Anims[g.Head] = anim;
        var ag = anim.gameObject;
        FxRoot.TrackWorld(ag); // 逆再生に入れる (人の子のまま)
        Rewind.Undo(_ => { if (ag) Object.Destroy(ag); }); // 弾かれる前へ戻ると守りの絵も消える
        g.Born[g.Head] = Time.time;
        g.Faded[g.Head] = false;
        g.Head = (g.Head + 1) % GuardEffectMax;
        g.Next = Time.time + GuardEffectGap;
        if (anim.UseSound && SoundManager.Instance)
        {
            g.Sound = SoundManager.Instance.PlaySound(anim.UseSound, false);
            if (g.Sound) g.Sound.time = GuardSoundFrom;
            g.SoundAt = Time.time;
        }
        _guardLive = true;
    }

    private static bool _guardLive;

    // 出ている守りの絵を流し終わりで止めて消し、音を割れる音の手前で絞る (出ている間だけ回る)
    private static void TickGuards()
    {
        if (!_guardLive) return;
        if (RewindPlayback.Active)
        {
            // 逆再生の間は絵を隠して控えを見せているので、流れの時刻を先へ延ばして止める
            float dt = Time.deltaTime;
            foreach (var g in GuardEffects.Values)
                for (int i = 0; i < GuardEffectMax; i++) g.Born[i] += dt;
            return;
        }
        bool any = false;
        float now = Time.time;
        foreach (var g in GuardEffects.Values)
        {
            for (int i = 0; i < GuardEffectMax; i++)
            {
                var a = g.Anims[i];
                if (a == null) continue;
                if (!a) { g.Anims[i] = null; continue; }
                any = true;
                float age = now - g.Born[i];
                float t = GuardFrom + age * GuardSpeed;
                if (t < GuardTo)
                {
                    if (a.Animator) a.Animator.Time = t;
                    continue;
                }
                if (!g.Faded[i])
                {
                    g.Faded[i] = true;
                    if (a.Animator) { a.Animator.Time = GuardTo; a.Animator.Paused = true; }
                }
                float k = 1f - (t - GuardTo) / GuardSpeed / GuardFade;
                if (k <= 0f) { Object.Destroy(a.gameObject); g.Anims[i] = null; continue; }
                if (a.Renderer) a.Renderer.color = new Color(1f, 1f, 1f, k);
            }
            if (g.Sound != null)
            {
                any = true;
                float s = now - g.SoundAt;
                if (!g.Sound || s >= GuardSoundEnd) { if (g.Sound) g.Sound.Stop(); g.Sound = null; }
                else if (s > GuardSoundEnd - 0.15f) g.Sound.volume = (GuardSoundEnd - s) / 0.15f;
            }
        }
        _guardLive = any;
    }
}
