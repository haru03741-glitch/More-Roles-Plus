#include "mrp_native.h"
#include "digest.h"
#include <math.h>

// 水の絵のタイル 1 枚を描く前の高さの写し (src/Terrain/WaterArt.cs の RawCells と WaterSim.ShownDepth / CraterScale と
// 同じ式と順序)。タイルを縁 3 升ぶん広げて描く高さを写し、3×3 でならした深さと濡れ具合を作る

struct RawArgs
{
    const int32_t* hgt; const int16_t* furn; const uint8_t* open; const int32_t* fill;
    int32_t w, h, x0, y0, tc;
    float ox, oy, cs;
    const float* crater; int32_t craterN; // くぼみ 1 つ = x, y, 半径, 効き (効きが 0 以下の物は渡さない)
    int32_t* raw; uint8_t* closed; int32_t* depth; int32_t* wet;
    int32_t any;
};

// WaterSim / WaterArt の定数と同じ値
static const int Full = 1024, TopFilm = Full * 3 / 100, FurnScan = 8, WetMin = 15, WetRamp = 45;
static const float CraterDepth = 1.15f, RimBulge = 0.5f;
static const int Nx[4] = { 1, -1, 0, 0 }, Ny[4] = { 0, 0, 1, -1 };

static int shown_depth(const RawArgs* a, int k)
{
    int f = a->furn ? a->furn[k] : 0, h = a->hgt[k];
    if (f == 0) return h;
    if (h > TopFilm) return h + f;
    int best = 0, len = a->w * a->h;
    for (int d = 0; d < 4; d++)
    {
        int j = k, step = Nx[d] + Ny[d] * a->w;
        for (int i = 0; i < FurnScan; i++)
        {
            j += step;
            if (j < 0 || j >= len || a->open[j] == 0) break;
            if (a->furn[j] != 0) continue;
            if (a->hgt[j] > best) best = a->hgt[j];
            break;
        }
    }
    return best < f ? best : f;
}

static float crater_scale(const RawArgs* a, float wx, float wy)
{
    float k = 1.0f;
    for (int i = 0; i < a->craterN; i++)
    {
        const float* c = a->crater + i * 4;
        float amt = c[3];
        float dx = wx - c[0], dy = wy - c[1], d2 = (dx * dx + dy * dy) / (c[2] * c[2]);
        if (d2 < 1.0f)
        {
            float m = CraterDepth * (1.0f - d2);
            k *= 1.0f - amt * (1.0f < m ? 1.0f : m);
        }
        else
        {
            float d = sqrtf(d2), e = d - 1.15f, rim = 1.0f - (e < 0.0f ? -e : e) / 0.3f;
            if (rim > 0.0f) k *= 1.0f + RimBulge * amt * rim;
        }
    }
    return k;
}

static inline int wet_of(int h) { return h <= WetMin ? 0 : (h - WetMin < WetRamp ? h - WetMin : WetRamp); }
static inline int wet_open(const RawArgs* a, int i) { return a->closed[i] ? 0 : wet_of(a->raw[i]); }
static inline int imax(int a, int b) { return a > b ? a : b; }

MRP_API void mrp_water_raw(RawArgs* a)
{
    int w = a->w, h = a->h, tc = a->tc, dw = tc + 4, rw = tc + 6;
    const uint8_t* open = a->open;
    int any = 0;
    for (int y = 0; y < rw; y++)
    for (int x = 0; x < rw; x++)
    {
        int cx = a->x0 + x - 3, cy = a->y0 + y - 3;
        bool inGrid = cx >= 1 && cy >= 1 && cx < w - 1 && cy < h - 1;
        int k = cy * w + cx;
        int v = inGrid ? shown_depth(a, k) : 0;
        uint8_t closed = inGrid && open[k] == 0;
        if (inGrid && a->fill && a->fill[k] >= 0 && open[k] == 0)
        {
            v = shown_depth(a, a->fill[k]);
            closed = 0;
        }
        else if (v == 0 && inGrid && open[k] == 0)
        {
            int n = 0, from = 0;
            if (open[k + 1]) { n++; from = k + 1; }
            if (open[k - 1]) { n++; from = k - 1; }
            if (open[k + w]) { n++; from = k + w; }
            if (open[k - w]) { n++; from = k - w; }
            if (n == 1) v = shown_depth(a, from);
        }
        if (v > 0 && a->craterN > 0) v = (int)((float)v * crater_scale(a, a->ox + ((float)cx + 0.5f) * a->cs, a->oy + ((float)cy + 0.5f) * a->cs));
        a->closed[y * rw + x] = closed;
        a->raw[y * rw + x] = v;
        if (v > 0 && x >= 2 && y >= 2 && x < rw - 2 && y < rw - 2) any = 1;
    }
    const int32_t* raw = a->raw;
    for (int y = 0; y < dw; y++)
    for (int x = 0; x < dw; x++)
    {
        int c = (y + 1) * rw + x + 1;
        int sum = raw[c] * 4 + (raw[c - 1] + raw[c + 1] + raw[c - rw] + raw[c + rw]) * 2
            + raw[c - rw - 1] + raw[c - rw + 1] + raw[c + rw - 1] + raw[c + rw + 1];
        a->depth[y * dw + x] = sum / 16;
        int wet = wet_of(raw[c]);
        if (a->closed[c])
        {
            int p = imax(imax(wet_open(a, c - 1), wet_open(a, c + 1)), imax(wet_open(a, c - rw), wet_open(a, c + rw)));
            int q = imax(imax(wet_open(a, c - rw - 1), wet_open(a, c - rw + 1)), imax(wet_open(a, c + rw - 1), wet_open(a, c + rw + 1)));
            wet = imax(wet, imax(p, q));
        }
        a->wet[y * dw + x] = wet;
    }
    a->any = any;
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_water_raw(RawArgs* a)
{
    Dg d(DgWaterRaw);
    int tc = a->tc, rw = tc + 6, dw = tc + 4;
    int x0 = a->x0 - 3, y0 = a->y0 - 3, x1 = a->x0 + tc + 2, y1 = a->y0 + tc + 2;
    d.InV(a->w); d.InV(a->h); d.InV(a->x0); d.InV(a->y0); d.InV(tc); d.InV(a->ox); d.InV(a->oy); d.InV(a->cs); d.InV(a->craterN);
    d.In(a->crater, (int64_t)a->craterN * 16);
    // 1 升外の隣も読むので 1 升広げる
    d.In2D(a->hgt, a->w, a->h, 4, x0 - 1, y0 - 1, x1 + 1, y1 + 1);
    d.In2D(a->furn, a->w, a->h, 2, x0 - 1, y0 - 1, x1 + 1, y1 + 1);
    d.In2D(a->open, a->w, a->h, 1, x0 - 1, y0 - 1, x1 + 1, y1 + 1);
    d.In2D(a->fill, a->w, a->h, 4, x0, y0, x1, y1);
    mrp_water_raw(a);
    d.Out(a->raw, (int64_t)rw * rw * 4); d.Out(a->closed, (int64_t)rw * rw);
    d.Out(a->depth, (int64_t)dw * dw * 4); d.Out(a->wet, (int64_t)dw * dw * 4);
    d.OutV(a->any);
    d.Commit();
}
