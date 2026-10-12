#include "mrp_native.h"
#include <stdlib.h>
#include <string.h>

// 補修フォームの栓 1 枚の見える画素と隠れる玉 (src/Terrain/FoamArt.cs の Cull と同じ結果)。
// 損傷マスクの穴の画素を縦横に Grow 画素ずつ広げた所を見える画素にし、届く四角に見える画素が無い玉を隠す。
// 小数の式の順番は C# と同じ

struct FoamCullArgs
{
    const uint8_t* mask; int32_t mw, mh;    // 損傷マスク (r g b a・R = 穴)
    float ox, oy, mppu;                     // マスクの原点と 1 単位あたりの画素
    int32_t w, h;                           // 栓の絵の大きさ
    float u0, v0;                           // 絵の左下の (u, v)
    float ax, ay, ux, uy, vx, vy;           // 口の端と、絵の u 軸・v 軸の世界での向き
    const float* balls; int32_t ballN;      // 玉 (u v r を 3 個ずつ)
    float reach;                            // 玉の影響が届く距離 (半径の倍数)
    uint8_t* vis;                           // 出力: 見える画素 (w × h)
    uint8_t* hidden;                        // 出力: 隠れる玉 (ballN)
    int32_t hiddenN;                        // 出力: 隠れた玉の数
};

static const int Ppu = 64, Grow = 16;

static inline int imin(int a, int b) { return a < b ? a : b; }
static inline int imax(int a, int b) { return a > b ? a : b; }

MRP_API void mrp_foam_cull(FoamCullArgs* a)
{
    int w = a->w, h = a->h;
    const float inv = 1.0f / Ppu;
    uint8_t* hole = (uint8_t*)calloc((size_t)w * h, 1);
    uint8_t* tmp = (uint8_t*)calloc((size_t)w * h, 1);
    int32_t* run = (int32_t*)malloc(sizeof(int32_t) * (size_t)(imax(w, h) + 1));
    int32_t* sum = (int32_t*)calloc((size_t)(w + 1) * (h + 1), sizeof(int32_t));
    uint8_t* vis = a->vis;
    memset(vis, 0, (size_t)w * h);
    for (int y = 0; y < h; y++)
    {
        float v = a->v0 + (y + 0.5f) * inv;
        for (int x = 0; x < w; x++)
        {
            float u = a->u0 + (x + 0.5f) * inv;
            float wx = a->ax + a->ux * u + a->vx * v, wy = a->ay + a->uy * u + a->vy * v;
            int px = (int)((wx - a->ox) * a->mppu), py = (int)((wy - a->oy) * a->mppu);
            if (px < 0 || py < 0 || px >= a->mw || py >= a->mh) continue;
            if (a->mask[((size_t)py * a->mw + px) * 4] > 5) hole[y * w + x] = 1;
        }
    }
    // 横に広げる: [x − Grow, x + Grow] に穴が 1 つでもあれば立てる (行ごとの累積で数える)
    for (int y = 0; y < h; y++)
    {
        const uint8_t* src = hole + y * w;
        run[0] = 0;
        for (int x = 0; x < w; x++) run[x + 1] = run[x] + src[x];
        for (int x = 0; x < w; x++)
            if (run[imin(w - 1, x + Grow) + 1] - run[imax(0, x - Grow)] > 0) tmp[y * w + x] = 1;
    }
    // 縦に広げる
    for (int x = 0; x < w; x++)
    {
        run[0] = 0;
        for (int y = 0; y < h; y++) run[y + 1] = run[y] + tmp[y * w + x];
        for (int y = 0; y < h; y++)
            if (run[imin(h - 1, y + Grow) + 1] - run[imax(0, y - Grow)] > 0) vis[y * w + x] = 1;
    }
    // 見える画素の累積和で、玉の届く四角に見える画素が 1 つも無ければ隠す
    for (int y = 0; y < h; y++)
    {
        int row = 0;
        for (int x = 0; x < w; x++)
        {
            row += vis[y * w + x];
            sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
        }
    }
    int hidden = 0;
    for (int b = 0; b < a->ballN; b++)
    {
        float bu = a->balls[b * 3], bv = a->balls[b * 3 + 1], br = a->balls[b * 3 + 2];
        float reach = br * 1.15f * a->reach;
        int x0 = imax(0, (int)((bu - reach - a->u0) * Ppu)), x1 = imin(w, (int)((bu + reach - a->u0) * Ppu) + 2);
        int y0 = imax(0, (int)((bv - reach - a->v0) * Ppu)), y1 = imin(h, (int)((bv + reach - a->v0) * Ppu) + 2);
        bool hide = x1 <= x0 || y1 <= y0
            || sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0] == 0;
        a->hidden[b] = hide ? 1 : 0;
        if (hide) hidden++;
    }
    a->hiddenN = hidden;
    free(hole); free(tmp); free(run); free(sum);
}
