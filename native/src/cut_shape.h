#pragma once
#include <math.h>
#include <float.h>

// 壊す形 (src/Terrain/CutShape.cs の SignedDistance と同じ式と順序)。形の縁までの距離 (内側は負)。
// kind 0 = 円 (cx cy r)・1 = 長方形 (cx cy tx ty nx ny hl hd)・2 = 凸 (頂点 n 個・法線 n 個)
static inline float cut_shape_sd(int kind, const float* s, int n, float x, float y)
{
    if (kind == 0)
    {
        float dx = x - s[0], dy = y - s[1];
        return sqrtf(dx * dx + dy * dy) - s[2];
    }
    if (kind == 1)
    {
        float px = x - s[0], py = y - s[1];
        float qu = fabsf(px * s[2] + py * s[3]) - s[6];
        float qv = fabsf(px * s[4] + py * s[5]) - s[7];
        float ou = qu > 0.0f ? qu : 0.0f, ov = qv > 0.0f ? qv : 0.0f;
        float m = qu > qv ? qu : qv;
        return sqrtf(ou * ou + ov * ov) + (m < 0.0f ? m : 0.0f);
    }
    const float* v = s;
    const float* nn = s + n * 2;
    float inside = -FLT_MAX;
    bool outside = false;
    for (int i = 0; i < n; i++)
    {
        float d = (x - v[i * 2]) * nn[i * 2] + (y - v[i * 2 + 1]) * nn[i * 2 + 1];
        if (d > 0.0f) outside = true;
        if (d > inside) inside = d;
    }
    if (!outside) return inside;
    float best = FLT_MAX;
    for (int i = 0; i < n; i++)
    {
        int j = (i + 1) % n;
        float ax = v[i * 2], ay = v[i * 2 + 1];
        float sx = v[j * 2] - ax, sy = v[j * 2 + 1] - ay, l2 = sx * sx + sy * sy;
        float t = 0.0f;
        if (l2 > 0)
        {
            t = ((x - ax) * sx + (y - ay) * sy) / l2;
            t = t < 0.0f ? 0.0f : t > 1.0f ? 1.0f : t;
        }
        float dx = ax + sx * t - x, dy = ay + sy * t - y;
        float d2 = dx * dx + dy * dy;
        if (d2 < best) best = d2;
    }
    return sqrtf(best);
}

// 家具の範囲 (xMin yMin xMax yMax を n 個) のどれかの中か
static inline bool cut_inside_any(const float* keep, int n, float x, float y)
{
    for (int i = 0; i < n; i++)
    {
        const float* k = keep + i * 4;
        if (x >= k[0] && x <= k[2] && y >= k[1] && y <= k[3]) return true;
    }
    return false;
}
