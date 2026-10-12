#include "mrp_native.h"

// 水の縁の走査 (src/Terrain/WaterSim.cs の LinkPass の前半と同じ結果)。
// 範囲の升の前の縁の印を消し、開いている升の向きのうち隣へ流れない向きを光線の待ち行列へ升の番号順に並べる。
// はしごの上の升の横の向きは並べない・ガラスの手すりの向きは −1 − (升 × 4 + 向き) で並べる (光線を引かない印)。
// 前に落ちる縁/段差の縁があった升は cleared へ並べる (C# が表から同じ升を消す)。dry = 1 は印を消さずに答えだけ出す (照合用)

struct LinkScanArgs
{
    const uint8_t* open; const int8_t* lvl; const uint8_t* edge;
    const uint8_t* rim;                             // 升ごと: 0〜3 ビット = その向きにガラスの手すり・4 ビット = はしごの上
    uint8_t* fallMask; uint8_t* stepMask; uint8_t* seaMask; uint8_t* lipMask;
    uint8_t* barB; int16_t* barH;                   // 升 × 4 + 向き
    int32_t w, x0, y0, x1, y1, dry;
    int32_t* q; int32_t qCap, qN;                   // 戻り: 光線の待ち行列 (容量を超えた分は数えるだけ・同じ範囲を dry で引き直せば同じ並び)
    int32_t* cleared; int32_t clearedCap, clearedN; // 戻り: 升 × 16 + 落ちる縁の印 + 段差の印 × 2 (落ちる縁と段差の表の件数より多くはならない)
    int32_t seaDirty;                               // 戻り: 海の印を消した
};

static const int NoLevel = -128, VoidLevel = -2;
static const int Nx[4] = { 1, -1, 0, 0 }, Ny[4] = { 0, 0, 1, -1 }, Opp[4] = { 1, 0, 3, 2 };

MRP_API void mrp_link_scan(LinkScanArgs* a)
{
    const int w = a->w;
    int qN = 0, cN = 0, sea = 0;
    for (int y = a->y0; y <= a->y1; y++)
    for (int x = a->x0; x <= a->x1; x++)
    {
        const int k = y * w + x;
        const int fm = a->fallMask[k], sm = a->stepMask[k];
        if (fm != 0 || sm != 0)
        {
            if (cN < a->clearedCap) a->cleared[cN] = k * 16 + (fm != 0 ? 1 : 0) + (sm != 0 ? 2 : 0);
            cN++;
        }
        if (!a->dry)
        {
            if (fm != 0)
            {
                a->fallMask[k] = 0;
                a->lipMask[k] = 0;
                for (int d = 0; d < 4; d++) { a->barB[k * 4 + d] = 0; a->barH[k * 4 + d] = 0; }
            }
            if (sm != 0) a->stepMask[k] = 0;
            if (a->seaMask[k] != 0) { a->seaMask[k] = 0; sea = 1; }
        }
        const int lk = a->lvl[k];
        if (a->open[k] == 0 || lk == NoLevel || lk == VoidLevel) continue;
        const int rim = a->rim[k], ek = a->edge[k];
        for (int d = 0; d < 4; d++)
        {
            const int j = k + Nx[d] + Ny[d] * w;
            if (a->open[j] != 0 && (ek >> d & 1) != 0 && (a->edge[j] >> Opp[d] & 1) != 0) continue;
            if (Nx[d] != 0 && (rim & 16) != 0) continue;
            if (qN < a->qCap) a->q[qN] = (rim >> d & 1) != 0 ? -1 - (k * 4 + d) : k * 4 + d;
            qN++;
        }
    }
    a->qN = qN;
    a->clearedN = cN;
    a->seaDirty = sea;
}
