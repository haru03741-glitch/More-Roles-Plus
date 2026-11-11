namespace MoreRolesPlus.Fx;

// 全身を描き替える連番の表 (道具が書き出す。手で書き換えない)。
// Own = 同梱の絵 (格子の 1 升の大きさ・列と行・足元の位置)。Vanilla = 本編が持っている絵を名前で読む (絵の番号・足元合わせのずらし)。
// どちらも: 回り始めるコマ・24 コマ/秒の各コマに出す絵・絵ごとの頭の動き (着せ替えが追う。NaN = その絵では着せ替えを隠す)
internal static partial class CrewFlip
{
    private static readonly Clip[] Clips =
    {
        Clip.Vanilla("burn", "Seeker_Transform", new ushort[] { 1, 48, 49, 50, 51, 53, 55, 72, 74, 76, 137, 138, 162, 163 }, -0.05f, -0.05f, 46,
            new byte[] { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3, 1, 1, 3, 3, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 7, 7, 8, 8, 9, 9, 7, 7, 8, 8, 9, 9, 7, 7, 8, 8, 9, 9, 9, 7, 7, 8, 8, 9, 7, 7, 8, 8, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 11, 10, 12, 13 },
            new float[] { 0.0f, -0.054f, 0.145f, 0.386f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN },
            new float[] { 0.0f, -0.001f, -0.064f, -0.166f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN }),
        Clip.Vanilla("chicken", "Seeker_Transform", new ushort[] { 1, 48, 49, 50, 51, 53, 55, 164, 165, 166, 167, 180, 182 }, -0.05f, -0.05f, 35,
            new byte[] { 0, 0, 0, 1, 1, 2, 2, 3, 3, 7, 8, 9, 10, 9, 10, 9, 10, 9, 10, 9, 10, 9, 10, 9, 10, 11, 11, 12, 12, 4, 4, 5, 5, 6, 6, 5, 5, 5, 6, 6 },
            new float[] { 0.0f, -0.054f, 0.145f, 0.386f, float.NaN, float.NaN, float.NaN, 0.48f, 0.502f, 0.457f, 0.481f, 0.549f, 0.508f },
            new float[] { 0.0f, -0.001f, -0.064f, -0.166f, float.NaN, float.NaN, float.NaN, -0.639f, -0.669f, -0.59f, -0.624f, -0.735f, -0.688f }),
        Clip.Vanilla("clasp", "Seeker_Transform", new ushort[] { 1, 48, 49, 50, 164, 165, 166, 167 }, -0.05f, -0.05f, 12,
            new byte[] { 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 5, 6, 7, 6, 7, 6, 7, 6, 7, 6, 7, 6, 7, 6, 7 },
            new float[] { 0.0f, -0.054f, 0.145f, 0.386f, 0.48f, 0.502f, 0.457f, 0.481f },
            new float[] { 0.0f, -0.001f, -0.064f, -0.166f, -0.639f, -0.669f, -0.59f, -0.624f }),
        Clip.Vanilla("crush", "killNeck_victim", new ushort[] { 1, 18, 31, 32, 34, 35 }, -0.005f, 0.36f, 13,
            new byte[] { 0, 0, 1, 2, 3, 3, 3, 4, 4, 5, 5, 4, 4, 5 },
            new float[] { 0.0f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN },
            new float[] { 0.0f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN }),
        Clip.Vanilla("drown", "killGun_victim", new ushort[] { 1, 8, 9, 10, 11, 12 }, -1.145f, 0.27f, 57,
            new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 5 },
            new float[] { 0.0f, -0.25f, -0.635f, -0.861f, float.NaN, float.NaN },
            new float[] { 0.0f, 0.229f, 0.653f, 0.447f, float.NaN, float.NaN }, mirror: true, offx: new float[] { -1.145f, -0.875f, -0.025f, 0.29f, 1.015f, 1.075f }),
        Clip.Vanilla("seizure", "killNeck_victim", new ushort[] { 1, 10, 18, 20, 21, 27, 29, 31, 32, 34, 35 }, -0.005f, 0.36f, 46,
            new byte[] { 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 5, 5, 6, 6, 5, 5, 6, 6, 5, 5, 6, 6, 5, 5, 6, 6, 7, 7, 7, 8, 8, 9, 9, 10 },
            new float[] { 0.0f, -0.029f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN },
            new float[] { 0.0f, 0.087f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN }),
    };
}
