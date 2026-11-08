using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 移した役職の効果音。Resources/Sounds/<名前>.wav (16bit PCM) があればそれを、無ければ本編の音で代わりに鳴らす
public static class PortSound
{
    private static readonly Dictionary<string, AudioClip> Cache = new();
    private static readonly HashSet<string> Missing = new();

    public static AudioClip Get(string name)
    {
        if (Cache.TryGetValue(name, out var c) && c) return c;
        if (Missing.Contains(name)) return null;
        c = Load(name);
        if (c) Cache[name] = c;
        else Missing.Add(name);
        return c;
    }

    // 自分の画面で鳴らす。at を渡すと、そこから range より遠いと聞こえない (近いほど大きい)
    public static void Play(string name, Func<AudioClip> fallback, float volume = 1f, Vector2? at = null, float range = 0f)
    {
        var sm = SoundManager.Instance;
        if (!sm) return;
        if (at is Vector2 pos && range > 0f)
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) return;
            float d = Vector2.Distance(lp.GetTruePosition(), pos);
            if (d > range) return;
            volume *= 1f - 0.7f * (d / range);
        }
        var clip = (name != null ? Get(name) : null) ?? fallback?.Invoke();
        if (clip) sm.PlaySound(clip, false, volume, sm.SfxChannel);
    }

    // 専用の音がまだ無い時に、本編の似た音 (Fallback と同じ選び方) をそのまま鳴らす
    public static void PlayVanilla(string kind, float volume = 1f) => Play(null, () => Fallback(kind), volume);

    // 全員の画面で鳴らす (その場所から range まで聞こえる)。送った人の端末でも鳴る
    public static void PlayAll(string name, Vector2 at, float range, float volume = 1f)
    {
        AllCall.Send((name, at.x, at.y, range, volume));
        Play(name, () => Fallback(name), volume, at, range);
    }

    private static readonly RemoteCall<(string name, float x, float y, float range, float volume)> AllCall = new("PortSound.All", Route.Anyone,
        (w, m) => { w.Write(m.name); w.Write(m.x); w.Write(m.y); w.Write(m.range); w.Write(m.volume); },
        r => (r.ReadString(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        (_, m) => Play(m.name, () => Fallback(m.name), m.volume, FxMath.V2(m.x, m.y), m.range));

    // 名前の頭で本編の似た音を選ぶ
    public static AudioClip Fallback(string name)
    {
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (name.EndsWith("_alert") && ShipStatus.Instance) return ShipStatus.Instance.SabotageSound;
        if ((name.EndsWith("_boom") || name.EndsWith("_beam") || name.EndsWith("_shot")) && lp) return lp.KillSfx;
        return hud ? hud.TaskCompleteSound : null;
    }

    private static AudioClip Load(string name)
    {
        string res = $"MoreRolesPlus.Resources.Sounds.{name}.wav";
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
        if (s == null) return null;
        try
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return FromWav(ms.ToArray(), name);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"sound {name}: {e.Message}");
            return null;
        }
    }

    // RIFF/WAVE の 16bit PCM (1ch・2ch)
    private static AudioClip FromWav(byte[] b, string name)
    {
        if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') return null;
        int channels = 1, rate = 44100, bits = 16, at = 12, dataAt = -1, dataLen = 0;
        while (at + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
            int len = BitConverter.ToInt32(b, at + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, at + 10);
                rate = BitConverter.ToInt32(b, at + 12);
                bits = BitConverter.ToInt16(b, at + 22);
            }
            else if (id == "data") { dataAt = at + 8; dataLen = Math.Min(len, b.Length - dataAt); break; }
            at += 8 + len + (len & 1);
        }
        if (dataAt < 0 || bits != 16 || channels < 1) return null;
        int count = dataLen / 2;
        var f = new float[count];
        for (int i = 0; i < count; i++) f[i] = BitConverter.ToInt16(b, dataAt + i * 2) / 32768f;
        var data = new Il2CppStructArray<float>((long)count);
        for (int i = 0; i < count; i++) data[i] = f[i];
        var clip = AudioClip.Create("MrpSound_" + name, count / channels, channels, rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }
}
