#include "mrp_native.h"
#include <math.h>

// 床の上の水の計算の 1 小刻み (src/Terrain/WaterSim.cs の DropVoid → Flow → Heights と同じ結果)。
// 整数だけで計算する。升を回す順番も C# と同じ (Flow の途中で起こした升は同じ回のうちに回る)

struct WaterFlowArgs
{
    int32_t* hgt; int32_t* flux; int32_t* move;
    const uint8_t* open; const uint8_t* edge; const int8_t* lvl;
    const int16_t* bed; const int16_t* furn;
    const uint8_t* stepMask; const uint8_t* fallMask; const uint8_t* seaMask; const uint8_t* lipMask;
    const int16_t* lipH; const uint8_t* barB; const int16_t* barH; const int32_t* stepTo;
    int32_t* act; uint8_t* inList; int32_t* fo; uint8_t* tileDirty; int32_t* dirtyOut;
    int32_t actN, foN, dirtyN;
    int32_t w, h, tw, th, tileCells;
    int32_t sub, subN, dry, seaLevel;
    int64_t voidAdd;
};

// WaterSim の定数と同じ値
static const int Gain = 56, Damp = 236;
static const int DryHold = 48, WetHold = 3, MinDepth = 6, FallLip = 24, FallGain = 72;
static const int WeirDiv = 64 * 256 / FallGain;
static const int Full = 1024, StepHeight = Full / 4, BarMax = Full;
static const int NoLevel = -128, VoidLevel = -2;
// 小刻みの数とタイルの升の数は C# の定数と同じ (割り算を定数にするため。違えば計算しない)
static const int SubN = 4, TileCells = 16;
static const int Nx[4] = { 1, -1, 0, 0 }, Ny[4] = { 0, 0, 1, -1 }, Opp[4] = { 1, 0, 3, 2 };

static inline int isqrt(int64_t v)
{
    if (v <= 0) return 0;
    int64_t r = (int64_t)sqrt((double)v);
    while (r * r > v) r--;
    while ((r + 1) * (r + 1) <= v) r++;
    return (int)r;
}

static inline int imin(int a, int b) { return a < b ? a : b; }
static inline int imax(int a, int b) { return a > b ? a : b; }

static inline int weir(int e) { return e <= 0 ? 0 : imin(e / 2, (int)((int64_t)e * isqrt(16LL * e) / WeirDiv)); }

static inline int share(int f, int s) { return (int)((int64_t)f * (s + 1) / SubN - (int64_t)f * s / SubN); }

static inline void mark_tile(WaterFlowArgs* a, int tx, int ty)
{
    if (tx < 0 || ty < 0 || tx >= a->tw || ty >= a->th) return;
    int t = ty * a->tw + tx;
    if (a->tileDirty[t]) return;
    a->tileDirty[t] = 1;
    a->dirtyOut[a->dirtyN++] = t;
}

static inline void mark(WaterFlowArgs* a, int k)
{
    const int tc = TileCells;
    int y = k / a->w, x = k - y * a->w;
    mark_tile(a, x / tc, y / tc);
    int lx = x % tc, ly = y % tc;
    if (lx == 0) mark_tile(a, x / tc - 1, y / tc);
    else if (lx == tc - 1) mark_tile(a, x / tc + 1, y / tc);
    if (ly == 0) mark_tile(a, x / tc, y / tc - 1);
    else if (ly == tc - 1) mark_tile(a, x / tc, y / tc + 1);
}

static inline void wake(WaterFlowArgs* a, int k)
{
    if (a->inList[k] != 0) return;
    int y = k / a->w, x = k - y * a->w;
    if (x < 1 || y < 1 || x >= a->w - 1 || y >= a->h - 1) return;
    a->inList[k] = 1;
    a->act[a->actN++] = k;
}

static inline bool passable(const WaterFlowArgs* a, int k, int d)
{
    return (a->edge[k] >> d & 1) != 0 && (a->edge[k + Nx[d] + Ny[d] * a->w] >> Opp[d] & 1) != 0;
}

static inline int lip_of(const WaterFlowArgs* a, int k, int d)
{
    return (a->lipMask[k] >> d & 1) != 0 ? a->lipH[k * 4 + d] : FallLip;
}

static inline int fall_rate(const WaterFlowArgs* a, int k, int d, int h)
{
    int key = k * 4 + d, b = a->barB[key];
    if (b == 0) return weir(h - lip_of(a, k, d));
    int open = b == 255 ? 0 : weir(h - FallLip) * (256 - b);
    int over = a->barH[key] >= BarMax ? 0 : weir(h - imax(FallLip, (int)a->barH[key])) * (b == 255 ? 256 : b);
    return (open + over) / 256;
}

