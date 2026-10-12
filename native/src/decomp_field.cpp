#include "mrp_native.h"

// 外壁が抜けた時の気圧の範囲と口からの道のり (src/Terrain/Decompression.cs の Regions・Field・FindGrips と同じ結果)。
// 置き場は全部 C# が渡す (ここではメモリを取らない)

static const int Nx[8] = { 1, -1, 0, 0, 1, -1, 1, -1 };
static const int Ny[8] = { 0, 0, 1, -1, 1, 1, -1, -1 };

static inline bool pass(const uint8_t* edge, int w, int k, int d)
{
    int j = k + Nx[d] + Ny[d] * w;
    return (edge[k] >> d & 1) != 0 && (edge[j] >> (d ^ 1) & 1) != 0;
}

// 斜め: どちらかの回り道が縦横で通れる時だけ
static inline bool passDiag(const uint8_t* edge, int w, int k, int dx, int dy)
{
    int ix = dx > 0 ? 0 : 1, iy = dy > 0 ? 2 : 3;
    return (pass(edge, w, k, ix) && pass(edge, w, k + dx, iy)) || (pass(edge, w, k, iy) && pass(edge, w, k + dy * w, ix));
}

struct RegionArgs
{
    const uint16_t* sub; const uint8_t* edge;
    int32_t w, h;
    int32_t* comp;                          // 戻り: 範囲の番号 (−1 = 閉じた升)
    int32_t* stack;                         // 置き場 (w × h)
    int32_t* cells;                         // 戻り: 範囲ごとの升の数 (w × h まで)
    int32_t* press;                         // 戻り: 範囲ごとの気圧 (w × h まで)
    const int32_t* oldComp; const int32_t* oldPress;   // 作り直す前 (null = 全部満タン)
    int64_t* sum; int32_t* cnt;             // 置き場 (w × h)
    int32_t full;
    int32_t compN;                          // 戻り
};

// 範囲を塗り直し、前の範囲の気圧を升の数で混ぜて移す
MRP_API void mrp_decomp_regions(RegionArgs* a)
{
    const int w = a->w, n = a->w * a->h;
    const uint8_t* edge = a->edge;
    int32_t* comp = a->comp;
    for (int k = 0; k < n; k++) comp[k] = -1;
    int nc = 0;
    for (int s = 0; s < n; s++)
    {
        if (a->sub[s] == 0 || comp[s] >= 0) continue;
        int id = nc, count = 0, top = 0;
        comp[s] = id;
        a->stack[top++] = s;
        while (top > 0)
        {
            int k = a->stack[--top];
            count++;
            for (int d = 0; d < 4; d++)
            {
                if (!pass(edge, w, k, d)) continue;
                int j = k + Nx[d] + Ny[d] * w;
                if (comp[j] >= 0) continue;
                comp[j] = id;
                a->stack[top++] = j;
            }
        }
        a->cells[nc++] = count;
    }
    a->compN = nc;
    if (a->oldComp == nullptr)
    {
        for (int c = 0; c < nc; c++) a->press[c] = a->full;
        return;
    }
    for (int c = 0; c < nc; c++) { a->sum[c] = 0; a->cnt[c] = 0; }
    for (int k = 0; k < n; k++)
    {
        int c = comp[k], o = a->oldComp[k];
        if (c < 0 || o < 0) continue;
        a->sum[c] += a->oldPress[o];
        a->cnt[c]++;
    }
    for (int c = 0; c < nc; c++) a->press[c] = a->cnt[c] > 0 ? (int32_t)(a->sum[c] / a->cnt[c]) : a->full;
}

struct FieldArgs
{
    const uint16_t* sub; const uint8_t* edge; const uint8_t* furn;
    int32_t w, h, cap;
    uint16_t* dist;                         // 種の升は 0・ほかは far で渡す
    int32_t* par;                           // −1 で渡す
    const int32_t* seeds; int32_t seedN;    // 種 (C# が入れた順)
    int32_t* qk; int32_t* qnext;            // 桶の置き場 (種の数 + 8 × w × h)
    int32_t gripR, gripMax;
    int64_t* grips; int32_t gripN;          // 戻り: 出っ張った角の候補 (塞がりの数 << 32 | 升)・小さい順。置き場は w × h
};

