using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// ノヴァの効果音。音は絵と同じ所 (どの端末でも同じ溜めと光線を再生している) から鳴らすので電文は増やさない。
// どこから撃たれたか分かるように、左右に振り、遠いほど響きの多い版 (<名前>_f) へ音量を移す (近い = 乾いた直接音・遠い = 高い所が落ちて響きが長い)。
// 溜めと照射は人ごとのループ (近い版と遠い版を同時に回し、距離で音量だけ配分する)。撃つ人が自分の視界に無い時の溜めは鳴らさない (居場所が漏れる)
internal static partial class NovaFx
{
    private const float FireRange = 40f, ChargeRange = 22f;  // ここより遠いと聞こえない (光線は船の大半に届く)
    private const float LoudDist = 3f;                       // ここまでは最大の音量
    private const float DryFade = 12f, WetFade = 10f;        // 乾いた版は 2 から DryFade で消え、響きの版は 2 から WetFade で満ちる
    private const float PanDist = 6f, PanMax = 0.9f, WetPan = 0.6f; // 響きは周りの壁から返るので左右の振れを小さく
    private const int OneShots = 6, Smalls = 6;  // 大きな音 (撃つ・着弾・満タン) とアーク・照明の音の声は分ける (細かい音で大きな音の尾が切られない)
    private const float BrownoutGap = 0.35f;     // 照明が続けてちらついた時は音を重ねない

    private sealed class Voice
    {
        public AudioSource ChargeN, ChargeF, BeamN, BeamF;
        public bool Charging, Full, Beaming, Ending, Shown;
        public float Lv, NextArc, FireLv, X, Y, C0;
        public float VolN = -1f, VolF = -1f, Pan = 9f, Pitch = -1f, BVolN = -1f, BVolF = -1f, BPan = 9f;
        public int Seen, BSeen;
    }

    private static GameObject _sndHost;
    private static AudioSource[] _shotN, _shotF, _small;
    private static int _shotNext, _smallNext, _sndFrame;
    private static float _brownAt = -9f;
    private static readonly Dictionary<byte, Voice> Voices = new();
    private static bool _sndOn;    // 回っているループがある (無ければ毎フレーム何もしない)
    private static float _earX, _earY;
    // 試験用: 聞く位置を自分からずらす (1 窓で遠さと左右を確かめる)
    internal static bool EarShift;
    internal static float EarShiftX, EarShiftY;
    private static int _played;
    private static string _lastSnd = "-";
    internal static string SoundState()
    {
        int loops = 0;
        foreach (var v in Voices.Values) { if (v.Charging) loops++; if (v.Beaming) loops++; }
        return $"played={_played} last={_lastSnd} loops={loops} on={_sndOn} ear=({_earX:0.0},{_earY:0.0})";
    }

    // ---- 単発 ----

    // (x, y) で鳴らす。range より遠いと鳴らさない
    private static void Shot1(string clip, float x, float y, float vol, float range)
    {
        if (MeetingHud.Instance || !Ear() || !Mix(x, y, range, out float n, out float f, out float pan) || !SndReady()) return;
        Play1(clip, vol * n, pan, PortSound.Get(clip), vol * f, pan * WetPan, PortSound.Get(clip + "_f"));
    }

    // アーク: 近い版だけを細かい音の声で (遠いと響きに埋もれる)
    private static void Small1(string clip, float x, float y, float vol, float range)
    {
        if (MeetingHud.Instance || !Ear() || !Mix(x, y, range, out float n, out _, out float pan) || !SndReady()) return;
        PlaySmall(clip, vol * n, pan);
    }

    private static void PlaySmall(string clip, float vol, float pan)
    {
        var src = _small[_smallNext];
        _smallNext = (_smallNext + 1) % Smalls;
        Start1(src, PortSound.Get(clip), vol, pan, FxMath.Range(0.94f, 1.06f));
        _played++;
        _lastSnd = $"{clip} n={vol:0.00} pan={pan:0.00}";
    }

