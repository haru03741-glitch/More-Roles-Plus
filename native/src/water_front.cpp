#include "mrp_native.h"
#include <math.h>

// 水の影の写しの抜き型: 家具の絵の網目の三角形を、画素の真ん中が入る所だけ 255 で塗る
// (src/Terrain/WaterArt.cs の FillTrisCs と同じ結果)。頂点は抜き型の画素の座標へ直してから渡す。
// 小数の式の順番は C# と同じ

struct FrontFillArgs
{
    const float* px; const float* py;   // 頂点 (抜き型の画素の座標・画素の真ん中が整数)
    const uint16_t* tri; int32_t triN;  // 三角形の頂点の番号 (3 個ずつ・triN = 番号の数)
    int32_t m;                          // 抜き型の一辺の画素数
    uint8_t* buf;                       // 塗る先 (m × m)
    int32_t any;                        // 出力: 1 画素でも塗ったか
};

static inline int imin(int a, int b) { return a < b ? a : b; }
static inline int imax(int a, int b) { return a > b ? a : b; }

MRP_API void mrp_front_fill(FrontFillArgs* a)
{
    const float* px = a->px; const float* py = a->py;
    int m = a->m;
    uint8_t* buf = a->buf;
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
            buf[y * m + x] = 255;
            any = 1;
        }
    }
    a->any = any;
}
