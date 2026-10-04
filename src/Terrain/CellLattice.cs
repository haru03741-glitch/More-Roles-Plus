using System;

namespace MoreRolesPlus.Terrain;

// シェーダの細胞模様 (_Cells = バンドルの cells.png) を CPU で引く。作り方は MrpBundleBuilder.MakeCells と同じ
// (同じ種の乱数・同じ種点・画素の中心で一番近い種点の値)。値は種点ごとに異なる 8 bit (0..195 の順位から作る)ので、
// 値がそのまま細胞の番号になる。壁の絵を割った塊はこの細胞 1 つ分 (世界座標 × PieceScale で引いた時の細胞)
internal static class CellLattice
{
    private const int Size = 512, Sites = 14;

    // 割れた塊 1 つ = この倍率で引いた細胞 1 つ (1 / 0.16 / 14 ≈ 0.45 単位)。シェーダの _PieceScale にもこの値を渡す
    public const float PieceScale = 0.16f;

    private static float[] _sx, _sy;
    private static byte[] _key;

    private static void Ensure()
    {
        if (_key != null) return;
        int n = Sites * Sites;
        var rnd = new Random(7);
        _sx = new float[n]; _sy = new float[n];
        var sv = new float[n];
        for (int j = 0; j < Sites; j++)
        for (int i = 0; i < Sites; i++)
        {
            int k = j * Sites + i;
            _sx[k] = (i + 0.15f + (float)rnd.NextDouble() * 0.7f) / Sites;
            _sy[k] = (j + 0.15f + (float)rnd.NextDouble() * 0.7f) / Sites;
            sv[k] = (float)rnd.NextDouble();
        }
        _key = new byte[n];
        var order = new int[n];
        for (int k = 0; k < n; k++) order[k] = k;
        Array.Sort(order, (a, b) => sv[a] != sv[b] ? sv[a].CompareTo(sv[b]) : a.CompareTo(b));
        for (int r = 0; r < n; r++) _key[order[r]] = ToByte((r + 0.5f) / n);
    }

    // 順位から作る細胞の値 (0..1)。ビルダーも同じ式で PNG に書く
    private static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);

    // 世界座標 (wx, wy) を scale 倍して引いた細胞の値 (0..255)。テクスチャの点サンプリングと同じく画素の中心で決める
    public static byte KeyAt(float wx, float wy, float scale)
    {
        Ensure();
        float u = wx * scale, v = wy * scale;
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        int tx = Math.Min(Size - 1, (int)(u * Size)), ty = Math.Min(Size - 1, (int)(v * Size));
        u = (tx + 0.5f) / Size; v = (ty + 0.5f) / Size;
        int ci = (int)(u * Sites), cj = (int)(v * Sites);
        float best = float.MaxValue;
        byte key = 0;
        for (int dj = -1; dj <= 1; dj++)
        for (int di = -1; di <= 1; di++)
        {
            int ii = (ci + di + Sites) % Sites, jj = (cj + dj + Sites) % Sites;
            int k = jj * Sites + ii;
            float px = _sx[k] + (ci + di - ii) / (float)Sites;
            float py = _sy[k] + (cj + dj - jj) / (float)Sites;
            float d = (u - px) * (u - px) + (v - py) * (v - py);
            if (d < best) { best = d; key = _key[k]; }
        }
        return key;
    }
}
