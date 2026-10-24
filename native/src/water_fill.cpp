#include "mrp_native.h"
#include "digest.h"
#include "capture.h"
#include <math.h>

// 水の升の写し元を決め直す。
// 歩ける所の地図の升 (1 升 = 水の升の 1/sub) を、水の開いた升から床マスクの床の上だけ幅優先で max 升まで塗り広げ、
// 水の閉じた升ごとに、中の地図の升でいちばん近い写し元を選ぶ。写し元が別のタイルなら、写し元のタイルに描き直す向きの印を付ける。
// 小数の式は書いた順に計算する

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
    Cap c(DgWaterFill);
    int64_t rn = (int64_t)a->rw * a->rh, maskB = a->mask ? ((int64_t)a->mw * a->mh + 7) >> 3 : 0;
    c.RawV(*a);
    c.Rect(a->solidOpen, a->sw, a->ry0 + a->rh, 1, a->rx0, a->ry0, a->rx0 + a->rw - 1, a->ry0 + a->rh - 1);
    c.Blob(a->waterOpen, n);
    c.Blob(a->mask, maskB);
    c.Blob(a->fillFrom, n * 4);
    c.Blob(a->targets, tb);
    c.Blob(a->src, rn * 4);
    c.Blob(a->dist, rn);
    c.Blob(a->queue, rn * 4);
    mrp_water_fill(a);
    d.Out(a->fillFrom, n * 4); d.Out(a->targets, tb); d.OutV(a->cells);
    d.Commit();
    c.Blob(a->fillFrom, n * 4);
    c.Blob(a->targets, tb);
    c.Blob(a->src, rn * 4);
    c.Blob(a->dist, rn);
    c.Blob(a->queue, rn * 4);
    c.RawV(*a);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_water_fill と同じ)
void rp_water_fill(Rp& r)
{
    WaterFillArgs a, want;
    r.RawV(a);
    int64_t n = (int64_t)a.w * a.h, tb = (int64_t)a.tw * ((a.h + a.tc - 1) / a.tc), rn = (int64_t)a.rw * a.rh;
    int64_t lw, lm, lf, lt, ls, ld, lq;
    a.solidOpen = (const uint8_t*)r.RectInto(a.sw, a.ry0 + a.rh, 1, a.rx0, a.ry0, a.rx0 + a.rw - 1, a.ry0 + a.rh - 1, 0xA5);
    a.waterOpen = (const uint8_t*)r.Blob(&lw);
    a.mask = (const uint8_t*)r.Blob(&lm);
    a.fillFrom = (int32_t*)r.Blob(&lf);
    a.targets = (uint8_t*)r.Blob(&lt);
    a.src = (int32_t*)r.Blob(&ls);
    a.dist = (uint8_t*)r.Blob(&ld);
    a.queue = (int32_t*)r.Blob(&lq);
    if (!r.ok) return;
    if (lw != n || lm != (a.mask ? ((int64_t)a.mw * a.mh + 7) >> 3 : 0) || lf != n * 4 || lt != tb || ls != rn * 4 || ld != rn || lq != rn * 4)
    { r.Fail("water_fill: input blob sizes differ"); return; }
    mrp_water_fill(&a);
    int64_t wf, wt, ws, wd, wq;
    const void* f = r.Blob(&wf);
    const void* t = r.Blob(&wt);
    const void* s = r.Blob(&ws);
    const void* dd = r.Blob(&wd);
    const void* q = r.Blob(&wq);
    r.RawV(want);
    if (!r.ok) return;
    if (wf != n * 4 || wt != tb || ws != rn * 4 || wd != rn || wq != rn * 4) { r.Fail("water_fill: output blob sizes differ"); return; }
    r.Same("fillFrom", a.fillFrom, f, n * 4);
    r.Same("targets", a.targets, t, tb);
    r.Same("src", a.src, s, rn * 4);
    r.Same("dist", a.dist, dd, rn);
    r.Same("queue", a.queue, q, rn * 4);
    r.Same("cells", &a.cells, &want.cells, 4);
}
static RpReg reg_water_fill(DgWaterFill, rp_water_fill);
#endif
