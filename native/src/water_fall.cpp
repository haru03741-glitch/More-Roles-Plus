#include "mrp_native.h"
#include "digest.h"
#include "capture.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

// 滝の絵 (src/Terrain/WaterFall.cs の DrawCs と同じ結果)。縁ごとの膜・着地の泡・手すりを越える膜を、世界の画素の格子に揃えた升の絵へ描き、
// 前回の範囲と合わせて画素 (RGBA32) へ直す。転送は C# 側。小数の式の順番は C# と同じ

struct FallTile
{
    float* dens; float* foam; float* dep; float* pool;
    uint8_t* px;
    int32_t tx, ty, isVoid;
    int32_t x0p, y0p, x1p, y1p;     // 入出力: 前回描いた範囲 (描いた後は今回の範囲)
    int32_t bx0, by0, bx1, by1;     // 作業: 今回描いた範囲
    int32_t changed;                // 出力: 送り直す (今回か前回に描いた)
};

struct FallEdge
{
    float x0, y0, nx, ny, q, head, tail, fall, depth, barH, barF, lipH, cut, cutFrom;
    float dx, dy, qa, xa, ya, qb, xb, yb;  // 着く所のずれと、隣の縁とならした量・ずれ (縁の両端)
    float da, db, ha, hb, ta, tb;          // 隣の縁とならした深さ・膜の先端と後ろの端 (落ちた割合・縁の両端)
    float ea, eb;                          // 隣に流れる縁が無い側へ膜を薄れながら延ばす幅 (無ければ 0)
    int32_t lip, isVoid;
    int32_t disc; float discX, discY, discR, discS;   // 着地の泡
    int32_t lipMode; float lipS;                      // 1 = 手すりを垂れる膜・2 = 奥の手すりを越える膜
    float wob[12];                                    // 手すりの膜の揺れ (縁に沿った 1 画素ごと)
};

struct FallArgs
{
    FallTile* tiles; int32_t tileN, tilePx;
    const FallEdge* edges; int32_t edgeN;
    float ppu, clock;
    // 手前の床 (WaterSim.FrontCut)
    const uint8_t* open; const int32_t* hgt; int32_t w, h; float ox, oy, cell;
    const float* low; int32_t lowN;          // 奥向きの低い壁 (x0 x1 y0 y1 top base の 6 個ずつ)
    // 縁に立つ物の絵 (WaterBarrier)
    const uint8_t* bits; int32_t bw, bh; float box, boy, bppu;
    // 床マスク (FloorMask・膜の始点を絵の崖の縁へ寄せる)
    const uint8_t* mask; int32_t mw, mh; float mox, moy, mppu;
    int64_t puts, clipped;                   // 出力: 置いた画素・升が無くて置けなかった画素
};

static const float SheetFull = 240.0f, DensMax = 1.3f, ThinD = 0.35f, PoolFade = 0.36f, SideBody = 0.75f, Thin = 0.5f;
static const float ThrowMax = 0.35f, ThrowGain = 0.5f, Spread = 0.4f;
static const float LipIn = 0.1f;   // 膜を縁から内へさかのぼらせる長さ (水たまりの縁の線にかぶせる)
static const float ApronMin = 0.12f, ApronGain = 0.06f, ApronMax = 0.45f, ApronDens = 0.8f;   // 着地の白い水の長さ (落ちた高さ 1 単位あたり) と濃さ
static const float LipHalf = 0.13f, LipDrop = 0.3f, LipDens = 0.9f, CrestUp = 0.06f, CrestDens = 0.6f;
static const float Full = 1024.0f;
static const float BrinkStep = 0.02f; static const int BrinkSteps = 15;

static inline float fmn(float a, float b) { return a < b ? a : b; }
static inline float fmx(float a, float b) { return a > b ? a : b; }
static inline int imin(int a, int b) { return a < b ? a : b; }
static inline int imax(int a, int b) { return a > b ? a : b; }
static inline int iclamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

