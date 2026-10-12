#include "mrp_native.h"
#include <bit>
#include <string.h>

// 水の升の形を作り直す (src/Terrain/WaterSim.cs の Rebuild と同じ結果)。
// 水の升 1 つ = SolidMap の升 sub×sub 個。歩ける升 (か水だけが届く升) の並びを升ごとのビットにし、
// 4 個未満・ばらばら・閉じた扉の升は 0。境の通れる向きは範囲と外周 1 升に書く。
// 開いているか (open) が変わった升は範囲の升の番号順に changed へ並べる (水の寄せと起こしは C# が同じ順に行う)

struct CellsArgs
{
    const uint8_t* solid; const uint8_t* wet;      // SolidMap の升ごと (wet は無ければ null)
    int32_t sw; int32_t w, h; int32_t sub;
    int32_t x0, y0, x1, y1;                         // 水の升の範囲 (含む・C# で端を切ってある)
    const uint8_t* doorFloor; const uint8_t* platFloor; const uint8_t* shut; const uint8_t* open;
    uint16_t* subOut; uint8_t* edgeOut;
    int32_t* changed; int32_t changedN;             // 戻り: 開閉が変わった升
};

static bool connected(int bits, int sub)
{
    if (sub == 4)
    {
        // 4×4 は 1 回で全部の升を上下左右へ広げる (左端の列 0x1111・右端の列 0x8888 から横へはみ出さない)
        int seen = bits & -bits, frontier = seen;
        while (frontier != 0)
        {
            int grow = ((frontier << 1) & ~0x1111) | ((frontier >> 1) & ~0x8888) | (frontier << 4) | (frontier >> 4);
            grow &= bits & ~seen;
            seen |= grow;
            frontier = grow;
        }
        return seen == bits;
    }
    int first = bits & -bits;
    int seen = first, frontier = first;
    while (frontier != 0)
    {
        int grow = 0;
        for (int i = 0; i < sub * sub; i++)
        {
            if ((frontier >> i & 1) == 0) continue;
            int x = i % sub, y = i / sub;
            if (x > 0) grow |= 1 << (i - 1);
            if (x < sub - 1) grow |= 1 << (i + 1);
            if (y > 0) grow |= 1 << (i - sub);
            if (y < sub - 1) grow |= 1 << (i + sub);
        }
        grow &= bits & ~seen;
        seen |= grow;
        frontier = grow;
    }
    return seen == bits;
}

// 2 つの升の境で向かい合う歩ける升の組の数。column なら a の列 ia と b の列 ib、そうでなければ行
static int facing(int a, int b, int ia, int ib, bool column, int sub)
{
    int n = 0;
    for (int i = 0; i < sub; i++)
    {
        int pa = column ? i * sub + ia : ia * sub + i;
        int pb = column ? i * sub + ib : ib * sub + i;
        if ((a >> pa & 1) != 0 && (b >> pb & 1) != 0) n++;
    }
    return n;
}

// 4 バイトのうち 0 でない物を下位 4 ビットに (バイト 0 → ビット 0)
static inline int nonzero4(uint32_t v)
{
    uint32_t t = ((v & 0x7F7F7F7Fu) + 0x7F7F7F7Fu) | v;   // 0 でないバイトの最上位ビットが立つ
    t = (t >> 7) & 0x01010101u;
    return (int)((t * 0x01020408u) >> 24) & 0xF;
}

// 4×4 の升の境で向かい合う歩ける升の組が 2 つ以上か (+x・−x・+y・−y)
static inline bool facing4(int a, int b, int d)
{
    int m;
    switch (d)
    {
        case 0: m = (a >> 3) & b & 0x1111; break;
        case 1: m = a & (b >> 3) & 0x1111; break;
        case 2: m = (a >> 12) & b & 0xF; break;
        default: m = a & (b >> 12) & 0xF; break;
    }
    return std::popcount((unsigned)m) >= 2;
}

MRP_API void mrp_water_cells(CellsArgs* a)
{
    const int w = a->w, h = a->h, sub = a->sub, sw = a->sw;
    int nc = 0;
    for (int y = a->y0; y <= a->y1; y++)
    for (int x = a->x0; x <= a->x1; x++)
    {
        int bits = 0;
        if (sub == 4)
            for (int sy = 0, row = y * 4 * sw + x * 4; sy < 4; sy++, row += sw)
            {
                uint32_t v;
                memcpy(&v, a->solid + row, 4);
                if (a->wet != nullptr) { uint32_t u; memcpy(&u, a->wet + row, 4); v |= u; }
                bits |= nonzero4(v) << (sy * 4);
            }
        else
            for (int sy = 0, row = y * sub * sw + x * sub; sy < sub; sy++, row += sw)
            for (int sx = 0; sx < sub; sx++)
                if (a->solid[row + sx] != 0 || a->wet != nullptr && a->wet[row + sx] != 0) bits |= 1 << (sy * sub + sx);
        int k = y * w + x;
        if (a->doorFloor[k] != 0 || a->platFloor[k] != 0) bits = 0xFFFF;
        if (std::popcount((unsigned)bits) < 4 || !connected(bits, sub) || a->shut[k] != 0) bits = 0;
        a->subOut[k] = (uint16_t)bits;
        uint8_t open = bits != 0 ? 1 : 0;
        if (open != a->open[k]) a->changed[nc++] = k;
    }
    a->changedN = nc;
    int ey0 = a->y0 - 1 < 1 ? 1 : a->y0 - 1, ey1 = a->y1 + 1 > h - 2 ? h - 2 : a->y1 + 1;
    int ex0 = a->x0 - 1 < 1 ? 1 : a->x0 - 1, ex1 = a->x1 + 1 > w - 2 ? w - 2 : a->x1 + 1;
    const uint16_t* s = a->subOut;
    for (int y = ey0; y <= ey1; y++)
    for (int x = ex0; x <= ex1; x++)
    {
        int k = y * w + x;
        int e = 0;
        int c = s[k];
        if (c != 0 && sub == 4)
        {
            if (facing4(c, s[k + 1], 0)) e |= 1;
            if (facing4(c, s[k - 1], 1)) e |= 2;
            if (facing4(c, s[k + w], 2)) e |= 4;
            if (facing4(c, s[k - w], 3)) e |= 8;
        }
        else if (c != 0)
        {
            if (facing(c, s[k + 1], sub - 1, 0, true, sub) >= 2) e |= 1;   // +x
            if (facing(c, s[k - 1], 0, sub - 1, true, sub) >= 2) e |= 2;   // −x
            if (facing(c, s[k + w], sub - 1, 0, false, sub) >= 2) e |= 4;  // +y
            if (facing(c, s[k - w], 0, sub - 1, false, sub) >= 2) e |= 8;  // −y
        }
        a->edgeOut[k] = (uint8_t)e;
    }
}
