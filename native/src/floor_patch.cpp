#include "mrp_native.h"
#include <math.h>

// 持ち上げた家具の跡を床マスクの床にする (src/Terrain/FloorMask.cs の Apply と同じ結果) と、
// 家具の今いる所を水の床の型から抜く (CutMoved と同じ結果)。小数の式の順番は C# と同じにする

struct FloorPatchArgs
{
    uint8_t* bits; int32_t w;                 // 床マスク (1 ビット = 1 画素・書き足す)
    int32_t x0, y0, x1, y1;                   // マスクの画素の範囲
    float ox, oy, ppu;                        // マスクの左下と 1 単位の画素数
    float rxMin, ryMin, sx, sy;               // 跡の四角の左下と、1 単位が形の何画素か
    const uint8_t* shape; int32_t n;          // 形 (n×n・左下原点・1 = 床にする)
    uint8_t* dry; int32_t dw;                 // 戻り: 跡にする前のマスクで床でなかった画素 (DW×DH のビット・0 で渡す)
};

MRP_API void mrp_floor_patch(FloorPatchArgs* a)
{
    const int n = a->n, w = a->w, dw = a->dw;
    for (int my = a->y0; my <= a->y1; my++)
    {
        int sy0 = (int)floorf((((float)my + 0.5f) / a->ppu + a->oy - a->ryMin) * a->sy);
        if (sy0 < 0 || sy0 >= n) continue;
        for (int mx = a->x0; mx <= a->x1; mx++)
        {
            int sx0 = (int)floorf((((float)mx + 0.5f) / a->ppu + a->ox - a->rxMin) * a->sx);
            if (sx0 < 0 || sx0 >= n || a->shape[sy0 * n + sx0] == 0) continue;
            int k = my * w + mx;
            if ((a->bits[k >> 3] & (1 << (k & 7))) == 0)
            {
                int d = (my - a->y0) * dw + (mx - a->x0);
                a->dry[d >> 3] |= (uint8_t)(1 << (d & 7));
            }
            a->bits[k >> 3] |= (uint8_t)(1 << (k & 7));
        }
    }
}

struct FloorCutArgs
{
    uint8_t* into; int32_t n;                 // 水の床の型 (n×n)。画素 k の真ん中 = x + k × step
    float x, y, step;
    float px0, py0, cx, cy, cs, sn;           // 元の足元・今の足元・今の向きから元へ戻す回転
    float mcx, mcy, half;                     // 動いた先の範囲 (中心と半径)
    float ox, oy, ppu;                        // マスクの左下と 1 単位の画素数
    const uint8_t* dry; int32_t dx0, dy0, dw, dh; // 跡にする前に床でなかった画素 (家具そのもの)
};

MRP_API void mrp_floor_cut_moved(FloorCutArgs* a)
{
    const int n = a->n;
    for (int py = 0; py < n; py++)
    {
        float wy = a->y + (float)py * a->step - a->cy;
        if (wy < a->mcy - a->cy - a->half || wy > a->mcy - a->cy + a->half) continue;
        for (int px = 0; px < n; px++)
        {
            int i = py * n + px;
            if (a->into[i] == 0) continue;
            float wx = a->x + (float)px * a->step - a->cx;
            // 今の所から元の所へ戻す
            float ox = a->px0 + wx * a->cs - wy * a->sn, oy = a->py0 + wx * a->sn + wy * a->cs;
            int dx = (int)floorf((ox - a->ox) * a->ppu) - a->dx0, dy = (int)floorf((oy - a->oy) * a->ppu) - a->dy0;
            if (dx < 0 || dy < 0 || dx >= a->dw || dy >= a->dh) continue;
            int d = dy * a->dw + dx;
            if ((a->dry[d >> 3] & (1 << (d & 7))) != 0) a->into[i] = 0;
        }
    }
}