static inline int sub_out(WaterFlowArgs* a, int k, int d, int g)
{
    if (g <= 0) g = 1;
    int q = share(g, a->sub);
    if (q > 0) { int* f = a->fo + a->foN * 3; f[0] = k; f[1] = d; f[2] = q; a->foN++; }
    return q;
}

static void drop_void(WaterFlowArgs* a)
{
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        if (a->lvl[k] != VoidLevel || a->hgt[k] <= 0) continue;
        a->voidAdd += a->hgt[k];
        a->hgt[k] = 0;
        mark(a, k);
    }
}

static void flow(WaterFlowArgs* a)
{
    const int subGain = Gain * SubN, subDamp = 256 - (256 - Damp) / SubN;
    int w = a->w;
    a->foN = 0;
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        int h = a->hgt[k];
        int sum = 0;
        int lk = a->lvl[k];
        int falls = a->foN;
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * w;
            int f = 0;
            if (h > 0 && a->open[j] != 0 && passable(a, k, d))
            {
                int lj = a->lvl[j];
                bool known = lk != NoLevel && lj != NoLevel;
                if (!known || lj <= lk)
                {
                    int hj = a->hgt[j];
                    int diff = known && lj < lk ? h - DryHold
                        : h + a->bed[k] + a->furn[k] - hj - a->bed[j] - a->furn[j] - (hj == 0 ? DryHold : WetHold);
                    f = a->flux[k * 4 + d] * subDamp / 256 + (diff > 0 ? diff * subGain / 256 : 0);
                    if (f < 0) f = 0;
                }
            }
            else if (h > 0 && (a->stepMask[k] >> d & 1) != 0)
            {
                int t = a->stepTo[k * 4 + d], ht = a->hgt[t];
                int diff = a->lvl[t] < lk
                    ? h - imax(0, ht - StepHeight) - (ht <= StepHeight ? DryHold : WetHold)
                    : h - StepHeight - ht - (ht == 0 ? DryHold : WetHold);
                if (diff > 0) sum += sub_out(a, k, d, diff * Gain / 256);
            }
            else if ((a->fallMask[k] >> d & 1) != 0 && h > FallLip)
            {
                int g = fall_rate(a, k, d, h);
                if (g > 0) sum += sub_out(a, k, d, g);
            }
            else if (h > 0 && (a->seaMask[k] >> d & 1) != 0)
            {
                int diff = h + a->bed[k] + a->furn[k] - a->seaLevel - WetHold;
                if (diff > 0) sum += sub_out(a, k, d, diff * Gain / 256);
            }
            a->flux[k * 4 + d] = f;
            int q = share(f, a->sub);
            a->move[k * 4 + d] = q;
            sum += q;
            if (f > 0) wake(a, j);
        }
        if (sum > h)
        {
            for (int d = 0; d < 4; d++)
            {
                a->flux[k * 4 + d] = (int)((int64_t)a->flux[k * 4 + d] * h / sum);
                a->move[k * 4 + d] = (int)((int64_t)a->move[k * 4 + d] * h / sum);
            }
            for (int x = falls; x < a->foN; x++) a->fo[x * 3 + 2] = (int)((int64_t)a->fo[x * 3 + 2] * h / sum);
        }
    }
}

static void heights(WaterFlowArgs* a)
{
    int w = a->w;
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        int dh = 0;
        for (int d = 0; d < 4; d++)
        {
            dh -= a->move[k * 4 + d];
            int j = k + Nx[d] + Ny[d] * w;
            dh += a->move[j * 4 + Opp[d]];
        }
        if (dh != 0) { a->hgt[k] += dh; mark(a, k); }
        if (a->dry && a->hgt[k] > 0 && a->hgt[k] < MinDepth) { a->hgt[k]--; mark(a, k); }
    }
}

// 1 小刻みの DropVoid → Flow → Heights (減圧の吸い出しの無い時)。actN / foN / dirtyN / voidAdd を書き戻す。
// 定数が C# と違えば何もせず 0 (C# 版で回す)
MRP_API int32_t mrp_water_substep(WaterFlowArgs* a)
{
    if (a->subN != SubN || a->tileCells != TileCells) return 0;
    drop_void(a);
    flow(a);
    heights(a);
    return 1;
}