    // 自分の周りで鳴らす (構え・船の照明)
    private static void Here1(string clip, float vol)
    {
        if (MeetingHud.Instance || !SndReady()) return;
        PlaySmall(clip, vol, 0f);
    }

    private static void Play1(string clip, float vn, float pn, AudioClip cn, float vf, float pf, AudioClip cf)
    {
        int i = _shotNext;
        _shotNext = (_shotNext + 1) % OneShots;
        float pitch = FxMath.Range(0.97f, 1.03f);
        Start1(_shotN[i], cn, vn, pn, pitch);
        Start1(_shotF[i], cf, vf, pf, pitch);
        _played++;
        _lastSnd = $"{clip} n={vn:0.00} f={vf:0.00} pan={pn:0.00}";
    }

    private static void Start1(AudioSource src, AudioClip c, float vol, float pan, float pitch)
    {
        src.Stop();
        if (!c || vol < 0.005f) return;
        src.clip = c;
        src.volume = vol;
        src.panStereo = pan;
        src.pitch = pitch;
        src.Play();
    }

    // ---- きっかけ (NovaFx の各所から) ----

    private static void SndStance(bool on)
    {
        if (!RewindPlayback.Active) Here1(on ? "nova_stance_on" : "nova_stance_off", on ? 0.55f : 0.45f);
    }

    // 撃った: 溜め量で軽い破裂と満タンの裂け目を混ぜる。壁に当たれば先で焼ける音
    private static void SndFire(PlayerControl p, float x, float y, float lv, bool hit, float hx, float hy)
    {
        if (p && Voices.TryGetValue(p.PlayerId, out var v)) v.FireLv = lv;
        else if (p) VoiceOf(p.PlayerId).FireLv = lv;
        float light = 1f - 0.65f * lv, heavy = lv * FxMath.Sqrt(lv);
        if (light > 0.05f) Shot1("nova_fire", x, y, light, FireRange);
        if (heavy > 0.05f) Shot1("nova_fire_full", x, y, heavy, FireRange);
        if (hit) Shot1("nova_impact", hx, hy, 0.6f + 0.4f * lv, FireRange);
    }

    // 船の照明が落ちた瞬間 (満タンの乱れ)
    private static void SndBrownout(float strength)
    {
        float now = Time.time;
        if (now - _brownAt < BrownoutGap) return;
        _brownAt = now;
        Here1("nova_brownout", 0.2f + 0.5f * strength);
    }

    // ---- ループ (毎フレーム・Shots と Beams を進めた後) ----

