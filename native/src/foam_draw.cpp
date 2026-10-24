#include "mrp_native.h"
#include "digest.h"
#include "capture.h"
#include <math.h>

// 補修フォームの栓 1 枚の絵 (src/Terrain/FoamArt.cs の Draw と同じ結果)。
// 玉の場を 2 画素おきの格子に足し込み → 画素へ補間 → 喉の形で切る → 高さから法線を出して色を塗る。小数の式の順番は C# と同じ

struct FoamArgs
{
    float* f; float* g; float* hs;      // 場 (w × h)・格子 (gw × gh)・高さ (w × h)
    uint8_t* px;                        // 色 (r g b a)
    const uint8_t* vis;                 // 穴から見える画素 (null = 全部)
    const uint8_t* pores;               // 気泡の穴
    const float* balls; int32_t ballN;  // 描く玉 (u v r start を 4 個ずつ・隠れた玉は除いて元の順)
    int32_t w, h;
    float u0, v0, reach, vmin, vmax, bleed, umax;
    int32_t softInner;
    float p;
};

static const int Ppu = 64;
static const float GrowShare = 0.3f, CureFrom = 0.75f, CureTo = 1.6f, Thresh = 0.42f;

static inline float clampf(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
static inline int clampi(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
static inline int imin(int a, int b) { return a < b ? a : b; }
static inline int imax(int a, int b) { return a > b ? a : b; }

static inline float height(float f)
{
    float t = clampf((f - Thresh) / (Thresh * 1.6f), 0.0f, 1.0f);
    return sqrtf(t * (2.0f - t));
}

MRP_API void mrp_foam_draw(FoamArgs* a)
{
    int w = a->w, h = a->h;
    float* F = a->f;
    const float inv = 1.0f / Ppu;
    int gw = (w + 2) >> 1, gh = (h + 2) >> 1;
    float* G = a->g;
    for (int i = 0; i < gw * gh; i++) G[i] = 0.0f;
    const float ginv = 2.0f / Ppu;
    float p = a->p;
    for (int b = 0; b < a->ballN; b++)
    {
        float bu = a->balls[b * 4], bv = a->balls[b * 4 + 1], br = a->balls[b * 4 + 2], bstart = a->balls[b * 4 + 3];
        float k = (p - bstart) / GrowShare;
        if (k <= 0.0f) continue;
        float s;
        if (k >= 1.0f) s = 1.0f + 0.1f * clampf((p - bstart - GrowShare) / 0.6f, 0.0f, 1.0f);
        else
        {
            float c1 = 1.2f, c3 = c1 + 1.0f, q = k - 1.0f;
            s = 1.0f + c3 * q * q * q + c1 * q * q;
        }
        float r = br * s;
        if (r <= 0.005f) continue;
        float reach = r * a->reach, inv4 = 1.0f / (4.0f * r * r);
        float cu = bu - a->u0 - 0.5f * inv, cv = bv - a->v0 - 0.5f * inv;
        int x0 = imax(0, (int)((cu - reach) * (Ppu / 2.0f))), x1 = imin(gw - 1, (int)((cu + reach) * (Ppu / 2.0f)) + 1);
        int y0 = imax(0, (int)((cv - reach) * (Ppu / 2.0f))), y1 = imin(gh - 1, (int)((cv + reach) * (Ppu / 2.0f)) + 1);
        for (int y = y0; y <= y1; y++)
        {
            float dv = y * ginv - cv;
            float rest = 1.0f - dv * dv * inv4;
            if (rest <= 0.0f) continue;
            int row = y * gw;
            for (int x = x0; x <= x1; x++)
            {
                float du = x * ginv - cu;
                float q = rest - du * du * inv4;
                if (q > 0.0f) G[row + x] += q * q * q;
            }
        }
    }
    for (int y = 0; y < h; y++)
    {
        int gy = y >> 1, row = y * w;
        int g0 = gy * gw, g1 = imin(gy + 1, gh - 1) * gw;
        if ((y & 1) == 0)
            for (int x = 0; x < w; x++)
            {
                int gx = x >> 1;
                F[row + x] = (x & 1) == 0 ? G[g0 + gx] : 0.5f * (G[g0 + gx] + G[g0 + imin(gx + 1, gw - 1)]);
            }
        else
            for (int x = 0; x < w; x++)
            {
                int gx = x >> 1, gx1 = imin(gx + 1, gw - 1);
                F[row + x] = (x & 1) == 0 ? 0.5f * (G[g0 + gx] + G[g1 + gx])
                    : 0.25f * (G[g0 + gx] + G[g0 + gx1] + G[g1 + gx] + G[g1 + gx1]);
            }
    }

    // 喉の形で切る
    const float Rim = 1.5f / Ppu;
    const uint8_t* vis = a->vis;
    float vmin = a->vmin, vmax = a->vmax, bleed = a->bleed, umax = a->umax;
    for (int y = 0; y < h; y++)
    {
        float v = a->v0 + (y + 0.5f) * inv;
        float ev = a->softInner ? vmax - v : fminf(v - vmin, vmax - v);
        int row = y * w;
        for (int x = 0; x < w; x++)
        {
            if (vis && vis[row + x] == 0) continue;
            float u = a->u0 + (x + 0.5f) * inv;
            float e = fminf(ev, fminf(u + bleed, umax - u));
            float cap = e <= 0.0f ? 0.0f : e < Rim ? Thresh + 0.001f : Thresh + (e - Rim) * 6.0f;
            if (F[row + x] > cap) F[row + x] = cap;
        }
    }

    float cure = clampf((p - CureFrom) / (CureTo - CureFrom), 0.0f, 1.0f);
    float wet = 1.0f - cure;
    float br = 238.0f + (205.0f - 238.0f) * cure, bg = 222.0f + (178.0f - 222.0f) * cure, bb = 168.0f + (118.0f - 168.0f) * cure;
    const float lx = -0.45f, ly = 0.55f, lz = 0.70f;
    float hl = sqrtf(lx * lx + ly * ly + (lz + 1.0f) * (lz + 1.0f));
    float hx = lx / hl, hy = ly / hl, hz = (lz + 1.0f) / hl;
    uint8_t* px = a->px;
    float* Hs = a->hs;
    for (int i = 0; i < w * h; i++) Hs[i] = height(F[i]);
    const uint8_t* pores = a->pores;
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        int i = y * w + x, o = i * 4;
        float f = F[i];
        if (vis && vis[i] == 0) { px[o + 3] = 0; continue; }
        if (f < Thresh * 0.9f)
        {
            int sx = x - 2, sy = y + 3;
            float fs = sx >= 0 && sy < h ? F[sy * w + sx] : 0.0f;
            px[o] = 20; px[o + 1] = 16; px[o + 2] = 10;
            px[o + 3] = fs >= Thresh ? (uint8_t)90 : (uint8_t)0;
            continue;
        }
        float alpha = clampf((f - Thresh * 0.9f) / (Thresh * 0.1f), 0.0f, 1.0f);
        float hc = Hs[i];
        float hxp = x + 1 < w ? Hs[i + 1] : hc, hxm = x > 0 ? Hs[i - 1] : hc;
        float hyp = y + 1 < h ? Hs[i + w] : hc, hym = y > 0 ? Hs[i - w] : hc;
        float gx = (hxm - hxp) * 9.0f, gy = (hym - hyp) * 9.0f, gl = sqrtf(gx * gx + gy * gy + 1.0f);
        float nx = gx / gl, ny = gy / gl, nz = 1.0f / gl;
        float diff = clampf(nx * lx + ny * ly + nz * lz, 0.0f, 1.0f);
        float shade = (0.5f + 0.6f * diff) * (0.72f + 0.28f * hc);
        uint8_t pore = pores[i];
        if (pore != 0 && hc > 0.1f) shade *= 1.0f - (0.1f + 0.35f * cure) * (pore / 255.0f);
        float r = br * shade, g = bg * shade, b = bb * shade;
        float sp = clampf(nx * hx + ny * hy + nz * hz, 0.0f, 1.0f);
        sp *= sp; sp *= sp; sp *= sp; sp *= sp;
        sp *= sp;
        float sheen = sp;
        float broad = clampf(nx * hx + ny * hy + nz * hz, 0.0f, 1.0f);
        broad *= broad; broad *= broad; broad *= broad;
        float gloss = (0.9f * wet + 0.08f) * sheen + 0.3f * wet * broad;
        r += (255.0f - r) * gloss; g += (255.0f - g) * gloss; b += (255.0f - b) * gloss;
        if (hc < 0.07f) { r = 70.0f; g = 54.0f; b = 32.0f; }
        px[o] = (uint8_t)clampi((int)r, 0, 255);
        px[o + 1] = (uint8_t)clampi((int)g, 0, 255);
        px[o + 2] = (uint8_t)clampi((int)b, 0, 255);
        px[o + 3] = (uint8_t)(int)(alpha * 255.0f);
    }
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_foam_draw(FoamArgs* a)
{
    Dg d(DgFoamDraw);
    int64_t n = (int64_t)a->w * a->h;
    d.InV(a->w); d.InV(a->h); d.InV(a->u0); d.InV(a->v0); d.InV(a->reach); d.InV(a->vmin); d.InV(a->vmax); d.InV(a->bleed); d.InV(a->umax);
    d.InV(a->softInner); d.InV(a->p); d.InV(a->ballN);
    d.In(a->vis, a->vis ? n : 0); d.In(a->pores, n); d.In(a->balls, (int64_t)a->ballN * 16);
    d.In(a->px, n * 4);
    Cap c(DgFoamDraw);
    int64_t gn = (int64_t)((a->w + 2) >> 1) * ((a->h + 2) >> 1);
    c.RawV(*a);
    c.Blob(a->vis, a->vis ? n : 0);
    c.Blob(a->pores, n);
    c.Blob(a->balls, (int64_t)a->ballN * 16);
    c.Blob(a->f, n * 4);
    c.Blob(a->g, gn * 4);
    c.Blob(a->hs, n * 4);
    c.Blob(a->px, n * 4);
    mrp_foam_draw(a);
    d.Out(a->px, n * 4);
    d.Commit();
    c.Blob(a->f, n * 4);
    c.Blob(a->g, gn * 4);
    c.Blob(a->hs, n * 4);
    c.Blob(a->px, n * 4);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_foam_draw と同じ)
void rp_foam_draw(Rp& r)
{
    FoamArgs a;
    r.RawV(a);
    int64_t n = (int64_t)a.w * a.h, gn = (int64_t)((a.w + 2) >> 1) * ((a.h + 2) >> 1);
    int64_t lv, lp, lb, lf, lg, lh, lx;
    a.vis = (const uint8_t*)r.Blob(&lv);
    a.pores = (const uint8_t*)r.Blob(&lp);
    a.balls = (const float*)r.Blob(&lb);
    a.f = (float*)r.Blob(&lf);
    a.g = (float*)r.Blob(&lg);
    a.hs = (float*)r.Blob(&lh);
    a.px = (uint8_t*)r.Blob(&lx);
    if (!r.ok) return;
    if (lp != n || lb != (int64_t)a.ballN * 16 || lf != n * 4 || lg != gn * 4 || lh != n * 4 || lx != n * 4) { r.Fail("foam_draw: input blob sizes differ"); return; }
    mrp_foam_draw(&a);
    int64_t wf, wg, wh, wx;
    const void* f = r.Blob(&wf);
    const void* g = r.Blob(&wg);
    const void* h = r.Blob(&wh);
    const void* x = r.Blob(&wx);
    if (!r.ok) return;
    if (wf != n * 4 || wg != gn * 4 || wh != n * 4 || wx != n * 4) { r.Fail("foam_draw: output blob sizes differ"); return; }
    r.Same("f", a.f, f, n * 4);
    r.Same("g", a.g, g, gn * 4);
    r.Same("hs", a.hs, h, n * 4);
    r.Same("px", a.px, x, n * 4);
}
static RpReg reg_foam_draw(DgFoamDraw, rp_foam_draw);
#endif
