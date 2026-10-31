using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// トリガーハッピーの機関銃とロケットランチャーの大砲の効果音。どちらも全員の端末で同じ絵を出している所で鳴らすので電文は増やさない。
// 見えていない人の音は鳴らさない (音で居場所が漏れる)。弾は 1 秒に 100 発を超えるので、着弾・跳弾・薬莢は数を絞って鳴らす。
// 壁を壊す音の声を使い回すと連射の間に壁の崩れる音が切られるので、自前の声を持つ
internal static class GunSound
{
    private const float PanDist = 10f, PanMax = 0.7f, FullDist = 3f;
    private const float GunRange = 14f, GunMuffle = 7f;      // 機関銃は大きいので遠くまで届く
    private const float SmallRange = 8f, SmallMuffle = 4f;   // 着弾・薬莢
    private const float CannonRange = 14f, CannonMuffle = 7f;
    private const int Voices = 10;
    private const float HitGap = 0.06f, RicGap = 0.22f, CaseGap = 0.14f, DeflectGap = 0.05f;
    private const float SpinTop = 40f;
    private const int Every = 4;                              // ループの音量と左右は 4 フレームに 1 回だけ書き直す

    private static GameObject _host;
    private static AudioSource[] _voices;
    private static AudioSource _fly;
    private static int _next, _flyFrame;
    private static float _nextHit, _nextRic, _nextCase, _nextDeflect;
    private static int _deflects;
    private static byte _flyId = 255;

    internal static bool Muted;

    // 確かめ用
    private static int _played, _skipped;
    private static string _last = "-";
    internal static string Describe() => $"played={_played} skipped={_skipped} last={_last} fly={(_fly && _fly.isPlaying ? 1 : 0)}";

    // ---- 一発物 ----

    private static void At(string clip, float x, float y, float vol, float pitch, float range, float muffle)
    {
        if (Muted || !Gain(x, y, range, muffle, ref vol, out float pan, out bool far) || !Ensure()) return;
        var c = MrpBundle.Clip(far ? clip + "_m" : clip);
        if (!c) return;
        var src = _voices[_next];
        _next = (_next + 1) % Voices;
        src.Stop();
        src.clip = c;
        src.volume = vol;
        src.panStereo = pan;
        src.pitch = pitch;
        src.Play();
        _played++;
        _last = clip;
        RewindSounds.Record(c, vol, pan, pitch, 0f);
    }

