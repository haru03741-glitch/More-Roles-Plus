using System;
using System.Threading.Tasks;

namespace MoreRolesPlus.Roles;

// ポータルの床の装置の絵: 斜め上から見た厚みのある紫の台座と、左右と手前に張り出した丸い足 3 つ。奥にレンズのくぼみ・手前に立つ所の円。
// 本編の家具と同じく部品ごとに太い黒の縁取りを付け、上面・側面の明暗・継ぎ目・縁の照りを 1 枚の RGBA に焼く (光は左上から)。
// 計算は C# の数値だけなので作業スレッドで作れる。役職が付いた時に作り始め、初めて置く時に受け取る
internal static class PortalMachineArt
{
    internal const float Thick = 0.18f;       // 台座の厚み (上面は床の点より画面でこれだけ上)
    private const float PodTop = 0.22f;       // 足のいちばん高い所 (根元の真ん中・台座より少し高く盛り上がる)
    internal const float PadR = 0.62f;        // 台座の半径 (床の上の長さ)
    internal const float Squash = 0.58f;      // 床の上の奥行きは画面でこの倍に縮む
    internal const float WellZ = 0.2f;        // レンズのくぼみの中心 (台座の真ん中から奥へ・床の上の長さ)
    private const float WellR = 0.24f;
    private const float PlateZ = -0.24f, PlateR = 0.22f; // 手前の立つ所の円
    private const float RingR = 0.5f;         // 上面に彫った輪
    // 足: 台座の中心からの向き (床の上)・根元と先の距離・根元と先の半分の幅。台座の外へ出た所だけを描く
    private const float FinFrom = 0.5f, FinTo = 0.95f, FinHalf0 = 0.2f, FinHalf1 = 0.13f;
    private static readonly float[] FinX = { -0.995f, 0.995f, 0f }, FinZ = { 0.1f, 0.1f, -1f };

    // 絵の範囲 (台座の真ん中の床の点が原点)
    internal const float Left = -1.04f, Right = 1.04f, Bottom = -0.66f, Top = 0.62f;
    internal const int Ppu = 200;
    internal static int TexW => (int)((Right - Left) * Ppu);
    internal static int TexH => (int)((Top - Bottom) * Ppu);

    private const int Super = 2;              // 縁をなめらかにするため 2x2 で作って平均する
    private const float Outline = 0.045f;     // 外側の縁取りの太さ (部品どうしの境は半分ずつ)
    private const float OutR = 0.07f, OutG = 0.04f, OutB = 0.12f;

    private static Task<byte[]> _bake;

    internal static void Prewarm() => _bake ??= Task.Run(Bake);

    // 焼き終わっていなければ待つ。失敗したら次に呼ばれた時に焼き直す
    internal static byte[] Pixels()
    {
        Prewarm();
        try { return _bake.Result; }
        catch { _bake = null; throw; }
    }

    private enum Kind : byte { None, PadTop, FinTop, PadSide, FinSide }

    // 画面の点 (x, y) に見える面。部品ごとに上面から下へ高さの分だけずらして最初に形に入った所を探し、
    // いちばん手前 (床の上の奥行きが小さい) 部品が見える。id = 0 なし・1 台座・2〜 足
    private static Kind Hit(float x, float y, out float gx, out float gz, out float s, out int fin, out int id)
    {
        Kind best = Kind.None;
        gx = gz = s = 0f; fin = -1; id = 0;
        float bestZ = float.MaxValue;
        // 台座 (円柱): 上面か、手前の側面か
        {
            float tz = (y - Thick) / Squash;
            if (x * x + tz * tz <= PadR * PadR) { best = Kind.PadTop; bestZ = tz; gx = x; gz = tz; s = 0f; id = 1; }
            else if (MathF.Abs(x) < PadR)
            {
                float fz = -MathF.Sqrt(PadR * PadR - x * x);
                float hgt = y - fz * Squash;
                if (hgt >= 0f && hgt <= Thick) { best = Kind.PadSide; bestZ = fz; gx = x; gz = fz; s = Thick - hgt; id = 1; }
            }
        }
        // 足 (丸く盛り上がった塊): 高い所から下へ探し、盛り上がりの面に当たれば上面・それより下なら側面
        const int Steps = 40;
        const float Dh = PodTop / Steps;
        for (int i = 0; i < FinX.Length; i++)
        {
            bool above = false; // 1 つ上の刻みが形の中で面より上だった (= 面を上から通り抜けた)
            for (int k = 0; k <= Steps; k++)
            {
                float hh = PodTop - Dh * k;
                float qz = (y - hh) / Squash;
                if (qz >= bestZ) break;
                if (x * x + qz * qz < PadR * PadR || !InFin(i, x, qz, out float q, out float t)) { above = false; continue; }
                float top = PodHeight(q, t);
                if (hh > top) { above = true; continue; }
                best = above || k == 0 ? Kind.FinTop : Kind.FinSide;
                bestZ = qz; gx = x; gz = qz; s = Thick * (1f - hh / top); fin = i; id = 2 + i;
                break;
            }
        }
        return best;
    }

