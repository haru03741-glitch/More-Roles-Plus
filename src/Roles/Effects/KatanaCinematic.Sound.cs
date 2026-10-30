using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 必殺技の音: 演出を見る人 (斬る人と斬られる人) だけが聞く。鳴っている間は本編の環境音と音楽を止める (静寂から一気に、が効くように)。
// 音は絵より長く、画面が閉じた後の袈裟・横一文字・連撃の跡まで続く (ホストはこの合図のコマで倒し、裂け目を刻む)。
// 会議や船の入れ替えで打ち切った時だけ途中で止める
internal static unsafe partial class KatanaCinematic
{
    private const string SoundRes = "MoreRolesPlus.Resources.Cinema.katana_ult.pcm";
    private static AudioClip _clip;
    private static AudioSource _voice;
    private static AudioSource[] _muted = Array.Empty<AudioSource>();

    private static void StartSound()
    {
        var sm = SoundManager.Instance;
        if (!sm) return;
        if (!_clip) _clip = LoadClip();
        if (!_clip) return;
        StopSound();
        var all = UnityEngine.Object.FindObjectsOfType<AudioSource>();
        var list = new System.Collections.Generic.List<AudioSource>();
        IntPtr amb = sm.AmbienceChannel ? sm.AmbienceChannel.Pointer : IntPtr.Zero, mus = sm.MusicChannel ? sm.MusicChannel.Pointer : IntPtr.Zero;
        foreach (var s in all)
        {
            if (!s || s.mute) continue;
            var g = s.outputAudioMixerGroup;
            IntPtr gp = g ? g.Pointer : IntPtr.Zero;
            if ((gp != IntPtr.Zero && (gp == amb || gp == mus)) || (s.loop && s.isPlaying))
            {
                s.mute = true;
                list.Add(s);
            }
        }
        _muted = list.ToArray();
        if (!_voice)
        {
            var go = new GameObject("MrpKatanaCinemaVoice");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _voice = go.AddComponent<AudioSource>();
            _voice.playOnAwake = false;
            _voice.loop = false;
            _voice.spatialBlend = 0f;
            _voice.outputAudioMixerGroup = sm.SfxChannel;
        }
        _voice.clip = _clip;
        _voice.volume = 1f;
        _voice.Play();
    }

    // 演出の絵が終わった後: 音が鳴り終わったら止めていた音を戻す (止めた物がある間だけ見る)
    private static void SoundTick()
    {
        if (_muted.Length == 0) return;
        if (_voice && _voice.isPlaying) return;
        StopSound();
    }

    private static void StopSound()
    {
        if (_voice && _voice.isPlaying) _voice.Stop();
        foreach (var s in _muted) if (s) s.mute = false;
        _muted = Array.Empty<AudioSource>();
    }

    private static AudioClip LoadClip()
    {
        using var st = typeof(KatanaCinematic).Assembly.GetManifestResourceStream(SoundRes);
        if (st == null) return null;
        var raw = new byte[st.Length];
        for (int n = 0; n < raw.Length;)
        {
            int r = st.Read(raw, n, raw.Length - n);
            if (r <= 0) return null;
            n += r;
        }
        // 頭 4 バイト = 1 秒あたりの数・あとは 16bit の 1ch
        int rate = BitConverter.ToInt32(raw, 0);
        int count = (raw.Length - 4) / 2;
        var data = new Il2CppStructArray<float>((long)count);
        float* dp = (float*)((byte*)data.Pointer + 4 * IntPtr.Size);
        fixed (byte* p = raw)
        {
            short* s = (short*)(p + 4);
            for (int i = 0; i < count; i++) dp[i] = s[i] / 32768f;
        }
        var clip = AudioClip.Create("MrpKatanaUlt", count, 1, rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }
}
