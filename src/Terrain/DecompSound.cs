using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 外壁の穴の持続音: 吸い出しのゴォォ (ループ) と、泡で口が狭まる時の笛 (ループ)。
// DecompFx が毎フレーム口ごとに Feed し、いちばん大きく聞こえる口に合わせる。Feed が来ないフレーム (会議・口が無い) は薄れて止まる。
// 遠い口はこもった音 (<名前>_m)。どちらの音にするかは鳴らし始めに決め、鳴っている間は差し替えない (差し替えはぷつっと鳴る)。
// 音量と高さの書き込みは interop なので、値を刻んで変わった時だけ・PollEvery ごとに書く
internal static class DecompSound
{
    private const float Range = 30f, Muffle = 12f, FullDist = 3f;
    private const float RoarVolume = 0.8f, WhistleVolume = 0.45f;
    private const float PanDist = 10f, PanMax = 0.7f;
    private const float Ease = 8f;          // 1 秒あたりの寄り (立ち上がり・消え際でぷつっと鳴らさない)
    private const float WriteEvery = 0.05f;

    private sealed class Voice
    {
        public string Clip;
        public AudioSource Src;
        public bool Playing, Muffled;
        public float Want, WantPitch = 1f, WantPan, WantFar; // このフレームの目標 (Feed が書く)
        public float Vol, Pitch = 1f;                         // 今の値 (寄せた後)
        public int QVol = -1, QPitch = -1, QPan = int.MinValue;
    }

    private static readonly Voice Roar = new() { Clip = "noise_decomp_loop" };
    private static readonly Voice Whistle = new() { Clip = "noise_decomp_whistle" };
    private static GameObject _host;
    private static bool _fed, _active, _hostReady;
    private static float _writeAcc;
    private static long _lastMs;

    // 確認用
    internal static int Pops;
    internal static string State => $"roar={Text(Roar)} whistle={Text(Whistle)} pops={Pops}";
    private static string Text(Voice v) => $"{(v.Playing ? (v.Muffled ? "on(m)" : "on") : "off")} vol={v.Vol:0.00} pitch={v.Pitch:0.00}";

    // vis = 噴き出しの見た目の強さ (0..1)・gust = 突風の脈 (約 0.65〜1.15)・open = 口の開き (泡が狭める・1 = 全開)。
    // whistle = 泡で狭まっている口だけ (開通の瞬間の先行の噴流は false)
    internal static void Feed(float vis, float gust, float dx, float dy, float open, bool whistle)
    {
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d >= Range) return;
        float near = d <= FullDist ? 1f : 1f - (d - FullDist) / (Range - FullDist);
        near *= FxMath.Sqrt(near);
        float pan = FxMath.Clamp(dx / PanDist, -PanMax, PanMax);
        _fed = true;
        float v = vis * gust * near * RoarVolume;
        if (v > Roar.Want) { Roar.Want = v; Roar.WantPitch = 0.85f + 0.2f * vis + 0.25f * (gust - 1f); Roar.WantPan = pan; Roar.WantFar = d; }
        if (!whistle || open >= 1f) return;
        // 泡が吹き付け始めると鳴り出し、狭まるほど高く強く、閉じる寸前に消える
        float closed = 1f - open;
        float w = FxMath.Min(1f, closed * 1.6f) * FxMath.Min(1f, open / 0.06f) * near * WhistleVolume;
        if (w > Whistle.Want) { Whistle.Want = w; Whistle.WantPitch = 0.75f + 1.0f * closed; Whistle.WantPan = pan; Whistle.WantFar = d; }
    }

    // DecompFx.Tick の後に毎フレーム
    public static void Tick()
    {
        if (!_fed && !_active) return;
        try { TickCore(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[DecompSound] {e}");
            _active = false;
            StopAll();
        }
        _fed = false;
        Roar.Want = Whistle.Want = 0f;
    }

    private static void TickCore()
    {
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        if (!EnsureHost()) return;
        float k = FxMath.Min(1f, dt * Ease);
        Approach(Roar, k);
        Approach(Whistle, k);
        _active = Roar.Playing || Whistle.Playing || _fed;
        if (!_active) { _lastMs = 0; return; }
        _writeAcc += dt;
        if (_writeAcc < WriteEvery) return;
        _writeAcc = 0f;
        Write(Roar);
        Write(Whistle);
    }

    private static void Approach(Voice v, float k)
    {
        v.Vol += (v.Want - v.Vol) * k;
        if (v.Want > 0f) v.Pitch += (v.WantPitch - v.Pitch) * k;
    }

    private static void Write(Voice v)
    {
        if (!v.Playing)
        {
            if (v.Want <= 0.005f) return;
            var clip = Fx.MrpBundle.Clip(v.WantFar > Muffle ? v.Clip + "_m" : v.Clip);
            if (!clip) return;
            v.Muffled = v.WantFar > Muffle;
            v.Src.clip = clip;
            v.Pitch = v.WantPitch;
            v.QVol = v.QPitch = -1; v.QPan = int.MinValue;
            v.Playing = true;
            Apply(v);
            v.Src.Play();
            return;
        }
        if (v.Want <= 0f && v.Vol < 0.005f)
        {
            v.Src.Stop();
            v.Playing = false;
            v.Vol = 0f;
            return;
        }
        Apply(v);
    }

    private static void Apply(Voice v)
    {
        // こもった音で鳴らし始めた後に近づいた (逆も) 分は、音量だけで合わせる
        float vol = v.Vol * (v.Muffled ? 1.3f : 1f);
        int qv = (int)(vol * 64f), qp = (int)(v.Pitch * 128f), qn = (int)(v.WantPan * 32f);
        if (qv != v.QVol) { v.QVol = qv; v.Src.volume = qv / 64f; }
        if (qp != v.QPitch) { v.QPitch = qp; v.Src.pitch = qp / 128f; }
        if (qn != v.QPan && v.Want > 0f) { v.QPan = qn; v.Src.panStereo = qn / 32f; }
    }

    private static void StopAll()
    {
        foreach (var v in new[] { Roar, Whistle })
        {
            v.Playing = false;
            v.Vol = 0f;
            try { if (v.Src) { v.Src.volume = 0f; v.Src.Stop(); } } catch { }
        }
    }

    // 本編の効果音の音量設定に従うよう、効果音のミキサーへ流す。置き場は消えない (DontDestroyOnLoad) ので作った後は bool だけ見る
    private static bool EnsureHost()
    {
        if (_hostReady) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        _host = new GameObject("MrpDecompSound");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        Roar.Src = Make(sm);
        Whistle.Src = Make(sm);
        Roar.Playing = Whistle.Playing = false;
        _hostReady = true;
        return true;
    }

    private static AudioSource Make(SoundManager sm)
    {
        var s = _host.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = true;
        s.spatialBlend = 0f;
        s.volume = 0f;
        s.outputAudioMixerGroup = sm.SfxChannel;
        return s;
    }
}