static inline float Hash(int a, int b)
{
    uint32_t h = (uint32_t)a * 374761393u + (uint32_t)b * 668265263u;
    h = (h ^ (h >> 13)) * 1274126177u;
    return (float)((h ^ (h >> 16)) & 0xFFFFu) / 65535.0f;
}

static inline float Noise(int col, float t)
{
    int i = (int)floorf(t);
    float f = t - (float)i;
    f = f * f * (3.0f - 2.0f * f);
    float a = Hash(col, i);
    return a + (Hash(col, i + 1) - a) * f;
}

struct Ctx
{
    FallArgs* a;
    FallTile* hit; int htx, hty, hv;
    int64_t puts, clipped;
};

// floorf と同じ値 (MSVC の floorf は関数呼び出しで重い)。|v| < 2^31 の範囲だけで使う
static inline int Floor(float v) { int i = (int)v; return v < (float)i ? i - 1 : i; }

static inline int FloorDiv(int v, int d) { return (v < 0 ? v - (d - 1) : v) / d; }

static FallTile* TileOf(Ctx& c, int v, int tx, int ty)
{
    if (tx != c.htx || ty != c.hty || v != c.hv)
    {
        c.hit = nullptr;
        for (int i = 0; i < c.a->tileN; i++)
        {
            FallTile* t = &c.a->tiles[i];
            if (t->tx == tx && t->ty == ty && t->isVoid == v) { c.hit = t; break; }
        }
        c.htx = tx; c.hty = ty; c.hv = v;
    }
    return c.hit;
}

static inline void Grow(FallTile* t, int x0, int y0, int x1, int y1)
{
    if (x0 < t->bx0) t->bx0 = x0;
    if (y0 < t->by0) t->by0 = y0;
    if (x1 > t->bx1) t->bx1 = x1;
    if (y1 > t->by1) t->by1 = y1;
}

static inline void Add(FallTile* t, int i, float w, float foam, float dep, float pool)
{
    t->dens[i] += w;
    t->foam[i] += w * foam;
    if (dep > 0.0f && t->dep[i] < dep) t->dep[i] = dep;
    if (pool > 0.0f && t->pool[i] < pool) t->pool[i] = pool;
}

static void Put(Ctx& c, int v, int gx, int gy, float w, float foam, float dep, float pool)
{
    int n = c.a->tilePx;
    int tx = FloorDiv(gx, n), ty = FloorDiv(gy, n);
    FallTile* t = TileOf(c, v, tx, ty);
    if (!t) { c.clipped++; return; }
    c.puts++;
    int x = gx - tx * n, y = gy - ty * n;
    Grow(t, x, y, x, y);
    Add(t, y * n + x, w, foam, dep, pool);
}

static void Splat(Ctx& c, int v, float wx, float wy, float w, float foam, float dep, float pool)
{
    float ppu = c.a->ppu;
    float fx = wx * ppu - 0.5f, fy = wy * ppu - 0.5f;
    int x = Floor(fx), y = Floor(fy);
    float ax = fx - (float)x, ay = fy - (float)y;
    float w00 = (1.0f - ax) * (1.0f - ay) * w, w10 = ax * (1.0f - ay) * w, w01 = (1.0f - ax) * ay * w, w11 = ax * ay * w;
    int n = c.a->tilePx;
    int tx = FloorDiv(x, n), ty = FloorDiv(y, n);
    int lx = x - tx * n, ly = y - ty * n;
    // 4 画素が 1 枚の升に収まる (ほとんどの点) ならまとめて置く。置く順と値は 1 画素ずつ置く時と同じ
    if (lx + 1 < n && ly + 1 < n)
    {
        FallTile* t = TileOf(c, v, tx, ty);
        if (!t) { c.clipped += 4; return; }
        c.puts += 4;
        Grow(t, lx, ly, lx + 1, ly + 1);
        int i = ly * n + lx;
        Add(t, i, w00, foam, dep, pool);
        Add(t, i + 1, w10, foam, dep, pool);
        Add(t, i + n, w01, foam, dep, pool);
        Add(t, i + n + 1, w11, foam, dep, pool);
        return;
    }
    Put(c, v, x, y, w00, foam, dep, pool);
    Put(c, v, x + 1, y, w10, foam, dep, pool);
    Put(c, v, x, y + 1, w01, foam, dep, pool);
    Put(c, v, x + 1, y + 1, w11, foam, dep, pool);
}

