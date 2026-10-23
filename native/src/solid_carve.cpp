#include "mrp_native.h"
#include "digest.h"
#include "cut_shape.h"

// 壊した所を歩ける所の地図に塗り足す (src/Terrain/SolidMap.cs の Carve の、壁の線を描いた後の部分と同じ結果)。
// 形の中・太らせた壁の外で、もう歩ける升と基準点から塗り、太らせた分を 2 段戻して地図と島の番号へ書く。
// 塗る順番 (種は升の番号順 → 基準点・左右下上) で島の番号の引き継ぎが決まるので C# と同じ順に回す

struct CarveArgs
{
    uint8_t* open; uint8_t* island; const uint8_t* outside;   // 地図全体 (island・outside は無ければ null)
    int32_t w, h; float ox, oy, ppu; int32_t reach;
    int32_t x0, y0, bw, bh;
    int32_t shapeKind; const float* shape; int32_t shapeN;
    const float* keep; int32_t keepN;                          // xMin yMin xMax yMax (無ければ 0 個)
    int32_t refK; int32_t refLab;                              // 基準点の升 (範囲の外なら -1) と島の番号
    const uint8_t* barrier;                                    // 切った後の壁の線 (bw × bh)
    uint8_t* scratch;                                          // bw × bh × 5 (blocked・inside・seen・lab・作業)
    int32_t* queue;                                            // bw × bh
    int32_t opened;                                            // 戻り: 書いた升の数
};

static void dilate1(const uint8_t* src, uint8_t* tmp, uint8_t* dst, int w, int h)
{
    const int r = 1;
    for (int i = 0; i < w * h; i++) { tmp[i] = 0; dst[i] = 0; }
    for (int y = 0; y < h; y++)
    {
        int row = y * w, last = -1000000;
        for (int x = 0; x < w; x++) { if (src[row + x] != 0) last = x; if (x - last <= r) tmp[row + x] = 1; }
        last = 1000000;
        for (int x = w - 1; x >= 0; x--) { if (src[row + x] != 0) last = x; if (last - x <= r) tmp[row + x] = 1; }
    }
    for (int x = 0; x < w; x++)
    {
        int last = -1000000;
        for (int y = 0; y < h; y++) { if (tmp[y * w + x] != 0) last = y; if (y - last <= r) dst[y * w + x] = 1; }
        last = 1000000;
        for (int y = h - 1; y >= 0; y--) { if (tmp[y * w + x] != 0) last = y; if (last - y <= r) dst[y * w + x] = 1; }
    }
}

static inline bool outside_cell(const CarveArgs* a, int x, int y)
{
    return x < 0 || y < 0 || x >= a->w || y >= a->h || a->outside[y * a->w + x] == 1;
}

static bool bare_sky_cell(const CarveArgs* a, int x, int y)
{
    if (x < 0 || y < 0 || x >= a->w || y >= a->h) return true;
    uint8_t v = a->outside[y * a->w + x];
    int r = a->reach;
    return v == 1 || (v == 2 && (outside_cell(a, x - r, y) || outside_cell(a, x + r, y) ||
                                 outside_cell(a, x, y - r) || outside_cell(a, x, y + r)));
}

