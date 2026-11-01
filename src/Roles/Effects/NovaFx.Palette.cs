using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 光線の色の組 (ホストの設定で選ぶ)。光線の柱・照準・溜めの玉・閃光・塵・筒・キャノンの光る所はみな同じ 5 段から色を取る:
//   0 = 最も暗い地・1 = 暗いにじみ・2 = 本体・3 = 明るい筋・4 = 芯 (白に近い)
// 溶けた壁の火花・白熱・赤熱・焦げは物の温度の色なので組に関わらず橙のまま
internal static partial class NovaFx
{
    private static readonly float[][] Palettes =
    {
        // 白黒: 光をほぼ返さない黒い柱に、白い筋
        new[] { 0.006f, 0.006f, 0.008f,  0.07f, 0.07f, 0.075f,  0.38f, 0.38f, 0.4f,  0.8f, 0.8f, 0.82f,  1f, 1f, 1f },
        // 黒と紫: 暗い藍紫の柱に、薄紫の筋
        new[] { 0.012f, 0f, 0.06f,  0.11f, 0.06f, 0.3f,  0.33f, 0.26f, 0.95f,  0.74f, 0.6f, 1f,  0.97f, 0.84f, 1f },
    };
    private static readonly string[] GlowArt = { "nova_cannon_glow", "nova_cannon_glow_violet" };
    private static readonly int[] RampIds = new int[15];

    private static int _pal = -1;
    private static float[] P = Palettes[0];
    private static Sprite[][] _glowSets = new Sprite[2][];

    // 段 i の色 (a = 濃さ)
    private static Color Pc(int i, float a) => FxMath.Rgba(P[i * 3], P[i * 3 + 1], P[i * 3 + 2], a);

    // 段 i と段 i+1 の間 (f = 0〜1)
    private static Color Pm(int i, float f, float a)
    {
        int j = i * 3, k = j + 3;
        return FxMath.Rgba(P[j] + (P[k] - P[j]) * f, P[j + 1] + (P[k + 1] - P[j + 1]) * f, P[j + 2] + (P[k + 2] - P[j + 2]) * f, a);
    }

    // 設定が変わった時だけ色の表を差し替え、光線の材質へ 5 段を書く (撃つ時と構える時に呼ぶ)
    private static void UsePalette()
    {
        int idx = System.Math.Clamp((int)Impostor.Nova.BeamColor, 0, Palettes.Length - 1);
        var mat = MrpBundle.NovaBeamMaterial;
        if (idx == _pal && (!_rampPending || !mat)) return;
        if (idx != _pal)
        {
            _pal = idx;
            P = Palettes[idx];
            _glowSets[idx] ??= ButtonIcons.Frames(GlowArt[idx], 32, GlowPpu, 4);
            if (_glowSets[idx] != null && _glowSets[idx].Length > 0) _glow = _glowSets[idx];
            for (int i = 0; i < 4; i++)
            {
                int s = i == 3 ? 4 : i;
                LayerC[i] = Pc(s, LayerA[i]);
            }
            for (int i = 0; i < 3; i++) ConeC[i] = i == 2 ? Pm(2, 0.3f, ConeA[i]) : Pc(i, ConeA[i]);
            _rampPending = true;
        }
        if (!mat) return;
        if (RampIds[0] == 0)
            for (int i = 0; i < 15; i++) RampIds[i] = Shader.PropertyToID("_R" + (i / 3) + "rgb"[i % 3]);
        for (int i = 0; i < 15; i++) mat.SetFloat(RampIds[i], P[i]);
        _rampPending = false;
    }

    private static bool _rampPending;
    private static readonly float[] LayerA = { 0.6f, 0.6f, 0.9f, 1f };
    private static readonly float[] ConeA = { 0.6f, 0.7f, 0.9f };
}
