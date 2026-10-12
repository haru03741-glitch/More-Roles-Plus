#include "mrp_native.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

// 歩ける所の地図を作る時の升の計算 (src/Terrain/SolidMap.cs の Build と同じ結果)。
// 1. 壁の線の升を縦横それぞれ dilate 升太らせる (blocked)
// 2. 種の升から一番近い通れる升を探し、そこから 4 近傍で塗る。地図の端に届いた塗りは壁の外へ漏れたので捨てる
// 3. 太らせた分を戻す (壁の線そのものの升は閉じたまま)。段ごとにまとめて開ける
// 4. 歩ける所を 4 近傍のつながりで番号分けする (番号は升の並び順で最初に出会った順)
// 升の並びと塗る順は C# と同じにする (島の番号が全員の手元で同じになるように)

// 升 8 つを 1 語として読み書きする (升は 1 バイト)
static inline uint64_t ld8(const uint8_t* p) { uint64_t v; memcpy(&v, p, 8); return v; }
static inline void st8(uint8_t* p, uint64_t v) { memcpy(p, &v, 8); }
static const uint64_t Lo7 = 0x7F7F7F7F7F7F7F7FULL, Ones = 0x0101010101010101ULL;
// 0 でないバイトを 1 に
static inline uint64_t nz8(uint64_t v) { return ((((v & Lo7) + Lo7) | v) >> 7) & Ones; }

// 横に r 升以内に壁がある升 (tmp)・その縦に r 升以内 (dst) を 1 にする (C# の DilateGrid と同じ結果)。
// ずらした行どうしの OR を 8 升ずつ。0/1 に揃えるのは最後に 1 回
static void dilate(const uint8_t* __restrict src, uint8_t* __restrict tmp, uint8_t* __restrict dst, int w, int h, int r)
{
    for (int y = 0; y < h; y++)
    {
        const uint8_t* __restrict s = src + y * w;
        uint8_t* __restrict t = tmp + y * w;
        int x = 0;
        for (; x < r && x < w; x++)
        {
            uint8_t v = 0;
            for (int xx = x - r < 0 ? 0 : x - r, xe = x + r > w - 1 ? w - 1 : x + r; xx <= xe; xx++) v |= s[xx];
            t[x] = v;
        }
        for (; x + 8 <= w - r; x += 8)
        {
            uint64_t v = ld8(s + x);
            for (int d = 1; d <= r; d++) v |= ld8(s + x - d) | ld8(s + x + d);
            st8(t + x, v);
        }
        for (; x < w; x++)
        {
            uint8_t v = 0;
            for (int xx = x - r < 0 ? 0 : x - r, xe = x + r > w - 1 ? w - 1 : x + r; xx <= xe; xx++) v |= s[xx];
            t[x] = v;
        }
    }
    for (int y = 0; y < h; y++)
    {
        uint8_t* __restrict d = dst + y * w;
        int ya = y - r < 0 ? 0 : y - r, yb = y + r > h - 1 ? h - 1 : y + r;
        int x = 0;
        for (; x + 8 <= w; x += 8)
        {
            uint64_t v = 0;
            for (int yy = ya; yy <= yb; yy++) v |= ld8(tmp + yy * w + x);
            st8(d + x, nz8(v));
        }
        for (; x < w; x++)
        {
            uint8_t v = 0;
            for (int yy = ya; yy <= yb; yy++) v |= tmp[yy * w + x];
            d[x] = v != 0;
        }
    }
}

// 伸びる int 列 (塗りの積み・塗った横一列の記録)
struct IntBuf
{
    int32_t* d = nullptr; int n = 0, cap = 0; bool bad = false;
    ~IntBuf() { free(d); }
    inline void push(int32_t v)
    {
        if (n == cap)
        {
            int nc = cap < 1024 ? 1024 : cap * 2;
            int32_t* p = (int32_t*)realloc(d, sizeof(int32_t) * nc);
            if (!p) { bad = true; return; }
            d = p; cap = nc;
        }
        d[n++] = v;
    }
};

