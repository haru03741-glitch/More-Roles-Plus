using System;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 誰に聞こえるか
internal enum Ears : byte
{
    All,        // 範囲の中の全員
    Self,       // 鳴らした本人だけ
    Impostors,  // 鳴らした本人とインポスター
}

// 場所のある効果音の聞こえ方 (どの役職の音も同じ決まりで鳴らす)。
// 遠いほど小さく (3 マスまでは小さくならない)、左右は音のした側へ寄せる。
// 間に壁がある時と muffle より遠い時はこもった音 (<名前>_m・無ければ同じ音を小さく)。幽霊は壁を抜けるのでこもらない。
// 音を出した人が透明・ベントの中なら本人以外には鳴らさない。死んだ人の音は幽霊にだけ。会議中は鳴らさない。
// 聞く位置は観戦中ならカメラの追う人。電文は送らない (状態を配る電文の受け手で呼ぶ)
internal static class Hearing
{
    internal const float FullDist = 3f;
    private const float PanDist = 10f, PanMax = 0.7f;
    private const float MuffledGain = 0.8f;
    private const float NoMuffledClipGain = 0.6f; // こもった音が無い時は同じ音を小さくするだけ

    // 確かめ用
    private static int _played, _skipped;
    private static string _last = "-";
    internal static string Describe() => $"played={_played} skipped={_skipped} last={_last}";

    // 埋め込みの音 (Resources/Sounds) を先に、無ければバンドルの音
    internal static AudioClip Clip(string name) => PortSound.Get(name) ?? MrpBundle.Clip(name);

    // (x, y) で鳴らす。source = 音を出した人 (場所だけの音なら null)。muffle < 0 は範囲の 6 割より遠いとこもる
    internal static bool At(string clip, float x, float y, float range, float volume, PlayerControl source = null,
        Ears ears = Ears.All, float pitch = 1f, float delay = 0f, float muffle = -1f)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || MeetingHud.Instance || !CanHear(lp, source, ears)) { _skipped++; return false; }
        if (!Gain(lp, x, y, range, muffle < 0f ? range * 0.6f : muffle, ref volume, out float pan, out bool muffled)) { _skipped++; return false; }
        AudioClip c = muffled ? Clip(clip + "_m") : null;
        bool muffledClip = c;
        if (muffled && !muffledClip) volume *= NoMuffledClipGain;
        if (!c) c = Clip(clip);
        if (!c) { _skipped++; return false; }
        BreakNoise.Voice(c, volume, pan, pitch, delay, false);
        _played++;
        _last = muffledClip ? clip + "_m" : clip;
        return true;
    }

    // 人の足元で鳴らす
    internal static bool At(string clip, PlayerControl source, float range, float volume, Ears ears = Ears.All, float pitch = 1f, float delay = 0f)
    {
        if (!source) return false;
        Vector2 p = source.GetTruePosition();
        return At(clip, p.x, p.y, range, volume, source, ears, pitch, delay);
    }

    // 場所の無い、自分の手元だけの音 (自分の能力の手ごたえなど)
    internal static void Self(string clip, float volume, float pitch = 1f)
    {
        if (MeetingHud.Instance) return;
        var c = Clip(clip);
        if (!c) { _skipped++; return; }
        BreakNoise.Voice(c, volume, 0f, pitch, 0f, false);
        _played++;
        _last = clip;
    }

    internal static bool CanHear(PlayerControl lp, PlayerControl source, Ears ears)
    {
        if (source && source.PlayerId == lp.PlayerId) return true;
        if (ears == Ears.Self) return !source; // 本人の端末でだけ呼ぶ場所の音
        if (ears == Ears.Impostors && !PortKit.IsImpostor(lp)) return false;
        if (!source) return true;
        if (!source.Visible) return false;
        bool deadSrc = source.Data != null && source.Data.IsDead;
        bool deadMe = lp.Data != null && lp.Data.IsDead;
        return !deadSrc || deadMe;
    }

    // 距離で小さく・左右に振る・こもるか。遠すぎるなら false
    internal static bool Gain(PlayerControl lp, float x, float y, float range, float muffle, ref float vol, out float pan, out bool muffled)
    {
        pan = 0f;
        muffled = false;
        Vector2 me = Watch.Ear(lp);
        float dx = x - me.x, dy = y - me.y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d >= range) return false;
        float near = d <= FullDist ? 1f : 1f - (d - FullDist) / (range - FullDist);
        vol *= MathF.Pow(near, 1.5f);
        pan = Math.Clamp(dx / PanDist, -PanMax, PanMax);
        bool ghost = lp.Data != null && lp.Data.IsDead;
        muffled = d > muffle || (!ghost && BreakNoise.WallBetween(me, FxMath.V2(x, y)));
        if (muffled) vol *= MuffledGain;
        return true;
    }
}