    private static bool Gain(float x, float y, float range, float muffle, ref float vol, out float pan, out bool far)
    {
        pan = 0f;
        far = false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || MeetingHud.Instance) return false;
        Vector2 me = Watch.Ear(lp);
        float dx = x - me.x, dy = y - me.y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d >= range) return false;
        float near = d <= FullDist ? 1f : 1f - (d - FullDist) / (range - FullDist);
        vol *= MathF.Pow(near, 1.5f);
        pan = Math.Clamp(dx / PanDist, -PanMax, PanMax);
        far = d > muffle;
        return true;
    }

    private static void At(string clip, PlayerControl pc, float vol, float range, float muffle)
    {
        if (!pc || !pc.Visible) return;
        var p = pc.transform.position;
        At(clip, p.x, p.y, vol, FxMath.Range(0.97f, 1.03f), range, muffle);
    }

    // ---- 機関銃 ----

    internal static void Raise(PlayerControl pc) => At("noise_gat_raise", pc, 0.6f, SmallRange, SmallMuffle);
    internal static void Lower(PlayerControl pc) => At("noise_gat_lower", pc, 0.45f, SmallRange, SmallMuffle);

    // 弾が壁に当たった。大半は短い「チッ」、ときどき跳ね返る「チュイーン」(跳ね返る設定ならよく鳴る)
    internal static void Impact(float x, float y, bool ricochets)
    {
        float now = Time.time;
        if (now >= _nextRic && FxMath.Range(0f, 1f) < (ricochets ? 0.45f : 0.15f))
        {
            _nextRic = now + RicGap;
            _nextHit = now + HitGap;
            int i = (int)FxMath.Range(0f, 4.999f) + 1;
            At("noise_gat_ric_" + i, x, y, 0.65f, FxMath.Range(0.9f, 1.12f), SmallRange, SmallMuffle);
            return;
        }
        if (now < _nextHit) { _skipped++; return; }
        _nextHit = now + HitGap;
        int h = (int)FxMath.Range(0f, 2.999f) + 1;
        At("noise_gat_hit_" + h, x, y, 0.35f, FxMath.Range(0.9f, 1.15f), SmallRange, SmallMuffle);
    }

    // サムライの刀が弾を受けた・斬った「キン」(跳ね返した時は刃の鳴りを重ねる)
    internal static void Deflect(float x, float y, bool reflect)
    {
        float now = Time.time;
        if (now < _nextDeflect) return;
        _nextDeflect = now + DeflectGap;
        int i = (int)FxMath.Range(0f, 4.999f) + 1;
        At("noise_gat_ric_" + i, x, y, 0.55f, FxMath.Range(1.35f, 1.6f), SmallRange, SmallMuffle);
        if (reflect || ++_deflects % 4 == 0)
            At("noise_samurai_clash", x, y, reflect ? 0.5f : 0.3f, FxMath.Range(1.15f, 1.3f), SmallRange, SmallMuffle);
    }

    // 機関銃が居合で斬られた: 金属が裂けて割れる
    internal static void Break(float x, float y) =>
        At("noise_rl_escape", x, y, 0.75f, FxMath.Range(1.15f, 1.25f), GunRange, GunMuffle);

    // 薬莢が床で跳ねた
    internal static void Case(float x, float y)
    {
        float now = Time.time;
        if (now < _nextCase) return;
        _nextCase = now + CaseGap;
        int i = (int)FxMath.Range(0f, 2.999f) + 1;
        At("noise_gat_case_" + i, x, y, 0.25f, FxMath.Range(0.95f, 1.25f), SmallRange * 0.7f, SmallMuffle);
    }

    // 人ごとの連射と回りの唸り (全員の端末・銃の絵の毎フレームから)。firing = 撃っている・spin = 銃身の回り (段/秒)
    internal sealed class Loops
    {
        internal AudioSource Fire, Spin;
        internal bool FireOn, SpinOn, Far;
        internal bool WantFire, WantSpin;   // 前に見た「撃っている・回っている」(聞こえず止めている間も、変わった時だけ出し直す)
        internal float Pitch = -1f, SpinVol = -1f, FireVol = -1f, Pan = 9f;
        internal int Frame;
    }

    internal static void TickGun(Loops l, GameObject go, PlayerControl owner, bool show, bool firing, float spin)
    {
        if (l == null) return;
        bool want = firing, wantSpin = spin > 0.5f;
        // 撃ち始め・撃ち止めと、4 フレームに 1 回だけ見え方と距離から音量を出し直す (毎フレーム Unity に聞かない)
        bool edge = want != l.WantFire || wantSpin != l.WantSpin;
        if (!edge && ++l.Frame < Every) return;
        l.Frame = 0;
        l.WantFire = want;
        l.WantSpin = wantSpin;
        if (!show || Muted || !owner || !owner.Visible || MeetingHud.Instance)
        {
            StopGun(l);
            return;
        }
        var p = owner.transform.position;
        float vol = 1f;
        if (!Gain(p.x, p.y, GunRange, GunMuffle, ref vol, out float pan, out bool far))
        {
            StopGun(l);
            return;
        }
        if (!EnsureLoops(l, go)) return;
        if (want != l.FireOn)
        {
            l.FireOn = want;
            if (want)
            {
                var c = MrpBundle.Clip(far ? "noise_gat_fire_loop_m" : "noise_gat_fire_loop");
                if (c) { l.Fire.clip = c; l.Fire.time = FxMath.Range(0f, 1.4f); l.Fire.Play(); }
                l.Far = far;
            }
            else
            {
                l.Fire.Stop();
                At("noise_gat_tail", p.x, p.y, 0.6f, FxMath.Range(0.97f, 1.03f), GunRange, GunMuffle);
            }
        }
        if (wantSpin != l.SpinOn)
        {
            l.SpinOn = wantSpin;
            if (wantSpin)
            {
                var c = MrpBundle.Clip(far ? "noise_gat_spin_loop_m" : "noise_gat_spin_loop");
                if (c) { l.Spin.clip = c; l.Spin.Play(); }
            }
            else l.Spin.Stop();
        }
        float fv = vol * 0.7f;
        if (MathF.Abs(fv - l.FireVol) > 0.02f) { l.FireVol = fv; l.Fire.volume = fv; }
        float k = Math.Clamp(spin / SpinTop, 0f, 1f);
        float sv = vol * 0.28f * MathF.Pow(k, 0.7f);
        if (MathF.Abs(sv - l.SpinVol) > 0.02f) { l.SpinVol = sv; l.Spin.volume = sv; }
        float pitch = 0.45f + 0.6f * k;
        if (MathF.Abs(pitch - l.Pitch) > 0.03f) { l.Pitch = pitch; l.Spin.pitch = pitch; }
        if (MathF.Abs(pan - l.Pan) > 0.03f) { l.Pan = pan; l.Fire.panStereo = l.Spin.panStereo = pan; }
    }

    internal static void StopGun(Loops l)
    {
        if (l == null) return;
        if (l.FireOn && l.Fire) l.Fire.Stop();
        if (l.SpinOn && l.Spin) l.Spin.Stop();
        l.FireOn = l.SpinOn = false;
        l.FireVol = l.SpinVol = l.Pitch = -1f;
        l.Pan = 9f;
    }

    // 銃の絵の GameObject に付ける (銃と一緒に消える・隠すと止まる)
    private static bool EnsureLoops(Loops l, GameObject go)
    {
        if (l.Fire) return true;
        var sm = SoundManager.Instance;
        if (!sm || !go) return false;
        l.Fire = Make(go, sm, true);
        l.Spin = Make(go, sm, true);
        return true;
    }

    // ---- 大砲 ----

    internal static void Load(PlayerControl launcher) => At("noise_rl_load", launcher, 0.75f, CannonRange, CannonMuffle);

    // 詰められた人が中で刀を振った
    internal static void Bang(PlayerControl launcher, bool strong)
    {
        if (!launcher || !launcher.Visible) return;
        var p = launcher.transform.position;
        At("noise_rl_bang_" + ((int)FxMath.Range(0f, 2.999f) + 1), p.x, p.y, strong ? 0.9f : 0.7f,
            strong ? FxMath.Range(0.85f, 0.9f) : FxMath.Range(0.95f, 1.08f), CannonRange, CannonMuffle);
    }

    internal static void Escape(PlayerControl launcher) => At("noise_rl_escape", launcher, 0.85f, CannonRange, CannonMuffle);

    internal static void Fire(PlayerControl launcher, Vector2 muzzle)
    {
        if (!launcher || !launcher.Visible) return;
        At("noise_rl_fire", muzzle.x, muzzle.y, 1f, FxMath.Range(0.96f, 1.03f), CannonRange * 1.3f, CannonMuffle);
    }

    // 着弾の火と破片 (爆発そのものは壁の爆発の音が鳴る)
    internal static void Blast(Vector2 at)
    {
        StopFly();
        At("noise_rl_blast", at.x, at.y, 0.75f, FxMath.Range(0.95f, 1.05f), CannonRange * 1.3f, CannonMuffle);
    }

    // 飛んでいる人が切る風 (全員の端末・飛んでいる間の毎 tick)。鳴らすのは 1 人だけで、鳴っている間に飛んだ別の人は鳴らさない
    internal static void Fly(PlayerControl flier)
    {
        if (!flier) return;
        byte id = flier.PlayerId;
        if (_flyId != 255 && id != _flyId) return;
        if (++_flyFrame < 3) return;
        _flyFrame = 0;
        if (!flier.Visible || Muted || MeetingHud.Instance) { StopFly(); return; }
        var p = flier.transform.position;
        float vol = 0.55f;
        if (!Gain(p.x, p.y, CannonRange, CannonMuffle, ref vol, out float pan, out bool far) || !Ensure()) { StopFly(); return; }
        if (id != _flyId)
        {
            var c = MrpBundle.Clip(far ? "noise_rl_fly_loop_m" : "noise_rl_fly_loop");
            if (!c) return;
            _flyId = id;
            _fly.clip = c;
            _fly.pitch = FxMath.Range(0.95f, 1.05f);
            _fly.Play();
        }
        _fly.volume = vol;
        _fly.panStereo = pan;
    }

    internal static void StopFly()
    {
        _flyId = 255;
        if (_fly && _fly.isPlaying) _fly.Stop();
    }

    // 本編の効果音の音量設定に従うよう、効果音のミキサーへ流す
    private static bool Ensure()
    {
        if (_host) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        _host = new GameObject("MrpGunSound");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _voices = new AudioSource[Voices];
        for (int i = 0; i < Voices; i++) _voices[i] = Make(_host, sm, false);
        _fly = Make(_host, sm, true);
        return true;
    }

    private static AudioSource Make(GameObject go, SoundManager sm, bool loop)
    {
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f;
        s.outputAudioMixerGroup = sm.SfxChannel;
        return s;
    }
}
