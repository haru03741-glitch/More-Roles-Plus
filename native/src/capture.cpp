#include "capture.h"
#include <atomic>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// 書き出しの形: 頭に "MRPCAP01"、そのあと呼び出しごとに [u32 印][i32 部品][i32 塊の数][i64 中身のバイト数][中身]。
// 塊 = [i64 長さ][バイト列]。長さ -1 = 無い・-2 - i = ファイルの中で前に出た i 番目の大きい塊と同じ (中身は書かない)。
// 大きい塊 = 長さ ShareMin 以上 (地図の作りのように呼び出しごとに変わらない入力を 1 回だけ書く)。
// 範囲の塊は頭に切った範囲 (i32 ×4) を付ける。小さい端が先 (PC も Android も同じ)
static const uint32_t RecMark = 0x43455243u;
static const int64_t ShareMin = 4096;
static const int MaxBlobs = 64;

static FILE* g_file;
static std::atomic_flag g_lock;
static std::atomic<int64_t> g_seen[DgCount], g_kept[DgCount];
static int64_t g_perPart, g_every;
static int64_t g_records;
// 書けずに捨てた呼び出し (塊が多すぎる・領域を取れない)・表を広げられず途中で閉じたか
static std::atomic<int64_t> g_dropped;
static bool g_broken;

// 書いた大きい塊の表 (中身の指紋 2 つ + 長さ → 番号)。開き直すたびに空にする
struct Shared { uint64_t a, b; int64_t len; int64_t id; };
static Shared* g_shared;
static int64_t g_sharedCap, g_sharedN;

static void hash2(const uint8_t* p, int64_t n, uint64_t* a, uint64_t* b)
{
    uint64_t x = 14695981039346656037ull, y = 0x84222325cbf29ce4ull;
    int64_t i = 0;
    for (; i + 8 <= n; i += 8)
    {
        uint64_t w;
        memcpy(&w, p + i, 8);
        x = (x ^ w) * 1099511628211ull;
        y = (y ^ (w + 0x9e3779b97f4a7c15ull)) * 0x100000001b3ull;
        y ^= y >> 29;
    }
    for (; i < n; i++) { x = (x ^ p[i]) * 1099511628211ull; y = (y ^ p[i]) * 0x100000001b3ull; y ^= y >> 29; }
    *a = x; *b = y ^ (uint64_t)n;
}

// 前に同じ塊を書いていれば番号、無ければ表に足して -1 (表は開番地法・半分埋まったら倍に)
static int64_t share(const uint8_t* p, int64_t n)
{
    uint64_t a, b;
    hash2(p, n, &a, &b);
    if ((g_sharedN + 1) * 2 > g_sharedCap)
    {
        int64_t cap = g_sharedCap ? g_sharedCap * 2 : 1024;
        Shared* t = (Shared*)calloc((size_t)cap, sizeof(Shared));
        // 表を広げられなければ記録をやめる (番号が再生側とずれた塊を書かない)
        if (!t) { fclose(g_file); g_file = nullptr; g_broken = true; return -1; }
        for (int64_t i = 0; i < g_sharedCap; i++)
            if (g_shared[i].len)
            {
                int64_t j = (int64_t)(g_shared[i].a & (uint64_t)(cap - 1));
                while (t[j].len) j = (j + 1) & (cap - 1);
                t[j] = g_shared[i];
            }
        free(g_shared);
        g_shared = t; g_sharedCap = cap;
    }
    int64_t j = (int64_t)(a & (uint64_t)(g_sharedCap - 1));
    for (; g_shared[j].len; j = (j + 1) & (g_sharedCap - 1))
        if (g_shared[j].a == a && g_shared[j].b == b && g_shared[j].len == n) return g_shared[j].id;
    g_shared[j].a = a; g_shared[j].b = b; g_shared[j].len = n; g_shared[j].id = g_sharedN++;
    return -1;
}

Cap::Cap(int i) : on(false), id(i), buf(nullptr), len(0), cap(0), blobs(0)
{
    if (!g_file) return;
    // 部品ごとに perPart 回まで。every = 0 は最初の 4 回と 2 の累乗回目 (水の刻みのように何千回も呼ばれる部品でも、台本の後の段まで残る)・every > 0 は every 回に 1 回
    int64_t seen = g_seen[id].fetch_add(1, std::memory_order_relaxed);
    if (g_every == 0 ? !(seen < 4 || (seen & (seen - 1)) == 0) : seen % g_every != 0) return;
    if (g_kept[id].fetch_add(1, std::memory_order_relaxed) >= g_perPart) return;
    on = true;
}

Cap::~Cap() { free(buf); }

