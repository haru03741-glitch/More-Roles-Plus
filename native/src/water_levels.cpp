#include "mrp_native.h"
#include <bit>

// 水の升を作る時の下ごしらえ (src/Terrain/WaterOpen.cs の LoadWet の濾し・WaterSim.IslandLevels の升ごとの島・
// SpreadWetIslands と同じ結果)

// 表の「水が届く升」のビット列から、歩ける升と船の外を除いた升を wet に 1 にする (wet は 0 で渡す)
struct WetFilterArgs
{
    const uint8_t* bits;                // (n + 7) / 8 バイト・升 k = bits[k >> 3] の (k & 7) ビット目
    const uint8_t* solid;               // SolidMap の歩ける所
    const uint8_t* outside;             // SolidMap の船の外 (1 = 外・null = 無い)
    int32_t n;
    uint8_t* wet;                       // 戻り
    int32_t count;                      // 戻り: 1 にした升の数
};

MRP_API void mrp_wet_filter(WetFilterArgs* a)
{
    const uint8_t* bits = a->bits;
    const uint8_t* solid = a->solid;
    const uint8_t* outside = a->outside;
    uint8_t* wet = a->wet;
    int n = a->n, cnt = 0;
    for (int k8 = 0; k8 < n; k8 += 8)
    {
        unsigned b = bits[k8 >> 3];
        while (b != 0)
        {
            int k = k8 + std::countr_zero(b);
            b &= b - 1;
            if (k >= n || solid[k] != 0 || (outside && outside[k] == 1)) continue;
            wet[k] = 1;
            cnt++;
        }
    }
    a->count = cnt;
}

// 水の升ごとに、升の中の最初の歩ける升 (ビットの低い順) の島を isl へ・島ごとの升の数を area へ。
// 歩ける升を含まない (水だけが届く) 升は wetOnly へ 1。spread なら、その升へ開いている隣から島を幅優先で渡す
// (升の番号の順に並べた島のある升から・隣は右・左・上・下の順)。isl・wetOnly・area は 0 で渡す
struct IslandCellsArgs
{
    const uint16_t* sub;                // 水の升ごとの SolidMap の升のビット (Sub × Sub)
    int32_t w, h, subN;                 // 水の升の並び・Sub
    const uint8_t* solid;               // SolidMap の歩ける所
    const uint8_t* island;              // SolidMap の島の番号 (null = 無い)
    int32_t sw;                         // SolidMap の幅
    int32_t areaN;                      // area の長さ (島の数 + 1)
    int32_t spread;
    uint8_t* isl; uint8_t* wetOnly; int32_t* area; // 戻り
    int32_t* queue;                     // 置き場 (w*h)
    int32_t left;                       // 戻り: 島をもらえなかった水だけの升の数
};

MRP_API void mrp_island_cells(IslandCellsArgs* a)
{
    const int w = a->w, n = a->w * a->h, sub = a->subN, sw = a->sw;
    const uint16_t* subs = a->sub;
    const uint8_t* solid = a->solid;
    const uint8_t* island = a->island;
    uint8_t* isl = a->isl;
    uint8_t* wetOnly = a->wetOnly;
    for (int k = 0; k < n; k++)
    {
        unsigned bits = subs[k];
        if (bits == 0) continue;
        int x = k % w, y = k / w, s = -1;
        while (bits != 0)
        {
            int b = std::countr_zero(bits);
            bits &= bits - 1;
            int c = (y * sub + b / sub) * sw + x * sub + b % sub;
            if (solid[c] != 0) { s = c; break; }
        }
        if (s < 0) { wetOnly[k] = 1; continue; }
        int id = island ? island[s] : 0;
        if (id <= 0 || id >= a->areaN) continue;
        isl[k] = (uint8_t)id;
        a->area[id]++;
    }
    a->left = 0;
    if (!a->spread) return;
    int32_t* q = a->queue;
    int head = 0, tail = 0, left = 0;
    for (int k = 0; k < n; k++)
    {
        if (subs[k] == 0) continue;
        if (isl[k] != 0) q[tail++] = k;
        else if (wetOnly[k] != 0) left++;
    }
    const int step[4] = { 1, -1, w, -w };
    while (head < tail && left > 0)
    {
        int k = q[head++];
        for (int d = 0; d < 4; d++)
        {
            int j = k + step[d];
            if (j < 0 || j >= n || wetOnly[j] == 0 || isl[j] != 0) continue;
            isl[j] = isl[k];
            left--;
            q[tail++] = j;
        }
    }
    a->left = left;
}
