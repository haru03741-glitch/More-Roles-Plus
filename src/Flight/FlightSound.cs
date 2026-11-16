using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦で自分の船が出す音: エンジンの噴射 (出力で高さと大きさが変わる)・加速の点火・姿勢制御の噴射のガス。
// 本編の効果音の音量設定に従うよう効果音のミキサーへ流す。値が変わった時だけ書く (AudioSource の設定は interop)
internal static class FlightSound
{
    private static GameObject _host;
    private static AudioSource _engine, _rcs, _shot;
    private static float _vol = -1f, _pitch = -1f, _rcsVol = -1f;
    private static bool _ready; // 音の口は場面をまたいで残す (毎フレームの Unity の物の生存確認を避ける)

    private static bool Ensure()
    {
        if (_ready) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        var e = MrpBundle.Clip("noise_flt_engine_loop");
        var r = MrpBundle.Clip("noise_flt_rcs_loop");
        if (!e || !r) return false;
        _host = new GameObject("MrpFlightSound");
        Object.DontDestroyOnLoad(_host);
        _engine = Make(sm, true, e);
        _rcs = Make(sm, true, r);
        _shot = Make(sm, false, null);
        _vol = _pitch = _rcsVol = -1f;
        _ready = true;
        return true;
    }

    private static AudioSource Make(SoundManager sm, bool loop, AudioClip clip)
    {
        var s = _host.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f;
        s.outputAudioMixerGroup = sm.SfxChannel;
        if (clip) s.clip = clip;
        return s;
    }

    // 毎フレーム (3D の場面の間だけ): thr = 噴射の出力 0..1・rcs = 姿勢制御の噴射の強さ 0..1
    internal static void Tick(float thr, float rcs)
    {
        if (!Ensure()) return;
        float vol = 0.22f + 0.38f * thr;
        float pitch = 0.8f + 0.4f * thr;
        if (_vol < 0f) _engine.Play();
        if (System.Math.Abs(vol - _vol) > 0.01f) { _vol = vol; _engine.volume = vol; }
        if (System.Math.Abs(pitch - _pitch) > 0.01f) { _pitch = pitch; _engine.pitch = pitch; }
        float rv = rcs > 0.12f ? 0.12f + 0.3f * rcs : 0f;
        if (System.Math.Abs(rv - _rcsVol) > 0.01f)
        {
            if (rv > 0f && _rcsVol <= 0f) _rcs.Play();
            else if (rv <= 0f) _rcs.Stop();
            _rcsVol = rv;
            _rcs.volume = rv;
        }
    }

    internal static void Ignite()
    {
        if (!Ensure()) return;
        var c = MrpBundle.Clip("noise_flt_ignite");
        if (c) _shot.PlayOneShot(c, 0.7f);
    }

    internal static void Stop()
    {
        if (!_ready) return;
        _engine.Stop();
        _rcs.Stop();
        _shot.Stop();
        _vol = _pitch = _rcsVol = -1f;
    }
}