static bool BarrierAt(const FallArgs* a, float x, float y)
{
    if (!a->bits) return false;
    int mx = (int)floorf((x - a->box) * a->bppu), my = (int)floorf((y - a->boy) * a->bppu);
    if (mx < 0 || my < 0 || mx >= a->bw || my >= a->bh) return false;
    int k = my * a->bw + mx;
    return (a->bits[k >> 3] & (1 << (k & 7))) != 0;
}

static bool Blocked(const FallArgs* a, const FallEdge& e, float u)
{
    float x = e.x0 - e.ny * u, y = e.y0 + e.nx * u;
    return e.ny != 0.0f ? BarrierAt(a, x, y + 0.03f) || BarrierAt(a, x, y - 0.03f) : BarrierAt(a, x, y);
}

static float FrontCut(const FallArgs* a, float x, float y0, float y1)
{
    if (!a->open) return -3.40282347e+38f;
    int cx = (int)floorf((x - a->ox) / a->cell);
    if (cx < 0 || cx >= a->w) return -3.40282347e+38f;
    int cy = (int)floorf((y0 - a->oy) / a->cell), end = (int)floorf((y1 - a->oy) / a->cell);
    if (cy >= a->h) cy = a->h - 1;
    for (; cy >= 0 && cy >= end; cy--)
    {
        int c = cy * a->w + cx;
        if (a->open[c] == 0) continue;
        float top = a->oy + (float)(cy + 1) * a->cell;
        float px = a->ox + ((float)cx + 0.5f) * a->cell, py = a->oy + ((float)cy + 0.5f) * a->cell;
        for (int i = 0; i < a->lowN; i++)
        {
            const float* l = a->low + i * 6;
            if (px >= l[0] && px <= l[1] && py >= l[2] && py <= l[3]) { top = fmx(l[4], l[5] + (float)a->hgt[c] * (1.0f / Full)); break; }
        }
        return top;
    }
    return -3.40282347e+38f;
}

static inline int MaskAt(const FallArgs* a, float x, float y)
{
    int mx = Floor((x - a->mox) * a->mppu), my = Floor((y - a->moy) * a->mppu);
    if (mx < 0 || my < 0 || mx >= a->mw || my >= a->mh) return -1;
    int i = my * a->mw + mx;
    return (a->mask[i >> 3] & (1 << (i & 7))) != 0 ? 1 : 0;
}

// 縁 (水の升の境) から床マスクの崖の縁 (床が終わる所) までの、外向きを正とする距離。膜はそこから始める
// (縁と崖の縁の間の水は床の水の絵が描く)。縁の上が床なら外へ、床でなければ内へ BrinkSteps まで探す。見つからなければ 0
static float Brink(const FallArgs* a, float x, float y, float nx, float ny)
{
    if (!a->mask) return 0.0f;
    int at = MaskAt(a, x, y);
    if (at < 0) return 0.0f;
    float sgn = at != 0 ? 1.0f : -1.0f;
    for (int k = 1; k <= BrinkSteps; k++)
    {
        float d = (float)k * BrinkStep * sgn;
        int m = MaskAt(a, x + nx * d, y + ny * d);
        if (m < 0) return 0.0f;
        if (m != at) return at != 0 ? d : d + BrinkStep;
    }
    return 0.0f;
}

