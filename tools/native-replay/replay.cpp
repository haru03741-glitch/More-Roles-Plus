// C++ の部品の記録 (native capture で書き出した .cap) をゲームの外で食わせ直し、記録した出力とバイト単位で比べる。
//   replay <file.cap>...   戻り 0 = 全部一致・1 = 違いあり・2 = 読めない
// native/src の全部と一緒に MRP_REPLAY 付きで組む (tools/native-replay/build.ps1)。PC は exe・Android は adb で実機に置いて走らせる
#include "../../native/src/capture.h"
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char* const Names[DgCount] = {
    "floorRaw", "floorBlur", "floorLips", "waterPixels", "waterSpreadFace", "waterStep",
    "damageStamp", "solidCarve", "waterFace", "pieceCells", "fallRay", "waterCells", "linkScan",
    "waterRaw", "hullClip", "foamDraw", "foamCull", "decompRegions", "decompField", "floorFlood",
    "floorPatch", "floorCutMoved", "liftShape", "waterFill", "faceHeights", "faceCutHoles",
    "frontFill", "fallDraw", "fireStep", "solidFlood", "solidOutside", "wetFilter", "islandCells",
    "waterSpray",
};

// 部品ごとの再生の関数 (各 .cpp の RpReg が起動時に入れる・定数で 0 に初期化されるので登録の順は問わない)
static RpFn g_fns[DgCount];
RpReg::RpReg(int id, RpFn fn) { if (id >= 0 && id < DgCount) g_fns[id] = fn; }
RpFn rp_find(int id) { return id >= 0 && id < DgCount ? g_fns[id] : nullptr; }

// 1 回の呼び出しの間に借りた領域 (塊・地図) を最後にまとめて返す
static void* g_owned[256];
static int g_ownedN;
static void* own(size_t bytes)
{
    void* p = malloc(bytes ? bytes : 1);
    if (p && g_ownedN < 256) g_owned[g_ownedN++] = p;
    return p;
}
static void free_owned() { for (int i = 0; i < g_ownedN; i++) free(g_owned[i]); g_ownedN = 0; }

void Rp::Fail(const char* fmt, ...)
{
    if (!ok) return;
    ok = false;
    va_list v;
    va_start(v, fmt);
    vsnprintf(why, sizeof why, fmt, v);
    va_end(v);
}

void Rp::Raw(void* into, int64_t bytes)
{
    if (k >= n) { Fail("blob %d missing", k); memset(into, 0, (size_t)bytes); return; }
    if (len[k] != bytes) { Fail("blob %d is %lld bytes, want %lld", k, (long long)len[k], (long long)bytes); memset(into, 0, (size_t)bytes); k++; return; }
    memcpy(into, data[k], (size_t)bytes);
    k++;
}

const void* Rp::Blob(int64_t* bytes)
{
    if (k >= n) { Fail("blob %d missing", k); return nullptr; }
    if (bytes) *bytes = len[k] < 0 ? 0 : len[k];
    const void* p = len[k] < 0 ? nullptr : data[k];
    k++;
    return p;
}

static bool clip(int stride, int rows, int& x0, int& y0, int& x1, int& y1)
{
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 >= stride) x1 = stride - 1;
    if (y1 >= rows) y1 = rows - 1;
    return true;
}

// 範囲の塊の頭 (切った範囲) が今の引数から求めた範囲と同じかを見て、行の並びの先頭を返す
static const uint8_t* rect_body(Rp& r, int64_t bytes, const uint8_t* p, int x0, int y0, int x1, int y1, int elem)
{
    if (!p) { r.Fail("rect blob is null"); return nullptr; }
    int32_t h[4];
    memcpy(h, p, 16);
    if (h[0] != x0 || h[1] != y0 || h[2] != x1 || h[3] != y1) { r.Fail("rect %d,%d-%d,%d != %d,%d-%d,%d", h[0], h[1], h[2], h[3], x0, y0, x1, y1); return nullptr; }
    int64_t row = x1 >= x0 ? (int64_t)(x1 - x0 + 1) * elem : 0, rowsN = y1 >= y0 ? y1 - y0 + 1 : 0;
    if (bytes != 16 + row * rowsN) { r.Fail("rect blob %lld bytes, want %lld", (long long)bytes, (long long)(16 + row * rowsN)); return nullptr; }
    return p + 16;
}

