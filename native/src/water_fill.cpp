#include "mrp_native.h"
#include "digest.h"
#include <math.h>

// 水の升の写し元を決め直す (src/Terrain/WaterArt.cs の Fill と同じ結果)。
// 歩ける所の地図の升 (1 升 = 水の升の 1/sub) を、水の開いた升から床マスクの床の上だけ幅優先で max 升まで塗り広げ、
// 水の閉じた升ごとに、中の地図の升でいちばん近い写し元を選ぶ。写し元が別のタイルなら、写し元のタイルに描き直す向きの印を付ける。
// 小数の式の順番は C# と同じにする

struct WaterFillArgs
{
    const uint8_t* solidOpen; int32_t sw;            // 歩ける所の地図 (0 = 歩けない)
    const uint8_t* waterOpen; int32_t w, h, sub;     // 水の升 (0 = 閉じた)
    const uint8_t* mask; int32_t mw, mh;             // 床マスク (1 ビット = 1 画素・null = 床なし)
    float mox, moy, mppu;
    float sox, soy, cell;                            // 地図の左下と 1 升の大きさ
    int32_t max;                                     // 塗り広げる升の数
    int32_t x0, y0, x1, y1;                          // 決め直す水の升 [x0, x1) × [y0, y1)
    int32_t rx0, ry0, rw, rh;                        // 塗る地図の升の範囲
    int32_t* src; uint8_t* dist; int32_t* queue;     // rw*rh の置き場
    int32_t* fillFrom;                               // 水の升ごとの写し元 (−1 = 無し・書き換える)
    uint8_t* targets; int32_t tw, tc;                // タイルごとの描き直す向きの印 (足す)
    int32_t cells;                                   // 戻り: 写し元のある升の数の増減
};

static const int TileDx[8] = { -1, 0, 1, -1, 1, -1, 0, 1 }, TileDy[8] = { -1, -1, -1, 0, 0, 1, 1, 1 };

// FloorMask.AllFloor: 世界の四角 (x, y から size 四方) の画素が全部床か
static bool all_floor(const WaterFillArgs* a, float x, float y, float size)
{
    if (!a->mask) return false;
    int x0 = (int)floorf((x - a->mox) * a->mppu), y0 = (int)floorf((y - a->moy) * a->mppu);
    int x1 = (int)ceilf((x + size - a->mox) * a->mppu), y1 = (int)ceilf((y + size - a->moy) * a->mppu);
    if (x0 < 0 || y0 < 0 || x1 > a->mw || y1 > a->mh) return false;
    for (int my = y0; my < y1; my++)
    for (int mx = x0, k = my * a->mw + x0; mx < x1; mx++, k++)
        if ((a->mask[k >> 3] & (1 << (k & 7))) == 0) return false;
    return true;
}

MRP_API void mrp_water_fill(WaterFillArgs* a)
{
    const int w = a->w, sub = a->sub, sw = a->sw, rw = a->rw, rh = a->rh, rx0 = a->rx0, ry0 = a->ry0, n = rw * rh;
    int32_t* src = a->src; uint8_t* dist = a->dist; int32_t* queue = a->queue;
    for (int i = 0; i < n; i++) { src[i] = -1; dist[i] = 0; }
    int head = 0, tail = 0;
    for (int y = 0; y < rh; y++)
    for (int x = 0; x < rw; x++)
    {
        int k = (ry0 + y) * sw + rx0 + x;
        if (a->solidOpen[k] == 0) continue;
        int wx = (rx0 + x) / sub, wy = (ry0 + y) / sub;
        if (wx >= w || wy >= a->h || a->waterOpen[wy * w + wx] == 0) continue;
        src[y * rw + x] = wy * w + wx;
        queue[tail++] = y * rw + x;
    }
    while (head < tail)
    {
        int k = queue[head++];
        int d = dist[k] + 1;
        if (d > a->max) continue;
        int y = k / rw, x = k - y * rw;
        int js[4]; int jn = 0;
        if (x > 0) js[jn++] = k - 1;
        if (x < rw - 1) js[jn++] = k + 1;
        if (y > 0) js[jn++] = k - rw;
        if (y < rh - 1) js[jn++] = k + rw;
        for (int i = 0; i < jn; i++)
        {
            int j = js[i];
            if (src[j] != -1) continue;
            if (!all_floor(a, a->sox + (float)(rx0 + j % rw) * a->cell, a->soy + (float)(ry0 + j / rw) * a->cell, a->cell)) { src[j] = -2; continue; }
            src[j] = src[k];
            dist[j] = (uint8_t)d;
            queue[tail++] = j;
        }
    }

    const int tw = a->tw, tc = a->tc;
    int cells = 0;
    for (int wy = a->y0; wy < a->y1; wy++)
    for (int wx = a->x0; wx < a->x1; wx++)
    {
        int wk = wy * w + wx;
        if (a->fillFrom[wk] >= 0) cells--;
        a->fillFrom[wk] = -1;
        if (a->waterOpen[wk] != 0) continue;
        int best = -1, bestD = 0x7FFFFFFF;
        for (int sy = wy * sub - ry0; sy < wy * sub + sub - ry0 && sy < rh; sy++)
        for (int sx = wx * sub - rx0; sx < wx * sub + sub - rx0 && sx < rw; sx++)
        {
            if (sx < 0 || sy < 0) continue;
            int k = sy * rw + sx;
            if (src[k] >= 0 && dist[k] < bestD) { bestD = dist[k]; best = src[k]; }
        }
        if (best < 0) continue;
        a->fillFrom[wk] = best;
        cells++;
        int ftx = best % w / tc, fty = best / w / tc, ttx = wx / tc, tty = wy / tc;
        if (ftx == ttx && fty == tty) continue;
        for (int i = 0; i < 8; i++)
            if (ttx - ftx == TileDx[i] && tty - fty == TileDy[i]) a->targets[fty * tw + ftx] |= (uint8_t)(1 << i);
    }
    a->cells = cells;
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_water_fill(WaterFillArgs* a)
{
    Dg d(DgWaterFill);
    int64_t n = (int64_t)a->w * a->h, tb = (int64_t)a->tw * ((a->h + a->tc - 1) / a->tc);
    d.InV(a->sw); d.InV(a->w); d.InV(a->h); d.InV(a->sub); d.InV(a->mw); d.InV(a->mh); d.InV(a->mox); d.InV(a->moy); d.InV(a->mppu);
    d.InV(a->sox); d.InV(a->soy); d.InV(a->cell); d.InV(a->max); d.InV(a->x0); d.InV(a->y0); d.InV(a->x1); d.InV(a->y1);
    d.InV(a->rx0); d.InV(a->ry0); d.InV(a->rw); d.InV(a->rh); d.InV(a->tw); d.InV(a->tc);
    d.In2D(a->solidOpen, a->sw, a->ry0 + a->rh, 1, a->rx0, a->ry0, a->rx0 + a->rw - 1, a->ry0 + a->rh - 1);
    d.In(a->waterOpen, n); d.In(a->fillFrom, n * 4); d.In(a->targets, tb);
    mrp_water_fill(a);
    d.Out(a->fillFrom, n * 4); d.Out(a->targets, tb); d.OutV(a->cells);
    d.Commit();
}
