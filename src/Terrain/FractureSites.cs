using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 割れ目の形: 当たった点 (爆心) から放射状に並べた種点の、いちばん近い種点ごとの細胞 (ボロノイ) に割る。
// 中心ほど種点が密 (細かく砕ける)・外ほど疎 (大きな塊)。輪ごとに同じ放射線の間へ置くので、
// 隣の輪の境が放射線の向きにつながって割れ目が中心から伸びる。
// 種点は損傷マスクの原点からの 1/256 単位へ量子化した値だけを使う。塊の並び (大きさ順) は瓦礫の当たり判定の
// 順位として電文で配るので、生成の後の判定は +−× だけ (端末の CPU が違っても同じ結果)。
// GPU (シェーダの _UseDamage = 4・5) は同じ種点を _MrpPieceSites から読む (行 0..255 = 破壊の番号・256..511 = 剥げかけの枠)
internal static class FractureSites
{
    public const int Max = 48;          // 1 回の破壊の種点の上限 (シェーダのループ回数と同じ)
    public const int Width = 64, Rows = 512;
    public const int PeelRow0 = 256; // 剥げかけ (崩れる前のひび) の行の始まり
    private const float Quant = 256f;   // 量子化の刻み = 1/256 単位 (16 bit で 256 単位まで)
    private const int Spokes = 11;      // 放射線の本数
    private const float R0 = 0.16f;     // いちばん内側の輪の半径
    private const float Growth = 1.65f; // 輪ごとに半径をこの倍に
    internal const float Margin = 0.6f; // 抜いた範囲からこれより遠い種点は捨てる
    private const double AngleScale = 65536.0 / (2.0 * Math.PI); // 電文の角度 (ushort) → ラジアン

    private static readonly int SitesTexId = Shader.PropertyToID("_MrpPieceSites");
    private static Texture2D _tex;
    private static byte[] _pixels;

    // 今の破壊の種点 (量子化した世界座標)
    private static readonly float[] Xs = new float[Max], Ys = new float[Max];
    private static readonly ushort[] Qx = new ushort[Max], Qy = new ushort[Max];
    private static int _n, _from;
    private static readonly int[] Starts = new int[9];
    internal static int Count => _n;
    // 重ねた割れ目 i の種点の番号の始まり (終わりは i + 1 の始まり)
    internal static int Start(int i) => Starts[i];
    private const float Gap = 0.08f; // 前の割れ目の種点にこれより近い種点は置かない (同じ所を叩き直すとひびが育つだけ)

    // 中心 c・種 seed で種点を作り、テクスチャの行 row へ書く。area = 抜いた範囲 (世界座標)
    public static void Build(Vector2 c, int seed, Rect area, int row) => Build(new[] { new CrackPattern(c, seed, area) }, row);

    // 割れ目を重ねる: 古い順に並べた割れ目の種点を 1 行にまとめる (後から叩いた所の細胞が前のひびの上に重なる)。
    // 種点の上限は割れ目ごとの Cap (全部で Max まで)
    public static void Build(IReadOnlyList<CrackPattern> patterns, int row)
    {
        _n = 0;
        int count = Math.Min(patterns.Count, Starts.Length - 1);
        for (int i = 0; i < count; i++)
        {
            Starts[i] = _from = _n;
            int cap = Math.Min(Max, _n + patterns[i].Cap);
            Generate(patterns[i], cap);
        }
        Starts[count] = _n;
        Write(row);
    }

    // 種点を作る。向きのある割れ目は、放射状の並びを向き (Axis) に沿って Stretch 倍に伸ばして横を縮め、
    // 向きの先ほど広げて後ろを詰める (Bias)。先ほど大きな塊・後ろほど細かい
    private static void Generate(CrackPattern pt, int cap)
    {
        Vector2 o = DamageMap.Origin;
        Vector2 c = pt.Center;
        Rect area = pt.Area;
        var rnd = new System.Random(pt.Seed * 7919 + 101);
        double ux = Cos(pt.Axis / AngleScale), uy = Sin(pt.Axis / AngleScale);
        double stretch = pt.Stretch, bias = pt.Bias, side = 1.0 / (1.0 + 0.35 * (stretch - 1.0));
        double r0 = R0 * pt.Ring;
        // 中心から抜いた範囲のいちばん遠い角まで (歪めて縮む向きのぶん先まで回す)
        float fx = Math.Max(Math.Abs(area.xMin - c.x), Math.Abs(area.xMax - c.x)), fy = Math.Max(Math.Abs(area.yMin - c.y), Math.Abs(area.yMax - c.y));
        double reach = (MathF.Sqrt(fx * fx + fy * fy) + pt.Margin) / Math.Min(1.0, side * (1.0 - bias));

        void Put(double a, double r, bool force = false)
        {
            double lx = Cos(a) * r, ly = Sin(a) * r;
            double al = lx * ux + ly * uy, sd = -lx * uy + ly * ux;
            double f = r > 0 ? 1.0 + bias * al / r : 1.0;
            al *= stretch * f;
            sd *= side * f;
            Add((float)(c.x + al * ux - sd * uy), (float)(c.y + al * uy + sd * ux), area, o, pt.Margin, cap, force);
        }

        // 放射線の向き (等間隔から少しずらす)
        var spoke = new double[Spokes + 1];
        double baseA = rnd.NextDouble() * Math.PI * 2, step = Math.PI * 2 / Spokes;
        for (int i = 0; i < Spokes; i++) spoke[i] = baseA + (i + (rnd.NextDouble() - 0.5) * 0.5) * step;
        spoke[Spokes] = spoke[0] + Math.PI * 2;

        Put(0, 0, force: true); // 中心は必ず入れる (種点が 0 個にならないように)
        // いちばん内側は 3 つ (放射線に揃えない)
        double a0 = rnd.NextDouble() * Math.PI * 2;
        for (int i = 0; i < 3; i++)
        {
            double a = a0 + i * Math.PI * 2 / 3 + (rnd.NextDouble() - 0.5) * 0.6, r = r0 * (0.8 + rnd.NextDouble() * 0.4);
            Put(a, r);
        }
        for (double r = r0 * Growth; r <= reach; r *= Growth)
        for (int i = 0; i < Spokes; i++)
        {
            double skip = rnd.NextDouble(), ja = rnd.NextDouble(), jr = rnd.NextDouble();
            // 外の輪は所々抜く (隣の 2 つ分の大きな塊ができる)
            if (r > r0 * Growth * Growth * 1.5 && skip < 0.22) continue;
            double a = (spoke[i] + spoke[i + 1]) * 0.5 + (ja - 0.5) * 0.25 * step, rr = r * (0.86 + jr * 0.28);
            Put(a, rr);
        }
    }