// (sx, sy) から can(k) の升を 4 近傍で塗る (mark)。横一列ずつ塗り、上下の行の続きを積む。
// 塗る升の集合は 1 升ずつの塗りと同じ (島の番号は呼ぶ側が升の並び順で決めるので塗る順に依らない)。
// 塗った横一列は spans へ (y, x0, x1)。地図の端に触れたら border
template <class Can, class Mark>
static int span_fill(int w, int h, int sx, int sy, Can can, Mark mark, IntBuf& stack, IntBuf* spans, bool& border)
{
    int cells = 0;
    stack.n = 0;
    stack.push(sx); stack.push(sy);
    while (stack.n > 0 && !stack.bad)
    {
        int y = stack.d[--stack.n], x = stack.d[--stack.n];
        int row = y * w;
        if (!can(row + x)) continue;
        int x0 = x, x1 = x;
        while (x0 > 0 && can(row + x0 - 1)) x0--;
        while (x1 < w - 1 && can(row + x1 + 1)) x1++;
        for (int i = x0; i <= x1; i++) mark(row + i);
        cells += x1 - x0 + 1;
        if (x0 == 0 || x1 == w - 1 || y == 0 || y == h - 1) border = true;
        if (spans) { spans->push(y); spans->push(x0); spans->push(x1); }
        for (int ny = y - 1; ny <= y + 1; ny += 2)
        {
            if (ny < 0 || ny >= h) continue;
            int nrow = ny * w;
            bool in = false;
            for (int i = x0; i <= x1; i++)
            {
                bool c = can(nrow + i);
                if (c && !in) { stack.push(i); stack.push(ny); }
                in = c;
            }
        }
    }
    return cells;
}

struct SolidFloodArgs
{
    const uint8_t* barrier;             // 壁の線の升
    int32_t w, h, dilate;
    const int32_t* seedX; const int32_t* seedY; int32_t seedN; // 種の升 (地図の升・範囲外もありうる)
    uint8_t* blocked;                   // 戻り: 太らせた壁
    uint8_t* tmp;                       // 置き場 (w*h)
    uint8_t* open;                      // 戻り: 歩ける所 (1)
    uint8_t* leaked;                    // 戻り: 漏れた塗り (1)
    uint8_t* island;                    // 戻り: 島の番号 (valid の時だけ書く)
    int32_t* queue;                     // 使わない (null・並びを変えないために残す)
    uint8_t* seedRes;                   // 戻り: 種ごと 0 = 使わない・1 = 受け入れた・2 = 漏れた
    int32_t openCells, islands, accepted, leakedN, valid; // 戻り
};

static int nearest_free(const uint8_t* blocked, int w, int h, int cx, int cy)
{
    for (int r = 0; r <= 3; r++)
    for (int dy = -r; dy <= r; dy++)
    for (int dx = -r; dx <= r; dx++)
    {
        int ax = dx < 0 ? -dx : dx, ay = dy < 0 ? -dy : dy;
        if ((ax > ay ? ax : ay) != r) continue;
        int x = cx + dx, y = cy + dy;
        if (x <= 0 || y <= 0 || x >= w - 1 || y >= h - 1) continue;
        if (blocked[y * w + x] == 0) return y * w + x;
    }
    return -1;
}

