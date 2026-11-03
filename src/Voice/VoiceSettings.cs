using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;

namespace MoreRolesPlus.Voice;

// ボイスチャットの自分だけの設定 (ホストは関係ない): 人ごとの聞こえる大きさ・マイクの大きさ・ボイスチェンジャー
internal static class VoiceSettings
{
    internal const int MinGain = 50, MaxGain = 300;  // マイクの大きさ (%)
    internal const int MaxVolume = 200;               // 人ごとの大きさ (%)

    private static ConfigEntry<string> _volumes;
    private static ConfigEntry<int> _changer, _micGain;
    private static ConfigEntry<bool> _denoise;
    private static readonly Dictionary<string, int> Volumes = new();
    private static bool _dirty;

    internal static void Bind(ConfigFile cfg)
    {
        _volumes = cfg.Bind("Voice", "PlayerVolumes", "", "人ごとの聞こえる大きさ (フレンドコード=%;...)");
        _changer = cfg.Bind("Voice", "Changer", 0, "ボイスチェンジャー (0 なし / 1 高い / 2 低い / 3 ロボット / 4 無線)");
        _micGain = cfg.Bind("Voice", "MicGain", 100, "マイクの大きさ (%)");
        _denoise = cfg.Bind("Voice", "Denoise", true, "ノイズキャンセリング (周りの雑音を抑える)");
        foreach (var part in _volumes.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq > 0 && int.TryParse(part[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
                Volumes[part[..eq]] = Math.Clamp(pct, 0, MaxVolume);
        }
    }

    internal static int Changer
    {
        get => _changer != null ? Math.Clamp(_changer.Value, 0, VoiceFx.Count - 1) : 0;
        set
        {
            if (_changer == null || _changer.Value == value) return;
            _changer.Value = value;
            VoiceFx.Reset();
        }
    }

    internal static int MicGainPercent
    {
        get => _micGain != null ? Math.Clamp(_micGain.Value, MinGain, MaxGain) : 100;
        set { if (_micGain != null) _micGain.Value = Math.Clamp(value, MinGain, MaxGain); }
    }

    internal static float MicGain => MicGainPercent / 100f;

    internal static bool Denoise
    {
        get => _denoise == null || _denoise.Value;
        set
        {
            if (_denoise == null || _denoise.Value == value) return;
            _denoise.Value = value;
            VoiceDenoise.Reset();
        }
    }

    // 人を覚える名前: フレンドコード (無い時は表示名)
    private static string Key(PlayerControl p)
    {
        var d = p ? p.Data : null;
        if (d == null) return null;
        return !string.IsNullOrEmpty(d.FriendCode) ? d.FriendCode : "name:" + d.PlayerName;
    }

    internal static int PercentOf(PlayerControl p)
    {
        string k = Key(p);
        return k != null && Volumes.TryGetValue(k, out int v) ? v : 100;
    }

    internal static float VolumeOf(PlayerControl p) => PercentOf(p) / 100f;

    // つまみを動かしている間は覚えるだけ (書き出しは窓を閉じた時)
    internal static void SetPercent(PlayerControl p, int pct)
    {
        string k = Key(p);
        if (k == null) return;
        pct = Math.Clamp(pct, 0, MaxVolume);
        if (pct == 100) Volumes.Remove(k);
        else Volumes[k] = pct;
        _dirty = true;
    }

    internal static void Save()
    {
        if (!_dirty || _volumes == null) return;
        _dirty = false;
        var sb = new StringBuilder();
        foreach (var kv in Volumes) sb.Append(kv.Key.Replace(";", "")).Append('=').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
        _volumes.Value = sb.ToString();
    }
}
