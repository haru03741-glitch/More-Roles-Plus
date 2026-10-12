#include "mrp_native.h"
#include <math.h>

// 歩ける所の地図の上を種から距離の近い順に塗り広げる (src/Terrain/FloorFlood.cs の Fill と同じ結果)。
// 通れる升 = 歩ける升で周り 8 升も歩ける升。距離は升 1 つ = 5・斜め = 7。
// 塗った升の数が target に達した所で打ち切り、その時の距離 (far) より先の升は消す。
// 距離は升ごとに一意なので、同じ距離の升をどの順で取り出しても結果は同じ。
// 置き場 (dist・pass) は範囲全体の大きさで、呼ぶ前も後も「まっさら」(0xFFFF・0)。触った升だけ戻す。
// 答えは届いた升を囲む矩形に切り出して out へ詰める

struct FloorFloodArgs
{
    const uint8_t* open; int32_t mapW;  // 歩ける所の地図 (null = 地図が無い = 全部通れる)
    int32_t x0, y0, w, h;               // 塗る範囲 (地図の升)
    int32_t lx, ly, search, nearestOnly; // 種の升 (範囲の中)・種を探す半径 (升)・1 升だけ (打撃)
    int32_t target, maxCost;
    uint16_t* dist;                     // w*h の置き場 (0xFFFF = 届いていない)
    uint8_t* pass;                      // w*h の置き場 (0 = まだ・1 = 通れる・2 = 通れない・+4 = 箱に入っている)
    int32_t* nxt; int32_t* prv;         // w*h の置き場 (箱の中のリスト)
    int32_t* touched;                   // w*h の置き場 (通れるか調べた升)
    uint16_t* out;                      // w*h の置き場 (戻り: 切り出した距離の表・行ごとに bw 個)
    int32_t bx, by, bw, bh;             // 戻り: 切り出した矩形 (範囲の中の升)
    int32_t popped, far, found;         // 戻り
};

static const int Cs = 5, Cd = 7;
static const int Dx[8] = { 1, -1, 0, 0, 1, 1, -1, -1 };
static const int Dy[8] = { 0, 0, 1, -1, 1, -1, 1, -1 };

