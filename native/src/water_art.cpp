#include "mrp_native.h"
#include <math.h>

// 水の絵のタイルの画素 (src/Terrain/WaterArt.cs の Draw の画素ループ / SpreadFace と同じ結果)。
// 小数の式は C# と同じ順に float で計算する (縮約 (FMA) を切ってビルドする)

// 3 次の B スプラインの重み (WaterArt.Weights)
static inline void weights(float t, float& w0, float& w1, float& w2, float& w3)
{
    float t2 = t * t, t3 = t2 * t, s = 1.0f - t;
    const float sixth = 1.0f / 6.0f;
    w0 = s * s * s * sixth;
    w1 = (3.0f * t3 - 6.0f * t2 + 4.0f) * sixth;
    w2 = (-3.0f * t3 + 3.0f * t2 + 3.0f * t + 1.0f) * sixth;
    w3 = t3 * sixth;
}

static inline float row4(const int32_t* src, int i, float a0, float a1, float a2, float a3)
{
    return a0 * (float)src[i] + a1 * (float)src[i + 1] + a2 * (float)src[i + 2] + a3 * (float)src[i + 3];
}

// タイルの中の位置 (cu, cv: 升の単位) の高さ (WaterArt.Sample)。src は dw×dw (縁 2 升ぶん広げた升の値)
static inline float sample(const int32_t* src, int dw, float cu, float cv)
{
    float u = cu - 0.5f + 2.0f, v = cv - 0.5f + 2.0f;
    int u0 = (int)floorf(u), v0 = (int)floorf(v);
    if (u0 < 1 || v0 < 1 || u0 + 2 >= dw || v0 + 2 >= dw) return 0.0f;
    float fu = u - (float)u0, fv = v - (float)v0;
    float a0, a1, a2, a3, b0, b1, b2, b3;
    weights(fu, a0, a1, a2, a3);
    weights(fv, b0, b1, b2, b3);
    int r = (v0 - 1) * dw + u0 - 1;
    return b0 * row4(src, r, a0, a1, a2, a3) + b1 * row4(src, r + dw, a0, a1, a2, a3)
         + b2 * row4(src, r + dw * 2, a0, a1, a2, a3) + b3 * row4(src, r + dw * 3, a0, a1, a2, a3);
}

static inline bool open_at(const uint8_t* open, int k, int count) { return k >= 0 && k < count && open[k] != 0; }

// 壁の際の溝を戻した「水を見せてよい」画素か (WaterArt.NearOpen と同じ幅: 左右 2・上 1・下 2)
static inline bool near_open(const uint8_t* open, int sx, int sy, int sw, int sh)
{
    int count = sw * sh, k = sy * sw + sx;
    if (open_at(open, k, count)) return true;
    if (sx >= 2 && sx < sw - 2)
        for (int d = 1; d <= 2; d++)
            if (open_at(open, k + d, count) || open_at(open, k - d, count)) return true;
    if (sy >= 1 && open_at(open, k - sw, count)) return true;
    if (sy < sh - 2)
        for (int d = 1; d <= 2; d++)
            if (open_at(open, k + d * sw, count)) return true;
    return false;
}

static inline bool outside_cell(const uint8_t* out, int x, int y, int w, int h)
{
    return x < 0 || y < 0 || x >= w || y >= h || out[y * w + x] == 1;
}

// n×n 画素の RGBA (R = 深さ・G = 見せてよいか・B = 濡れ・A = 0) を px へ。
// depth / wet = dw×dw の升の値、タイルの左下の升 = (x0, y0)。floorOn なら壁際の判定 (near_open と furn) を飛ばす。
// B スプラインの横の和 (row4) と位置の変換は列か行だけで決まるので先に表にする (式と順序は C# と同じなので結果も同じ)
static const int MaxPx = 256, MaxDw = 64;

