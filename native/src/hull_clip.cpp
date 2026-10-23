#include "mrp_native.h"
#include "digest.h"
#include <math.h>

// 船体のメッシュの三角形から喉の塊を引く (src/Terrain/HullThroat.cs の Apply と同じ結果)。
// 三角形ごとに、喉を輪郭の太さだけ広げた塊 (grown) の外はそのまま残し、広げた塊の中で喉そのもの (holes) の外は輪郭の色で残す。
// 小数の式の順番は C# と同じにする (縮約はビルドで切っている)

struct HullClipArgs
{
    float* v; uint8_t* c; float* u;         // 頂点 (x y z)・色 (r g b a)・uv (x y)。vn 個の後ろへ足す
    int32_t vn, vcap;
    const int32_t* triIn; int32_t triInN;   // 元の三角形 (頂点の番号 3 個ずつ)
    int32_t* triOut; int32_t triOutN, triOutCap;
    const float* holes; const int32_t* holeN; // 喉の塊 (メッシュの座標・反時計回りの凸多角形の点を続けて)・塊ごとの点の数
    const float* grown; const int32_t* grownN;
    int32_t holeCount;
    float minX, minY, maxX, maxY;            // 広げた塊全部の範囲
    int32_t outR, outG, outB;                // 輪郭の色
    int32_t over;                            // 足りなかった (頂点・三角形の置き場 = 1・塊の置き場 = 2)。足りなければ何も書き換えない
};

static const int MaxPts = 32, MaxPolys = 2048;

struct Poly { int32_t n; float x[MaxPts], y[MaxPts]; };

// 塊の置き場 (メインスレッドからだけ呼ぶ)
static Poly g_pool[3][MaxPolys];

struct PolyList { Poly* p; int32_t n; };

static float area(const Poly& p)
{
    float s = 0.0f;
    for (int i = 0, j = p.n - 1; i < p.n; j = i++) s += p.x[j] * p.y[i] - p.x[i] * p.y[j];
    return s * 0.5f;
}

static float side(float ax, float ay, float bx, float by, float qx, float qy) { return (bx - ax) * (qy - ay) - (by - ay) * (qx - ax); }

// s を半平面 (a→b の左側 = keepLeft・右側 = !keepLeft) で切る。3 点未満なら false
static bool half_plane(const Poly& s, float ax, float ay, float bx, float by, bool keepLeft, Poly& o, int32_t* over)
{
    o.n = 0;
    for (int i = 0; i < s.n; i++)
    {
        int j = (i + 1) % s.n;
        float cx = s.x[i], cy = s.y[i], nx = s.x[j], ny = s.y[j];
        float dc = side(ax, ay, bx, by, cx, cy), dn = side(ax, ay, bx, by, nx, ny);
        if (!keepLeft) { dc = -dc; dn = -dn; }
        if (dc >= 0.0f)
        {
            if (o.n >= MaxPts) { *over |= 2; return false; }
            o.x[o.n] = cx; o.y[o.n] = cy; o.n++;
        }
        if ((dc > 0.0f && dn < 0.0f) || (dc < 0.0f && dn > 0.0f))
        {
            float t = dc / (dc - dn);
            if (o.n >= MaxPts) { *over |= 2; return false; }
            o.x[o.n] = cx + (nx - cx) * t; o.y[o.n] = cy + (ny - cy) * t; o.n++;
        }
    }
    return o.n >= 3;
}

static void load(Poly& p, const float* pts, int n)
{
    p.n = n;
    for (int i = 0; i < n; i++) { p.x[i] = pts[i * 2]; p.y[i] = pts[i * 2 + 1]; }
}

static bool intersect(const Poly& s, const float* h, int hn, Poly& out, int32_t* over)
{
    Poly a = s, b;
    for (int i = 0; i < hn; i++)
    {
        int j = (i + 1) % hn;
        if (!half_plane(a, h[i * 2], h[i * 2 + 1], h[j * 2], h[j * 2 + 1], true, b, over)) return false;
        a = b;
    }
    if (!(area(a) > 1e-9f)) return false;
    out = a;
    return true;
}