void* Rp::RectInto(int stride, int rows, int elem, int x0, int y0, int x1, int y1, uint8_t fill)
{
    int64_t bytes;
    const uint8_t* p = (const uint8_t*)Blob(&bytes);
    if (!ok) return nullptr;
    int64_t total = (int64_t)stride * rows * elem;
    uint8_t* full = (uint8_t*)own((size_t)total);
    memset(full, fill, (size_t)total);
    clip(stride, rows, x0, y0, x1, y1);
    const uint8_t* s = rect_body(*this, bytes, p, x0, y0, x1, y1, elem);
    if (!s) return full;
    int64_t row = x1 >= x0 ? (int64_t)(x1 - x0 + 1) * elem : 0;
    for (int y = y0; y <= y1 && row > 0; y++, s += row) memcpy(full + ((int64_t)y * stride + x0) * elem, s, (size_t)row);
    return full;
}

void Rp::RectSame(const char* what, const void* got, int stride, int rows, int elem, int x0, int y0, int x1, int y1, uint8_t fill)
{
    int64_t bytes;
    const uint8_t* p = (const uint8_t*)Blob(&bytes);
    if (!ok) return;
    clip(stride, rows, x0, y0, x1, y1);
    const uint8_t* s = rect_body(*this, bytes, p, x0, y0, x1, y1, elem);
    if (!s) return;
    const uint8_t* g = (const uint8_t*)got;
    int64_t row = x1 >= x0 ? (int64_t)(x1 - x0 + 1) * elem : 0;
    for (int y = 0; y < rows; y++)
    {
        const uint8_t* line = g + (int64_t)y * stride * elem;
        bool in = y >= y0 && y <= y1 && row > 0;
        for (int64_t b = 0; b < (int64_t)stride * elem; b++)
        {
            bool inside = in && b >= (int64_t)x0 * elem && b < (int64_t)x0 * elem + row;
            uint8_t want = inside ? s[(int64_t)(y - y0) * row + (b - (int64_t)x0 * elem)] : fill;
            if (line[b] != want)
            {
                Fail("%s differs at x=%lld y=%d byte %lld: got %u want %u%s", what, (long long)(b / elem), y, (long long)(b % elem), line[b], want, inside ? "" : " (outside the range)");
                return;
            }
        }
    }
}

void Rp::Same(const char* what, const void* a, const void* b, int64_t bytes)
{
    if (!ok) return;
    const uint8_t* x = (const uint8_t*)a;
    const uint8_t* y = (const uint8_t*)b;
    for (int64_t i = 0; i < bytes; i++)
        if (x[i] != y[i]) { Fail("%s differs at byte %lld: got %u want %u", what, (long long)i, x[i], y[i]); return; }
}

static uint8_t* read_all(const char* path, int64_t* size)
{
    FILE* f = fopen(path, "rb");
    if (!f) return nullptr;
    // 2GB を超える記録もある (水の刻みを多く残した時) ので 64 ビットで位置を取る
#if defined(_WIN32)
    _fseeki64(f, 0, SEEK_END);
    int64_t n = _ftelli64(f);
    _fseeki64(f, 0, SEEK_SET);
#else
    fseeko(f, 0, SEEK_END);
    int64_t n = ftello(f);
    fseeko(f, 0, SEEK_SET);
#endif
    uint8_t* p = (uint8_t*)malloc(n > 0 ? (size_t)n : 1);
    if (p && fread(p, 1, (size_t)n, f) != (size_t)n) { free(p); p = nullptr; }
    fclose(f);
    *size = n;
    return p;
}