MRP_API void mrp_solid_carve(CarveArgs* a)
{
    const int bw = a->bw, bh = a->bh, bn = bw * bh, x0 = a->x0, y0 = a->y0, W = a->w;
    const uint8_t* barrier = a->barrier;
    uint8_t* blocked = a->scratch;
    uint8_t* inside = blocked + bn;
    uint8_t* seen = inside + bn;
    uint8_t* lab = seen + bn;
    uint8_t* tmp = lab + bn;
    int32_t* queue = a->queue;
    a->opened = 0;

    dilate1(barrier, tmp, blocked, bw, bh);

    for (int y = 0; y < bh; y++)
    for (int x = 0; x < bw; x++)
    {
        int k = y * bw + x;
        inside[k] = 0; seen[k] = 0; lab[k] = 0;
        float wx = a->ox + ((float)(x0 + x) + 0.5f) / a->ppu, wy = a->oy + ((float)(y0 + y) + 0.5f) / a->ppu;
        if (cut_shape_sd(a->shapeKind, a->shape, a->shapeN, wx, wy) >= 0.0f) continue;
        if (a->keepN > 0 && cut_inside_any(a->keep, a->keepN, wx, wy)) continue;
        if (a->outside && bare_sky_cell(a, x0 + x, y0 + y)) continue;
        inside[k] = 1;
    }

    int tail = 0;
    for (int k = 0; k < bn; k++)
    {
        if (inside[k] == 0 || blocked[k] != 0) continue;
        int g = (y0 + k / bw) * W + x0 + k % bw;
        if (a->open[g] == 0) continue;
        seen[k] = 1; queue[tail++] = k;
        lab[k] = a->island ? a->island[g] : 0;
    }
    if (a->refK >= 0)
    {
        int k = a->refK;
        if (seen[k] == 0 && blocked[k] == 0) { seen[k] = 1; queue[tail++] = k; lab[k] = (uint8_t)a->refLab; }
    }
    int head = 0;
    while (head < tail)
    {
        int k = queue[head++];
        int x = k % bw, y = k / bw;
        int nb[4] = { x > 0 ? k - 1 : -1, x < bw - 1 ? k + 1 : -1, y > 0 ? k - bw : -1, y < bh - 1 ? k + bw : -1 };
        for (int i = 0; i < 4; i++)
        {
            int k2 = nb[i];
            if (k2 < 0 || seen[k2] != 0 || inside[k2] == 0 || blocked[k2] != 0) continue;
            seen[k2] = 1;
            lab[k2] = lab[k];
            queue[tail++] = k2;
        }
    }
    // 太らせた分を戻す (形の中の、壁の線そのものでない升)。角の升は斜めにしか届かないので 2 段
    int grown = tail;
    for (int step = 0; step < 2; step++)
    {
        int from = grown;
        for (int k = 0; k < bn; k++)
        {
            if (seen[k] != 0 || inside[k] == 0 || barrier[k] != 0 || blocked[k] == 0) continue;
            int x = k % bw, y = k / bw;
            int from2 = x > 0 && seen[k - 1] == 1 ? k - 1 : x < bw - 1 && seen[k + 1] == 1 ? k + 1 :
                        y > 0 && seen[k - bw] == 1 ? k - bw : y < bh - 1 && seen[k + bw] == 1 ? k + bw : -1;
            if (from2 < 0) continue;
            lab[k] = lab[from2];
            queue[grown++] = k;
        }
        for (int i = from; i < grown; i++) seen[queue[i]] = 1;
    }
    for (int i = 0; i < grown; i++)
    {
        int k = queue[i];
        int g = (y0 + k / bw) * W + x0 + k % bw;
        a->open[g] = 1;
        if (a->island && a->island[g] == 0) a->island[g] = lab[k];
    }
    a->opened = grown;
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_solid_carve(CarveArgs* a)
{
    Dg d(DgSolidCarve);
    int x1 = a->x0 + a->bw - 1, y1 = a->y0 + a->bh - 1;
    d.InV(a->w); d.InV(a->h); d.InV(a->ox); d.InV(a->oy); d.InV(a->ppu); d.InV(a->reach); d.InV(a->x0); d.InV(a->y0); d.InV(a->bw); d.InV(a->bh);
    d.InV(a->shapeKind); d.InV(a->refK); d.InV(a->refLab);
    d.In(a->shape, (int64_t)(a->shapeKind == 0 ? 3 : a->shapeKind == 1 ? 8 : a->shapeN * 4) * 4);
    d.In(a->keep, (int64_t)a->keepN * 16);
    d.In(a->barrier, (int64_t)a->bw * a->bh);
    d.In2D(a->open, a->w, a->h, 1, a->x0, a->y0, x1, y1);
    d.In2D(a->island, a->w, a->h, 1, a->x0, a->y0, x1, y1);
    d.In2D(a->outside, a->w, a->h, 1, a->x0, a->y0, x1, y1);
    mrp_solid_carve(a);
    d.Out2D(a->open, a->w, a->h, 1, a->x0, a->y0, x1, y1);
    d.Out2D(a->island, a->w, a->h, 1, a->x0, a->y0, x1, y1);
    d.OutV(a->opened);
    d.Commit();
}
