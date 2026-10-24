#include "mrp_native.h"
#include "digest.h"
#include "capture.h"
#include <math.h>

// 持ち上げた家具の跡を床マスクの床にすると、
// 家具の今いる所を水の床の型から抜く。小数の式は書いた順に計算する

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

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_floor_patch(FloorPatchArgs* a)
{
    Dg d(DgFloorPatch);
    int64_t dryBytes = ((int64_t)a->dw * (a->y1 - a->y0 + 1) + 7) / 8;
    d.InV(a->w); d.InV(a->x0); d.InV(a->y0); d.InV(a->x1); d.InV(a->y1); d.InV(a->ox); d.InV(a->oy); d.InV(a->ppu);
    d.InV(a->rxMin); d.InV(a->ryMin); d.InV(a->sx); d.InV(a->sy); d.InV(a->n); d.InV(a->dw);
    d.In(a->shape, (int64_t)a->n * a->n);
    d.InBits(a->bits, a->w, a->x0, a->y0, a->x1, a->y1);
    d.In(a->dry, dryBytes);
    Cap c(DgFloorPatch);
    // 床マスクは書く範囲の最後の画素を含む所まで (範囲の外は読み書きしない)
    int64_t bitBytes = a->x1 >= a->x0 && a->y1 >= a->y0 ? ((int64_t)a->y1 * a->w + a->x1) / 8 + 1 : 0;
    int64_t shapeBytes = (int64_t)a->n * a->n > 0 ? (int64_t)a->n * a->n : 0;
    if (dryBytes < 0) dryBytes = 0;
    c.RawV(*a);
    c.Blob(a->bits, bitBytes);
    c.Blob(a->shape, shapeBytes);
    c.Blob(a->dry, dryBytes);
    mrp_floor_patch(a);
    d.OutBits(a->bits, a->w, a->x0, a->y0, a->x1, a->y1);
    d.Out(a->dry, dryBytes);
    d.Commit();
    c.Blob(a->bits, bitBytes);
    c.Blob(a->dry, dryBytes);
    c.End();
}

MRP_API void mrp_dg_floor_cut_moved(FloorCutArgs* a)
{
    Dg d(DgFloorCutMoved);
    int64_t n = (int64_t)a->n * a->n;
    d.InV(a->n); d.InV(a->x); d.InV(a->y); d.InV(a->step); d.InV(a->px0); d.InV(a->py0); d.InV(a->cx); d.InV(a->cy); d.InV(a->cs); d.InV(a->sn);
    d.InV(a->mcx); d.InV(a->mcy); d.InV(a->half); d.InV(a->ox); d.InV(a->oy); d.InV(a->ppu); d.InV(a->dx0); d.InV(a->dy0); d.InV(a->dw); d.InV(a->dh);
    d.In(a->dry, ((int64_t)a->dw * a->dh + 7) / 8);
    d.In(a->into, n);
    Cap c(DgFloorCutMoved);
    int64_t dryBytes = ((int64_t)a->dw * a->dh + 7) / 8;
    if (n < 0) n = 0;
    if (dryBytes < 0) dryBytes = 0;
    c.RawV(*a);
    c.Blob(a->into, n);
    c.Blob(a->dry, dryBytes);
    mrp_floor_cut_moved(a);
    d.Out(a->into, n);
    d.Commit();
    c.Blob(a->into, n);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_floor_patch / mrp_dg_floor_cut_moved と同じ)
void rp_floor_patch(Rp& r)
{
    FloorPatchArgs a;
    r.RawV(a);
    int64_t bitsLen = 0, dryLen = 0, wantBitsLen = 0, wantDryLen = 0;
    a.bits = (uint8_t*)r.Blob(&bitsLen);
    a.shape = (const uint8_t*)r.Blob();
    a.dry = (uint8_t*)r.Blob(&dryLen);
    const uint8_t* wantBits = (const uint8_t*)r.Blob(&wantBitsLen);
    const uint8_t* wantDry = (const uint8_t*)r.Blob(&wantDryLen);
    if (!r.ok) return;
    if (wantBitsLen != bitsLen || wantDryLen != dryLen) { r.Fail("patch blobs %lld/%lld %lld/%lld", (long long)bitsLen, (long long)wantBitsLen, (long long)dryLen, (long long)wantDryLen); return; }
    mrp_floor_patch(&a);
    r.Same("bits", a.bits, wantBits, bitsLen);
    r.Same("dry", a.dry, wantDry, dryLen);
}
static RpReg reg_floor_patch(DgFloorPatch, rp_floor_patch);

void rp_floor_cut_moved(Rp& r)
{
    FloorCutArgs a;
    r.RawV(a);
    int64_t intoLen = 0, wantLen = 0;
    a.into = (uint8_t*)r.Blob(&intoLen);
    a.dry = (const uint8_t*)r.Blob();
    const uint8_t* want = (const uint8_t*)r.Blob(&wantLen);
    if (!r.ok) return;
    if (wantLen != intoLen) { r.Fail("into %lld/%lld bytes", (long long)intoLen, (long long)wantLen); return; }
    mrp_floor_cut_moved(&a);
    r.Same("into", a.into, want, intoLen);
}
static RpReg reg_floor_cut_moved(DgFloorCutMoved, rp_floor_cut_moved);
#endif