    // 足の形の中か (q = 真ん中の線からの横の割合 0〜1・先の丸みも含む / t = 根元 0 から先 1)
    private static bool InFin(int i, float gx, float gz, out float q, out float t)
    {
        float a = gx * FinX[i] + gz * FinZ[i], p = MathF.Abs(-gx * FinZ[i] + gz * FinX[i]);
        q = t = 0f;
        if (a < FinFrom - 0.05f || a > FinTo) return false;
        t = Math.Clamp((a - FinFrom) / (FinTo - FinFrom), 0f, 1f);
        float half = FinHalf0 + (FinHalf1 - FinHalf0) * t;
        float tipC = FinTo - half;
        float d = a > tipC ? MathF.Sqrt((a - tipC) * (a - tipC) + p * p) : p;
        q = d / half;
        return q <= 1f;
    }

    // 足の上面の高さ: 真ん中が高い丸い背・先へ向かって低い
    private static float PodHeight(float q, float t) => PodTop * (0.55f + 0.45f * MathF.Sqrt(MathF.Max(0f, 1f - q * q))) * (1f - 0.3f * t);

    private static byte[] Bake()
    {
        int w = TexW, h = TexH, sw = w * Super, sh = h * Super;
        float step = 1f / (Ppu * Super);
        var kind = new Kind[sw * sh];
        var gxs = new float[sw * sh];
        var gzs = new float[sw * sh];
        var ss = new float[sw * sh];
        var fins = new sbyte[sw * sh];
        var ids = new byte[sw * sh];
        for (int j = 0; j < sh; j++)
        {
            float y = Bottom + (j + 0.5f) * step;
            for (int i = 0; i < sw; i++)
            {
                float x = Left + (i + 0.5f) * step;
                int c = j * sw + i;
                kind[c] = Hit(x, y, out gxs[c], out gzs[c], out ss[c], out int fin, out int id);
                fins[c] = (sbyte)fin;
                ids[c] = (byte)id;
            }
        }

        // 外 (何もない所) までの距離と、別の部品までの距離 (升の数・2 回なめる近似)。縁取りに使う
        var distOut = Chamfer(ids, sw, sh, false);
        var distAny = Chamfer(ids, sw, sh, true);

        float outlineCells = Outline * Ppu * Super, innerCells = outlineCells * 0.5f;
        var sr = new float[sw * sh];
        var sg = new float[sw * sh];
        var sb = new float[sw * sh];
        var sa = new float[sw * sh];
        for (int j = 0; j < sh; j++)
            for (int i = 0; i < sw; i++)
            {
                int c = j * sw + i;
                var k = kind[c];
                if (k == Kind.None) continue;
                Shade(k, gxs[c], gzs[c], ss[c], fins[c], out float r, out float g, out float b);
                if (k == Kind.PadTop)
                {
                    // 上面と側面の境: すぐ下は暗い継ぎ目、その上は縁の照り
                    int below = 0;
                    for (int q = 1; q <= 11 && j - q >= 0; q++)
                    {
                        var kb = kind[c - q * sw];
                        if (ids[c - q * sw] != ids[c]) break;
                        if (kb == Kind.PadSide || kb == Kind.FinSide) { below = q; break; }
                    }
                    if (below > 0 && below <= 4) Mix(ref r, ref g, ref b, OutR, OutG, OutB, 1f);
                    else if (below > 0 && below <= 10) Mix(ref r, ref g, ref b, 1f, 0.97f, 1f, 0.6f * (1f - (below - 5) / 6f));
                }
                float o = MathF.Max(Math.Clamp(outlineCells - distOut[c] + 0.5f, 0f, 1f), Math.Clamp(innerCells - distAny[c] + 0.5f, 0f, 1f));
                if (o > 0f) Mix(ref r, ref g, ref b, OutR, OutG, OutB, o);
                sr[c] = r; sg[c] = g; sb[c] = b; sa[c] = 1f;
            }

        // 2x2 を平均して 1 画素に (色は不透明な升だけで平均・透明度は升の割合)
        var px = new byte[w * h * 4];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f;
                for (int v = 0; v < Super; v++)
                    for (int u = 0; u < Super; u++)
                    {
                        int c = (j * Super + v) * sw + i * Super + u;
                        r += sr[c] * sa[c]; g += sg[c] * sa[c]; b += sb[c] * sa[c]; a += sa[c];
                    }
                int o = (j * w + i) * 4;
                if (a <= 0f) continue;
                px[o] = ToByte(r / a); px[o + 1] = ToByte(g / a); px[o + 2] = ToByte(b / a);
                px[o + 3] = ToByte(a / (Super * Super));
            }
        return px;
    }

    // 各升から、何もない升 (other = true なら別の部品の升も) までのおおよその距離
    private static float[] Chamfer(byte[] ids, int sw, int sh, bool other)
    {
        const float Far = 1e6f, D1 = 1f, D2 = 1.4142f;
        var d = new float[ids.Length];
        for (int j = 0; j < sh; j++)
            for (int i = 0; i < sw; i++)
            {
                int c = j * sw + i;
                if (ids[c] == 0) { d[c] = 0f; continue; }
                if (i == 0 || j == 0 || i == sw - 1 || j == sh - 1) { d[c] = 0.5f; continue; }
                float v = Far;
                // 別の部品と接する升は 0.5 (境の真ん中)
                if (other && (Diff(ids, c, c - 1) || Diff(ids, c, c + 1) || Diff(ids, c, c - sw) || Diff(ids, c, c + sw))) v = 0.5f;
                v = MathF.Min(v, MathF.Min(MathF.Min(d[c - 1] + D1, d[c - sw] + D1), MathF.Min(d[c - sw - 1] + D2, d[c - sw + 1] + D2)));
                d[c] = v;
            }
        for (int j = sh - 2; j >= 1; j--)
            for (int i = sw - 2; i >= 1; i--)
            {
                int c = j * sw + i;
                if (d[c] == 0f) continue;
                d[c] = MathF.Min(d[c], MathF.Min(MathF.Min(d[c + 1] + D1, d[c + sw] + D1), MathF.Min(d[c + sw + 1] + D2, d[c + sw - 1] + D2)));
            }
        return d;
    }

    private static bool Diff(byte[] ids, int a, int b) => ids[b] != 0 && ids[b] != ids[a];

    private static void Shade(Kind k, float gx, float gz, float s, int fin, out float r, out float g, out float b)
    {
        float rad = MathF.Sqrt(gx * gx + gz * gz);
        switch (k)
        {
            case Kind.PadTop:
            {
                // 少し盛り上がった上面: 縁へ向かって暗く、左奥が明るい
                float f = 1.05f - 0.14f * (rad / PadR) * (rad / PadR) - 0.07f * gx / PadR + 0.04f * gz / PadR;
                r = 0.8f * f; g = 0.7f * f; b = 0.98f * f;
                // 彫った輪 (暗い線と、その内側の手前に照り)
                float dr = rad - RingR;
                if (MathF.Abs(dr) < 0.008f) Scale(ref r, ref g, ref b, 0.7f);
                else if (dr < 0f && dr > -0.024f && gz < 0f) Scale(ref r, ref g, ref b, 1.1f);
                // 手前の立つ所: 少し明るい円と溝
                float px = gx, pz = gz - PlateZ, pr = MathF.Sqrt(px * px + pz * pz);
                if (pr < PlateR - 0.012f) Scale(ref r, ref g, ref b, 1.07f - 0.1f * pz / PlateR);
                else if (pr < PlateR + 0.006f) Scale(ref r, ref g, ref b, 0.68f);
                // 奥のレンズのくぼみ: 暗い穴 (奥ほど暗い)・手前の縁は照り・奥の縁は影
                float wz = gz - WellZ, wr = MathF.Sqrt(gx * gx + wz * wz);
                if (wr < WellR)
                {
                    float back = Math.Clamp(0.5f + 0.5f * wz / WellR, 0f, 1f);
                    float d = 1f - 0.45f * back;
                    r = 0.12f * d; g = 0.08f * d; b = 0.26f * d;
                }
                else if (wr < WellR + 0.025f)
                {
                    if (wz < 0f) Mix(ref r, ref g, ref b, 1f, 0.97f, 1f, 0.6f * (-wz / (WellR + 0.025f)));
                    else Scale(ref r, ref g, ref b, 0.7f);
                }
                return;
            }
            case Kind.FinTop:
            {
                InFin(fin, gx, gz, out float q, out float t);
                // 丸く盛り上がった上面: 肩の向き (横の軸) が光 (左奥) を向くほど明るい・先へ向かって暗い。真ん中に照り
                float sp = -gx * FinZ[fin] + gz * FinX[fin];
                float sq = sp < 0f ? -q : q;
                float lit = sq * (-FinZ[fin] * -0.6f + FinX[fin] * 0.5f);
                float f = (1.04f - 0.22f * q * q + 0.3f * lit) * (1.02f - 0.18f * t) - 0.05f * gx / FinTo;
                r = 0.74f * f; g = 0.62f * f; b = 0.95f * f;
                if (q < 0.35f && t > 0.15f && t < 0.75f) Mix(ref r, ref g, ref b, 1f, 0.97f, 1f, 0.35f * (1f - q / 0.35f));
                return;
            }
            case Kind.PadSide:
            {
                // 円柱の側面: 左が明るく右が暗い・下ほど暗い・24° ごとの継ぎ目・手前の板に小さな灯り
                float nx = rad > 1e-4f ? gx / rad : 0f;
                float v = s / Thick;
                float f = (0.98f - 0.22f * nx) * (1.08f - 0.4f * v);
                r = 0.5f * f; g = 0.33f * f; b = 0.8f * f;
                float ang = MathF.Atan2(gz, gx) * 180f / MathF.PI;
                const float Panel = 24f;
                float u = ang / Panel - MathF.Floor(ang / Panel);
                float arc = PadR * Panel * MathF.PI / 180f;
                float edge = MathF.Min(u, 1f - u) * arc;
                if (edge < 0.008f) Scale(ref r, ref g, ref b, 0.62f);
                else if (gz < -0.2f && v > 0.32f && v < 0.62f && MathF.Abs(u - 0.5f) * arc < 0.028f && ((int)MathF.Floor(ang / Panel) & 1) == 0)
                {
                    r = 0.4f; g = 1f; b = 0.92f;
                }
                // 下の縁は床に接して濃い
                if (v > 0.85f) Scale(ref r, ref g, ref b, 0.8f);
                return;
            }
            default:
            {
                float along = gx * FinX[fin] + gz * FinZ[fin];
                float t = Math.Clamp((along - FinFrom) / (FinTo - FinFrom), 0f, 1f);
                float v = s / Thick;
                float f = (0.92f - 0.12f * gx / FinTo - 0.1f * t) * (1.05f - 0.38f * v);
                r = 0.42f * f; g = 0.27f * f; b = 0.7f * f;
                return;
            }
        }
    }

    private static void Scale(ref float r, ref float g, ref float b, float f) { r *= f; g *= f; b *= f; }

    private static void Mix(ref float r, ref float g, ref float b, float tr, float tg, float tb, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        r += (tr - r) * t; g += (tg - g) * t; b += (tb - b) * t;
    }

    private static byte ToByte(float v) => (byte)(255f * Math.Clamp(v, 0f, 1f) + 0.5f);
}