// 小さい順 (ヒープソート。Android は libc++ をつながないので std::sort は使えない)
static void sortI64(int64_t* v, int n)
{
    auto down = [v](int i, int m)
    {
        for (;;)
        {
            int c = 2 * i + 1;
            if (c >= m) return;
            if (c + 1 < m && v[c + 1] > v[c]) c++;
            if (v[i] >= v[c]) return;
            int64_t t = v[i]; v[i] = v[c]; v[c] = t;
            i = c;
        }
    };
    for (int i = n / 2 - 1; i >= 0; i--) down(i, n);
    for (int m = n - 1; m > 0; m--)
    {
        int64_t t = v[0]; v[0] = v[m]; v[m] = t;
        down(0, m);
    }
}

static inline bool solid(const uint16_t* sub, const uint8_t* furn, int w, int h, int x, int y)
{
    if (x < 0 || y < 0 || x >= w || y >= h) return true;
    int k = y * w + x;
    return sub[k] == 0 || furn[k] != 0;
}

// 口からの道のり (縦横 2・斜め 3 の最短路を道のりの値ごとの桶で)。桶の中は入れた順に回す (同じ値なら先に来た方が親)
MRP_API void mrp_decomp_field(FieldArgs* a)
{
    const int w = a->w, h = a->h, cap = a->cap;
    const uint8_t* edge = a->edge;
    const uint8_t* furn = a->furn;
    uint16_t* dist = a->dist;
    int32_t head[256], tail[256];
    a->gripN = 0;
    if (cap + 4 > 256) return; // 桶は 256 本まで (C# の Cap = 84。上げる時はここも広げる)
    for (int v = 0; v < cap + 4; v++) head[v] = tail[v] = -1;
    int used = 0;
    for (int i = 0; i < a->seedN; i++)
    {
        int e = used++;
        a->qk[e] = a->seeds[i]; a->qnext[e] = -1;
        if (tail[0] < 0) head[0] = e; else a->qnext[tail[0]] = e;
        tail[0] = e;
    }
    if (a->seedN == 0) return;
    for (int v = 0; v <= cap; v++)
        for (int e = head[v]; e >= 0; e = a->qnext[e])
        {
            int k = a->qk[e];
            if (dist[k] != v) continue;
            // 家具の上: 空気は通るので家具の升へは伸ばすが、家具を抜けて床へは伸ばさない
            bool onFurn = furn[k] != 0 && v > 0;
            for (int d = 0; d < 8; d++)
            {
                int dx = Nx[d], dy = Ny[d];
                bool diag = d >= 4;
                if (diag ? !passDiag(edge, w, k, dx, dy) : !pass(edge, w, k, d)) continue;
                int j = k + dx + dy * w, nv = v + (diag ? 3 : 2);
                if (nv > cap || nv >= dist[j] || (onFurn && furn[j] == 0)) continue;
                dist[j] = (uint16_t)nv;
                a->par[j] = k;
                int q = used++;
                a->qk[q] = j; a->qnext[q] = -1;
                if (tail[nv] < 0) head[nv] = q; else a->qnext[tail[nv]] = q;
                tail[nv] = q;
            }
        }

    // 出っ張った角の候補: 流れの届く升に面した塞がった升のうち、周り (2R+1)² 升の塞がりが少ない物
    const uint16_t* sub = a->sub;
    const int R = a->gripR;
    int gn = 0;
    for (int y = R; y < h - R; y++)
    for (int x = R; x < w - R; x++)
    {
        if (!solid(sub, furn, w, h, x, y)) continue;
        bool face = false;
        for (int d = 0; d < 4 && !face; d++)
        {
            int j = (y + Ny[d]) * w + x + Nx[d];
            face = !solid(sub, furn, w, h, x + Nx[d], y + Ny[d]) && dist[j] != 0xFFFF;
        }
        if (!face) continue;
        int n = 0;
        for (int dy = -R; dy <= R; dy++)
        for (int dx = -R; dx <= R; dx++)
            if (solid(sub, furn, w, h, x + dx, y + dy)) n++;
        if (n >= 4 && n <= a->gripMax) a->grips[gn++] = (int64_t)n << 32 | (uint32_t)(y * w + x);
    }
    sortI64(a->grips, gn);
    a->gripN = gn;
}
