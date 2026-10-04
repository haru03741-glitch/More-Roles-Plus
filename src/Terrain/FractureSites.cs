using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 割れ目の形: 当たった点 (爆心) から放射状に並べた種点の、いちばん近い種点ごとの細胞 (ボロノイ) に割る。
// 中心ほど種点が密 (細かく砕ける)・外ほど疎 (大きな塊)。輪ごとに同じ放射線の間へ置くので、
// 隣の輪の境が放射線の向きにつながって割れ目が中心から伸びる。
// 種点は損傷マスクの原点からの 1/256 単位へ量子化した値だけを使う。塊の並び (大きさ順) は瓦礫の当たり判定の
// 順位として電文で配るので、生成の後の判定は +−× だけ (端末の CPU が違っても同じ結果)。
// GPU (シェーダの _UseDamage = 4) は同じ種点を _MrpPieceSites (行 = 破壊の番号・列 = 種点) から読む
internal static class FractureSites
{
    public const int Max = 48;          // 1 回の破壊の種点の上限 (シェーダのループ回数と同じ)
    public const int Width = 64, Rows = 256;
    private const float Quant = 256f;   // 量子化の刻み = 1/256 単位 (16 bit で 256 単位まで)
    private const int Spokes = 11;      // 放射線の本数
    private const float R0 = 0.16f;     // いちばん内側の輪の半径
    private const float Growth = 1.65f; // 輪ごとに半径をこの倍に
    private const float Margin = 0.6f;  // 抜いた範囲からこれより遠い種点は捨てる

    private static readonly int SitesTexId = Shader.PropertyToID("_MrpPieceSites");
    private static Texture2D _tex;
    private static byte[] _pixels;

    // 今の破壊の種点 (量子化した世界座標)
    private static readonly float[] Xs = new float[Max], Ys = new float[Max];
    private static readonly ushort[] Qx = new ushort[Max], Qy = new ushort[Max];
    private static int _n;
    internal static int Count => _n;

    // 中心 c・種 seed で種点を作り、破壊の番号 gen の行へ書く。area = 抜いた範囲 (世界座標)
    public static void Build(Vector2 c, int seed, Rect area, byte gen)
    {
        _n = 0;
        Vector2 o = DamageMap.Origin;
        var rnd = new System.Random(seed * 7919 + 101);
        // 中心から抜いた範囲のいちばん遠い角まで
        float fx = Math.Max(Math.Abs(area.xMin - c.x), Math.Abs(area.xMax - c.x)), fy = Math.Max(Math.Abs(area.yMin - c.y), Math.Abs(area.yMax - c.y));
        float reach = MathF.Sqrt(fx * fx + fy * fy);

        // 放射線の向き (等間隔から少しずらす)
        var spoke = new double[Spokes + 1];
        double baseA = rnd.NextDouble() * Math.PI * 2, step = Math.PI * 2 / Spokes;
        for (int i = 0; i < Spokes; i++) spoke[i] = baseA + (i + (rnd.NextDouble() - 0.5) * 0.5) * step;
        spoke[Spokes] = spoke[0] + Math.PI * 2;

        Add(c.x, c.y, area, o, force: true); // 中心は必ず入れる (種点が 0 個にならないように)
        // いちばん内側は 3 つ (放射線に揃えない)
        double a0 = rnd.NextDouble() * Math.PI * 2;
        for (int i = 0; i < 3; i++)
        {
            double a = a0 + i * Math.PI * 2 / 3 + (rnd.NextDouble() - 0.5) * 0.6, r = R0 * (0.8 + rnd.NextDouble() * 0.4);
            Add((float)(c.x + Cos(a) * r), (float)(c.y + Sin(a) * r), area, o);
        }
        for (double r = R0 * Growth; r <= reach + Margin; r *= Growth)
        for (int i = 0; i < Spokes; i++)
        {
            double skip = rnd.NextDouble(), ja = rnd.NextDouble(), jr = rnd.NextDouble();
            // 外の輪は所々抜く (隣の 2 つ分の大きな塊ができる)
            if (r > R0 * Growth * Growth * 1.5 && skip < 0.22) continue;
            double a = (spoke[i] + spoke[i + 1]) * 0.5 + (ja - 0.5) * 0.25 * step, rr = r * (0.86 + jr * 0.28);
            Add((float)(c.x + Cos(a) * rr), (float)(c.y + Sin(a) * rr), area, o);
        }

        Ensure();
        int row = gen * Width * 4;
        for (int i = 0; i < Width; i++)
        {
            ushort x = i < _n ? Qx[i] : ushort.MaxValue, y = i < _n ? Qy[i] : ushort.MaxValue; // 使わない列はマップの外
            int k = row + i * 4;
            _pixels[k] = (byte)(x >> 8); _pixels[k + 1] = (byte)x;
            _pixels[k + 2] = (byte)(y >> 8); _pixels[k + 3] = (byte)y;
        }
        Upload();
    }

    private static void Add(float x, float y, Rect area, Vector2 o, bool force = false)
    {
        if (_n >= Max) return;
        float dx = Math.Max(0f, Math.Max(area.xMin - x, x - area.xMax)), dy = Math.Max(0f, Math.Max(area.yMin - y, y - area.yMax));
        if (!force && dx * dx + dy * dy > Margin * Margin) return;
        int qx = (int)Math.Round((x - o.x) * Quant), qy = (int)Math.Round((y - o.y) * Quant);
        if (qx < 0 || qy < 0 || qx >= ushort.MaxValue || qy >= ushort.MaxValue) return;
        Qx[_n] = (ushort)qx; Qy[_n] = (ushort)qy;
        Xs[_n] = o.x + qx / Quant; Ys[_n] = o.y + qy / Quant;
        _n++;
    }

    // sin / cos を四則演算だけで (Math.Sin は端末の CPU で最後の桁が違うことがあり、量子化の境目で種点がずれる)
    private static double Sin(double a)
    {
        a -= Math.Floor(a / (Math.PI * 2) + 0.5) * (Math.PI * 2); // -π..π
        if (a > Math.PI * 0.5) a = Math.PI - a;
        else if (a < -Math.PI * 0.5) a = -Math.PI - a;          // -π/2..π/2
        double x2 = a * a, t = a, sum = a;
        for (int k = 1; k <= 9; k++)
        {
            t *= -x2 / ((2 * k) * (2 * k + 1));
            sum += t;
        }
        return sum;
    }

    private static double Cos(double a) => Sin(a + Math.PI * 0.5);

    // (x, y) のいちばん近い種点の番号 (同じ距離なら番号の小さい方)
    public static byte KeyAt(float x, float y)
    {
        int best = 0;
        float bd = float.MaxValue;
        for (int i = 0; i < _n; i++)
        {
            float dx = x - Xs[i], dy = y - Ys[i];
            float d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = i; }
        }
        return (byte)best;
    }

    private static void Ensure()
    {
        if (_tex) return;
        _pixels = new byte[Width * Rows * 4];
        Array.Fill(_pixels, (byte)255);
        _tex = new Texture2D(Width, Rows, TextureFormat.RGBA32, false, true)
        {
            name = "MrpPieceSites",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        Shader.SetGlobalTexture(SitesTexId, _tex);
    }

    private static unsafe void Upload()
    {
        fixed (byte* p = _pixels) _tex.LoadRawTextureData((IntPtr)p, _pixels.Length);
        _tex.Apply(false, false);
    }

    // マップが変わった時
    public static void Clear()
    {
        if (_tex) UnityEngine.Object.Destroy(_tex);
        _tex = null;
        _pixels = null;
        _n = 0;
    }
}