    private static void TickSound(float now)
    {
        if (Shots.Count == 0 && Beams.Count == 0 && !_sndOn) return;
        if (MeetingHud.Instance) { StopSound(); return; }
        if (!SndReady() || !Ear()) return;
        int frame = ++_sndFrame;
        bool any = false;

        foreach (var s in Shots)
        {
            if (s.Stance || s.FiredAt >= 0f || s.EndAt >= 0f || s.Dur <= 0f || !s.P) continue;
            var v = VoiceOf(s.P.PlayerId);
            v.Seen = frame;
            float lv = FxMath.Clamp01((now - s.C0) / s.Dur);
            v.X = s.Px; v.Y = s.Py;
            v.Shown = s.Shown;
            if (v.Charging && s.C0 != v.C0) { Stop(v.ChargeN, v.ChargeF); v.Charging = false; } // 同じ人が溜め直した
            if (!v.Charging)
            {
                v.Charging = true; v.Full = false; v.Lv = 0f; v.C0 = s.C0;
                v.NextArc = now + FxMath.Range(0.8f, 1.4f);
                if (s.Shown) Shot1("nova_charge_start", s.Px, s.Py, 0.8f, ChargeRange);
                Loop(v.ChargeN, v.ChargeF, "nova_charge_loop");
                v.VolN = v.VolF = -1f; v.Pitch = -1f;
            }
            if (s.Shown)
            {
                if (lv >= 0.7f && v.Lv < 0.7f) Shot1("nova_plates", s.Px, s.Py, 0.75f, ChargeRange);
                if (lv >= 1f && !v.Full)
                {
                    v.Full = true;
                    Shot1("nova_full", s.Px, s.Py, 0.9f, FireRange);
                    Loop(v.ChargeN, v.ChargeF, "nova_full_loop");
                    v.VolN = v.VolF = -1f; v.Pitch = -1f;
                }
                // 砲身の間を跳ぶ太いアーク: 溜まるほど頻繁に
                if (now >= v.NextArc)
                {
                    v.NextArc = now + FxMath.Range(0.4f, 1.3f) * (1.3f - 0.75f * lv);
                    Small1("nova_charge_arc", s.Px, s.Py, 0.25f + 0.55f * lv, ChargeRange);
                }
            }
            v.Lv = lv;
            float loud = s.Shown ? (v.Full ? 0.85f : (0.3f + 0.6f * lv)) : 0f;
            float pitch = v.Full ? 1f : 0.8f + 0.45f * lv;
            Set(v.ChargeN, v.ChargeF, s.Px, s.Py, loud, v.Full ? FireRange : ChargeRange, ref v.VolN, ref v.VolF, ref v.Pan);
            if (pitch - v.Pitch > 0.02f || v.Pitch - pitch > 0.02f) { v.Pitch = pitch; v.ChargeN.pitch = v.ChargeF.pitch = pitch; }
            any = true;
        }

        foreach (var b in Beams)
        {
            if (!b.P) continue;
            var v = VoiceOf(b.P.PlayerId);
            float t = now - b.T0;
            float fade = t > b.Life - Thread ? FxMath.Clamp01((b.Life - t) / Thread) : 1f;
            if (fade <= 0f) continue;   // 光線は消えて床の焼け跡だけが冷めている
            // 長い光線は一番近い所から聞こえる
            float rx = _earX - b.X, ry = _earY - b.Y;
            float along = rx * b.Dx + ry * b.Dy;
            along = along < 0f ? 0f : along > b.Len ? b.Len : along;
            float bx = b.X + b.Dx * along, by = b.Y + b.Dy * along;
            if (v.BSeen == frame)
            {
                // 割れた枝など同じ人の 2 本目: 近い方を使う
                float d0 = (v.X - _earX) * (v.X - _earX) + (v.Y - _earY) * (v.Y - _earY);
                float d1 = (bx - _earX) * (bx - _earX) + (by - _earY) * (by - _earY);
                if (d1 >= d0) continue;
            }
            v.BSeen = frame;
            v.X = bx; v.Y = by;
            if (!v.Beaming)
            {
                v.Beaming = true; v.Ending = false;
                Loop(v.BeamN, v.BeamF, "nova_beam_loop");
                v.BVolN = v.BVolF = -1f;
            }
            if (fade < 1f && !v.Ending)
            {
                v.Ending = true;
                Shot1("nova_beam_end", bx, by, 0.5f + 0.4f * v.FireLv, FireRange);
            }
            else if (fade >= 1f) v.Ending = false;   // 刀で押し止められて照射が延びた
            Set(v.BeamN, v.BeamF, bx, by, (0.55f + 0.45f * v.FireLv) * fade, FireRange, ref v.BVolN, ref v.BVolF, ref v.BPan);
            any = true;
        }

        foreach (var kv in Voices)
        {
            var v = kv.Value;
            if (v.Charging && v.Seen != frame)
            {
                v.Charging = false;
                Stop(v.ChargeN, v.ChargeF);
                // 撃たずに終わった (光線に替わった時は撃つ音が鳴っている)
                if (v.Shown && !FiredLately(kv.Key, now)) Shot1("nova_cancel", v.X, v.Y, 0.6f, ChargeRange);
            }
            if (v.Beaming && v.BSeen != frame)
            {
                v.Beaming = false;
                Stop(v.BeamN, v.BeamF);
            }
        }
        _sndOn = any;
    }

    private static bool FiredLately(byte id, float now)
    {
        foreach (var s in Shots) if (s.P && s.P.PlayerId == id && s.FiredAt >= 0f && now - s.FiredAt < 0.5f) return true;
        return false;
    }