static void push(PolyList& l, const Poly& p, int32_t* over)
{
    if (l.n >= MaxPolys) { *over |= 2; return; }
    l.p[l.n++] = p;
}

// s − h を凸の塊に分けて into へ足す
static void subtract(const Poly& s, const float* h, int hn, PolyList& into, int32_t* over)
{
    Poly rest = s, outside, next;
    for (int i = 0; i < hn; i++)
    {
        int j = (i + 1) % hn;
        float ax = h[i * 2], ay = h[i * 2 + 1], bx = h[j * 2], by = h[j * 2 + 1];
        if (half_plane(rest, ax, ay, bx, by, false, outside, over) && area(outside) > 1e-9f) push(into, outside, over);
        if (!half_plane(rest, ax, ay, bx, by, true, next, over)) return;
        rest = next;
    }
}

// 点の並びの範囲 (x0 y0 x1 y1)
static void bounds(const float* pts, int n, float* b)
{
    b[0] = b[2] = pts[0]; b[1] = b[3] = pts[1];
    for (int i = 1; i < n; i++)
    {
        b[0] = fminf(b[0], pts[i * 2]); b[1] = fminf(b[1], pts[i * 2 + 1]);
        b[2] = fmaxf(b[2], pts[i * 2]); b[3] = fmaxf(b[3], pts[i * 2 + 1]);
    }
}

static bool apart(const float* b, float x0, float y0, float x1, float y1) { return x1 < b[0] || x0 > b[2] || y1 < b[1] || y0 > b[3]; }

// h か s のどれかの辺の外側 (右) に相手が全部ある = 重ならない (s は割らずにそのまま残す)。両方が反時計回りの凸の時だけ正しい
static bool separated(const Poly& s, const float* h, int hn)
{
    for (int i = 0; i < hn; i++)
    {
        int j = (i + 1) % hn;
        float ax = h[i * 2], ay = h[i * 2 + 1], bx = h[j * 2], by = h[j * 2 + 1];
        int k = 0;
        while (k < s.n && side(ax, ay, bx, by, s.x[k], s.y[k]) <= 0.0f) k++;
        if (k == s.n) return true;
    }
    for (int i = 0; i < s.n; i++)
    {
        int j = (i + 1) % s.n;
        int k = 0;
        while (k < hn && side(s.x[i], s.y[i], s.x[j], s.y[j], h[k * 2], h[k * 2 + 1]) <= 0.0f) k++;
        if (k == hn) return true;
    }
    return false;
}

static uint8_t mix(uint8_t a, uint8_t b, uint8_t c, float wa, float wb, float wc)
{
    float r = nearbyintf((float)a * wa + (float)b * wb + (float)c * wc);
    int v = (int)r;
    return (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v);
}