static void Sheet(Ctx& c, const FallEdge& e)
{
    const FallArgs* a = c.a;
    float ppu = a->ppu, now = a->clock;
    float fall = e.fall;
    float tau0 = fmn(1.0f, e.tail / fall), tau1 = fmn(1.0f, e.head / fall);
    float tx = -e.ny, ty = e.nx, half = a->cell * 0.5f;
    float s0 = e.x0 * tx + e.y0 * ty;
    int k0 = (int)ceilf((s0 - half - e.ea) * ppu), k1 = (int)ceilf((s0 + half + e.eb) * ppu) - 1;
    if (k1 < k0) return;
    float dx = e.dx, dy = e.dy;
    float qa = e.qa, qb = e.qb, xa = e.xa, xb = e.xb, ya = e.ya, yb = e.yb;
    int cols = k1 - k0 + 1;
    float jump = fmx(fabsf(xb - xa), fabsf(yb - ya)) * ppu;
    int sub = iclamp((int)ceilf(jump / (float)cols), 1, 6);
    float wo = 1.0f, wb = 1.0f;
    if (!e.lip && e.barF > 0.0f)
    {
        float eb = fmx(0.0f, e.depth - e.barH);
        wo = e.depth * sqrtf(e.depth);
        wb = eb * sqrtf(eb);
        float mean = (1.0f - e.barF) * wo + e.barF * wb;
        if (mean > 0.0f) { wo /= mean; wb /= mean; } else { wo = 1.0f; wb = 0.0f; }
    }
    int v = e.isVoid;
    for (int col = k0; col <= k1; col++)
    {
        float u0 = (float)col / ppu - s0;
        // 列の深さと膜の先端・後ろの端は隣の縁との間でつなぎ (升ごとの段を作らない)、縁の外へ延ばした列は端の値のまま薄れる
        float fc = fmn(1.0f, (u0 < 0.0f ? -u0 : u0) / half);
        float fade = u0 < -half && e.ea > 0.0f ? 1.0f - (-u0 - half) / e.ea : u0 > half && e.eb > 0.0f ? 1.0f - (u0 - half) / e.eb : 1.0f;
        if (fade <= 0.0f) continue;
        float dep = u0 < 0.0f ? e.depth + (e.da - e.depth) * fc : e.depth + (e.db - e.depth) * fc;
        float t0 = u0 < 0.0f ? tau0 + (e.ta - tau0) * fc : tau0 + (e.tb - tau0) * fc;
        float t1 = u0 < 0.0f ? tau1 + (e.ha - tau1) * fc : tau1 + (e.hb - tau1) * fc;
        if (t1 <= t0) continue;
        bool top = !e.lip && e.barF > 0.0f && Blocked(a, e, u0);
        float z0 = top && e.depth > e.barH ? e.barH : 0.0f;
        float thin = 1.0f;
        float thr = fmn(ThrowMax, ThrowGain * sqrtf(fmx(0.0f, dep - z0))) * (0.9f + 0.2f * Noise(col + 7919, now * 0.6f));
        for (int si = 0; si < sub; si++)
        {
            float u = u0 + (float)si / (ppu * (float)sub);
            float f = fmn(1.0f, u < 0.0f ? -u / half : u / half);
            float q = u < 0.0f ? e.q + (qa - e.q) * f : e.q + (qb - e.q) * f;
            float ldx = u < 0.0f ? dx + (xa - dx) * f : dx + (xb - dx) * f;
            float ldy = u < 0.0f ? dy + (ya - dy) * f : dy + (yb - dy) * f;
            float dm = q * fade / SheetFull;
            if (dm < ThinD) { if (si == 0) thin = 1.0f + (Noise(col, now * 1.3f) * 1.6f - 0.8f) * (1.0f - dm / ThinD) * 1.25f; dm *= thin; }
            if (dm > DensMax) dm = DensMax;
            float dc = dm * (top ? wb : wo);
            if (dc > DensMax) dc = DensMax;
            if (dm <= 0.01f) continue;
            dm /= (float)sub; dc /= (float)sub;
            float sx = e.x0 + tx * u, sy = e.y0 + ty * u;
            float h0 = fmx(0.3f, -ldy);
            float ex = sx + ldx + e.nx * thr, ey = sy + ldy + e.ny * thr;
            float br = Brink(a, sx, sy, e.nx, e.ny);
            float cut = v ? FrontCut(a, sx + e.nx * a->cell * 0.25f, e.cutFrom, ey) : -3.40282347e+38f;   // ずらす前の縁の外側で探す
            sx += e.nx * br; sy += e.ny * br;
            float gx = sx, gy = sy - h0, hh = h0 + z0;
            if (sy + z0 <= cut) continue;
            if (e.ny > 0.0f && !e.lip) continue;
            float len = (fabsf(ex - sx) + fabsf(ey - sy) + 2.0f * hh) * ppu * (t1 - t0);
            int steps = iclamp((int)ceilf(len), 1, 600);
            float w = fmn(1.0f, len / (float)steps) * 0.85f;
            // 膜の後ろの端が縁を離れるまでは、縁から内へさかのぼって水たまりの色のまま濃くしていく (水たまりの縁の線と膜の間を見せない)
            if (t0 <= 0.0f && !e.lip)
            {
                int ni = (int)ceilf(LipIn * ppu);
                for (int b = 1; b <= ni; b++)
                {
                    float s = (float)b / (float)ni;
                    Splat(c, v, sx - e.nx * LipIn * s, sy - e.ny * LipIn * s, dc * 0.85f * (1.0f - s), 0.05f, dep, 1.0f);
                }
            }
            // 着いた水は着地の手前 (水たまりの上) へ白く泡立って広がり、薄れて水たまりに戻る (奈落へ落ちる水は除く)
            if (!v && t1 >= 1.0f)
            {
                float lap = fmn(ApronMax, ApronMin + ApronGain * hh);
                int na = (int)ceilf(lap * ppu);
                float base = dm * w / (1.0f + Thin) * ApronDens;
                for (int b = 1; b <= na; b++)
                {
                    float s = (float)b / (float)na, k = 1.0f - s;
                    Splat(c, v, ex, ey - lap * s, base * k * sqrtf(k), 0.9f - 0.4f * s, dep, 0.0f);
                }
            }
            for (int j = 0; j <= steps; j++)
            {
                float tau = t0 + (t1 - t0) * (float)j / (float)steps;
                float x = gx + (ex - gx) * tau, y = gy + (ey - gy) * tau + hh * (1.0f - tau * tau);
                if (y < cut) break;
                float foam = tau > 0.92f ? 0.8f : tau < 0.03f ? 0.4f : 0.05f + 0.3f * tau * tau;
                float al = v && tau > 0.7f ? (1.0f - tau) / 0.3f : 1.0f;
                float sp = tau >= Spread ? 1.0f : tau / Spread;
                float d = dc + (dm - dc) * sp * sp * (3.0f - 2.0f * sp);
                if (d <= 0.005f) continue;
                float val = d * w * al / (1.0f + Thin * tau);
                float pf = 1.0f - fmn(1.0f, fmx(0.0f, sy - y) / (PoolFade * hh));
                float pool = pf * pf * (3.0f - 2.0f * pf);
                if (e.nx == 0.0f) { Splat(c, v, x, y, val, foam, dep, pool); continue; }
                float span = (x - sx) * SideBody;
                int n = imax(1, (int)ceilf(fabsf(span) * ppu));
                for (int b = 0; b < n; b++) Splat(c, v, x - span * (float)b / (float)n, y, val, foam, dep, pool);
            }
        }
    }
}