    // ループの音量と左右を、変わった時だけ書く
    private static void Set(AudioSource n, AudioSource f, float x, float y, float loud, float range, ref float vn, ref float vf, ref float pan)
    {
        float a = 0f, b = 0f, p = pan;
        if (loud > 0f && Mix(x, y, range, out float gn, out float gf, out float pp)) { a = loud * gn; b = loud * gf; p = pp; }
        if (a - vn > 0.015f || vn - a > 0.015f) { vn = a; n.volume = a; }
        if (b - vf > 0.015f || vf - b > 0.015f) { vf = b; f.volume = b; }
        if (p - pan > 0.03f || pan - p > 0.03f) { pan = p; n.panStereo = p; f.panStereo = p * WetPan; }
    }

    private static void Loop(AudioSource n, AudioSource f, string clip)
    {
        var cn = PortSound.Get(clip);
        var cf = PortSound.Get(clip + "_f");
        n.Stop(); f.Stop();
        n.volume = f.volume = 0f;
        if (cn) { n.clip = cn; n.pitch = 1f; n.Play(); }
        if (cf) { f.clip = cf; f.pitch = 1f; f.Play(); }
    }

    private static void Stop(AudioSource n, AudioSource f)
    {
        n.Stop(); f.Stop();
    }

    // 会議・巻き戻し・抜けた: 全部止める
    private static void StopSound()
    {
        if (!_sndOn) return;
        _sndOn = false;
        foreach (var v in Voices.Values)
        {
            if (v.Charging || v.Full) Stop(v.ChargeN, v.ChargeF);
            if (v.Beaming) Stop(v.BeamN, v.BeamF);
            v.Charging = v.Full = v.Beaming = v.Ending = false;
        }
    }

    // ---- 距離 ----

    private static bool Ear()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return false;
        Vector2 e = Watch.Ear(lp);
        _earX = e.x; _earY = e.y;
        if (EarShift) { _earX += EarShiftX; _earY += EarShiftY; }
        return true;
    }

    // 近い版と遠い版の音量 (0〜1)・左右
    private static bool Mix(float x, float y, float range, out float near, out float far, out float pan)
    {
        float dx = x - _earX, dy = y - _earY;
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        near = far = pan = 0f;
        if (d >= range) return false;
        float g = d <= LoudDist ? 1f : 1f - (d - LoudDist) / (range - LoudDist);
        g *= FxMath.Sqrt(g);
        near = g * FxMath.Clamp01(1f - (d - 2f) / DryFade);
        far = g * 0.9f * FxMath.Clamp01((d - 2f) / WetFade);
        float p = dx / PanDist;
        pan = p > PanMax ? PanMax : p < -PanMax ? -PanMax : p;
        return true;
    }

    // ---- 声 ----

    private static Voice VoiceOf(byte id)
    {
        if (Voices.TryGetValue(id, out var v)) return v;
        v = new Voice
        {
            ChargeN = MakeSrc(true), ChargeF = MakeSrc(true),
            BeamN = MakeSrc(true), BeamF = MakeSrc(true),
        };
        Voices[id] = v;
        return v;
    }

    // 本編の効果音の音量設定に従うよう、効果音のミキサーへ流す
    private static bool SndReady()
    {
        if (_sndHost) return true;
        if (!SoundManager.Instance) return false;
        _sndHost = new GameObject("MrpNovaSound");
        Object.DontDestroyOnLoad(_sndHost);
        Voices.Clear();
        _shotN = new AudioSource[OneShots];
        _shotF = new AudioSource[OneShots];
        for (int i = 0; i < OneShots; i++) { _shotN[i] = MakeSrc(false); _shotF[i] = MakeSrc(false); }
        _small = new AudioSource[Smalls];
        for (int i = 0; i < Smalls; i++) _small[i] = MakeSrc(false);
        return true;
    }

    private static AudioSource MakeSrc(bool loop)
    {
        var s = _sndHost.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f;
        s.outputAudioMixerGroup = SoundManager.Instance.SfxChannel;
        return s;
    }
}