    private static void Write(int row)
    {
        Ensure();
        row *= Width * 4;
        for (int i = 0; i < Width; i++)
        {
            ushort x = i < _n ? Qx[i] : ushort.MaxValue, y = i < _n ? Qy[i] : ushort.MaxValue; // 使わない列はマップの外
            int k = row + i * 4;
            _pixels[k] = (byte)(x >> 8); _pixels[k + 1] = (byte)x;
            _pixels[k + 2] = (byte)(y >> 8); _pixels[k + 3] = (byte)y;
        }
        Upload();
    }

    // 電文の角度の向き (自前の sin/cos なので全員で同じ値)
    public static Vector2 Dir(ushort a) => new((float)Cos(a / AngleScale), (float)Sin(a / AngleScale));

    // 打撃の向きと壁の法線 (どちらも電文の角度) から、掠め具合 g (0 = 正面・1 = 壁に沿って掠める) と
    // 壁に沿って振った向き (割れ目を伸ばす向き) を出す。四則演算と自前の sin だけ (全員で同じ値)。g は 1/64 に丸める
    public static float Glance(Vector2 normal, Vector2 swing, out ushort along)
    {
        ushort n = TerrainWire.AngleIndex(normal), d = TerrainWire.AngleIndex(swing);
        ushort inward = (ushort)(n + 32768);
        short diff = (short)(d - inward);
        double s = Sin(diff / AngleScale);
        along = (ushort)(inward + (s >= 0 ? 16384 : -16384));
        double g = Math.Abs(s);
        if (Cos(diff / AngleScale) <= 0) g = 1; // 奥へ進まない振り
        return (float)(Math.Round(g * 64) / 64);
    }

    private static void Add(float x, float y, Rect area, Vector2 o, float margin, int cap, bool force)
    {
        if (_n >= cap) return;
        float dx = Math.Max(0f, Math.Max(area.xMin - x, x - area.xMax)), dy = Math.Max(0f, Math.Max(area.yMin - y, y - area.yMax));
        if (!force && dx * dx + dy * dy > margin * margin) return;
        int qx = (int)Math.Round((x - o.x) * Quant), qy = (int)Math.Round((y - o.y) * Quant);
        if (qx < 0 || qy < 0 || qx >= ushort.MaxValue || qy >= ushort.MaxValue) return;
        float wx = o.x + qx / Quant, wy = o.y + qy / Quant;
        for (int i = 0; i < _from; i++)
        {
            float gx = wx - Xs[i], gy = wy - Ys[i];
            if (gx * gx + gy * gy < Gap * Gap) return;
        }
        Qx[_n] = (ushort)qx; Qy[_n] = (ushort)qy;
        Xs[_n] = wx; Ys[_n] = wy;
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

    internal static float SiteX(int i) => Xs[i];
    internal static float SiteY(int i) => Ys[i];

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

    internal static void Warm() => Ensure();

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
        Bridge.Perf.Upload(_pixels.Length);
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

// 割れ目 1 つ分の種点の置き方。値はすべて量子化済み (電文の位置・角度、伸び・偏りは 1/64) から作る
internal readonly struct CrackPattern
{
    public readonly Vector2 Center;
    public readonly int Seed;
    public readonly Rect Area;      // 割れ目を描く範囲 (ここから Margin より遠い種点は捨てる)
    public readonly float Margin;
    public readonly int Cap;        // この割れ目の種点の上限 (重ねた時の取り分。最後の割れ目は残り全部)
    public readonly ushort Axis;    // 伸ばす向き (電文の角度)
    public readonly float Stretch;  // 向きに沿って何倍に伸ばすか (1 = 丸い)
    public readonly float Bias;     // 向きの先を広げ後ろを詰める割合 (0..0.6)
    public readonly float Ring;     // 内側の輪の大きさの倍率 (小さいほど中心が細かく砕ける)

    public CrackPattern(Vector2 center, int seed, Rect area, float margin = FractureSites.Margin, int cap = FractureSites.Max,
        ushort axis = 0, float stretch = 1f, float bias = 0f, float ring = 1f)
    {
        Center = center; Seed = seed; Area = area; Margin = margin; Cap = cap; Axis = axis;
        Stretch = Q(stretch); Bias = Q(bias); Ring = Q(ring);
    }

    private static float Q(float v) => (float)(Math.Round(v * 64.0) / 64.0);

    public CrackPattern WithArea(Rect area, float margin, int cap) => new(Center, Seed, area, margin, cap, Axis, Stretch, Bias, Ring);
}
