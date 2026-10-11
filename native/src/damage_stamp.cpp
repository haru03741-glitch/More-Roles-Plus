#include "mrp_native.h"
#include <math.h>
#include <float.h>

// 壊した形を損傷マスク (RGBA: R = 穴・G = 焦げ・B = 切り口) に書く (src/Terrain/DamageMap.cs の Stamp と同じ結果)。
// 形 (CutShape)・帯 (切った壁の線の近く)・壁の中の判定 (WallBody.Exposed)・家具よけ・船の外の判定も C# と同じ式と順序

struct StampArgs
{
    uint8_t* px; uint8_t* gens;
    int32_t w, h; float ox, oy, ppu;
    int32_t x0, y0, x1, y1;
    int32_t gen;
    int32_t shapeKind; const float* shape; int32_t shapeN;   // 0 = 円 (cx cy r)・1 = 長方形 (cx cy tx ty nx ny hl hd)・2 = 凸 (頂点 n 個・法線 n 個)
    float r; int32_t scorch;
    int32_t banded; int32_t bandSegs; const float* band; int32_t bandN;   // bandSegs = 1: 線分 (2 点ずつ)・0: 凸包
    int32_t hasBody; int32_t bodySpace; int32_t bodyMap; const float* bseg; int32_t bsegN; const float* bopen; int32_t bopenN; float refx, refy;
    const float* keep; int32_t keepN;                        // xMin yMin xMax yMax
    float floorY;
    const uint8_t* sOpen; const uint8_t* sOutside; int32_t sw, sh; float sox, soy, sppu; int32_t reach;
    int32_t tx0, ty0, tx1, ty1;
};

static const float HoleEdge = 0.25f, ScorchWidth = 0.45f, EmberCold = 0.45f, FurnitureScorch = 0.3f;
static const float BandHalf = 0.2f, LineBandEdge = 0.08f, LineBandHalf = 0.05f, BandUpStretch = 2.2f, BandDownSquash = 2.0f;
static const float LineBand = 0.08f;