static void Disc(Ctx& c, int v, float wx, float wy, float r, float s)
{
    float ppu = c.a->ppu;
    int cx = (int)floorf(wx * ppu), cy = (int)floorf(wy * ppu), rx = (int)(r * ppu), ry = imax(1, (int)(r * 0.6f * ppu));
    for (int y = -ry; y <= ry; y++)
    for (int x = -rx; x <= rx; x++)
    {
        float q = (float)x * (float)x / (float)(rx * rx) + (float)y * (float)y / (float)(ry * ry);
        if (q >= 1.0f) continue;
        Put(c, v, cx + x, cy + y, (1.0f - q) * 0.6f * s, 0.85f, 0.0f, 0.0f);
    }
}

static void LipSheet(Ctx& c, const FallEdge& e)
{
    float ppu = c.a->ppu, s = e.lipS;
    float tx = -e.ny, ty = e.nx;
    int n = (int)ceilf(LipHalf * 2.0f * ppu);
    for (int k = 0; k <= n && k < 12; k++)
    {
        float u = -LipHalf + (float)k / ppu;
        float x = e.x0 + tx * u, y = e.y0 + ty * u;
        float wob = e.wob[k];
        int ax = (int)floorf(x * ppu), ay = (int)floorf(y * ppu);
        float drop = LipDrop * (0.7f + 0.3f * wob);
        if (e.isVoid) drop = fmn(drop, y - e.cut);
        int len = (int)(drop * ppu);
        for (int j = 0; j <= len; j++)
            Put(c, e.isVoid, ax, ay - j, LipDens * s, j == 0 ? 0.9f : 0.15f, 0.0f, 0.0f);
    }
}

