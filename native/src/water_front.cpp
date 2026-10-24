#include "mrp_native.h"
#include "digest.h"
#include "capture.h"
#include <math.h>

// 水の影の写しの抜き型: 家具の絵の網目の三角形を、画素の真ん中が入る所だけ 255 で塗る
// (src/Terrain/WaterArt.cs の FillTrisCs と同じ結果)。頂点は抜き型の画素の座標へ直してから渡す。
// 絵の不透明な所の型 (mask) があれば、三角形の中でも型の上で透明な画素は塗らない (網目が四角の絵は透明な所まで網目に入る)。
// 小数の式の順番は C# と同じ

struct FrontFillArgs
{
    const float* px; const float* py;   // 頂点 (抜き型の画素の座標・画素の真ん中が整数)
    const uint16_t* tri; int32_t triN;  // 三角形の頂点の番号 (3 個ずつ・triN = 番号の数)
    int32_t m;                          // 抜き型の一辺の画素数
    uint8_t* buf;                       // 塗る先 (m × m)
    int32_t any;                        // 出力: 1 画素でも塗ったか
    const float* mu; const float* mv;   // 頂点の型の上の座標 (型の画素単位・左下が 0)。mask が無ければ使わない
    const uint8_t* mask;                // 絵の不透明な所の型 (mw × mh・下の行から・0 = 透明)。nullptr = 型なし
    int32_t mw, mh;
};

static inline int imin(int a, int b) { return a < b ? a : b; }
static inline int imax(int a, int b) { return a > b ? a : b; }

MRP_API void mrp_front_fill(FrontFillArgs* a)
{
    const float* px = a->px; const float* py = a->py;
    int m = a->m;
    uint8_t* buf = a->buf;
    const uint8_t* mask = a->mask;
    int mw = a->mw, mh = a->mh;
    int any = 0;
    for (int n = 0; n + 2 < a->triN; n += 3)
    {
        int ia = a->tri[n], ib = a->tri[n + 1], ic = a->tri[n + 2];
        float ax = px[ia], ay = py[ia], bx = px[ib], by = py[ib], cx = px[ic], cy = py[ic];
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (fabsf(area) < 1e-6f) continue;
        int x0 = imax(0, (int)ceilf(fminf(ax, fminf(bx, cx)))), x1 = imin(m - 1, (int)floorf(fmaxf(ax, fmaxf(bx, cx))));
        int y0 = imax(0, (int)ceilf(fminf(ay, fminf(by, cy)))), y1 = imin(m - 1, (int)floorf(fmaxf(ay, fmaxf(by, cy))));
        float inv = 1.0f / area;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float fx = (float)x, fy = (float)y;
            float w0 = ((bx - fx) * (cy - fy) - (by - fy) * (cx - fx)) * inv;
            float w1 = ((cx - fx) * (ay - fy) - (cy - fy) * (ax - fx)) * inv;
            if (w0 < 0.0f || w1 < 0.0f || w0 + w1 > 1.0f) continue;
            if (mask)
            {
                float w2 = 1.0f - w0 - w1;
                float u = w0 * a->mu[ia] + w1 * a->mu[ib] + w2 * a->mu[ic];
                float v = w0 * a->mv[ia] + w1 * a->mv[ib] + w2 * a->mv[ic];
                int iu = imin(mw - 1, imax(0, (int)floorf(u))), iv = imin(mh - 1, imax(0, (int)floorf(v)));
                if (!mask[iv * mw + iu]) continue;
            }
            buf[y * m + x] = 255;
            any = 1;
        }
    }
    a->any = any;
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_front_fill(FrontFillArgs* a)
{
    Dg d(DgFrontFill);
    int verts = 0;
    for (int i = 0; i < a->triN; i++) if (a->tri[i] + 1 > verts) verts = a->tri[i] + 1;
    int64_t mm = (int64_t)a->m * a->m;
    d.InV(a->triN); d.InV(a->m); d.InV(a->mw); d.InV(a->mh);
    d.In(a->tri, (int64_t)a->triN * 2); d.In(a->px, (int64_t)verts * 4); d.In(a->py, (int64_t)verts * 4);
    if (a->mask) { d.In(a->mu, (int64_t)verts * 4); d.In(a->mv, (int64_t)verts * 4); d.In(a->mask, (int64_t)a->mw * a->mh); }
    d.In(a->buf, mm);
    Cap c(DgFrontFill);
    int64_t vb = (int64_t)verts * 4, maskB = (int64_t)a->mw * a->mh;
    c.RawV(*a);
    c.Blob(a->tri, (int64_t)a->triN * 2);
    c.Blob(a->px, vb);
    c.Blob(a->py, vb);
    c.Blob(a->mask ? a->mu : nullptr, vb);
    c.Blob(a->mask ? a->mv : nullptr, vb);
    c.Blob(a->mask, maskB);
    c.Blob(a->buf, mm);
    mrp_front_fill(a);
    d.Out(a->buf, mm); d.OutV(a->any);
    d.Commit();
    c.Blob(a->buf, mm);
    c.RawV(*a);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_front_fill と同じ)
void rp_front_fill(Rp& r)
{
    FrontFillArgs a, want;
    r.RawV(a);
    int64_t lt, lx, ly, lu, lv, lm, lb;
    a.tri = (const uint16_t*)r.Blob(&lt);
    a.px = (const float*)r.Blob(&lx);
    a.py = (const float*)r.Blob(&ly);
    a.mu = (const float*)r.Blob(&lu);
    a.mv = (const float*)r.Blob(&lv);
    a.mask = (const uint8_t*)r.Blob(&lm);
    a.buf = (uint8_t*)r.Blob(&lb);
    if (!r.ok) return;
    int64_t mm = (int64_t)a.m * a.m;
    if (lt != (int64_t)a.triN * 2 || lx != ly || lb != mm || (a.mask && (lu != lx || lv != lx || lm != (int64_t)a.mw * a.mh)))
    { r.Fail("front_fill: input blob sizes differ"); return; }
    mrp_front_fill(&a);
    int64_t wb;
    const void* b = r.Blob(&wb);
    r.RawV(want);
    if (!r.ok) return;
    if (wb != mm) { r.Fail("front_fill: output blob size differs"); return; }
    r.Same("buf", a.buf, b, mm);
    r.Same("any", &a.any, &want.any, 4);
}
static RpReg reg_front_fill(DgFrontFill, rp_front_fill);
#endif
