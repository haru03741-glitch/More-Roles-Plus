using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライの効果音。鳴らすのは刀の絵を出す所 (どの端末でも同じ絵を再生している) なので電文は増やさない。
// 見えていない人の音は鳴らさない (音で居場所が漏れる)。納刀の「チン」と遅れ斬撃は先に予約して鳴らすので、
// 壁を壊した音の声を使い回すと途中で消される → 自前の声を持つ
internal static class SamuraiSound
{
    private const float Range = 9f, Muffle = 5f, FullDist = 3f, PanDist = 10f, PanMax = 0.7f;
    private const int Voices = 8;
    private const float ChinAt = 0.38f, SlideLen = 0.30f;

    private static GameObject _host;
    private static AudioSource[] _voices;
    private static AudioSource _charge, _lock;
    private static int _next, _swing;
    private static float _chargePitch;
    private static bool _chargeOn;     // 溜めの間は毎フレーム呼ばれるので、鳴っているかは Unity に聞かずこちらで持つ

    // 会議の終わりなど、絵だけ元へ戻す時は鳴らさない
    internal static bool Muted;

    // 確かめ用: 鳴らした数と最後の音
    private static int _played;
    private static string _last = "-";
    internal static string Describe() => $"played={_played} last={_last} charge={(_chargeOn ? 1 : 0)} lock={(_lock && _lock.isPlaying ? 1 : 0)}";

    // pc の所で鳴らす。delay 秒後に鳴らす予約もできる
    internal static void At(string clip, PlayerControl pc, float vol, float delay = 0f)
    {
        if (!pc || !pc.Visible) return;
        Vector3 p = pc.transform.position;
        At(clip, p.x, p.y, vol, delay);
    }

    internal static void At(string clip, float x, float y, float vol, float delay = 0f)
    {
        if (Muted || !Gain(x, y, ref vol, out float pan, out bool far) || !Ensure()) return;
        var c = MrpBundle.Clip(far ? clip + "_m" : clip);
        if (!c) return;
        var src = _voices[_next];
        _next = (_next + 1) % Voices;
        src.Stop();
        src.clip = c;
        src.volume = vol;
        src.panStereo = pan;
        float pitch = FxMath.Range(0.97f, 1.03f);
        src.pitch = pitch;
        if (delay > 0f) src.PlayDelayed(delay);
        else src.Play();
        _played++;
        _last = clip;
        RewindSounds.Record(c, vol, pan, pitch, delay);
    }