void Cap::Put(const void* p, int64_t bytes)
{
    if (len + bytes > cap)
    {
        int64_t want = cap ? cap * 2 : 4096;
        while (want < len + bytes) want *= 2;
        uint8_t* nb = (uint8_t*)realloc(buf, (size_t)want);
        if (!nb) { on = false; g_dropped++; return; }
        buf = nb; cap = want;
    }
    memcpy(buf + len, p, (size_t)bytes);
    len += bytes;
}

void Cap::Raw(const void* p, int64_t bytes) { Blob(p, bytes); }

void Cap::Blob(const void* p, int64_t bytes)
{
    if (!on) return;
    if (blobs >= MaxBlobs) { on = false; g_dropped++; return; }
    int64_t n = p && bytes >= 0 ? bytes : -1;
    Put(&n, 8);
    if (n > 0) Put(p, n);
    blobs++;
}

void Cap::Rect(const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1)
{
    if (!on) return;
    if (!p) { Blob(nullptr, 0); return; }
    if (blobs >= MaxBlobs) { on = false; g_dropped++; return; }
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 >= stride) x1 = stride - 1;
    if (y1 >= rows) y1 = rows - 1;
    int64_t row = x1 >= x0 ? (int64_t)(x1 - x0 + 1) * elem : 0;
    int64_t h = y1 >= y0 ? y1 - y0 + 1 : 0;
    int64_t n = 16 + row * h;
    Put(&n, 8);
    int32_t r[4] = { x0, y0, x1, y1 };
    Put(r, 16);
    for (int y = y0; y <= y1 && row > 0; y++) Put((const uint8_t*)p + ((int64_t)y * stride + x0) * elem, row);
    blobs++;
}

void Cap::End()
{
    if (!on) return;
    while (g_lock.test_and_set(std::memory_order_acquire)) {}
    if (g_file)
    {
        // 大きい塊を前に書いた物と突き合わせ、同じなら番号だけにする (番号はファイルに書いた順なので、決めるのは鍵の中)
        int64_t refs[MaxBlobs], body = 0, at = 0;
        for (int i = 0; i < blobs; i++)
        {
            int64_t n;
            memcpy(&n, buf + at, 8);
            refs[i] = n >= ShareMin ? share(buf + at + 8, n) : -1;
            body += 8 + (refs[i] >= 0 ? 0 : n > 0 ? n : 0);
            at += 8 + (n > 0 ? n : 0);
        }
        if (!g_file) { g_lock.clear(std::memory_order_release); on = false; return; }
        int32_t head[3] = { (int32_t)RecMark, id, blobs };
        fwrite(head, 4, 3, g_file);
        fwrite(&body, 8, 1, g_file);
        at = 0;
        for (int i = 0; i < blobs; i++)
        {
            int64_t n;
            memcpy(&n, buf + at, 8);
            if (refs[i] >= 0) { int64_t r = -2 - refs[i]; fwrite(&r, 8, 1, g_file); }
            else { fwrite(&n, 8, 1, g_file); if (n > 0) fwrite(buf + at + 8, 1, (size_t)n, g_file); }
            at += 8 + (n > 0 ? n : 0);
        }
        g_records++;
    }
    g_lock.clear(std::memory_order_release);
    on = false;
}

// path = 書き出し先 (上書き)・perPart = 部品ごとに残す回数・every = 何回に 1 回残すか (0 = 最初の 4 回と 2 の累乗回目)。戻り = 1 開けた / 0 開けない
MRP_API int32_t mrp_cap_open(const char* path, int32_t perPart, int32_t every)
{
    while (g_lock.test_and_set(std::memory_order_acquire)) {}
    if (g_file) fclose(g_file);
    g_file = fopen(path, "wb");
    if (g_file) fwrite("MRPCAP01", 1, 8, g_file);
    g_perPart = perPart > 0 ? perPart : 1;
    g_every = every > 0 ? every : 0;
    g_records = 0;
    g_dropped = 0;
    g_broken = false;
    free(g_shared); g_shared = nullptr; g_sharedCap = 0; g_sharedN = 0;
    for (int i = 0; i < DgCount; i++) { g_seen[i] = 0; g_kept[i] = 0; }
    g_lock.clear(std::memory_order_release);
    return g_file ? 1 : 0;
}

// 閉じる。戻り = 書いた呼び出しの数 (開いていなければ -1・途中で閉じていれば -2)。dropped = 捨てた呼び出しの数
MRP_API int64_t mrp_cap_close(int64_t* dropped)
{
    while (g_lock.test_and_set(std::memory_order_acquire)) {}
    int64_t n = g_broken ? -2 : g_file ? g_records : -1;
    if (dropped) *dropped = g_dropped;
    if (g_file) { fclose(g_file); g_file = nullptr; }
    free(g_shared); g_shared = nullptr; g_sharedCap = 0; g_sharedN = 0;
    g_lock.clear(std::memory_order_release);
    return n;
}
