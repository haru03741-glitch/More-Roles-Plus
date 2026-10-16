#include "mrp_native.h"
#include <math.h>

// 水の床マスクの画素を作る (src/Terrain/FloorMask.cs の FillRaw / Fill と同じ結果)。
// 小数の式は C# と同じ順に float で計算する (縮約 (FMA) を切ってビルドする) ので、端末ごとの結果も C# 版と 1 バイトまで同じ

MRP_API int32_t mrp_abi() { return MRP_NATIVE_ABI; }

// 列ごと・行ごとに前もって計算する表の上限 (タイルの画素は 600 弱)
static const int MaxN = 2048;

// 床の 1 ビット地図から n×n 画素 (画素 k の真ん中 = x + k × wstep)。床 = 255。
// holes が null でなければ、床でない画素のうち穴の値 (損傷マスク RGBA の R を双線形で) が holeFloor 以上の所も床にする。
// 列の位置は行によらないので表にしておく (式は C# の FillRaw / DamageMap.HoleSmoothAt と同じ)
MRP_API void mrp_floor_raw(
    const uint8_t* bits, int32_t mw, int32_t mh, float mox, float moy, float mppu,
    float x, float y, float wstep, int32_t n, uint8_t* into,
    const uint8_t* holes, int32_t hw, int32_t hh, float hox, float hoy, float hppu, int32_t holeFloor)
{
    if (n <= 0 || n > MaxN) return;
    int32_t col[MaxN];
    float step = wstep * mppu;
    float bx = (x - mox) * mppu, by = (y - moy) * mppu;
    for (int px = 0; px < n; px++)
    {
        int mx = (int)(bx + (float)px * step);
        col[px] = mx >= 0 && mx < mw ? mx : -1;
    }
    for (int py = 0; py < n; py++)
    {
        int my = (int)(by + (float)py * step);
        uint8_t* row = into + (intptr_t)py * n;
        if (my < 0 || my >= mh)
        {
            for (int px = 0; px < n; px++) row[px] = 0;
            continue;
        }
        int baseK = my * mw;
        for (int px = 0; px < n; px++)
        {
            int mx = col[px];
            row[px] = mx >= 0 && (bits[(baseK + mx) >> 3] & (1 << ((baseK + mx) & 7))) != 0 ? 255 : 0;
        }
    }
    if (!holes) return;
    // 穴の地図の列 (左の画素と、その間の割合)。地図の外は -1
    float tcol[MaxN];
    for (int px = 0; px < n; px++)
    {
        float fx = (x + (float)px * wstep - hox) * hppu - 0.5f;
        int x0 = (int)floorf(fx);
        col[px] = x0 < 0 || x0 + 1 >= hw ? -1 : x0;
        tcol[px] = fx - (float)x0;
    }
    for (int py = 0; py < n; py++)
    {
        float fy = (y + (float)py * wstep - hoy) * hppu - 0.5f;
        int y0 = (int)floorf(fy);
        if (y0 < 0 || y0 + 1 >= hh) continue;
        float ty = fy - (float)y0;
        const uint8_t* r0 = holes + (intptr_t)y0 * hw * 4;
        const uint8_t* r1 = r0 + (intptr_t)hw * 4;
        uint8_t* row = into + (intptr_t)py * n;
        for (int px = 0; px < n; px++)
        {
            int x0 = col[px];
            if (row[px] != 0 || x0 < 0) continue;
            int i = x0 * 4;
            int p00 = r0[i], p01 = r0[i + 4], p10 = r1[i], p11 = r1[i + 4];
            // 4 つとも 0 なら穴の値は 0 (式を通しても 0)
            if ((p00 | p01 | p10 | p11) == 0) continue;
            float tx = tcol[px];
            float a = (float)p00 + (float)(p01 - p00) * tx;
            float b = (float)p10 + (float)(p11 - p10) * tx;
            if ((int)(a + (b - a) * ty) >= holeFloor) row[px] = 255;
        }
    }
}