static inline float clamp01(float v) { return v < 0.0f ? 0.0f : v > 1.0f ? 1.0f : v; }
static inline float clampf(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
static inline float fminf_(float a, float b) { return a < b ? a : b; }   // Math.Min (NaN は来ない)
static inline float fmaxf_(float a, float b) { return a > b ? a : b; }

static float shape_sd(const StampArgs* a, float x, float y)
{
    const float* s = a->shape;
    if (a->shapeKind == 0)
    {
        float dx = x - s[0], dy = y - s[1];
        return sqrtf(dx * dx + dy * dy) - s[2];
    }
    if (a->shapeKind == 1)
    {
        float px = x - s[0], py = y - s[1];
        float qu = fabsf(px * s[2] + py * s[3]) - s[6];
        float qv = fabsf(px * s[4] + py * s[5]) - s[7];
        float ou = fmaxf_(qu, 0.0f), ov = fmaxf_(qv, 0.0f);
        return sqrtf(ou * ou + ov * ov) + fminf_(fmaxf_(qu, qv), 0.0f);
    }
    int k = a->shapeN;
    const float* v = s;
    const float* n = s + k * 2;
    float inside = -FLT_MAX;
    bool outside = false;
    for (int i = 0; i < k; i++)
    {
        float d = (x - v[i * 2]) * n[i * 2] + (y - v[i * 2 + 1]) * n[i * 2 + 1];
        if (d > 0.0f) outside = true;
        if (d > inside) inside = d;
    }
    if (!outside) return inside;
    float best = FLT_MAX;
    for (int i = 0; i < k; i++)
    {
        int j = (i + 1) % k;
        float ax = v[i * 2], ay = v[i * 2 + 1];
        float sx = v[j * 2] - ax, sy = v[j * 2 + 1] - ay, l2 = sx * sx + sy * sy;
        float t = l2 > 0 ? clampf(((x - ax) * sx + (y - ay) * sy) / l2, 0.0f, 1.0f) : 0.0f;
        float dx = ax + sx * t - x, dy = ay + sy * t - y;
        best = fminf_(best, dx * dx + dy * dy);
    }
    return sqrtf(best);
}

static bool inside_convex(const float* hull, int n, float x, float y)
{
    for (int k = 0; k < n; k++)
    {
        int j = (k + 1) % n;
        float ax = hull[k * 2], ay = hull[k * 2 + 1], bx = hull[j * 2], by = hull[j * 2 + 1];
        if ((bx - ax) * (y - ay) - (by - ay) * (x - ax) < 0) return false;
    }
    return true;
}

static float band_distance(const float* hull, int n, float x, float y)
{
    if (n >= 3 && inside_convex(hull, n, x, y)) return 0.0f;
    float best = FLT_MAX;
    for (int k = 0; k < n; k++)
    {
        int j = (k + 1) % n;
        float ax = hull[k * 2], ay = hull[k * 2 + 1];
        float sx = hull[j * 2] - ax, sy = hull[j * 2 + 1] - ay, l2 = sx * sx + sy * sy;
        float t = l2 > 0 ? clampf(((x - ax) * sx + (y - ay) * sy) / l2, 0.0f, 1.0f) : 0.0f;
        float ex = x - (ax + sx * t), ey = y - (ay + sy * t);
        if (ey > 0) ey /= BandUpStretch;
        else ey *= BandDownSquash;
        float d = ex * ex + ey * ey;
        if (d < best) best = d;
        if (n == 2) break;
    }
    return sqrtf(best);
}

static float segment_band_distance(const float* segs, int n, float x, float y)
{
    float best = FLT_MAX;
    for (int k = 0; k + 1 < n; k += 2)
    {
        float ax = segs[k * 2], ay = segs[k * 2 + 1];
        float sx = segs[k * 2 + 2] - ax, sy = segs[k * 2 + 3] - ay, l2 = sx * sx + sy * sy;
        float t = l2 > 0 ? clampf(((x - ax) * sx + (y - ay) * sy) / l2, 0.0f, 1.0f) : 0.0f;
        float ex = x - (ax + sx * t), ey = y - (ay + sy * t);
        float d = ex * ex + ey * ey;
        if (d < best) best = d;
    }
    return sqrtf(best);
}

// SolidMap
static inline bool s_outside_cell(const StampArgs* a, int x, int y)
{
    return x < 0 || y < 0 || x >= a->sw || y >= a->sh || a->sOutside[y * a->sw + x] == 1;
}

static bool s_near_outside(const StampArgs* a, float px, float py)
{
    if (!a->sOutside) return false;
    int x = (int)floorf((px - a->sox) * a->sppu), y = (int)floorf((py - a->soy) * a->sppu);
    int r = a->reach;
    return s_outside_cell(a, x, y) || s_outside_cell(a, x - r, y) || s_outside_cell(a, x + r, y) ||
           s_outside_cell(a, x, y - r) || s_outside_cell(a, x, y + r);
}

static bool s_bare_sky(const StampArgs* a, float px, float py)
{
    if (!a->sOutside) return false;
    int x = (int)floorf((px - a->sox) * a->sppu), y = (int)floorf((py - a->soy) * a->sppu);
    if (x < 0 || y < 0 || x >= a->sw || y >= a->sh) return true;
    uint8_t v = a->sOutside[y * a->sw + x];
    int r = a->reach;
    return v == 1 || (v == 2 && (s_outside_cell(a, x - r, y) || s_outside_cell(a, x + r, y) ||
                                 s_outside_cell(a, x, y - r) || s_outside_cell(a, x, y + r)));
}

static bool s_solid(const StampArgs* a, float px, float py)
{
    if (!a->sOpen) return false;
    int x = (int)floorf((px - a->sox) * a->sppu), y = (int)floorf((py - a->soy) * a->sppu);
    if (x < 0 || y < 0 || x >= a->sw || y >= a->sh) return true;
    return a->sOpen[y * a->sw + x] == 0;
}

// WallBody
static bool near_segs(const float* segs, int n, float px, float py, float d)
{
    float d2 = d * d;
    for (int i = 0; i + 1 < n; i += 2)
    {
        float ax = segs[i * 2], ay = segs[i * 2 + 1];
        float bx = segs[i * 2 + 2] - ax, by = segs[i * 2 + 3] - ay, l2 = bx * bx + by * by;
        float s = l2 > 0 ? clampf(((px - ax) * bx + (py - ay) * by) / l2, 0.0f, 1.0f) : 0.0f;
        float cx = ax + bx * s - px, cy = ay + by * s - py;
        if (cx * cx + cy * cy < d2) return true;
    }
    return false;
}

static int crossings(const StampArgs* a, float ax, float ay, float bx, float by)
{
    int n = 0;
    float dx = bx - ax, dy = by - ay;
    const float* s = a->bseg;
    for (int i = 0; i + 1 < a->bsegN; i += 2)
    {
        float cx0 = s[i * 2], cy0 = s[i * 2 + 1];
        float ex = s[i * 2 + 2] - cx0, ey = s[i * 2 + 3] - cy0;
        float den = dx * ey - dy * ex;
        if (fabsf(den) < 1e-12f) continue;
        float cx = cx0 - ax, cy = cy0 - ay;
        float t = (cx * ey - cy * ex) / den;
        float u = (cx * dy - cy * dx) / den;
        if (t > 0.0f && t <= 1.0f && u >= 0.0f && u < 1.0f) n++;
    }
    return n;
}

static bool nearest_opening(const StampArgs* a, float px, float py, float* qx, float* qy)
{
    float best = FLT_MAX;
    const float* s = a->bopen;
    for (int i = 0; i + 1 < a->bopenN; i += 2)
    {
        float ax = s[i * 2], ay = s[i * 2 + 1];
        float bx = s[i * 2 + 2] - ax, by = s[i * 2 + 3] - ay, l2 = bx * bx + by * by;
        float t = l2 > 0 ? clampf(((px - ax) * bx + (py - ay) * by) / l2, 0.0f, 1.0f) : 0.0f;
        float cx = ax + bx * t, cy = ay + by * t;
        float d = (cx - px) * (cx - px) + (cy - py) * (cy - py);
        if (d < best) { best = d; *qx = cx; *qy = cy; }
    }
    return best < FLT_MAX;
}

static bool exposed(const StampArgs* a, float x, float y)
{
    if (a->bodySpace && s_bare_sky(a, x, y)) return false;
    if (a->bodyMap)
    {
        if (!s_solid(a, x, y)) return false;
        if (near_segs(a->bseg, a->bsegN, x, y, LineBand) && !near_segs(a->bopen, a->bopenN, x, y, LineBand)) return false;
    }
    else if ((crossings(a, a->refx, a->refy, x, y) & 1) == 0) return false;
    float qx = 0, qy = 0;
    if (!nearest_opening(a, x, y, &qx, &qy)) return false;
    float dx = x - qx, dy = y - qy, l = sqrtf(dx * dx + dy * dy);
    if (l < 0.02f) return true;
    float tx = qx + dx / l * 0.01f, ty = qy + dy / l * 0.01f;
    return crossings(a, x, y, tx, ty) == 0;
}

static bool inside_any(const StampArgs* a, float x, float y)
{
    for (int i = 0; i < a->keepN; i++)
    {
        const float* k = a->keep + i * 4;
        if (x >= k[0] && x <= k[2] && y >= k[1] && y <= k[3]) return true;
    }
    return false;
}

MRP_API void mrp_damage_stamp(StampArgs* a)
{
    int tx0 = 0x7fffffff, ty0 = 0x7fffffff, tx1 = -1, ty1 = -1;
    float r = a->r;
    bool withScorch = a->scorch != 0;
    for (int py = a->y0; py <= a->y1; py++)
    {
        float wy = a->oy + ((float)py + 0.5f) / a->ppu;
        for (int px = a->x0; px <= a->x1; px++)
        {
            float wx = a->ox + ((float)px + 0.5f) / a->ppu;
            float sd = shape_sd(a, wx, wy);
            float hole = clamp01(0.5f - sd / HoleEdge * 0.5f);
            float scorch = withScorch ? clamp01(1.0f - sd / (r * ScorchWidth)) : 0.0f;
            float ember = sd > -0.03f ? clamp01(1.0f - sd / (HoleEdge * 2.0f)) * (withScorch ? 1.0f : EmberCold) : 0.0f;
            if (a->banded)
            {
                float bd = a->hasBody ? segment_band_distance(a->band, a->bandN, wx, wy) : band_distance(a->band, a->bandN, wx, wy);
                float band = a->hasBody
                    ? clamp01(0.5f + (LineBandHalf - bd) / LineBandEdge * 0.5f)
                    : clamp01(0.5f + (BandHalf - bd) / HoleEdge * 0.5f);
                if (band < 1.0f && hole > 0.0f && a->hasBody && exposed(a, wx, wy)) band = 1.0f;
                hole = fminf_(hole, band);
                scorch = fminf_(scorch, clamp01(1.0f - (bd - BandHalf) / (r * ScorchWidth)));
            }
            if ((hole > 0.0f || ember > 0.0f || scorch > FurnitureScorch) && inside_any(a, wx, wy))
            {
                hole = 0.0f;
                ember = 0.0f;
                scorch = fminf_(scorch, FurnitureScorch);
            }
            if (wy < a->floorY) hole = fminf_(hole, clamp01(0.5f - (a->floorY - wy) / HoleEdge * 0.5f));
            if ((scorch > 0.0f || ember > 0.0f) && s_near_outside(a, wx, wy)) { scorch = 0.0f; ember = 0.0f; }
            if (hole > 0.0f && s_bare_sky(a, wx, wy)) hole = 0.0f;

            int i = (py * a->w + px) * 4;
            uint8_t hb = (uint8_t)(int)(hole * 255.0f), sb = (uint8_t)(int)(scorch * 255.0f);
            if (hb > a->px[i])
            {
                a->px[i] = hb;
                a->gens[py * a->w + px] = (uint8_t)a->gen;
                if (px < tx0) tx0 = px;
                if (px > tx1) tx1 = px;
                if (py < ty0) ty0 = py;
                if (py > ty1) ty1 = py;
            }
            if (sb > a->px[i + 1]) a->px[i + 1] = sb;
            uint8_t eb = (uint8_t)(int)(ember * 255.0f);
            if (eb > a->px[i + 2]) a->px[i + 2] = eb;
        }
    }
    a->tx0 = tx0; a->ty0 = ty0; a->tx1 = tx1; a->ty1 = ty1;
}
