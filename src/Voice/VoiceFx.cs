using System;

namespace MoreRolesPlus.Voice;

// ボイスチェンジャー (自分の声を送る前に変える・24kHz で 1 つずつ)。
// 0 = そのまま / 1 = 高い声 / 2 = 低い声 / 3 = ロボット / 4 = 無線
internal static class VoiceFx
{
    internal const int Count = 5;

    // 高さを変える: 少しずつ長さの変わる遅れを 2 本、半分ずらして重ね、継ぎ目を sin² で消す
    private const int Win = 960;                 // 40ms
    private const int Line = 2048;               // 遅れの入れ物 (2 の累乗)
    private static readonly float[] Delay = new float[Line];
    private static int _w;
    private static float _phase;                 // 0〜Win

    // ロボット: くし形の響き + 低い音で揺らす
    private const int Comb = VoiceChat.Rate / 110;
    private static readonly float[] CombBuf = new float[Comb];
    private static int _combPos;
    private static float _ring;

    // 無線: 低い所と高い所を切って、少し歪ませる
    private static float _hpX, _hpY, _lp1, _lp2;

    internal static void Reset()
    {
        Array.Clear(Delay, 0, Delay.Length);
        Array.Clear(CombBuf, 0, CombBuf.Length);
        _w = _combPos = 0;
        _phase = _ring = 0f;
        _hpX = _hpY = _lp1 = _lp2 = 0f;
    }

    internal static float Apply(float x) => VoiceSettings.Changer switch
    {
        1 => Pitch(x, 1.45f),
        2 => Pitch(x, 0.72f),
        3 => Robot(x),
        4 => Radio(x),
        _ => x,
    };

    private static float Pitch(float x, float ratio)
    {
        Delay[_w] = x;
        // 遅れを毎回 (1 - 倍率) ずつ変える = 読む速さが倍率になる
        _phase += 1f - ratio;
        if (_phase >= Win) _phase -= Win;
        if (_phase < 0f) _phase += Win;
        float d2 = _phase + Win * 0.5f;
        if (d2 >= Win) d2 -= Win;
        float g1 = Fade(_phase), g2 = Fade(d2);
        float y = Read(_phase + 1f) * g1 + Read(d2 + 1f) * g2;
        _w = (_w + 1) & (Line - 1);
        return y;
    }

    private static float Fade(float d)
    {
        float s = MathF.Sin(MathF.PI * d / Win);
        return s * s;
    }

    private static float Read(float delay)
    {
        float pos = _w - delay;
        if (pos < 0f) pos += Line;
        int i = (int)pos;
        float f = pos - i;
        float a = Delay[i & (Line - 1)], b = Delay[(i + 1) & (Line - 1)];
        return a + (b - a) * f;
    }

    private static float Robot(float x)
    {
        float y = x + 0.55f * CombBuf[_combPos];
        CombBuf[_combPos] = y;
        _combPos = (_combPos + 1) % Comb;
        _ring += 2f * MathF.PI * 62f / VoiceChat.Rate;
        if (_ring > 2f * MathF.PI) _ring -= 2f * MathF.PI;
        return y * (0.35f + 0.65f * MathF.Sin(_ring)) * 0.8f;
    }

    private static float Radio(float x)
    {
        // 約 450Hz より下を切る
        float hp = 0.894f * (_hpY + x - _hpX);
        _hpX = x;
        _hpY = hp;
        // 約 2.8kHz より上を切る (2 段)
        _lp1 += 0.42f * (hp - _lp1);
        _lp2 += 0.42f * (_lp1 - _lp2);
        float d = _lp2 * 3.5f;
        return d / (1f + Math.Abs(d)) * 0.7f;
    }
}