static void emit(HullClipArgs* a, const Poly& poly, int i0, int i1, int i2, bool outline)
{
    if (poly.n < 3 || area(poly) < 1e-7f) return;
    const float* v0 = a->v + i0 * 3; const float* v1 = a->v + i1 * 3; const float* v2 = a->v + i2 * 3;
    float det = (v1[1] - v2[1]) * (v0[0] - v2[0]) + (v2[0] - v1[0]) * (v0[1] - v2[1]);
    if (fabsf(det) < 1e-12f) return;
    if (a->vn + poly.n > a->vcap || a->triOutN + (poly.n - 2) * 3 > a->triOutCap) { a->over |= 1; return; }
    const uint8_t* c0 = a->c + i0 * 4; const uint8_t* c1 = a->c + i1 * 4; const uint8_t* c2 = a->c + i2 * 4;
    const float* u0 = a->u + i0 * 2; const float* u1 = a->u + i1 * 2; const float* u2 = a->u + i2 * 2;
    int base = a->vn;
    for (int k = 0; k < poly.n; k++)
    {
        float px = poly.x[k], py = poly.y[k];
        float w0 = ((v1[1] - v2[1]) * (px - v2[0]) + (v2[0] - v1[0]) * (py - v2[1])) / det;
        float w1 = ((v2[1] - v0[1]) * (px - v2[0]) + (v0[0] - v2[0]) * (py - v2[1])) / det;
        float w2 = 1.0f - w0 - w1;
        float* v = a->v + a->vn * 3;
        v[0] = px; v[1] = py; v[2] = v0[2] * w0 + v1[2] * w1 + v2[2] * w2;
        uint8_t* c = a->c + a->vn * 4;
        if (outline) { c[0] = (uint8_t)a->outR; c[1] = (uint8_t)a->outG; c[2] = (uint8_t)a->outB; c[3] = c0[3]; }
        else
        {
            c[0] = mix(c0[0], c1[0], c2[0], w0, w1, w2); c[1] = mix(c0[1], c1[1], c2[1], w0, w1, w2);
            c[2] = mix(c0[2], c1[2], c2[2], w0, w1, w2); c[3] = mix(c0[3], c1[3], c2[3], w0, w1, w2);
        }
        float* u = a->u + a->vn * 2;
        u[0] = u0[0] * w0 + u1[0] * w1 + u2[0] * w2;
        u[1] = u0[1] * w0 + u1[1] * w1 + u2[1] * w2;
        a->vn++;
    }
    for (int k = 1; k + 1 < poly.n; k++)
    {
        int32_t* t = a->triOut + a->triOutN;
        t[0] = base; t[1] = base + k; t[2] = base + k + 1;
        a->triOutN += 3;
    }
}

static void keep_tri(HullClipArgs* a, int i0, int i1, int i2)
{
    if (a->triOutN + 3 > a->triOutCap) { a->over |= 1; return; }
    int32_t* t = a->triOut + a->triOutN;
    t[0] = i0; t[1] = i1; t[2] = i2;
    a->triOutN += 3;
}

