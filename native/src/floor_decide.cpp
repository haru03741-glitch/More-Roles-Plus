// 水の床の地図 (床・壁の面の 2 枚) を、船の絵から取り込んだ入力の 1 ビット地図から決める。
// 入出力はどれも FloorMask と同じ並び (w*h 画素・行 0 = 世界の下・行優先・画素 k = バイト k/8 のビット k%8)。
// 中では行ごとに 64 画素 = 1 語のビット列で持つ (メモリは 1 画素 1 ビット)。
// 別の実装 (画像処理の一般的な関数) と 1 ビットまで同じ答えにするための決め事:
//   開く/閉じる = 十字の膨張・収縮を k 回。画像の外は偽
//   まとまり = 4 近傍 (数と大きさだけを使う)
//   距離 = 背景までのユークリッド距離を double の sqrt にしてから比べる
//   ならし = float32 のガウス (sigma 1.6・半径 6) を縦 → 横の順に 1 次元の相関で・端は折り返し
// 小数の式は書いた順に計算する (縮約 (FMA) を切ってビルドする) ので、端末ごとの結果も同じ
#include "mrp_native.h"
#include <cstdint>
#include <cstring>
#include <cmath>
#include <vector>
#include <algorithm>
#ifdef MRP_FD_TEST
#include <chrono>
#endif
#ifdef _MSC_VER
#include <intrin.h>
inline int ctz64(uint64_t v) { unsigned long i; _BitScanForward64(&i, v); return (int)i; }
inline int popc64(uint64_t v) { return (int)__popcnt64(v); }
#else
inline int ctz64(uint64_t v) { return __builtin_ctzll(v); }
inline int popc64(uint64_t v) { return __builtin_popcountll(v); }
#endif