int main(int argc, char** argv)
{
    if (argc < 2) { printf("usage: replay <file.cap>...\n"); return 2; }
    int64_t count[DgCount] = {}, diff[DgCount] = {}, shown[DgCount] = {};
    int64_t skipped = 0;
    int bad = 0;
    for (int fi = 1; fi < argc; fi++)
    {
        int64_t size;
        uint8_t* file = read_all(argv[fi], &size);
        if (!file || size < 8 || memcmp(file, "MRPCAP01", 8) != 0) { printf("ERR %s: not a capture\n", argv[fi]); bad = 2; free(file); continue; }
        // 大きい塊 (2 回目からは番号だけ書かれる) の置き場。書き出し側 (capture.cpp) と同じ順で数える
        static const int64_t ShareMin = 4096;
        int64_t sharedCap = 1024, sharedN = 0;
        int64_t* sharedAt = (int64_t*)malloc((size_t)sharedCap * 8);
        int64_t* sharedLen = (int64_t*)malloc((size_t)sharedCap * 8);
        int64_t at = 8;
        while (at + 20 <= size)
        {
            int32_t head[3];
            int64_t body;
            memcpy(head, file + at, 12);
            memcpy(&body, file + at + 12, 8);
            at += 20;
            if ((uint32_t)head[0] != 0x43455243u || head[1] < 0 || head[1] >= DgCount || head[2] < 0 || head[2] > 64 || at + body > size)
            { printf("ERR %s: broken record at %lld\n", argv[fi], (long long)(at - 20)); bad = 2; break; }
            int id = head[1];
            RpFn fn = rp_find(id);
            // 塊の位置を読む (再生しない部品も大きい塊の番号は数える)
            int64_t from[64], len[64];
            int64_t p = at, end = at + body;
            int nb = 0;
            bool broken = false;
            for (; nb < head[2]; nb++)
            {
                if (p + 8 > end) { broken = true; break; }
                memcpy(&len[nb], file + p, 8);
                p += 8;
                if (len[nb] <= -2)
                {
                    int64_t s = -2 - len[nb];
                    if (s >= sharedN) { broken = true; break; }
                    from[nb] = sharedAt[s]; len[nb] = sharedLen[s];
                    continue;
                }
                int64_t l = len[nb] < 0 ? 0 : len[nb];
                if (p + l > end) { broken = true; break; }
                from[nb] = p;
                if (l >= ShareMin)
                {
                    if (sharedN == sharedCap)
                    {
                        sharedCap *= 2;
                        sharedAt = (int64_t*)realloc(sharedAt, (size_t)sharedCap * 8);
                        sharedLen = (int64_t*)realloc(sharedLen, (size_t)sharedCap * 8);
                    }
                    sharedAt[sharedN] = p; sharedLen[sharedN] = l; sharedN++;
                }
                p += l;
            }
            at = end;
            if (broken) { printf("ERR %s: broken blob in %s\n", argv[fi], Names[id]); bad = 2; break; }
            if (!fn) { skipped++; continue; }
            // 塊ごとに別の領域へ写す (小数の配列の並びを揃える)
            uint8_t* data[64];
            for (int i = 0; i < nb; i++)
            {
                int64_t l = len[i] < 0 ? 0 : len[i];
                data[i] = (uint8_t*)own((size_t)l);
                memcpy(data[i], file + from[i], (size_t)l);
            }
            Rp r;
            r.n = nb; r.k = 0; r.data = data; r.len = len; r.ok = true; r.why[0] = 0;
            fn(r);
            if (r.ok && r.k != r.n) r.Fail("%d of %d blobs read", r.k, r.n);
            count[id]++;
            if (!r.ok)
            {
                diff[id]++;
                if (shown[id]++ < 3) printf("DIFF %s #%lld: %s\n", Names[id], (long long)count[id], r.why);
                if (!bad) bad = 1;
            }
            free_owned();
        }
        free(sharedAt); free(sharedLen);
        free(file);
    }
    for (int i = 0; i < DgCount; i++)
        if (count[i]) printf("%s %s n=%lld diff=%lld\n", diff[i] ? "DIFF" : "OK  ", Names[i], (long long)count[i], (long long)diff[i]);
    if (skipped) printf("(no replay yet for %lld records)\n", (long long)skipped);
    printf("RESULT: %s\n", bad == 0 ? "OK" : bad == 1 ? "DIFF" : "ERROR");
    return bad;
}
