#include "digest.h"
#include <atomic>
#include <string.h>

static std::atomic<uint64_t> g_count[DgCount], g_in[DgCount], g_out[DgCount];

static const uint64_t Basis = 14695981039346656037ull, Prime = 1099511628211ull;

// 8 バイトずつ畳む (FNV-1a を語ごとに)。PC も Android も小さい端が先なので同じ値になる
static uint64_t fold(uint64_t h, const void* p, int64_t bytes)
{
    const uint8_t* s = (const uint8_t*)p;
    int64_t i = 0;
    for (; i + 8 <= bytes; i += 8)
    {
        uint64_t w;
        memcpy(&w, s + i, 8);
        h = (h ^ w) * Prime;
    }
    for (; i < bytes; i++) h = (h ^ s[i]) * Prime;
    return (h ^ (uint64_t)bytes) * Prime;
}

Dg::Dg(int i) : id(i), in(Basis), out(Basis) {}

void Dg::In(const void* p, int64_t bytes)
{
    // 無い配列も「無い」ことを指紋に残す
    if (!p || bytes <= 0) { in = (in ^ 0x9e3779b97f4a7c15ull) * Prime; return; }
    in = fold(in, p, bytes);
}

void Dg::Out(const void* p, int64_t bytes)
{
    if (!p || bytes <= 0) { out = (out ^ 0x9e3779b97f4a7c15ull) * Prime; return; }
    out = fold(out, p, bytes);
}

void Dg::Fold2D(uint64_t& h, const void* p, int stride, int rows, int elem, int x0, int y0, int x1, int y1)
{
    if (!p) { h = (h ^ 0x9e3779b97f4a7c15ull) * Prime; return; }
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 >= stride) x1 = stride - 1;
    if (y1 >= rows) y1 = rows - 1;
    for (int y = y0; y <= y1 && x0 <= x1; y++)
        h = fold(h, (const uint8_t*)p + ((int64_t)y * stride + x0) * elem, (int64_t)(x1 - x0 + 1) * elem);
}

void Dg::FoldBits(uint64_t& h, const uint8_t* p, int stride, int x0, int y0, int x1, int y1)
{
    if (!p) { h = (h ^ 0x9e3779b97f4a7c15ull) * Prime; return; }
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 >= stride) x1 = stride - 1;
    for (int y = y0; y <= y1 && x0 <= x1; y++)
    {
        int64_t b0 = ((int64_t)y * stride + x0) >> 3, b1 = ((int64_t)y * stride + x1) >> 3;
        h = fold(h, p + b0, b1 - b0 + 1);
    }
}

void Dg::Commit()
{
    g_count[id].fetch_add(1, std::memory_order_relaxed);
    g_in[id].fetch_add(in, std::memory_order_relaxed);
    g_out[id].fetch_add(out, std::memory_order_relaxed);
}

MRP_API void mrp_dg_reset()
{
    for (int i = 0; i < DgCount; i++) { g_count[i] = 0; g_in[i] = 0; g_out[i] = 0; }
}

// 部品ごとに 回数・入力・出力 の 3 つを into へ (cap は部品の数)。戻り = 部品の数
MRP_API int32_t mrp_dg_read(uint64_t* into, int32_t cap)
{
    int n = cap < DgCount ? cap : DgCount;
    for (int i = 0; i < n; i++)
    {
        into[i * 3] = g_count[i].load(std::memory_order_relaxed);
        into[i * 3 + 1] = g_in[i].load(std::memory_order_relaxed);
        into[i * 3 + 2] = g_out[i].load(std::memory_order_relaxed);
    }
    return DgCount;
}
