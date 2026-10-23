#include "mrp_native.h"
#include "digest.h"
#include <math.h>

// 壁の面の上の水の厚さを画素の A に書く (src/Terrain/WaterArt.cs の WriteFace / BaseDepth / CellBase と
// WaterSim.ShownDepth と同じ式と順序)

struct FaceArgs
{
    uint8_t* px; const uint8_t* face; int32_t n, x0, y0;
    float edge, ox, oy, cell;
    const uint8_t* open; const int16_t* furn; const int32_t* hgt; int32_t w, h;
};

// cell_base の結果をタイルの間だけ覚える (同じ升を画素ごとに何度も探すので)。範囲の外は覚えずに探す
static const int MemoW = 16 + 8, MemoH = 16 + 96, MemoBelow = 90;
struct FaceMemo { int bx, by; int32_t v[MemoW * MemoH]; };

static const int Full = 1024, TopFilm = Full * 3 / 100, FurnScan = 8, FaceFurnScan = 8, FaceBaseScan = 3, OvertopPx = 3;
static const int FaceTop = 253;
static const float FaceUnit = 64.0f, BaseIn = 1.0f / 32.0f, FaceScale = 96.0f;
static const int Nx[4] = { 1, -1, 0, 0 }, Ny[4] = { 0, 0, 1, -1 };

static int shown_depth(const FaceArgs* a, int k)
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

static int cell_base(const FaceArgs* a, int cx, int cy)
{
    int w = a->w;
    if (cx < 1 || cx >= w - 1) return -1;
    int open = 0;
    for (int d = 0; d <= FaceFurnScan; d++)
    {
        int y = cy - d;
        if (y < 1 || y >= a->h - 1) return -1;
        int k = y * w + cx;
        if (a->open[k] == 0)
        {
            if (open == 0 && d < FaceBaseScan) continue;
            return -1;
        }
        open++;
        if ((a->furn ? a->furn[k] : 0) == 0 || a->hgt[k] > TopFilm) return shown_depth(a, k);
    }
    return -1;
}

static int cell_base_m(const FaceArgs* a, FaceMemo* m, int cx, int cy)
{
    int x = cx - m->bx, y = cy - m->by;
    if (x < 0 || y < 0 || x >= MemoW || y >= MemoH) return cell_base(a, cx, cy);
    int32_t* p = &m->v[y * MemoW + x];
    if (*p == -2) *p = cell_base(a, cx, cy);
    return *p;
}

static float base_depth(const FaceArgs* a, FaceMemo* m, float wx, float wyBase)
{
    float fx = (wx - a->ox) / a->cell - 0.5f;
    int cx = (int)floorf(fx), cy = (int)floorf((wyBase - a->oy) / a->cell);
    float f = fx - (float)cx;
    int p = cell_base_m(a, m, cx, cy), q = cell_base_m(a, m, cx + 1, cy);
    if (p < 0 && q < 0)
    {
        p = cell_base_m(a, m, cx - 1, cy);
        q = cell_base_m(a, m, cx + 2, cy);
        if (p < 0 && q < 0) return 0.0f;
    }
    if (p < 0) p = q;
    else if (q < 0) q = p;
    return ((float)p * (1.0f - f) + (float)q * f) * (1.0f / (float)Full);
}

MRP_API void mrp_water_face(FaceArgs* a)
{
    int n = a->n;
    static FaceMemo memo;   // 描くのはメインスレッドだけ
    memo.bx = a->x0 - 4; memo.by = a->y0 - MemoBelow;
    for (int i = 0; i < MemoW * MemoH; i++) memo.v[i] = -2;
    for (int px = 0; px < n; px++)
    {
        uint8_t topA = 0;
        int carry = 0;
        for (int py = 0; py < n; py++)
        {
            int j = py * n + px, i = j * 4;
            uint8_t z = a->face[j];
            if (z <= FaceTop)
            {
                float wx = a->ox + ((float)a->x0 + (float)px * a->edge) * a->cell, wy = a->oy + ((float)a->y0 + (float)py * a->edge) * a->cell;
                float h = (float)z * (1.0f / FaceUnit);
                float v = 128.0f + (base_depth(a, &memo, wx, wy - h - BaseIn) - h) * FaceScale;
                v = v < 0.0f ? 0.0f : v > 255.0f ? 255.0f : v;
                uint8_t b = (uint8_t)v;
                a->px[i + 3] = b;
                topA = b;
                carry = b > 128 ? OvertopPx : 0;
            }
            else if (carry > 0)
            {
                if (z == 255) a->px[i + 1] = 0;
                a->px[i + 3] = topA;
                carry--;
            }
            else carry = 0;
        }
    }
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_water_face(FaceArgs* a)
{
    Dg d(DgWaterFace);
    int64_t nn = (int64_t)a->n * a->n;
    // 水の地図は船全体なので入力には入れない
    d.InV(a->n); d.InV(a->x0); d.InV(a->y0); d.InV(a->edge); d.InV(a->ox); d.InV(a->oy); d.InV(a->cell); d.InV(a->w); d.InV(a->h);
    d.In(a->face, nn); d.In(a->px, nn * 4);
    mrp_water_face(a);
    d.Out(a->px, nn * 4);
    d.Commit();
}