MRP_API void mrp_water_pixels(
    const int32_t* depth, const int32_t* wet, int32_t dw, int32_t n, int32_t x0, int32_t y0,
    float orgx, float orgy, float cell, float edge,
    const uint8_t* open, int32_t sw, int32_t sh, float sox, float soy, float sppu,
    int32_t floorOn, const uint8_t* furn, const uint8_t* outside, int32_t reach,
    float deepScale, float wetInv, uint8_t* px)
{
    if (n <= 0 || n > MaxPx || dw > MaxDw) return;
    // 列: 左の升・重み・世界の x・歩ける所の地図の列
    int cu0[MaxPx], csx[MaxPx], cbx[MaxPx];
    float ca[MaxPx][4];
    float cwx[MaxPx];
    for (int p = 0; p < n; p++)
    {
        float u = (float)p * edge;
        float uu = u - 0.5f + 2.0f;
        int u0 = (int)floorf(uu);
        cu0[p] = u0 < 1 || u0 + 2 >= dw ? -1 : u0;
        weights(uu - (float)u0, ca[p][0], ca[p][1], ca[p][2], ca[p][3]);
        float wx = orgx + ((float)x0 + u) * cell;
        cwx[p] = wx;
        csx[p] = (int)((wx - sox) * sppu);
        cbx[p] = (int)floorf((wx - sox) * sppu);
    }
    // 升の行ごとの横の和 (深さと濡れ)
    static thread_local float rd[MaxDw][MaxPx], rw[MaxDw][MaxPx];
    for (int r = 0; r < dw; r++)
        for (int p = 0; p < n; p++)
        {
            int u0 = cu0[p];
            if (u0 < 0) continue;
            int k = r * dw + u0 - 1;
            rd[r][p] = row4(depth, k, ca[p][0], ca[p][1], ca[p][2], ca[p][3]);
            rw[r][p] = row4(wet, k, ca[p][0], ca[p][1], ca[p][2], ca[p][3]);
        }
    for (int py = 0; py < n; py++)
    {
        float v = (float)py * edge;
        float vv = v - 0.5f + 2.0f;
        int v0 = (int)floorf(vv);
        bool rowIn = !(v0 < 1 || v0 + 2 >= dw);
        float b0, b1, b2, b3;
        weights(vv - (float)v0, b0, b1, b2, b3);
        float wy = orgy + ((float)y0 + v) * cell;
        int sy = (int)((wy - soy) * sppu);
        int by = (int)floorf((wy - soy) * sppu);
        for (int p = 0; p < n; p++)
        {
            int i = (py * n + p) * 4;
            float d = 0.0f, w = 0.0f;
            if (rowIn && cu0[p] >= 0)
            {
                d = b0 * rd[v0 - 1][p] + b1 * rd[v0][p] + b2 * rd[v0 + 1][p] + b3 * rd[v0 + 2][p];
                w = b0 * rw[v0 - 1][p] + b1 * rw[v0][p] + b2 * rw[v0 + 1][p] + b3 * rw[v0 + 2][p];
            }
            w = w * wetInv;
            bool vis = floorOn || near_open(open, csx[p], sy, sw, sh) && (!furn || !furn[py * n + p]);
            if (vis && outside)
            {
                int bx = cbx[p];
                if (bx < 0 || by < 0 || bx >= sw || by >= sh) vis = false;
                else
                {
                    uint8_t o = outside[by * sw + bx];
                    if (o == 1 || (o == 2 && (outside_cell(outside, bx - reach, by, sw, sh) || outside_cell(outside, bx + reach, by, sw, sh) ||
                                              outside_cell(outside, bx, by - reach, sw, sh) || outside_cell(outside, bx, by + reach, sw, sh))))
                        vis = false;
                }
            }
            float r = d * deepScale;
            float b = w * 255.0f + 0.5f;
            px[i] = (uint8_t)(int)(r < 255.0f ? r : 255.0f);
            px[i + 1] = vis ? 255 : 0;
            px[i + 2] = (uint8_t)(int)(b < 255.0f ? b : 255.0f);
            px[i + 3] = 0;
        }
    }
}

// A を面の外へ spread 画素にじませる (WaterArt.SpreadFace)。tmp は n×n の作業場所
MRP_API void mrp_water_spread_face(uint8_t* px, const uint8_t* face, int32_t n, int32_t spread, uint8_t* tmp)
{
    // A が全部 0 の行は横に広げても 0 なので飛ばす。縦は上下 spread 行の tmp が全部 0 なら何も変わらない
    uint8_t rowAny[256];
    if (n > 256) return;
    for (int y = 0; y < n; y++)
    {
        int row = y * n;
        uint8_t any = 0;
        for (int x = 0; x < n; x++) any |= px[(row + x) * 4 + 3];
        if (!any)
        {
            for (int x = 0; x < n; x++) tmp[row + x] = 0;
            rowAny[y] = 0;
            continue;
        }
        rowAny[y] = 1;
        for (int x = 0; x < n; x++)
        {
            uint8_t a = px[(row + x) * 4 + 3];
            if (a == 0)
            {
                int lo = x - spread < 0 ? 0 : x - spread, hi = x + spread > n - 1 ? n - 1 : x + spread;
                for (int k = lo; k <= hi; k++) { uint8_t b = px[(row + k) * 4 + 3]; if (b > a) a = b; }
            }
            tmp[row + x] = a;
        }
    }
    for (int y = 0; y < n; y++)
    {
        int lo = y - spread < 0 ? 0 : y - spread, hi = y + spread > n - 1 ? n - 1 : y + spread;
        bool near = false;
        for (int k = lo; k <= hi; k++) if (rowAny[k]) { near = true; break; }
        if (!near) continue;
        int row = y * n;
        for (int x = 0; x < n; x++)
        {
            int j = row + x, i = j * 4;
            if (px[i + 3] != 0) continue;
            uint8_t a = tmp[j];
            for (int k = lo; k <= hi; k++) { uint8_t b = tmp[k * n + x]; if (b > a) a = b; }
            if (a == 0) continue;
            px[i + 3] = a;
            if (face[j] == 255) px[i + 1] = 0;
        }
    }
}