static void LipCrest(Ctx& c, const FallEdge& e)
{
    float ppu = c.a->ppu, s = e.lipS;
    float tx = -e.ny, ty = e.nx;
    float top = fmx(e.y0, e.cut);
    int n = (int)ceilf(LipHalf * 2.0f * ppu);
    for (int k = 0; k <= n && k < 12; k++)
    {
        float u = -LipHalf + (float)k / ppu;
        float x = e.x0 + tx * u, y = top + ty * u;
        float wob = e.wob[k];
        int ax = (int)floorf(x * ppu), ay = (int)floorf(y * ppu);
        int len = imax(1, (int)((CrestUp * (0.6f + 0.4f * wob)) * ppu));
        for (int j = 0; j <= len; j++)
            Put(c, e.isVoid, ax, ay + j, LipDens * CrestDens * s * (1.0f - 0.6f * (float)j / ((float)len + 1.0f)), j == len ? 0.9f : 0.2f, 0.0f, 0.0f);
    }
}

MRP_API void mrp_fall_draw(FallArgs* a)
{
    int n = a->tilePx;
    for (int t = 0; t < a->tileN; t++)
    {
        FallTile* c = &a->tiles[t];
        for (int y = c->y0p; y <= c->y1p; y++)
        {
            int o = y * n + c->x0p, len = c->x1p - c->x0p + 1;
            memset(c->dens + o, 0, (size_t)len * sizeof(float));
            memset(c->foam + o, 0, (size_t)len * sizeof(float));
            memset(c->dep + o, 0, (size_t)len * sizeof(float));
            memset(c->pool + o, 0, (size_t)len * sizeof(float));
        }
        c->bx0 = n; c->by0 = n; c->bx1 = -1; c->by1 = -1;
    }
    Ctx cx{ a, nullptr, -2147483647 - 1, 0, -1, 0, 0 };
    for (int i = 0; i < a->edgeN; i++) Sheet(cx, a->edges[i]);
    for (int i = 0; i < a->edgeN; i++)
    {
        const FallEdge& e = a->edges[i];
        if (e.disc) Disc(cx, e.isVoid, e.discX, e.discY, e.discR, e.discS);
    }
    for (int i = 0; i < a->edgeN; i++)
    {
        const FallEdge& e = a->edges[i];
        if (e.lipMode == 2) LipCrest(cx, e);
        else if (e.lipMode == 1) LipSheet(cx, e);
    }
    a->puts += cx.puts;
    a->clipped += cx.clipped;
    for (int t = 0; t < a->tileN; t++)
    {
        FallTile* c = &a->tiles[t];
        bool now = c->bx1 >= 0, before = c->x1p >= 0;
        c->changed = now || before ? 1 : 0;
        if (!c->changed) continue;
        int ux0 = imin(now ? c->bx0 : n, before ? c->x0p : n), uy0 = imin(now ? c->by0 : n, before ? c->y0p : n);
        int ux1 = imax(c->bx1, c->x1p), uy1 = imax(c->by1, c->y1p);
        uint32_t* px = (uint32_t*)c->px;
        for (int y = uy0; y <= uy1; y++)
        for (int i = y * n + ux0, end = y * n + ux1; i <= end; i++)
        {
            float d = c->dens[i];
            if (d <= 0.0f) { px[i] = 0xFF000000u; continue; }
            uint32_t r = d >= 1.0f ? 255u : (uint32_t)(d * 255.0f);
            float f = c->foam[i] / d;
            uint32_t g = f >= 1.0f ? 255u : (uint32_t)(f * 255.0f);
            float dp = c->dep[i] * 0.5f;
            uint32_t bl = dp >= 1.0f ? 255u : (uint32_t)(dp * 255.0f);
            float pl = c->pool[i];
            uint32_t al = 255u - (pl >= 1.0f ? 255u : (uint32_t)(pl * 255.0f));
            px[i] = (al << 24) | (bl << 16) | (g << 8) | r;
        }
        c->x0p = c->bx0; c->y0p = c->by0; c->x1p = c->bx1; c->y1p = c->by1;
    }
}