static inline bool pass_at(FloorFloodArgs* a, int k, int lx, int ly)
{
    uint8_t p = a->pass[k];
    if (p == 0)
    {
        p = 1;
        if (a->open)
        {
            int x = a->x0 + lx, y = a->y0 + ly, mw = a->mapW;
            for (int dy = -1; dy <= 1 && p == 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (a->open[(y + dy) * mw + x + dx] == 0) { p = 2; break; }
        }
        a->pass[k] = p;
        a->touched[a->bw++] = k; // 数え終わるまで bw を触った升の数に使う
    }
    return (p & 3) == 1;
}

// 距離ごとの箱 (箱の番号 = 距離の下位ビット)。距離は 5 か 7 ずつしか増えないので、箱を小さい順に見れば距離の順に取り出せる。
// 箱の中は升の番号の双方向リスト (nxt・prv)。距離が縮んだ升は箱を移す。
// 待っている升の距離は「いま見ている距離 + 種の距離の幅 + 7」に収まるので、箱の数はそれより多ければ一周しても混ざらない
static const int Ring = 8192, RingMask = Ring - 1;
static int32_t g_head[Ring]; // メインスレッドからだけ呼ぶ
static const uint8_t Queued = 4;

struct Buckets
{
    int32_t* nxt; int32_t* prv; uint8_t* st; int count;

    void add(int k, int d)
    {
        int b = d & RingMask, h = g_head[b];
        nxt[k] = h; prv[k] = -1;
        if (h >= 0) prv[h] = k;
        g_head[b] = k;
        st[k] |= Queued;
        count++;
    }
    void remove(int k, int d)
    {
        int p = prv[k], n = nxt[k];
        if (p >= 0) nxt[p] = n; else g_head[d & RingMask] = n;
        if (n >= 0) prv[n] = p;
        st[k] &= (uint8_t)~Queued;
        count--;
    }
};

MRP_API void mrp_floor_flood(FloorFloodArgs* a)
{
    const int w = a->w, h = a->h;
    a->bw = 0;
    for (int i = 0; i < Ring; i++) g_head[i] = -1;
    Buckets q = { a->nxt, a->prv, a->pass, 0 };
    int seedMax = 0;

    // 種: 範囲の中の通れる升を、種の点からの直線距離で始める (打撃は一番近い 1 升だけ)
    const int s = a->search;
    int best = 0x7FFFFFFF, nearest = -1;
    for (int dy = -s; dy <= s; dy++)
    for (int dx = -s; dx <= s; dx++)
    {
        int x = a->lx + dx, y = a->ly + dy;
        int d2 = dx * dx + dy * dy;
        if (d2 > s * s || x < 0 || y < 0 || x >= w || y >= h || !pass_at(a, y * w + x, x, y)) continue;
        if (d2 < best) { best = d2; nearest = y * w + x; }
        if (a->nearestOnly) continue;
        int k = y * w + x;
        a->dist[k] = (uint16_t)(sqrtf((float)d2) * (float)Cs);
        if (a->dist[k] > seedMax) seedMax = a->dist[k];
        q.add(k, a->dist[k]);
    }
    a->found = nearest >= 0 ? 1 : 0;
    if (seedMax + Cd >= Ring) { nearest = -1; a->found = -1; } // 種の範囲が箱より広い (起こらない大きさ)
    a->popped = 0; a->far = 0;
    a->bx = a->by = 0; a->bh = 0;
    if (nearest < 0)
    {
        for (int i = 0, tn = a->bw; i < tn; i++) { int k = a->touched[i]; a->pass[k] = 0; a->dist[k] = 0xFFFF; }
        a->bw = 0;
        return;
    }
    if (a->nearestOnly) { a->dist[nearest] = 0; q.add(nearest, 0); }

    int popped = 0, far = 0, cur = 0;
    while (q.count > 0)
    {
        while (g_head[cur & RingMask] < 0) cur++;
        int k = g_head[cur & RingMask];
        int d = cur;
        q.remove(k, d);
        popped++;
        far = d;
        if (popped >= a->target) break;
        int y = k / w, x = k - y * w;
        for (int i = 0; i < 8; i++)
        {
            int nx = x + Dx[i], ny = y + Dy[i];
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
            int nk = ny * w + nx;
            if (!pass_at(a, nk, nx, ny)) continue;
            int nd = d + (i < 4 ? Cs : Cd);
            int od = a->dist[nk];
            if (nd > a->maxCost || nd >= od) continue;
            if (a->pass[nk] & Queued) q.remove(nk, od);
            a->dist[nk] = (uint16_t)nd;
            q.add(nk, nd);
        }
    }
    // 打ち切った後も表に残った (まだ確定していない) 升は、届いた一番遠い所より先なら消す。届いた升を囲む矩形を求める
    const int tn = a->bw;
    int bx0 = w, by0 = h, bx1 = -1, by1 = -1;
    for (int i = 0; i < tn; i++)
    {
        int k = a->touched[i];
        if (a->dist[k] == 0xFFFF) continue;
        if (a->dist[k] > far) { a->dist[k] = 0xFFFF; continue; }
        int y = k / w, x = k - y * w;
        if (x < bx0) bx0 = x;
        if (x > bx1) bx1 = x;
        if (y < by0) by0 = y;
        if (y > by1) by1 = y;
    }
    int bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
    for (int y = 0; y < bh; y++)
    for (int x = 0; x < bw; x++)
        a->out[y * bw + x] = a->dist[(by0 + y) * w + bx0 + x];
    for (int i = 0; i < tn; i++) { int k = a->touched[i]; a->pass[k] = 0; a->dist[k] = 0xFFFF; }
    a->bx = bx0; a->by = by0; a->bw = bw; a->bh = bh;
    a->popped = popped;
    a->far = far;
}
