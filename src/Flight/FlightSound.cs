using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦で自分の船が出す音: エンジンの噴射 (出力で高さと大きさが変わる)・加速の点火・姿勢制御の噴射のガス・主砲。
// 本編の効果音の音量設定に従うよう効果音のミキサーへ流す。値が変わった時だけ書く (AudioSource の設定は interop)
internal static class FlightSound
{
    private static GameObject _host;
    private static AudioSource _engine, _rcs, _shot;
    private static float _vol = -1f, _pitch = -1f, _rcsVol = -1f;
    private static bool _ready; // 音の口は場面をまたいで残す (毎フレームの Unity の物の生存確認を避ける)
    private static SoundManager _sm; // 口を作った時の本編の音の係 (後から足す口も同じ出力の群へ)

    private static bool Ensure()
    {
        if (_ready) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        var e = MrpBundle.Clip("noise_flt_engine_loop");
        var r = MrpBundle.Clip("noise_flt_rcs_loop");
        if (!e || !r) return false;
        _sm = sm;
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

    private static int _fireNext;
    private static AudioSource _siren;
    private static float _sirenVol = -1f, _sirenPitch = -1f;

    // 夜の街の警報。vol と pitch は鳴らす側が毎フレーム渡す (モーターが回り出す・止まる高さの変化)。値が変わった時だけ書く
    internal static void Siren(bool on, float vol, float pitch)
    {
        if (!on || vol <= 0.002f)
        {
            if (_siren && _sirenVol > 0f) _siren.Stop();
            _sirenVol = on ? 0f : -1f;
            return;
        }
        if (!Ensure()) return;
        if (!_siren)
        {
            var c = MrpBundle.Clip("noise_flt_siren_loop");
            if (!c) return;
            _siren = Make(_sm, true, c);
        }
        if (_sirenVol <= 0f)
        {
            _siren.volume = vol;
            _siren.pitch = pitch;
            _sirenVol = vol;
            _sirenPitch = pitch;
            _siren.Play();
            return;
        }
        if (System.Math.Abs(vol - _sirenVol) > 0.01f) { _sirenVol = vol; _siren.volume = vol; }
        if (System.Math.Abs(pitch - _sirenPitch) > 0.005f) { _sirenPitch = pitch; _siren.pitch = pitch; }
    }

    // 街の停電 (警報の始まり)
    internal static void Blackout()
    {
        if (!Ensure()) return;
        var c = MrpBundle.Clip("noise_flt_blackout");
        if (c) _shot.PlayOneShot(c, 0.75f);
    }

    // 主砲を撃った音 (連射で同じに聞こえないよう 3 つを順に)・溜め撃ちは別の音
    internal static void Fire(bool big, float vol)
    {
        if (!Ensure()) return;
        string name = big ? "noise_flt_fire_big" : _fireNext switch { 0 => "noise_flt_fire_1", 1 => "noise_flt_fire_2", _ => "noise_flt_fire_3" };
        _fireNext = (_fireNext + 1) % 3;
        var c = MrpBundle.Clip(name);
        if (c) _shot.PlayOneShot(c, (big ? 0.85f : 0.42f) * vol);
    }

    // 船がビルを擦っている間 (擦れの出来事が来るたびに少し延ばす)。k = 強さ 0..1
    private static AudioSource _scrape;
    private static float _scrapeHold, _scrapeVol = -1f;
    private static int _bldNext;

    internal static void Scrape(float k)
    {
        if (!Ensure()) return;
        if (!_scrape)
        {
            var c = MrpBundle.Clip("noise_flt_scrape_loop");
            if (!c) return;
            _scrape = Make(_sm, true, c);
        }
        _scrapeHold = 0.18f;
        float v = 0.3f + 0.45f * k;
        if (_scrapeVol <= 0f) { _scrape.volume = v; _scrape.pitch = 0.9f + 0.2f * k; _scrape.Play(); _scrapeVol = v; }
        else if (v > _scrapeVol + 0.05f) { _scrapeVol = v; _scrape.volume = v; }
    }

    // 毎フレーム: 擦れの出来事が途切れたら擦れの音を止める
    internal static void TickScrape(float dt)
    {
        if (_scrapeVol <= 0f) return;
        _scrapeHold -= dt;
        if (_scrapeHold > 0f) return;
        _scrape.Stop();
        _scrapeVol = -1f;
    }

    // 正面からぶつかった (k = 強さ 0..1)
    internal static void Crash(float k)
    {
        if (!Ensure()) return;
        var c = MrpBundle.Clip("noise_flt_crash");
        if (c) _shot.PlayOneShot(c, 0.55f + 0.4f * k);
    }

    // ビルが燃え始めた (外壁にひびが走って剥がれ、火が回る)
    internal static void BuildingBurn(float vol)
    {
        if (!Ensure() || vol <= 0.02f) return;
        var c = MrpBundle.Clip("noise_flt_bldburn");
        if (c) _shot.PlayOneShot(c, 0.7f * vol);
    }

    // ビルが崩れ始めた (h = 高さ・高いほど低く長く聞こえるよう少し下げる)
    internal static void BuildingCollapse(float vol, float h)
    {
        if (!Ensure() || vol <= 0.02f) return;
        var c = MrpBundle.Clip("noise_flt_collapse");
        if (!c) return;
        // 高さは鳴り始めの時だけ決める (鳴っている途中で変えると先の崩れの音程まで揺れる)
        if (!_collapse) _collapse = Make(_sm, false, c);
        if (!_collapse.isPlaying) _collapse.pitch = Mathf.Clamp(1.15f - h / 900f, 0.65f, 1.1f);
        _collapse.PlayOneShot(c, 0.95f * vol);
    }

    private static AudioSource _collapse;

    // 光弾がビルに当たった (vol = 離れるほど小さく)
    internal static void BuildingHit(float vol)
    {
        if (!Ensure()) return;
        _bldNext = (_bldNext + 1) % 3;
        var c = MrpBundle.Clip("noise_flt_bldhit_" + (_bldNext + 1));
        if (c) _shot.PlayOneShot(c, 0.5f * vol);
    }

    // 撃たれた車が爆ぜた (ガラスが割れ、燃料に火が回って燃え広がる)
    internal static void CarBoom(float vol)
    {
        if (!Ensure() || vol <= 0.02f) return;
        var c = MrpBundle.Clip("noise_flt_car_boom");
        if (c) _shot.PlayOneShot(c, 0.85f * vol);
    }

    private static AudioSource _carAlarm;
    private static int _hornNext;

    // 防犯アラームの鳴り続ける音 (vol 0 で止める)
    internal static void CarAlarm(float vol)
    {
        if (vol <= 0f)
        {
            if (_carAlarm) _carAlarm.Stop();
            return;
        }
        if (!Ensure()) return;
        if (!_carAlarm)
        {
            var c = MrpBundle.Clip("noise_flt_car_alarm_loop");
            if (!c) return;
            _carAlarm = Make(_sm, true, c);
        }
        _carAlarm.volume = vol;
        if (!_carAlarm.isPlaying) _carAlarm.Play();
    }

    // 船が低く抜けた時の下の車のクラクション (skid = タイヤがきしむ急ブレーキも重ねる)
    internal static void Horn(float vol, bool skid)
    {
        if (!Ensure()) return;
        _hornNext = (_hornNext + 1) % 2;
        var c = MrpBundle.Clip("noise_flt_horn_" + (_hornNext + 1));
        if (c) _shot.PlayOneShot(c, 0.6f * vol);
        if (!skid) return;
        var k = MrpBundle.Clip("noise_flt_skid");
        if (k) _shot.PlayOneShot(k, 0.5f * vol);
    }

    internal static void Stop()
    {
        if (!_ready) return;
        if (_scrape) _scrape.Stop();
        _scrapeVol = -1f;
        if (_collapse) _collapse.Stop();
        _engine.Stop();
        _rcs.Stop();
        if (_siren) _siren.Stop();
        if (_carAlarm) _carAlarm.Stop();
        _sirenVol = -1f;
        _shot.Stop();
        _vol = _pitch = _rcsVol = -1f;
    }
}