// 落ちる縁の升 (fall の向きのうち lip に無いものがある升) の床でない画素を床にする (WaterSim.StampLips と同じ)。
// 升 = 原点 (ox, oy) から 1 辺 cell の格子・w×h 升
// 落ちる縁のどれかで、画素から内へ縁から LipReach (滝の膜が崖の縁を探す幅と同じ) までに床マスクの床があるか。あれば塗らない
// (水の絵は床マスクの形で崖の縁まで届き、膜はそこから始まる)。絵の床がそれより手前で終わる所だけ塗る。
// 画素ごとに決める (升ごとに真ん中の列だけで決めると、2 方向へ落ちる角の升で、床の絵の外の列が升ごと床に塗られる)
static const float LipStep = 0.02f, LipReach = 0.3f;
static const int LipNx[4] = { 1, -1, 0, 0 }, LipNy[4] = { 0, 0, 1, -1 };

static bool lip_beyond(int bits, int cx, int cy, float wx, float wy, float ox, float oy, float cell,
    const uint8_t* mb, int32_t mw, int32_t mh, float mox, float moy, float mppu)
{
    if (!mb) return false;
    for (int d = 0; d < 4; d++)
    {
        if ((bits >> d & 1) == 0) continue;
        float ex = ox + ((float)cx + 0.5f) * cell + (float)LipNx[d] * (0.5f * cell);
        float ey = oy + ((float)cy + 0.5f) * cell + (float)LipNy[d] * (0.5f * cell);
        float p = LipNx[d] != 0 ? (ex - wx) * (float)LipNx[d] : (ey - wy) * (float)LipNy[d];
        for (int s = 1; p + (float)s * LipStep <= LipReach; s++)
        {
            float qx = wx - (float)LipNx[d] * ((float)s * LipStep), qy = wy - (float)LipNy[d] * ((float)s * LipStep);
            int mx = (int)floorf((qx - mox) * mppu), my = (int)floorf((qy - moy) * mppu);
            if (mx < 0 || my < 0 || mx >= mw || my >= mh) continue;
            int i = my * mw + mx;
            if ((mb[i >> 3] & (1 << (i & 7))) != 0) return true;
        }
    }
    return false;
}

MRP_API void mrp_floor_lips(const uint8_t* fall, const uint8_t* lip, int32_t w, int32_t h, float ox, float oy, float cell,
    float x, float y, float step, int32_t n, uint8_t* into,
    const uint8_t* mb, int32_t mw, int32_t mh, float mox, float moy, float mppu)
{
    if (n <= 0 || n > MaxN) return;
    int32_t col[MaxN];
    float inv = 1.0f / cell;
    for (int px = 0; px < n; px++)
    {
        int cx = (int)floorf((x + (float)px * step - ox) * inv);
        col[px] = cx < 0 || cx >= w ? -1 : cx;
    }
    for (int py = 0; py < n; py++)
    {
        int cy = (int)floorf((y + (float)py * step - oy) * inv);
        if (cy < 0 || cy >= h) continue;
        uint8_t* row = into + (intptr_t)py * n;
        int baseK = cy * w;
        for (int px = 0; px < n; px++)
        {
            int cx = col[px];
            if (row[px] != 0 || cx < 0) continue;
            int k = baseK + cx;
            int b = fall[k] & ~lip[k];
            if (b != 0 && !lip_beyond(b, cx, cy, x + (float)px * step, y + (float)py * step, ox, oy, cell, mb, mw, mh, mox, moy, mppu)) row[px] = 255;
        }
    }
}

// raw (m×m・m = n + 4) を 1, 4, 6, 4, 1 で縦横にならして into (n×n) へ。row は m×n の作業場所
MRP_API void mrp_floor_blur(const uint8_t* raw, int32_t n, uint16_t* row, uint8_t* into)
{
    int m = n + 4;
    for (int py = 0; py < m; py++)
    {
        const uint8_t* s = raw + (intptr_t)py * m;
        uint16_t* d = row + (intptr_t)py * n;
        for (int px = 0; px < n; px++)
            d[px] = (uint16_t)(s[px] + s[px + 4] + ((s[px + 1] + s[px + 3]) << 2) + s[px + 2] * 6);
    }
    for (int py = 0; py < n; py++)
    {
        const uint16_t* r = row + (intptr_t)py * n;
        uint8_t* d = into + (intptr_t)py * n;
        for (int px = 0; px < n; px++)
        {
            int v = r[px] + r[px + n * 4] + ((r[px + n] + r[px + n * 3]) << 2) + r[px + n * 2] * 6;
            d[px] = (uint8_t)((v + 128) >> 8);
        }
    }
}