MRP_API void mrp_solid_flood(SolidFloodArgs* a)
{
    int w = a->w, h = a->h, n = w * h;
    const uint8_t* barrier = a->barrier;
    uint8_t* blocked = a->blocked;
    uint8_t* open = a->open;
    dilate(barrier, a->tmp, blocked, w, h, a->dilate);
    memset(open, 0, n);
    int accepted = 0, leakedN = 0, openCells = 0;
    IntBuf stack, spans;
    for (int s = 0; s < a->seedN; s++)
    {
        a->seedRes[s] = 0;
        int start = nearest_free(blocked, w, h, a->seedX[s], a->seedY[s]);
        if (start < 0 || open[start] != 0) continue;
        bool border = false;
        spans.n = 0;
        int cells = span_fill(w, h, start % w, start / w,
            [&](int k) { return open[k] == 0 && blocked[k] == 0; }, [&](int k) { open[k] = 1; }, stack, &spans, border);
        if (stack.bad || spans.bad) { a->openCells = openCells; a->accepted = accepted; a->leakedN = leakedN; a->valid = 0; a->islands = 0; return; }
        if (border)
        {
            for (int i = 0; i < spans.n; i += 3) memset(open + spans.d[i] * w + spans.d[i + 1], 2, spans.d[i + 2] - spans.d[i + 1] + 1);
            leakedN++;
            a->seedRes[s] = 2;
        }
        else
        {
            accepted++;
            openCells += cells;
            a->seedRes[s] = 1;
        }
    }
    memset(a->leaked, 0, n);
    if (leakedN > 0)
        for (int k = 0; k < n; k++) { uint8_t l = open[k] == 2; a->leaked[k] = l; open[k] = l ? 0 : open[k]; }

    // 太らせた分を戻す: 太らせた壁の中で壁の線でない升のうち、上下左右に歩ける所がある升を段ごとにまとめて開ける。
    // 1 段の中では前の段の歩ける所だけを見る (先に全部の升を決めてから開ける)。ここで歩ける所は 0/1 だけ
    uint8_t* g = a->tmp;
    uint8_t* zero = (uint8_t*)calloc(w, 1);
    if (!zero) { a->openCells = openCells; a->accepted = accepted; a->leakedN = leakedN; a->valid = 0; a->islands = 0; return; }
    for (int step = 0; step <= a->dilate; step++)
    {
        int grown = 0;
        for (int y = 0; y < h; y++)
        {
            const uint8_t* __restrict op = open + y * w;
            const uint8_t* __restrict up = y > 0 ? op - w : zero;
            const uint8_t* __restrict dn = y < h - 1 ? op + w : zero;
            const uint8_t* __restrict bk = blocked + y * w;
            const uint8_t* __restrict br = barrier + y * w;
            uint8_t* __restrict gr = g + y * w;
            auto one = [&](int x)
            {
                uint8_t l = x > 0 ? op[x - 1] : 0, r = x < w - 1 ? op[x + 1] : 0;
                uint8_t v = (uint8_t)((bk[x] != 0) & (br[x] == 0) & (op[x] == 0) & ((l | r | up[x] | dn[x]) != 0));
                gr[x] = v;
                grown += v;
            };
            // 両端の升は 1 升ずつ・間は 8 升ずつ (左右の升を読むので x - 1 から x + 8 までが行の中)
            int x = 0;
            if (w > 0) one(x++);
            for (; x + 9 <= w; x += 8)
            {
                uint64_t nb = ld8(op + x - 1) | ld8(op + x + 1) | ld8(up + x) | ld8(dn + x);
                uint64_t v = nz8(nb) & nz8(ld8(bk + x)) & ~nz8(ld8(br + x)) & ~nz8(ld8(op + x)) & Ones;
                st8(gr + x, v);
                grown += (int)((v * Ones) >> 56);
            }
            for (; x < w; x++) one(x);
        }
        int k = 0;
        for (; k + 8 <= n; k += 8) st8(open + k, ld8(open + k) | ld8(g + k));
        for (; k < n; k++) open[k] |= g[k];
        openCells += grown;
    }
    free(zero);

    a->openCells = openCells;
    a->accepted = accepted;
    a->leakedN = leakedN;
    a->valid = accepted > 0 && openCells > n / 50;
    a->islands = 0;
    if (!a->valid) return;

    // 島の番号 = 升の並び順で最初に出会った順
    uint8_t* island = a->island;
    memset(island, 0, n);
    int count = 0;
    bool unused = false;
    for (int s = 0; s < n; s++)
    {
        if ((s & 7) == 0 && s + 8 <= n && (nz8(ld8(open + s)) & ~nz8(ld8(island + s)) & Ones) == 0) { s += 7; continue; }
        if (open[s] == 0 || island[s] != 0) continue;
        uint8_t id = count < 254 ? (uint8_t)(count + 1) : (uint8_t)255;
        count++;
        span_fill(w, h, s % w, s / w,
            [&](int k) { return open[k] != 0 && island[k] == 0; }, [&](int k) { island[k] = id; }, stack, nullptr, unused);
        if (stack.bad) { a->valid = 0; a->islands = 0; return; }
    }
    a->islands = count;
}