MRP_API void mrp_hull_clip(HullClipArgs* p)
{
    HullClipArgs a = *p;
    a.over = 0;
    a.triOutN = 0;
    // 塊の始まりの位置
    int32_t hOff[256], gOff[256];
    float hBox[256 * 4], gBox[256 * 4];
    if (a.holeCount > 256) { p->over = 2; return; }
    for (int h = 0, ho = 0, go = 0; h < a.holeCount; h++)
    {
        hOff[h] = ho; gOff[h] = go;
        bounds(a.holes + ho, a.holeN[h], hBox + h * 4);
        bounds(a.grown + go, a.grownN[h], gBox + h * 4);
        ho += a.holeN[h] * 2; go += a.grownN[h] * 2;
    }
    PolyList keep = { g_pool[0], 0 }, ring = { g_pool[1], 0 }, tmp = { g_pool[2], 0 };
    for (int k = 0; k + 2 < a.triInN && a.over == 0; k += 3)
    {
        int i0 = a.triIn[k], i1 = a.triIn[k + 1], i2 = a.triIn[k + 2];
        const float* v0 = a.v + i0 * 3; const float* v1 = a.v + i1 * 3; const float* v2 = a.v + i2 * 3;
        float tx0 = fminf(v0[0], fminf(v1[0], v2[0])), tx1 = fmaxf(v0[0], fmaxf(v1[0], v2[0]));
        float ty0 = fminf(v0[1], fminf(v1[1], v2[1])), ty1 = fmaxf(v0[1], fmaxf(v1[1], v2[1]));
        if (tx1 < a.minX || tx0 > a.maxX || ty1 < a.minY || ty0 > a.maxY)
        {
            keep_tri(&a, i0, i1, i2);
            continue;
        }
        Poly tri;
        tri.n = 3;
        tri.x[0] = v0[0]; tri.y[0] = v0[1]; tri.x[1] = v1[0]; tri.y[1] = v1[1]; tri.x[2] = v2[0]; tri.y[2] = v2[1];
        if (area(tri) < 0.0f) { float tx = tri.x[0], ty = tri.y[0]; tri.x[0] = tri.x[2]; tri.y[0] = tri.y[2]; tri.x[2] = tx; tri.y[2] = ty; }
        if (fabsf(area(tri)) < 1e-9f) continue;

        // 広げた喉の外 = そのまま / 中 = 喉そのものを引いた残りが輪郭。三角形と範囲が重ならない塊は見ない (余計に割らない)
        keep.n = 0; push(keep, tri, &a.over);
        ring.n = 0;
        for (int g = 0; g < a.holeCount; g++)
        {
            if (apart(gBox + g * 4, tx0, ty0, tx1, ty1)) continue;
            const float* gp = a.grown + gOff[g];
            int gn = a.grownN[g];
            tmp.n = 0;
            for (int i = 0; i < keep.n; i++)
            {
                if (separated(keep.p[i], gp, gn)) { push(tmp, keep.p[i], &a.over); continue; }
                Poly inside;
                if (intersect(keep.p[i], gp, gn, inside, &a.over)) push(ring, inside, &a.over);
                subtract(keep.p[i], gp, gn, tmp, &a.over);
            }
            PolyList s = keep; keep = tmp; tmp = s;
        }
        if (ring.n == 0) { keep_tri(&a, i0, i1, i2); continue; }
        for (int h = 0; h < a.holeCount; h++)
        {
            if (apart(hBox + h * 4, tx0, ty0, tx1, ty1)) continue;
            tmp.n = 0;
            const float* hp = a.holes + hOff[h];
            for (int i = 0; i < ring.n; i++)
            {
                if (separated(ring.p[i], hp, a.holeN[h])) push(tmp, ring.p[i], &a.over);
                else subtract(ring.p[i], hp, a.holeN[h], tmp, &a.over);
            }
            PolyList s = ring; ring = tmp; tmp = s;
        }
        for (int i = 0; i < keep.n; i++) emit(&a, keep.p[i], i0, i1, i2, false);
        for (int i = 0; i < ring.n; i++) emit(&a, ring.p[i], i0, i1, i2, true);
    }
    if (a.over != 0) { p->over = a.over; return; }
    p->vn = a.vn;
    p->triOutN = a.triOutN;
    p->over = 0;
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_hull_clip(HullClipArgs* p)
{
    Dg d(DgHullClip);
    int64_t holes = 0, grown = 0;
    if (p->holeCount <= 256)
        for (int i = 0; i < p->holeCount; i++) { holes += p->holeN[i]; grown += p->grownN[i]; }
    d.InV(p->vn); d.InV(p->vcap); d.InV(p->triInN); d.InV(p->triOutCap); d.InV(p->holeCount);
    d.InV(p->minX); d.InV(p->minY); d.InV(p->maxX); d.InV(p->maxY); d.InV(p->outR); d.InV(p->outG); d.InV(p->outB);
    d.In(p->v, (int64_t)p->vn * 12); d.In(p->c, (int64_t)p->vn * 4); d.In(p->u, (int64_t)p->vn * 8);
    d.In(p->triIn, (int64_t)p->triInN * 4);
    if (p->holeCount <= 256)
    {
        d.In(p->holeN, (int64_t)p->holeCount * 4); d.In(p->holes, holes * 8);
        d.In(p->grownN, (int64_t)p->holeCount * 4); d.In(p->grown, grown * 8);
    }
    mrp_hull_clip(p);
    d.OutV(p->over);
    if (p->over == 0)
    {
        d.OutV(p->vn); d.Out(p->v, (int64_t)p->vn * 12); d.Out(p->c, (int64_t)p->vn * 4); d.Out(p->u, (int64_t)p->vn * 8);
        d.OutV(p->triOutN); d.Out(p->triOut, (int64_t)p->triOutN * 4);
    }
    d.Commit();
}
