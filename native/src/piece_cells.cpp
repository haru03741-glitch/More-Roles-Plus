#include "mrp_native.h"
#include "digest.h"

// 抜いた画素を割れ目の細胞ごとに数える (src/Terrain/BreakPieces.cs の Spawn の画素の数え上げと同じ結果)。
// この破壊の番号を書いた画素のうち、周りの 3×3 に見える穴がある物を、いちばん近い種点の細胞へ足す

struct PieceAcc { int32_t x0, y0, x1, y1, count; int32_t pad; int64_t sumX, sumY; };

struct PieceArgs
{
    const uint8_t* px; const uint8_t* gens; int32_t w, h; int32_t gen; int32_t visibleFloor;
    int32_t rx0, ry0, rx1, ry1;            // 数える範囲 (rx1・ry1 は含まない)
    float ox, oy, ppu;
    const float* xs; const float* ys; int32_t n;
    PieceAcc* acc;                          // 256 個 (count = 0 は無し)
};

static bool visible(const PieceArgs* a, int px, int py)
{
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        int x = px + dx, y = py + dy;
        if (x < 0 || y < 0 || x >= a->w || y >= a->h) continue;
        if (a->px[(y * a->w + x) * 4] >= a->visibleFloor) return true;
    }
    return false;
}

MRP_API void mrp_piece_cells(PieceArgs* a)
{
    for (int i = 0; i < 256; i++) a->acc[i].count = 0;
    for (int py = a->ry0; py < a->ry1; py++)
    {
        float cy = a->oy + ((float)py + 0.5f) / a->ppu;
        for (int px = a->rx0; px < a->rx1; px++)
        {
            if (a->gens[py * a->w + px] != a->gen || !visible(a, px, py)) continue;
            float cx = a->ox + ((float)px + 0.5f) / a->ppu;
            int best = 0;
            float bd = 3.402823466e+38f;
            for (int i = 0; i < a->n; i++)
            {
                float dx = cx - a->xs[i], dy = cy - a->ys[i];
                float d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = i; }
            }
            PieceAcc* c = &a->acc[best & 255];
            if (c->count == 0) { c->x0 = c->x1 = px; c->y0 = c->y1 = py; c->sumX = 0; c->sumY = 0; }
            if (px < c->x0) c->x0 = px;
            if (px > c->x1) c->x1 = px;
            if (py < c->y0) c->y0 = py;
            if (py > c->y1) c->y1 = py;
            c->count++;
            c->sumX += px; c->sumY += py;
        }
    }
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_piece_cells(PieceArgs* a)
{
    Dg d(DgPieceCells);
    d.InV(a->w); d.InV(a->h); d.InV(a->gen); d.InV(a->visibleFloor); d.InV(a->rx0); d.InV(a->ry0); d.InV(a->rx1); d.InV(a->ry1);
    d.InV(a->ox); d.InV(a->oy); d.InV(a->ppu); d.InV(a->n);
    d.In2D(a->px, a->w, a->h, 4, a->rx0, a->ry0, a->rx1 - 1, a->ry1 - 1);
    d.In2D(a->gens, a->w, a->h, 1, a->rx0, a->ry0, a->rx1 - 1, a->ry1 - 1);
    d.In(a->xs, (int64_t)a->n * 4); d.In(a->ys, (int64_t)a->n * 4);
    mrp_piece_cells(a);
    // count = 0 の細胞は他の値が古いまま・pad は書かれないので入れない
    for (int i = 0; i < 256; i++)
    {
        const PieceAcc& c = a->acc[i];
        if (c.count == 0) continue;
        d.OutV(i); d.OutV(c.x0); d.OutV(c.y0); d.OutV(c.x1); d.OutV(c.y1); d.OutV(c.count); d.OutV(c.sumX); d.OutV(c.sumY);
    }
    d.Commit();
}