// 船の外 (SolidMap.Outside)。三角形 (ワールド座標 6 個ずつ) の中の升を絵として塗り、絵を dilate 升太らせて、
// 太らせた絵にも壁にも歩ける所にも掛からない升 = 1 (外)・絵は無いが外にも数えない升 = 2
struct SolidOutsideArgs
{
    uint8_t* art;                       // 絵の升 (C# が船体の塊を塗ってから渡す)・三角形をここへ足す
    const float* tris; int32_t triN;
    int32_t w, h; float ox, oy, ppu; int32_t dilate;
    const uint8_t* blocked; const uint8_t* open;
    uint8_t* tmp; uint8_t* grown;       // 置き場 (w*h)
    uint8_t* outside;                   // 戻り
};

static inline float fmin3(float a, float b, float c) { float m = a < b ? a : b; return m < c ? m : c; }
static inline float fmax3(float a, float b, float c) { float m = a > b ? a : b; return m > c ? m : c; }

// 閉じた輪郭 (3 辺) の中の升を塗る (升の中心で偶奇)。C# の FillPolygon と同じ式の順
static void fill_tri(uint8_t* grid, int w, int h, float ox, float oy, float ppu, const float* t)
{
    float px[3] = { t[0], t[2], t[4] }, py[3] = { t[1], t[3], t[5] };
    float minY = fmin3(py[0], py[1], py[2]), maxY = fmax3(py[0], py[1], py[2]);
    int y0 = (int)floorf((minY - oy) * ppu), y1 = (int)floorf((maxY - oy) * ppu);
    if (y0 < 0) y0 = 0;
    if (y1 > h - 1) y1 = h - 1;
    for (int y = y0; y <= y1; y++)
    {
        float wy = oy + ((float)y + 0.5f) / ppu;
        float xs[3]; int c = 0;
        for (int i = 0; i < 3; i++)
        {
            float ax = px[i], ay = py[i], bx = px[(i + 1) % 3], by = py[(i + 1) % 3];
            if ((ay > wy) == (by > wy)) continue;
            xs[c++] = ax + (wy - ay) / (by - ay) * (bx - ax);
        }
        // 並べ替え (偶奇なので同じ値の順は結果に効かない)
        for (int i = 1; i < c; i++)
            for (int j = i; j > 0 && xs[j - 1] > xs[j]; j--) { float s = xs[j]; xs[j] = xs[j - 1]; xs[j - 1] = s; }
        for (int k = 0; k + 1 < c; k += 2)
        {
            int x0 = (int)ceilf((xs[k] - ox) * ppu - 0.5f);
            int x1 = (int)floorf((xs[k + 1] - ox) * ppu - 0.5f);
            if (x0 < 0) x0 = 0;
            if (x1 > w - 1) x1 = w - 1;
            if (x1 >= x0) memset(grid + y * w + x0, 1, x1 - x0 + 1);
        }
    }
}

MRP_API void mrp_solid_outside(SolidOutsideArgs* a)
{
    int w = a->w, h = a->h, n = w * h;
    for (int i = 0; i < a->triN; i++) fill_tri(a->art, w, h, a->ox, a->oy, a->ppu, a->tris + i * 6);
    dilate(a->art, a->tmp, a->grown, w, h, a->dilate);
    const uint8_t* grown = a->grown; const uint8_t* blocked = a->blocked; const uint8_t* open = a->open; const uint8_t* art = a->art;
    uint8_t* out = a->outside;
    // 外 = 1・絵が無い = 2・他 = 0 (8 升ずつ)
    int k = 0;
    for (; k + 8 <= n; k += 8)
    {
        uint64_t z = ~nz8(ld8(grown + k) | ld8(blocked + k) | ld8(open + k)) & Ones, na = ~nz8(ld8(art + k)) & Ones;
        st8(out + k, z | ((na & ~z & Ones) << 1));
    }
    for (; k < n; k++)
    {
        uint8_t z = (uint8_t)((grown[k] | blocked[k] | open[k]) == 0), na = (uint8_t)(art[k] == 0);
        out[k] = (uint8_t)(z | ((na & (z ^ 1)) << 1));
    }
}