namespace {

constexpr float OUTLINE = 0.22f;  // これより暗い画素は縁取りの候補
constexpr double LINE_HALF = 0.05;
constexpr double SHARE = 0.5;
constexpr double INSET = 0.1;
constexpr double REACH = 0.2;
constexpr double LINE_CLOSE = 3;
constexpr double FURN_DEEP = 0.08;
constexpr int SPECK = 60;
constexpr double FACE_MAX = 4.0;

// 偶数への丸め
int pyround(double x) { return (int)std::nearbyint(x); }

#ifdef MRP_FD_TEST
double g_t[16];
inline double now() { return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
#define FD_T(i) (g_t[i] = now())
#else
#define FD_T(i) ((void)0)
#endif

// ---- 64 画素ずつのビット列 (行の余りのビットは常に 0) ----
struct Packed {
    int w = 0, h = 0, W = 0;
    std::vector<uint64_t> a;
    void init(int w_, int h_) { w = w_; h = h_; W = (w + 63) >> 6; a.assign((size_t)W * h, 0); }
    uint64_t* row(int y) { return &a[(size_t)y * W]; }
    const uint64_t* row(int y) const { return &a[(size_t)y * W]; }
    uint64_t tail() const { return (w & 63) ? ((1ull << (w & 63)) - 1) : ~0ull; }
    bool get(int x, int y) const { return (a[(size_t)y * W + (x >> 6)] >> (x & 63)) & 1; }
};

// 0/1 のバイト 8 個 → 8 ビット (掛け算で各バイトの最下位ビットを集める)
inline uint64_t gather8(const uint8_t* p) { uint64_t v; std::memcpy(&v, p, 8); return (v * 0x0102040810204080ull) >> 56; }

struct Spread { uint64_t t[256]; Spread() { for (int i = 0; i < 256; i++) { uint64_t v = 0; for (int k = 0; k < 8; k++) v |= (uint64_t)((i >> k) & 1) << (8 * k); t[i] = v; } } };
const Spread SPREAD;

void pack_row(const uint8_t* s, int w, uint64_t* r)
{
    int x = 0;
    for (; x + 8 <= w; x += 8) r[x >> 6] |= gather8(s + x) << (x & 63);
    for (; x < w; x++) r[x >> 6] |= (uint64_t)(s[x] != 0) << (x & 63);
}

[[maybe_unused]] void pack(const uint8_t* m, int w, int h, Packed& p)
{
    p.init(w, h);
    for (int y = 0; y < h; y++) pack_row(m + (size_t)y * w, w, p.row(y));
}

[[maybe_unused]] void unpack(const Packed& p, uint8_t* m)
{
    for (int y = 0; y < p.h; y++) {
        const uint64_t* r = p.row(y);
        uint8_t* d = m + (size_t)y * p.w;
        int x = 0;
        for (; x + 8 <= p.w; x += 8) { uint64_t v = SPREAD.t[(r[x >> 6] >> (x & 63)) & 0xff]; std::memcpy(d + x, &v, 8); }
        for (; x < p.w; x++) d[x] = (uint8_t)((r[x >> 6] >> (x & 63)) & 1);
    }
}

// 語ごとの演算 (d = f(d, s...))。余りのビットは 0 のまま保つ
template <class F> void each(Packed& d, F f) { for (size_t i = 0; i < d.a.size(); i++) d.a[i] = f(i); }
void invert(Packed& p) { const uint64_t tail = p.tail(); for (int y = 0; y < p.h; y++) { uint64_t* r = p.row(y); for (int i = 0; i < p.W; i++) r[i] = ~r[i]; r[p.W - 1] &= tail; } }

// 十字の膨張 (dil=true) か収縮 (画像の外は偽) を 1 回。src と dst は別の配列
void cross_once(const Packed& s, Packed& d, bool dil)
{
    const int W = s.W, h = s.h;
    const uint64_t tail = s.tail();
    for (int y = 0; y < h; y++) {
        const uint64_t* r = s.row(y);
        const uint64_t* up = y + 1 < h ? r + W : nullptr;
        const uint64_t* dn = y > 0 ? r - W : nullptr;
        uint64_t* o = d.row(y);
        for (int i = 0; i < W; i++) {
            uint64_t c = r[i];
            uint64_t l = (c << 1) | (i > 0 ? r[i - 1] >> 63 : 0);          // 画素 x に x-1 の値
            uint64_t rr = (c >> 1) | (i + 1 < W ? r[i + 1] << 63 : 0);     // 画素 x に x+1 の値
            uint64_t u = up ? up[i] : 0, dd = dn ? dn[i] : 0;
            uint64_t v = dil ? (c | l | rr | u | dd) : (c & l & rr & u & dd);
            if (i == W - 1) v &= tail;
            o[i] = v;
        }
    }
}

void morph(Packed& p, int k, bool dil, Packed& t)
{
    if (t.a.size() != p.a.size()) t.init(p.w, p.h);
    for (int i = 0; i < k; i++) { cross_once(p, t, dil); std::swap(p.a, t.a); }
}

// d |= s を横に dx ずらした物 (画素 x に x+dx の値・外は 0)
void shift_or(const Packed& s, Packed& d, int dx)
{
    const int W = s.W;
    const int ws = dx >= 0 ? (dx >> 6) : -((-dx + 63) >> 6);
    const int bs = dx - ws * 64;  // 0..63
    const uint64_t tail = s.tail();
    for (int y = 0; y < s.h; y++) {
        const uint64_t* r = s.row(y);
        uint64_t* o = d.row(y);
        for (int i = 0; i < W; i++) {
            int j = i + ws;
            uint64_t lo = (j >= 0 && j < W) ? r[j] : 0;
            uint64_t hi = (j + 1 >= 0 && j + 1 < W) ? r[j + 1] : 0;
            o[i] |= bs ? ((lo >> bs) | (hi << (64 - bs))) : lo;
        }
        o[W - 1] &= tail;  // 右端の外 (行の余りのビット) へずれた物は捨てる
    }
}

// 行の [x0, x1) のビットを立てる / 落とす / 数える
void set_range(uint64_t* r, int x0, int x1, bool v)
{
    while (x0 < x1) {
        int i = x0 >> 6, b = x0 & 63, n = std::min(64 - b, x1 - x0);
        uint64_t m = (n == 64 ? ~0ull : ((1ull << n) - 1)) << b;
        if (v) r[i] |= m; else r[i] &= ~m;
        x0 += n;
    }
}

int64_t count_range(const uint64_t* r, int x0, int x1)
{
    int64_t c = 0;
    while (x0 < x1) {
        int i = x0 >> 6, b = x0 & 63, n = std::min(64 - b, x1 - x0);
        uint64_t m = (n == 64 ? ~0ull : ((1ull << n) - 1)) << b;
        c += popc64(r[i] & m);
        x0 += n;
    }
    return c;
}

// ---- 4 近傍のまとまり (横の連なり単位) ----
struct Runs {
    std::vector<int32_t> y, x0, x1, comp;  // 連なり [x0, x1)
    std::vector<int32_t> rowStart;         // 行 y の最初の連なりの番号 (h+1 個)
    std::vector<int32_t> par, id;
    int n = 0;                             // まとまりの数 (comp は 0..n-1)
};

int find(std::vector<int32_t>& par, int32_t a) { while (par[a] != a) { par[a] = par[par[a]]; a = par[a]; } return a; }

// 行のビットから連なりを拾う (次の 1 と次の 0 を語ごとに ctz で探す。余りのビットは 0 なので連なりは w の手前で切れる)
void row_runs(const uint64_t* r, int W, int y, Runs& R)
{
    const int n = W << 6;
    int x = 0;
    for (;;) {
        int i = x >> 6;
        if (i >= W) return;
        uint64_t cur = r[i] & (~0ull << (x & 63));
        while (!cur) { if (++i >= W) return; cur = r[i]; }
        int b = (i << 6) + ctz64(cur);
        i = b >> 6;
        uint64_t z = ~r[i] & (~0ull << (b & 63));
        int e;
        for (;;) {
            if (z) { e = (i << 6) + ctz64(z); break; }
            if (++i >= W) { e = n; break; }
            z = ~r[i];
        }
        R.y.push_back(y); R.x0.push_back(b); R.x1.push_back(e);
        x = e;
    }
}

void runs_of(const Packed& p, Runs& R)
{
    const int h = p.h;
    R.y.clear(); R.x0.clear(); R.x1.clear(); R.rowStart.assign(h + 1, 0);
    for (int y = 0; y < h; y++) {
        R.rowStart[y] = (int32_t)R.x0.size();
        row_runs(p.row(y), p.W, y, R);
    }
    R.rowStart[h] = (int32_t)R.x0.size();
    size_t nr = R.x0.size();
    R.par.resize(nr);
    for (size_t i = 0; i < nr; i++) R.par[i] = (int32_t)i;
    for (int y = 1; y < h; y++) {
        int a = R.rowStart[y - 1], ae = R.rowStart[y], b = R.rowStart[y], be = R.rowStart[y + 1];
        while (a < ae && b < be) {
            if (R.x1[a] > R.x0[b] && R.x1[b] > R.x0[a]) {
                int32_t ra = find(R.par, a), rb = find(R.par, b);
                if (ra != rb) { if (ra < rb) R.par[rb] = ra; else R.par[ra] = rb; }
            }
            if (R.x1[a] < R.x1[b]) a++; else b++;
        }
    }
    R.comp.resize(nr);
    R.id.assign(nr, -1);
    int n = 0;
    for (size_t i = 0; i < nr; i++) {
        int32_t r = find(R.par, (int32_t)i);
        if (R.id[r] < 0) R.id[r] = n++;
        R.comp[i] = R.id[r];
    }
    R.n = n;
}

// ---- 距離 ----
// 前景 (m=1) から背景 (m=0) までの距離 (画像の外は背景でない) を t と比べる。gt=true は > t、false は <= t。
// 「t 以内に背景がある」= 横に dx ずれた列で縦に hmax(dx) 以内に背景がある、の dx についての和。
// hmax(dx) = sqrt(dx^2 + dy^2) <= t となる最大の dy (double の sqrt で決める)。
// 縦の膨張を h = 0,1,2.. と 1 段ずつ進め、hmax(dx) == h の dx をその時にずらして足す
void edt_cmp(const Packed& m, double t, bool gt, Packed& near)
{
    const int w = m.w, h = m.h, W = m.W;
    const int cap = (int)std::floor(t) + 1;
    std::vector<int> hmax(cap + 1, -1);
    for (int dx = 0; dx <= cap; dx++)
        for (int dy = 0; dy <= cap; dy++)
            if (std::sqrt((double)(dx * dx + dy * dy)) <= t) hmax[dx] = dy;
    Packed v = m, nv;
    invert(v);  // 背景
    nv.init(w, h); near.init(w, h);
    int top = hmax[0];
    for (int hh = 0; hh <= top; hh++) {
        if (hh > 0) {
            for (int y = 0; y < h; y++) {
                const uint64_t* r = v.row(y);
                const uint64_t* up = y + 1 < h ? r + W : nullptr;
                const uint64_t* dn = y > 0 ? r - W : nullptr;
                uint64_t* o = nv.row(y);
                for (int i = 0; i < W; i++) o[i] = r[i] | (up ? up[i] : 0) | (dn ? dn[i] : 0);
            }
            std::swap(v.a, nv.a);
        }
        for (int dx = 0; dx <= cap; dx++) {
            if (hmax[dx] != hh) continue;
            shift_or(v, near, dx);
            if (dx) shift_or(v, near, -dx);
        }
    }
    if (gt) invert(near);
}

// ---- ならし ----
// 重み (sigma 1.6・半径 6・相関の向き・合計 1)。16 進で書いて丸めの差を無くす
const double GW[13] = {
    0x1.ce2bd2b07f239p-13,
    0x1.ef2f583313748p-10,
    0x1.66fe269578b29p-7,
    0x1.6032dbb3c267cp-5,
    0x1.d3994cd6cc171p-4,
    0x1.a40fbc1ff8ce4p-3,
    0x1.feaa34f28d751p-3,
    0x1.a40fbc1ff8ce4p-3,
    0x1.d3994cd6cc171p-4,
    0x1.6032dbb3c267cp-5,
    0x1.66fe269578b29p-7,
    0x1.ef2f583313748p-10,
    0x1.ce2bd2b07f239p-13
};
constexpr int GR = 6;

// 縦横 ±r の箱で膨張 (チェビシェフ距離 r 以内)
void box_dilate(Packed& p, int r)
{
    Packed t;
    t.init(p.w, p.h);
    for (int dx = -r; dx <= r; dx++) shift_or(p, t, dx);
    const int W = p.W;
    for (int y = 0; y < p.h; y++) {
        uint64_t* o = p.row(y);
        for (int i = 0; i < W; i++) {
            uint64_t v = 0;
            for (int dy = -r; dy <= r; dy++) { int yy = y + dy; if (yy >= 0 && yy < p.h) v |= t.a[(size_t)yy * W + i]; }
            o[i] = v;
        }
    }
}

// ならしのガウスを、13x13 の箱の中が一様でない画素 (= 縁の隣り合う違う 2 画素から 6 以内) だけで計算する。
// 一様な所は 0 か 1 のまま (重みの和はほぼ 1 なので 0.5 との比べは変わらない)。計算する所の値は全体で回した時と同じ式・同じ順
void gauss_near_edges(Packed& f)
{
    const int w = f.w, h = f.h, W = f.W;
    const uint64_t tail = f.tail();
    Packed e;
    e.init(w, h);
    for (int y = 0; y < h; y++) {
        const uint64_t* r = f.row(y);
        const uint64_t* up = y + 1 < h ? r + W : nullptr;
        uint64_t* o = e.row(y);
        for (int i = 0; i < W; i++) {
            uint64_t c = r[i];
            uint64_t nx = (c >> 1) | (i + 1 < W ? r[i + 1] << 63 : 0);   // x+1 の値
            uint64_t valid = i == W - 1 ? (tail >> 1) : ~0ull;           // x+1 が画像の中
            uint64_t hd = (c ^ nx) & valid;
            o[i] |= hd | (hd << 1);
            if (i + 1 < W) o[i + 1] |= hd >> 63;
            if (up) { uint64_t vd = c ^ up[i]; o[i] |= vd; e.a[(size_t)(y + 1) * W + i] |= vd; }
        }
    }
    box_dilate(e, GR);   // ならしの値が変わりうる所
    Packed e1;           // 縦の段の値が要る所 = さらに横に ±GR
    e1.init(w, h);
    for (int dx = -GR; dx <= GR; dx++) shift_or(e, e1, dx);
    const double* fw = GW + GR;
    auto refl = [&](int y) { if (y < 0) y = -1 - y; if (y >= h) y = 2 * h - 1 - y; return y; };
    // 縦の段の値は行 y の横の段でしか使わないので 1 行分だけ持つ。結果は縦の段がまだ読む行を壊さないよう、GR+1 行遅れて f へ書く
    std::vector<float> b(w);
    std::vector<uint64_t> ring((size_t)(GR + 1) * W);
    std::vector<int> ringY(GR + 1, -1);
    auto flush = [&](int slot) { if (ringY[slot] >= 0) std::memcpy(f.row(ringY[slot]), &ring[(size_t)slot * W], W * 8); ringY[slot] = -1; };
    for (int y = 0; y < h; y++) {
        const uint64_t* m1 = e1.row(y);
        const uint64_t* m = e.row(y);
        const uint64_t* rows[2 * GR + 1];
        for (int j = -GR; j <= GR; j++) rows[j + GR] = f.row(refl(y + j));
        for (int i = 0; i < W; i++) {
            for (uint64_t bits = m1[i]; bits; bits &= bits - 1) {
                int bi = ctz64(bits), x = (i << 6) + bi;
                auto at = [&](int k) -> double { return (double)((rows[k][i] >> bi) & 1); };
                double tt = at(GR) * fw[0];
                for (int j = -GR; j < 0; j++) tt += (at(GR + j) + at(GR - j)) * fw[j];
                b[x] = (float)tt;
            }
        }
        int slot = y % (GR + 1);
        flush(slot);
        uint64_t* o = &ring[(size_t)slot * W];
        std::memcpy(o, rows[GR], W * 8);
        ringY[slot] = y;
        const float* sb = b.data();
        for (int i = 0; i < W; i++) {
            for (uint64_t bits = m[i]; bits; bits &= bits - 1) {
                int bi = ctz64(bits), x = (i << 6) + bi;
                auto at = [&](int xx) -> double { if (xx < 0) xx = -1 - xx; if (xx >= w) xx = 2 * w - 1 - xx; return (double)sb[xx]; };
                double tt = at(x) * fw[0];
                for (int j = -GR; j < 0; j++) tt += (at(x + j) + at(x - j)) * fw[j];
                if ((float)tt >= 0.5f) o[i] |= 1ull << bi; else o[i] &= ~(1ull << bi);
            }
        }
    }
    for (int k = 0; k <= GR; k++) flush((h + k) % (GR + 1));
}

// まとまりのうち大きさが SPECK 未満の物を消す (hole=true は反転した物 = 小さな穴を埋める)
void drop_small(Packed& m, bool hole, Runs& R)
{
    if (hole) invert(m);
    runs_of(m, R);
    std::vector<int64_t> sz(R.n, 0);
    for (size_t i = 0; i < R.x0.size(); i++) sz[R.comp[i]] += R.x1[i] - R.x0[i];
    for (size_t i = 0; i < R.x0.size(); i++)
        if (sz[R.comp[i]] < SPECK) set_range(m.row(R.y[i]), R.x0[i], R.x1[i], false);
    if (hole) invert(m);
}

void smooth(Packed& floor, Runs& R)
{
    FD_T(8);
    drop_small(floor, false, R);
    drop_small(floor, true, R);
    FD_T(9);
    gauss_near_edges(floor);
}

// まとまりのうち歩ける所 (good) の割合が SHARE 以上の物を、膨張→収縮 (close 回) してから floor に足す (drawn の所だけ)
void keep_share_close(const Packed& body, const Packed& good, const Packed& drawn, int close, Packed& floor, Runs& R)
{
    runs_of(body, R);
    std::vector<int64_t> tot(R.n, 0), hit(R.n, 0);
    for (size_t i = 0; i < R.x0.size(); i++) {
        tot[R.comp[i]] += R.x1[i] - R.x0[i];
        hit[R.comp[i]] += count_range(good.row(R.y[i]), R.x0[i], R.x1[i]);
    }
    Packed p, t;
    p.init(body.w, body.h);
    for (size_t i = 0; i < R.x0.size(); i++) {
        int c = R.comp[i];
        if ((double)hit[c] >= (double)tot[c] * SHARE) set_range(p.row(R.y[i]), R.x0[i], R.x1[i], true);
    }
    morph(p, close, true, t);
    morph(p, close, false, t);
    each(floor, [&](size_t i) { return floor.a[i] | (p.a[i] & drawn.a[i]); });
}

// 詰めたビット (画素 k = バイト k/8 のビット k%8) の bit 番目から 64 ビット。total = 全体のバイト数
inline uint64_t read64(const uint8_t* b, size_t total, size_t bit)
{
    size_t q = bit >> 3;
    int r = (int)(bit & 7);
    uint8_t t[9] = {};
    size_t n = total > q ? std::min<size_t>(9, total - q) : 0;
    std::memcpy(t, b + q, n);
    uint64_t lo;
    std::memcpy(&lo, t, 8);
    return r ? (lo >> r) | ((uint64_t)t[8] << (64 - r)) : lo;
}

// v の下位 nb ビットを bit 番目から OR で書く (書き先は先に 0 にしておく)
inline void write64(uint8_t* b, size_t total, size_t bit, uint64_t v, int nb)
{
    if (nb < 64) v &= (1ull << nb) - 1;
    size_t q = bit >> 3;
    int r = (int)(bit & 7);
    uint8_t t[9];
    uint64_t lo = v << r;
    std::memcpy(t, &lo, 8);
    t[8] = r ? (uint8_t)(v >> (64 - r)) : 0;
    size_t n = std::min<size_t>((size_t)((r + nb + 7) >> 3), total - q);
    for (size_t k = 0; k < n; k++) b[q + k] |= t[k];
}

void load_bits(const uint8_t* b, int w, int h, Packed& p)
{
    p.init(w, h);
    const size_t total = ((size_t)w * h + 7) >> 3;
    const uint64_t tail = p.tail();
    for (int y = 0; y < h; y++) {
        uint64_t* r = p.row(y);
        const size_t base = (size_t)y * w;
        for (int i = 0; i < p.W; i++) r[i] = read64(b, total, base + ((size_t)i << 6));
        r[p.W - 1] &= tail;
    }
}

void store_bits(const Packed& p, uint8_t* b)
{
    const size_t total = ((size_t)p.w * p.h + 7) >> 3;
    std::memset(b, 0, total);
    for (int y = 0; y < p.h; y++) {
        const uint64_t* r = p.row(y);
        const size_t base = (size_t)y * p.w;
        for (int i = 0; i < p.W; i++) write64(b, total, base + ((size_t)i << 6), r[i], std::min(64, p.w - (i << 6)));
    }
}

// 決める本体。dark = 絵のうち縁取りの暗い色の所 (明るさのしきい値は取り込む側で済ませる)
void decide(double ppu, const Packed& drawn, Packed& dark, const Packed& walk, const Packed& furn, const Packed& stand,
            const Packed& front, Packed& floor, Packed& face)
{
    const int w = drawn.w, h = drawn.h;
    Runs R;
    each(dark, [&](size_t i) { return dark.a[i] & drawn.a[i]; });
    Packed good; good.init(w, h);
    each(good, [&](size_t i) { return walk.a[i] & ~furn.a[i]; });
    FD_T(1);
    // 縁取りの線のうち太い所 (細い線は開くと消える)
    Packed wide = dark, t;
    int ko = std::max(1, pyround(LINE_HALF * ppu));
    morph(wide, ko, false, t);
    morph(wide, ko, true, t);
    int close = std::max(1, pyround(LINE_CLOSE * ppu / 100.0));
    FD_T(2);
    Packed body;
    floor.init(w, h); body.init(w, h);
    each(body, [&](size_t i) { return drawn.a[i] & ~dark.a[i]; });
    keep_share_close(body, good, drawn, close, floor, R);
    keep_share_close(wide, good, drawn, close, floor, R);
    FD_T(3);
    Packed cmp;
    edt_cmp(good, INSET * ppu, true, cmp);
    each(floor, [&](size_t i) { return floor.a[i] | (drawn.a[i] & good.a[i] & cmp.a[i]) | (walk.a[i] & ~drawn.a[i]); });
    {
        Packed nw = walk; invert(nw);
        edt_cmp(nw, REACH * ppu, false, cmp);
    }
    each(floor, [&](size_t i) { return floor.a[i] & cmp.a[i]; });
    // 家具の当たり判定の内側 (縁の帯を除く) は床にしない・立つ物と手前の絵を抜く
    edt_cmp(furn, FURN_DEEP * ppu, true, cmp);
    each(floor, [&](size_t i) { return floor.a[i] & ~cmp.a[i] & ~stand.a[i] & ~front.a[i]; });
    FD_T(4);
    smooth(floor, R);
    FD_T(5);
    // 壁の面: 列ごとに、床の真上から途切れずに続く床でない絵 (床から FACE_MAX 以内)。行の順に列ごとの状態を持って回す
    const int fmaxI = (int)std::floor(FACE_MAX * ppu);  // (y - last) <= FACE_MAX * ppu と同じ (左が整数)
    std::vector<int32_t> last(w, -1), gap(w, -1);
    std::vector<uint8_t> fl(w), dr(w), fr(w), o(w);
    auto unrow = [&](const uint64_t* r, uint8_t* d) {
        int x = 0;
        for (; x + 8 <= w; x += 8) { uint64_t v = SPREAD.t[(r[x >> 6] >> (x & 63)) & 0xff]; std::memcpy(d + x, &v, 8); }
        for (; x < w; x++) d[x] = (uint8_t)((r[x >> 6] >> (x & 63)) & 1);
    };
    face.init(w, h);
    for (int y = 0; y < h; y++) {
        unrow(floor.row(y), fl.data()); unrow(drawn.row(y), dr.data()); unrow(front.row(y), fr.data());
        for (int x = 0; x < w; x++) {
            if (fl[x]) last[x] = y;
            else if (!dr[x]) gap[x] = y;
            o[x] = (uint8_t)(dr[x] & (fl[x] ^ 1) & (fr[x] ^ 1) & (last[x] >= 0) & (last[x] > gap[x]) & (y - last[x] <= fmaxI));
        }
        pack_row(o.data(), w, face.row(y));
    }
    FD_T(6);
}

}  // namespace

// 床の地図を決める。入力 6 枚と出力 2 枚はどれも (w*h+7)/8 バイトの詰めたビット。ppu = 1 単位あたりの画素。戻り値 0 = 成功
MRP_API int32_t mrp_floor_decide(int32_t w, int32_t h, double ppu, const uint8_t* drawn, const uint8_t* dark, const uint8_t* walk,
                                 const uint8_t* furn, const uint8_t* stand, const uint8_t* front, uint8_t* outFloor, uint8_t* outFace)
{
    if (w <= 0 || h <= 0 || !(ppu > 0) || !drawn || !dark || !walk || !furn || !stand || !front || !outFloor || !outFace) return 1;
#ifdef MRP_FD_TEST
    g_t[15] = 7;
#endif
    FD_T(0);
    Packed pd, pk, pw, pf, ps, pr, floor, face;
    load_bits(drawn, w, h, pd); load_bits(dark, w, h, pk); load_bits(walk, w, h, pw);
    load_bits(furn, w, h, pf); load_bits(stand, w, h, ps); load_bits(front, w, h, pr);
    decide(ppu, pd, pk, pw, pf, ps, pr, floor, face);
    store_bits(floor, outFloor);
    store_bits(face, outFace);
    return 0;
}

// ---- 入力を作る下ごしらえ (どれも出力は w*h 画素の詰めたビットへ OR で足す) ----

// 読み戻した板 (RGBA32・行 0 = 下・cw*ch) を、全体の (cx,cy) へ落とす。drawn = 不透明度 > 0.25。
// dark が null でなければ、絵がある所のうち明るさ (0.299R+0.587G+0.114B) < 0.22 の画素も立てる
MRP_API void mrp_floor_chunk_bits(const uint8_t* rgba, int32_t cw, int32_t ch, int32_t cx, int32_t cy, int32_t w, int32_t h,
                                  uint8_t* drawn, uint8_t* dark)
{
    if (!rgba || !drawn || cw <= 0 || ch <= 0) return;
    for (int y = 0; y < ch; y++) {
        int gy = cy + y;
        if (gy < 0 || gy >= h) continue;
        const uint8_t* s = rgba + (size_t)y * cw * 4;
        for (int x = 0; x < cw; x++, s += 4) {
            int gx = cx + x;
            if (gx < 0 || gx >= w || s[3] < 64) continue;  // a/255 > 0.25
            size_t k = (size_t)gy * w + gx;
            drawn[k >> 3] |= (uint8_t)(1 << (k & 7));
            if (dark) {
                float lum = (0.299f * s[0] + 0.587f * s[1] + 0.114f * s[2]) / 255.0f;
                if (lum < OUTLINE) dark[k >> 3] |= (uint8_t)(1 << (k & 7));
            }
        }
    }
}

// 多角形 (画素の座標で n 点・x,y の順) の内側を立てる。画素の中心で偶奇の規則
MRP_API void mrp_floor_fill_poly(uint8_t* bits, int32_t w, int32_t h, const float* xy, int32_t n)
{
    if (!bits || !xy || n < 3) return;
    float y0 = xy[1], y1 = xy[1];
    for (int i = 1; i < n; i++) { y0 = std::min(y0, xy[2 * i + 1]); y1 = std::max(y1, xy[2 * i + 1]); }
    int ra = std::max(0, (int)std::floor(y0)), rb = std::min(h - 1, (int)std::ceil(y1));
    std::vector<float> xs;
    for (int y = ra; y <= rb; y++) {
        const float yc = y + 0.5f;
        xs.clear();
        for (int i = 0, j = n - 1; i < n; j = i++) {
            float ay = xy[2 * j + 1], by = xy[2 * i + 1];
            if ((ay <= yc) == (by <= yc)) continue;
            float ax = xy[2 * j], bx = xy[2 * i];
            xs.push_back(ax + (yc - ay) * (bx - ax) / (by - ay));
        }
        std::sort(xs.begin(), xs.end());
        for (size_t i = 0; i + 1 < xs.size(); i += 2) {
            int xa = std::max(0, (int)std::ceil(xs[i] - 0.5f)), xb = std::min(w, (int)std::ceil(xs[i + 1] - 0.5f));
            for (int x = xa; x < xb; x++) { size_t k = (size_t)y * w + x; bits[k >> 3] |= (uint8_t)(1 << (k & 7)); }
        }
    }
}

// 升の詰めたビット (sw*sh・1 単位に sppu 升) を画素 (w*h・1 単位に ppu 画素) へ引く。画素の中心が入る升 (はみ出しは端の升)
MRP_API void mrp_floor_grid_bits(const uint8_t* cells, int32_t sw, int32_t sh, double sppu, int32_t w, int32_t h, double ppu, uint8_t* out)
{
    if (!cells || !out || sw <= 0 || sh <= 0) return;
    std::vector<int32_t> gx(w);
    for (int x = 0; x < w; x++) gx[x] = std::min((int)((x + 0.5) / ppu * sppu), sw - 1);
    for (int y = 0; y < h; y++) {
        int cy = std::min((int)((y + 0.5) / ppu * sppu), sh - 1);
        size_t row = (size_t)cy * sw;
        for (int x = 0; x < w; x++) {
            size_t c = row + gx[x];
            if (!((cells[c >> 3] >> (c & 7)) & 1)) continue;
            size_t k = (size_t)y * w + x;
            out[k >> 3] |= (uint8_t)(1 << (k & 7));
        }
    }
}

#ifdef MRP_FD_TEST
// 部品ごとの確かめ用 (入出力は 0/1 のバイト)
MRP_API double* mrp_fd_times() { return g_t; }
MRP_API void mrp_fd_open(int w, int h, uint8_t* m, int k) { Packed p, t; pack(m, w, h, p); morph(p, k, false, t); morph(p, k, true, t); unpack(p, m); }
MRP_API void mrp_fd_close(int w, int h, uint8_t* m, int k) { Packed p, t; pack(m, w, h, p); morph(p, k, true, t); morph(p, k, false, t); unpack(p, m); }
MRP_API void mrp_fd_edt_gt(int w, int h, const uint8_t* m, double t, uint8_t* out) { Packed p, o; pack(m, w, h, p); edt_cmp(p, t, true, o); unpack(o, out); }
MRP_API void mrp_fd_smooth(int w, int h, uint8_t* m) { Runs R; Packed p; pack(m, w, h, p); smooth(p, R); unpack(p, m); }
#endif
