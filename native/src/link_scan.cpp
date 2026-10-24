#include "mrp_native.h"
#include "digest.h"
#include "capture.h"

// 水の縁の走査。
// 範囲の升の前の縁の印を消し、開いている升の向きのうち隣へ流れない向きを光線の待ち行列へ升の番号順に並べる。
// はしごの上の升の横の向きは並べない・ガラスの手すりの向きは −1 − (升 × 4 + 向き) で並べる (光線を引かない印)。
// 前に落ちる縁/段差の縁があった升は cleared へ並べる (C# が表と縁の枠から同じ升を消す)。dry = 1 は印を消さずに答えだけ出す (照合用)

struct LinkScanArgs
{
    const uint8_t* open; const int8_t* lvl; const uint8_t* edge;
    const uint8_t* rim;                             // 升ごと: 0〜3 ビット = その向きにガラスの手すり・4 ビット = はしごの上
    uint8_t* fallMask; uint8_t* stepMask; uint8_t* seaMask; uint8_t* lipMask;
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

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_link_scan(LinkScanArgs* a)
{
    Dg d(DgLinkScan);
    // 地図の高さは渡ってこないので範囲の行だけ (範囲は地図の中)
    int rows = a->y1 + 1;
    d.InV(a->w); d.InV(a->x0); d.InV(a->y0); d.InV(a->x1); d.InV(a->y1); d.InV(a->dry); d.InV(a->qCap); d.InV(a->clearedCap);
    d.In2D(a->open, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.In2D(a->lvl, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.In2D(a->edge, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.In2D(a->rim, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.In2D(a->fallMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.In2D(a->stepMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.In2D(a->seaMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    Cap c(DgLinkScan);
    // open と edge は範囲の外周 1 升も読む (範囲は C# が地図の内側 1 升に切ってある)
    int rows2 = a->y1 + 2;
    c.RawV(*a);
    c.Rect(a->open, a->w, rows2, 1, a->x0 - 1, a->y0 - 1, a->x1 + 1, a->y1 + 1);
    c.Rect(a->lvl, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->edge, a->w, rows2, 1, a->x0 - 1, a->y0 - 1, a->x1 + 1, a->y1 + 1);
    c.Rect(a->rim, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->fallMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->stepMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->seaMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->lipMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Blob(a->q, (int64_t)a->qCap * 4);
    c.Blob(a->cleared, (int64_t)a->clearedCap * 4);
    mrp_link_scan(a);
    d.Out2D(a->fallMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.Out2D(a->stepMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.Out2D(a->seaMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.Out2D(a->lipMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    d.OutV(a->qN); d.Out(a->q, (int64_t)(a->qN < a->qCap ? a->qN : a->qCap) * 4);
    d.OutV(a->clearedN); d.Out(a->cleared, (int64_t)(a->clearedN < a->clearedCap ? a->clearedN : a->clearedCap) * 4);
    d.OutV(a->seaDirty);
    d.Commit();
    c.Rect(a->fallMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->stepMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->seaMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Rect(a->lipMask, a->w, rows, 1, a->x0, a->y0, a->x1, a->y1);
    c.Blob(a->q, (int64_t)a->qCap * 4);
    c.Blob(a->cleared, (int64_t)a->clearedCap * 4);
    c.RawV(*a);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_link_scan と同じ)
void rp_link_scan(Rp& r)
{
    LinkScanArgs a, want;
    r.RawV(a);
    if (!r.ok) return;
    int rows = a.y1 + 1, rows2 = a.y1 + 2;
    a.open = (const uint8_t*)r.RectInto(a.w, rows2, 1, a.x0 - 1, a.y0 - 1, a.x1 + 1, a.y1 + 1, 0xA5);
    a.lvl = (const int8_t*)r.RectInto(a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    a.edge = (const uint8_t*)r.RectInto(a.w, rows2, 1, a.x0 - 1, a.y0 - 1, a.x1 + 1, a.y1 + 1, 0xA5);
    a.rim = (const uint8_t*)r.RectInto(a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    a.fallMask = (uint8_t*)r.RectInto(a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    a.stepMask = (uint8_t*)r.RectInto(a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    a.seaMask = (uint8_t*)r.RectInto(a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    a.lipMask = (uint8_t*)r.RectInto(a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    int64_t qLen = 0, cLen = 0;
    a.q = (int32_t*)r.Blob(&qLen);
    a.cleared = (int32_t*)r.Blob(&cLen);
    if (!r.ok) return;
    if (qLen != (int64_t)a.qCap * 4 || cLen != (int64_t)a.clearedCap * 4) { r.Fail("q/cleared size %lld/%lld", (long long)qLen, (long long)cLen); return; }
    mrp_link_scan(&a);
    r.RectSame("fallMask", a.fallMask, a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    r.RectSame("stepMask", a.stepMask, a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    r.RectSame("seaMask", a.seaMask, a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    r.RectSame("lipMask", a.lipMask, a.w, rows, 1, a.x0, a.y0, a.x1, a.y1, 0xA5);
    int64_t wq = 0, wc = 0;
    const void* postQ = r.Blob(&wq);
    const void* postC = r.Blob(&wc);
    if (!r.ok) return;
    if (wq != qLen || wc != cLen) { r.Fail("post q/cleared size %lld/%lld", (long long)wq, (long long)wc); return; }
    if (qLen > 0) r.Same("q", a.q, postQ, qLen);
    if (cLen > 0) r.Same("cleared", a.cleared, postC, cLen);
    r.RawV(want);
    r.Same("qN", &a.qN, &want.qN, 4);
    r.Same("clearedN", &a.clearedN, &want.clearedN, 4);
    r.Same("seaDirty", &a.seaDirty, &want.seaDirty, 4);
}
static RpReg reg_link_scan(DgLinkScan, rp_link_scan);
#endif