    // 距離で小さく・左右に振る。遠すぎる・会議中なら鳴らさない
    private static bool Gain(float x, float y, ref float vol, out float pan, out bool far)
    {
        pan = 0f;
        far = false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || MeetingHud.Instance) return false;
        Vector2 me = lp.GetTruePosition();
        float dx = x - me.x, dy = y - me.y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d >= Range) return false;
        float near = d <= FullDist ? 1f : 1f - (d - FullDist) / (Range - FullDist);
        vol *= MathF.Pow(near, 1.5f);
        pan = Math.Clamp(dx / PanDist, -PanMax, PanMax);
        far = d > Muffle;
        return true;
    }

    // ---- 場面ごと ----

    internal static void Draw(PlayerControl pc) => At("noise_samurai_draw", pc, 0.6f);

    // 納刀: 刀身が鞘へ滑り込み、鍔が鳴る (slow = 居合の後の伸ばした納刀の倍率・絵の鍔の光と同じ時刻)
    internal static void Sheathe(PlayerControl pc, float slow)
    {
        float chin = ChinAt * slow;
        At("noise_samurai_slide", pc, 0.5f, MathF.Max(0f, chin - SlideLen));
        At("noise_samurai_chin", pc, 0.75f, chin);
    }

    // 一振りの始まり。pow = 居合の溜め (0 = ふつうの振り)
    internal static void Swing(PlayerControl pc, float pow)
    {
        if (pow > 0.05f) { At("noise_samurai_iai", pc, 0.7f); return; }
        _swing = _swing % 3 + 1;
        At(_swing == 1 ? "noise_samurai_swing_1" : _swing == 2 ? "noise_samurai_swing_2" : "noise_samurai_swing_3", pc, 0.55f);
    }

    // 居合の段 2〜4 の斬線 = 空間を斬る
    internal static void Rend(PlayerControl pc, int tier)
    {
        if (tier >= 4) At("noise_samurai_rend_max", pc, 1f);
        else At("noise_samurai_rend", pc, 0.8f);
    }

    // 遅れ斬撃: n 本が delay 秒後から走る
    internal static void Delayed(PlayerControl pc, int n, float delay) =>
        At(n >= 9 ? "noise_samurai_delayed_9" : n >= 6 ? "noise_samurai_delayed_6" : "noise_samurai_delayed_3", pc, 0.7f, delay);

    internal static void Hit(PlayerControl target, bool ko) => At(ko ? "noise_samurai_hit_ko" : "noise_samurai_hit", target, ko ? 0.9f : 0.8f);
    internal static void Dash(PlayerControl pc) => At("noise_samurai_dash", pc, 0.45f);
    internal static void Dodge(PlayerControl pc) => At("noise_samurai_dodge", pc, 0.4f);
    internal static void Spent(PlayerControl pc) => At("noise_samurai_spent", pc, 0.4f);

    // 刀と刀: 二人の間で鳴らす (どちらかが見えていれば)
    internal static void Clash(PlayerControl a, PlayerControl b) => Between("noise_samurai_clash", a, b, 0.85f);

    internal static void LockOn(PlayerControl a, PlayerControl b)
    {
        Between("noise_samurai_lock_on", a, b, 0.85f);
        if (!Mid(a, b, out float x, out float y)) return;
        float vol = 0.45f;
        if (!Gain(x, y, ref vol, out float pan, out bool far) || !Ensure()) return;
        var c = MrpBundle.Clip(far ? "noise_samurai_lock_loop_m" : "noise_samurai_lock_loop");
        if (!c) return;
        _lock.clip = c;
        _lock.volume = vol;
        _lock.panStereo = pan;
        _lock.PlayDelayed(0.25f);
    }

    internal static void LockEnd(PlayerControl a, PlayerControl b)
    {
        StopLock();
        Between("noise_samurai_lock_break", a, b, 1f);
    }

    internal static void StopLock()
    {
        if (_lock && _lock.isPlaying) _lock.Stop();
    }

    private static void Between(string clip, PlayerControl a, PlayerControl b, float vol)
    {
        if (Mid(a, b, out float x, out float y)) At(clip, x, y, vol);
    }

    private static bool Mid(PlayerControl a, PlayerControl b, out float x, out float y)
    {
        x = y = 0f;
        if (!a || !b || !a.Visible && !b.Visible) return false;
        Vector3 p = a.transform.position, q = b.transform.position;
        x = (p.x + q.x) * 0.5f;
        y = (p.y + q.y) * 0.5f;
        return true;
    }

    // 居合の溜め (本人だけ): c = 溜め 0〜1。溜めるほど高く読む。高さは 0.04 動いた時だけ書き直す
    internal static void Charge(float c)
    {
        float pitch = 0.85f + 0.45f * c;
        if (!_chargeOn)
        {
            if (MeetingHud.Instance || !Ensure()) return;
            var clip = MrpBundle.Clip("noise_samurai_charge_loop");
            if (!clip) return;
            _charge.clip = clip;
            _charge.volume = 0.35f;
            _charge.pitch = _chargePitch = pitch;
            _charge.Play();
            _chargeOn = true;
            return;
        }
        if (MathF.Abs(pitch - _chargePitch) < 0.04f) return;
        _charge.pitch = _chargePitch = pitch;
    }

    internal static void ChargeFull()
    {
        if (MeetingHud.Instance || !Ensure()) return;
        var clip = MrpBundle.Clip("noise_samurai_charge_full");
        if (!clip) return;
        var src = _voices[_next];
        _next = (_next + 1) % Voices;
        src.Stop();
        src.clip = clip;
        src.volume = 0.5f;
        src.panStereo = 0f;
        src.pitch = 1f;
        src.Play();
    }

    internal static void ChargeStop()
    {
        if (!_chargeOn) return;
        _chargeOn = false;
        if (_charge) _charge.Stop();
    }

    // 本編の効果音の音量設定に従うよう、効果音のミキサーへ流す
    private static bool Ensure()
    {
        if (_host) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        _host = new GameObject("MrpSamuraiSound");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _voices = new AudioSource[Voices];
        for (int i = 0; i < Voices; i++) _voices[i] = Make(sm, false);
        _charge = Make(sm, true);
        _lock = Make(sm, true);
        return true;
    }

    private static AudioSource Make(SoundManager sm, bool loop)
    {
        var s = _host.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f;
        s.outputAudioMixerGroup = sm.SfxChannel;
        return s;
    }
}
