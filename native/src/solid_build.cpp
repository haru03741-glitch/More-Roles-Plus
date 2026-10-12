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

// 横に r 升以内に壁がある升 (tmp)・その縦に r 升以内 (dst) を 1 にする (C# の DilateGrid と同じ結果)。
// ずらした行どうしの OR で書く (分岐が無く、まとめて処理できる)
static void dilate(const uint8_t* __restrict src, uint8_t* __restrict tmp, uint8_t* __restrict dst, int w, int h, int r)
{
    for (int y = 0; y < h; y++)
    {
        const uint8_t* __restrict s = src + y * w;
        uint8_t* __restrict t = tmp + y * w;
        for (int x = 0; x < w; x++) t[x] = s[x];
        for (int d = 1; d <= r; d++)
        {
            for (int x = d; x < w; x++) t[x] |= s[x - d];
            for (int x = 0; x + d < w; x++) t[x] |= s[x + d];
        }
        for (int x = 0; x < w; x++) t[x] = t[x] != 0;
    }
    for (int y = 0; y < h; y++)
    {
        uint8_t* __restrict d = dst + y * w;
        int ya = y - r < 0 ? 0 : y - r, yb = y + r > h - 1 ? h - 1 : y + r;
        memcpy(d, tmp + ya * w, w);
        for (int yy = ya + 1; yy <= yb; yy++)
        {
            const uint8_t* __restrict t = tmp + yy * w;
            for (int x = 0; x < w; x++) d[x] |= t[x];
        }
    }
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
    int32_t* queue;                     // 置き場 (w*h)
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
    int32_t* queue = a->queue;
    dilate(barrier, a->tmp, blocked, w, h, a->dilate);

    memset(open, 0, n);
    int accepted = 0, leakedN = 0, openCells = 0;
    for (int s = 0; s < a->seedN; s++)
    {
        a->seedRes[s] = 0;
        int start = nearest_free(blocked, w, h, a->seedX[s], a->seedY[s]);
        if (start < 0 || open[start] != 0) continue;
        int head = 0, tail = 0;
        bool border = false;
        open[start] = 1;
        queue[tail++] = start;
        while (head < tail)
        {
            int k = queue[head++];
            int x = k % w, y = k / w;
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border = true;
            int nb[4]; int c = 0;
            if (x > 0) nb[c++] = k - 1;
            if (x < w - 1) nb[c++] = k + 1;
            if (y > 0) nb[c++] = k - w;
            if (y < h - 1) nb[c++] = k + w;
            for (int i = 0; i < c; i++)
            {
                int k2 = nb[i];
                if (open[k2] != 0 || blocked[k2] != 0) continue;
                open[k2] = 1;
                queue[tail++] = k2;
            }
        }
        if (border)
        {
            for (int i = 0; i < tail; i++) open[queue[i]] = 2;
            leakedN++;
            a->seedRes[s] = 2;
        }
        else
        {
            accepted++;
            openCells += tail;
            a->seedRes[s] = 1;
        }
    }
    memset(a->leaked, 0, n);
    if (leakedN > 0)
        for (int k = 0; k < n; k++) if (open[k] == 2) { open[k] = 0; a->leaked[k] = 1; }

    // 戻せる升は太らせた壁の中で壁の線でない所だけなので、その升 (行・列を詰めた値) を先に集めて段ごとに見る。
    // 升の番号の順に見るので、全部の升を見るのと同じ結果
    int cn = 0;
    for (int k = 0; k < n; k++) if (blocked[k] != 0 && barrier[k] == 0) cn++;
    int32_t* cand = (int32_t*)malloc(sizeof(int32_t) * (cn > 0 ? cn : 1));
    if (!cand) { a->openCells = openCells; a->accepted = accepted; a->leakedN = leakedN; a->valid = 0; a->islands = 0; return; }
    bool packed = w <= 0xFFFF && h <= 0x7FFF; // 詰められない大きさの地図は升の番号のまま持って割り算で戻す
    cn = 0;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int k = y * w + x;
            if (blocked[k] != 0 && barrier[k] == 0) cand[cn++] = packed ? (y << 16) | x : k;
        }
    for (int step = 0; step <= a->dilate; step++)
    {
        int grown = 0;
        for (int i = 0; i < cn; i++)
        {
            int x, y, k;
            if (packed) { x = cand[i] & 0xFFFF; y = cand[i] >> 16; k = y * w + x; }
            else { k = cand[i]; y = k / w; x = k - y * w; }
            if (open[k] != 0) continue;
            if ((x > 0 && open[k - 1] == 1) || (x < w - 1 && open[k + 1] == 1) ||
                (y > 0 && open[k - w] == 1) || (y < h - 1 && open[k + w] == 1))
                queue[grown++] = k;
        }
        for (int i = 0; i < grown; i++) open[queue[i]] = 1;
        openCells += grown;
    }
    free(cand);

    a->openCells = openCells;
    a->accepted = accepted;
    a->leakedN = leakedN;
    a->valid = accepted > 0 && openCells > n / 50;
    a->islands = 0;
    if (!a->valid) return;

    uint8_t* island = a->island;
    memset(island, 0, n);
    int count = 0;
    for (int s = 0; s < n; s++)
    {
        if (open[s] == 0 || island[s] != 0) continue;
        uint8_t id = count < 254 ? (uint8_t)(count + 1) : (uint8_t)255;
        count++;
        int head = 0, tail = 0;
        island[s] = id;
        queue[tail++] = s;
        while (head < tail)
        {
            int k = queue[head++];
            int x = k % w;
            int nb[4]; int c = 0;
            if (x > 0) nb[c++] = k - 1;
            if (x < w - 1) nb[c++] = k + 1;
            if (k >= w) nb[c++] = k - w;
            if (k + w < n) nb[c++] = k + w;
            for (int i = 0; i < c; i++)
            {
                int k2 = nb[i];
                if (open[k2] == 0 || island[k2] != 0) continue;
                island[k2] = id;
                queue[tail++] = k2;
            }
        }
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
            for (int x = x0; x <= x1; x++) grid[y * w + x] = 1;
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
    for (int k = 0; k < n; k++)
        out[k] = grown[k] == 0 && blocked[k] == 0 && open[k] == 0 ? 1 : art[k] == 0 ? 2 : 0;
}