// 升の絵 5 枚 (濃さ・泡・深さ・水たまり・色) を升の順に 1 つの塊へ並べる (升ごとに塊を分けると記録の上限を超える)
static uint8_t* PackTiles(const FallArgs* a, int64_t* bytes)
{
    int64_t tp = (int64_t)a->tilePx * a->tilePx * 4;
    *bytes = tp * 5 * a->tileN;
    uint8_t* p = (uint8_t*)malloc((size_t)(*bytes > 0 ? *bytes : 1));
    if (!p) { *bytes = 0; return nullptr; }
    for (int i = 0; i < a->tileN; i++)
    {
        const FallTile& t = a->tiles[i];
        uint8_t* o = p + i * tp * 5;
        memcpy(o, t.dens, (size_t)tp); memcpy(o + tp, t.foam, (size_t)tp); memcpy(o + tp * 2, t.dep, (size_t)tp);
        memcpy(o + tp * 3, t.pool, (size_t)tp); memcpy(o + tp * 4, t.px, (size_t)tp);
    }
    return p;
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_fall_draw(FallArgs* a)
{
    Dg d(DgFallDraw);
    int64_t tp = (int64_t)a->tilePx * a->tilePx;
    d.InV(a->tileN); d.InV(a->tilePx); d.InV(a->edgeN); d.InV(a->ppu); d.InV(a->clock); d.InV(a->w); d.InV(a->h); d.InV(a->ox); d.InV(a->oy); d.InV(a->cell);
    d.InV(a->lowN); d.InV(a->bw); d.InV(a->bh); d.InV(a->box); d.InV(a->boy); d.InV(a->bppu); d.InV(a->mw); d.InV(a->mh); d.InV(a->mox); d.InV(a->moy); d.InV(a->mppu);
    d.In(a->edges, (int64_t)a->edgeN * (int64_t)sizeof(FallEdge)); d.In(a->low, (int64_t)a->lowN * 24);
    for (int i = 0; i < a->tileN; i++)
    {
        const FallTile& t = a->tiles[i];
        d.InV(t.tx); d.InV(t.ty); d.InV(t.isVoid); d.InV(t.x0p); d.InV(t.y0p); d.InV(t.x1p); d.InV(t.y1p);
    }
    Cap c(DgFallDraw);
    int64_t tileB = (int64_t)a->tileN * (int64_t)sizeof(FallTile), mapN = (int64_t)a->w * a->h;
    int64_t packB = 0;
    uint8_t* pack = c.on ? PackTiles(a, &packB) : nullptr;
    c.RawV(*a);
    c.Blob(a->tiles, tileB);
    c.Blob(a->edges, (int64_t)a->edgeN * (int64_t)sizeof(FallEdge));
    c.Blob(a->low, (int64_t)a->lowN * 24);
    c.Blob(a->open, mapN);
    c.Blob(a->open ? a->hgt : nullptr, mapN * 4);
    c.Blob(a->bits, ((int64_t)a->bw * a->bh + 7) >> 3);
    c.Blob(a->mask, ((int64_t)a->mw * a->mh + 7) >> 3);
    c.Blob(pack, packB);
    free(pack);
    mrp_fall_draw(a);
    for (int i = 0; i < a->tileN; i++)
    {
        const FallTile& t = a->tiles[i];
        d.OutV(t.changed); d.OutV(t.x0p); d.OutV(t.y0p); d.OutV(t.x1p); d.OutV(t.y1p);
        d.Out(t.px, tp * 4); d.Out(t.dens, tp * 4); d.Out(t.foam, tp * 4); d.Out(t.dep, tp * 4); d.Out(t.pool, tp * 4);
    }
    d.OutV(a->puts); d.OutV(a->clipped);
    d.Commit();
    pack = c.on ? PackTiles(a, &packB) : nullptr;
    c.Blob(pack, packB);
    free(pack);
    c.Blob(a->tiles, tileB);
    c.RawV(*a);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_fall_draw と同じ)
void rp_fall_draw(Rp& r)
{
    FallArgs a, want;
    r.RawV(a);
    int64_t lt, le, ll, lo, lh, lb, lm, lp;
    a.tiles = (FallTile*)r.Blob(&lt);
    a.edges = (const FallEdge*)r.Blob(&le);
    a.low = (const float*)r.Blob(&ll);
    a.open = (const uint8_t*)r.Blob(&lo);
    a.hgt = (const int32_t*)r.Blob(&lh);
    a.bits = (const uint8_t*)r.Blob(&lb);
    a.mask = (const uint8_t*)r.Blob(&lm);
    uint8_t* pack = (uint8_t*)r.Blob(&lp);
    if (!r.ok) return;
    int64_t tp = (int64_t)a.tilePx * a.tilePx * 4, mapN = (int64_t)a.w * a.h;
    if (lt != (int64_t)a.tileN * (int64_t)sizeof(FallTile) || le != (int64_t)a.edgeN * (int64_t)sizeof(FallEdge) || ll != (int64_t)a.lowN * 24
        || lo != (a.open ? mapN : 0) || lh != (a.open ? mapN * 4 : 0) || lb != (a.bits ? ((int64_t)a.bw * a.bh + 7) >> 3 : 0) || lm != (a.mask ? ((int64_t)a.mw * a.mh + 7) >> 3 : 0)
        || lp != tp * 5 * a.tileN)
    { r.Fail("fall_draw: input blob sizes differ"); return; }
    // 升の絵 5 枚を塊から振り分ける (番地は記録のものを捨てて差し替える)
    for (int i = 0; i < a.tileN; i++)
    {
        uint8_t* o = pack + i * tp * 5;
        FallTile& t = a.tiles[i];
        t.dens = (float*)o; t.foam = (float*)(o + tp); t.dep = (float*)(o + tp * 2); t.pool = (float*)(o + tp * 3); t.px = o + tp * 4;
    }
    mrp_fall_draw(&a);
    int64_t wp, wt;
    const uint8_t* wantPack = (const uint8_t*)r.Blob(&wp);
    const FallTile* wantTile = (const FallTile*)r.Blob(&wt);
    r.RawV(want);
    if (!r.ok) return;
    if (wp != lp || wt != lt) { r.Fail("fall_draw: output blob sizes differ"); return; }
    r.Same("tiles", pack, wantPack, lp);
    for (int i = 0; i < a.tileN && r.ok; i++)
    {
        const FallTile& g = a.tiles[i];
        const FallTile& w = wantTile[i];
        r.Same("changed", &g.changed, &w.changed, 4);
        r.Same("rect", &g.x0p, &w.x0p, 16);
        r.Same("work", &g.bx0, &w.bx0, 16);
    }
    r.Same("puts", &a.puts, &want.puts, 16);
}
static RpReg reg_fall_draw(DgFallDraw, rp_fall_draw);
#endif
