using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 時が戻る間 (逆再生) に、戻した区間に鳴っていた効果音を逆向きに鳴らす (全員・自分が聞いた音だけを自分の端末で・電文なし)。
// 記録するのは 1 回鳴りの音だけ (mod の音と本編の PlaySound)。足音・環境音などのループは記録しない。
// 時刻の対応は逆再生の絵と同じ緩急 (TimeRewind.PlayStepCore の smoothstep) で、各音は「鳴り終わった時刻」に来た所で
// 終わりから負の pitch で読み始める (速さ = 区間を縮める倍率)。逆再生が終わると全部止める
internal static class RewindSounds
{
    private struct Note
    {
        public AudioClip Clip;
        public long At;          // 鳴り始めた時刻 (Environment.TickCount64)
        public float Len;        // 鳴っていた長さ (秒・pitch 込み)
        public float Vol, Pan, Pitch;
    }

    private struct Cue
    {
        public AudioClip Clip;
        public long StartMs;     // 逆再生の始まりから何 ms で鳴らすか
        public float From;       // 読み始める位置 (秒・音の中の)
        public float Vol, Pan, Pitch;
    }

    private const int MaxNotes = 128, Voices = 8;
    // 逆再生の頭と終わりは絵の緩急でゆっくりなので、そのままの速さだと音が間延びする。逆向きの速さは 0.5〜3 倍に収める
    private const float MinPitch = 0.5f, MaxPitch = 3f;
    private static readonly List<Note> Notes = new();
    private static readonly List<Cue> Cues = new();
    private static AudioSource[] _voices;
    private static GameObject _host;
    private static int _next, _cue;
    private static long _start;

    // テスト用
    internal static int Recorded => Notes.Count;
    internal static string Last = "-";

    internal static void Record(AudioClip clip, float vol, float pan, float pitch, float delay)
    {
        if (!TimeRewind.On || TimeRewind.Playing || !clip || pitch <= 0f) return;
        if (Notes.Count >= MaxNotes) Notes.RemoveAt(0);
        Notes.Add(new Note
        {
            Clip = clip,
            At = Environment.TickCount64 + (long)(delay * 1000f),
            Len = clip.length / pitch,
            Vol = vol, Pan = pan, Pitch = pitch,
        });
    }

    internal static void Clear() => Notes.Clear();

    // 戻した所より後に鳴った音は無かったことになる
    internal static void Forget(long afterMs)
    {
        for (int i = Notes.Count - 1; i >= 0; i--)
            if (Notes[i].At > afterMs) Notes.RemoveAt(i);
    }

    // 逆再生の始まり: 世界の時刻 [slotMs, playStart] を実時間 [playStart, playEnd] へ逆向きに縮めて並べる
    internal static void Begin(long playStart, long playEnd, long slotMs)
    {
        Stop();
        _start = playStart;
        long span = playStart - slotMs;
        float play = playEnd - playStart;
        if (span <= 0 || play <= 0f || slotMs <= 0) return;
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var n = Notes[i];
            long end = n.At + (long)(n.Len * 1000f);
            if (end <= slotMs) { Notes.RemoveAt(i); continue; }   // 戻せる所より前に鳴り終わった音はもう要らない
            if (n.At >= playStart) continue;
            long wEnd = Math.Min(end, playStart), wStart = Math.Max(n.At, slotMs);
            float r0 = play * Unsmooth((playStart - wEnd) / (float)span);
            float r1 = play * Unsmooth((playStart - wStart) / (float)span);
            if (r1 - r0 < 1f) continue;
            float speed = (wEnd - wStart) / (r1 - r0);
            Cues.Add(new Cue
            {
                Clip = n.Clip,
                StartMs = (long)r0,
                From = (wEnd - n.At) / 1000f * n.Pitch,
                Vol = n.Vol, Pan = n.Pan,
                Pitch = -FxMath.Clamp(n.Pitch * speed, MinPitch, MaxPitch),
            });
        }
        Cues.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        Last = $"notes={Notes.Count} cues={Cues.Count} span={span}ms play={play:0}ms";
        Plugin.Logger.LogInfo($"[RewindSounds] {Last}");
    }

    // 逆再生の間の毎 FixedUpdate: 時刻の来た音を鳴らす (鳴らす音が無ければ何もしない)
    internal static void Step(long now)
    {
        if (_cue >= Cues.Count) return;
        long t = now - _start;
        while (_cue < Cues.Count && Cues[_cue].StartMs <= t)
        {
            var c = Cues[_cue++];
            if (!c.Clip || !EnsureVoices()) continue;
            var src = _voices[_next];
            _next = (_next + 1) % Voices;
            src.Stop();
            src.clip = c.Clip;
            src.volume = c.Vol;
            src.panStereo = c.Pan;
            src.pitch = c.Pitch;
            src.Play();
            // 負の pitch は今の位置から頭へ向かって読む。位置は鳴らした後に置く (先に置くと Play で頭へ戻る)
            src.time = FxMath.Clamp(c.From, 0f, c.Clip.length - 0.01f);
        }
    }

    // 逆再生が終わった・会議が始まった時: 絵が消えた後に音を残さない
    internal static void Stop()
    {
        Cues.Clear();
        _cue = 0;
        if (_voices == null) return;
        foreach (var v in _voices) if (v && v.isPlaying) v.Stop();
    }

    // テスト用: 鳴っている声ごとの「音の名前:pitch:再生位置 (標本)」
    internal static string Probe()
    {
        if (_voices == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var v in _voices)
            if (v && v.isPlaying && v.clip) sb.Append(v.clip.name).Append(':').Append(v.pitch.ToString("0.00")).Append(':').Append(v.timeSamples).Append(' ');
        return sb.ToString();
    }

    // 逆再生の 0..1 の進み (smoothstep の値) から実時間の 0..1 を引く (smoothstep の逆関数)
    private static float Unsmooth(float y)
    {
        y = FxMath.Clamp(y, 0f, 1f);
        return 0.5f - MathF.Sin(MathF.Asin(1f - 2f * y) / 3f);
    }

    private static bool EnsureVoices()
    {
        if (_host) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        _host = new GameObject("MrpRewindSounds");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _voices = new AudioSource[Voices];
        for (int i = 0; i < Voices; i++)
        {
            var s = _host.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 0f;
            s.outputAudioMixerGroup = sm.SfxChannel;
            _voices[i] = s;
        }
        return true;
    }
}

// 本編の 1 回鳴りの効果音 (キル・ベント・扉・警報など) を時戻しの逆再生のために控える。ループ (足音・環境音) は控えない
[HarmonyPatch(typeof(SoundManager), nameof(SoundManager.PlaySound))]
internal static class RewindSoundsVanillaPatch
{
    public static void Postfix(AudioClip clip, bool loop, float volume)
    {
        if (!loop && TimeRewind.On) RewindSounds.Record(clip, volume, 0f, 1f, 0f);
    }
}
