#include "mrp_native.h"
#include <math.h>

// 小刻みの関数は全部 mrp_water_step の中へ展開する (引数の構造体を手元に写した物の値をレジスタに置いたままにするため)
#if defined(_MSC_VER)
#define MRP_INLINE __forceinline
#else
#define MRP_INLINE inline __attribute__((always_inline))
#endif

// 床の上の水の計算の 1 小刻み (src/Terrain/WaterSim.cs の DropVoid → Flow → Heights と同じ結果)。
// 整数だけで計算する。升を回す順番も C# と同じ (Flow の途中で起こした升は同じ回のうちに回る)

struct WaterFlowArgs
{
    int32_t* hgt; int32_t* flux; int32_t* move;
    const uint8_t* open; const uint8_t* edge; const int8_t* lvl;
    const int16_t* bed; const int16_t* furn;
    const uint8_t* stepMask; const uint8_t* fallMask; const uint8_t* seaMask; const uint8_t* lipMask;
    const int16_t* lipH; const uint8_t* barB; const int16_t* barH; const int32_t* stepTo;
    int32_t* act; uint8_t* inList; int32_t* fo; uint8_t* tileDirty; int32_t* dirtyOut;
    int32_t actN, foN, dirtyN;
    int32_t w, h, tw, th, tileCells;
    int32_t sub, subN, dry, seaLevel;
    int64_t voidAdd;
    uint8_t* stir;                  // 起こされた升 (C# の _stir)
    int32_t checkSleep, sleepViol;  // 照合: 眠っている升が流れ出すか浅いかを数える
    // 床の本当の高さ (useZ = 1 の時だけ読む)。升の島 → 島の基準の高さ (島の高さ − 島の段 × 段差・NoZ = 決まらない)
    const uint8_t* isl; const int32_t* islBase;
    int32_t islN, useZ;
    int32_t foCap, foOver;          // fo の容量 (縁 3 個ずつ)・入らなかった数 (C# が縁の数に合わせて広げるので 0 のはず)
    const int32_t* slot;            // 升 → 縁の枠の番号 + 1。lipH / barB / barH / stepTo は枠 × 4 + 向き (落ちる縁と段差の升だけ)
};

// WaterSim の定数と同じ値
static const int Gain = 56, Damp = 236;
static const int DryHold = 48, WetHold = 3, MinDepth = 6, FallLip = 24, FallGain = 72;
static const int WeirDiv = 64 * 256 / FallGain;
static const int Full = 1024, StepHeight = Full / 4, BarMax = Full;
static const int NoLevel = -128, VoidLevel = -2;
static const int32_t NoZ = INT32_MIN;
// 小刻みの数とタイルの升の数は C# の定数と同じ (割り算を定数にするため。違えば計算しない)
static const int SubN = 4, TileCells = 16;
static const int Nx[4] = { 1, -1, 0, 0 }, Ny[4] = { 0, 0, 1, -1 }, Opp[4] = { 1, 0, 3, 2 };

static MRP_INLINE int isqrt(int64_t v)
{
    if (v <= 0) return 0;
    int64_t r = (int64_t)sqrt((double)v);
    while (r * r > v) r--;
    while ((r + 1) * (r + 1) <= v) r++;
    return (int)r;
}

static MRP_INLINE int imin(int a, int b) { return a < b ? a : b; }
static MRP_INLINE int imax(int a, int b) { return a > b ? a : b; }

// 升の床の高さ (段と島の基準・bed と furn は含めない)。決まらない升は NoZ
static MRP_INLINE int32_t base_z(const WaterFlowArgs* a, int k)
{
    int l = a->lvl[k];
    if (l == NoLevel || l == VoidLevel) return NoZ;
    int i = a->isl[k];
    if (i == 0 || i >= a->islN || a->islBase[i] == NoZ) return NoZ;
    return a->islBase[i] + l * StepHeight;
}

static MRP_INLINE int weir(int e) { return e <= 0 ? 0 : imin(e / 2, (int)((int64_t)e * isqrt(16LL * e) / WeirDiv)); }

static MRP_INLINE int share(int f, int s) { return (int)((int64_t)f * (s + 1) / SubN - (int64_t)f * s / SubN); }

static MRP_INLINE void mark_tile(WaterFlowArgs* a, int tx, int ty)
{
    if (tx < 0 || ty < 0 || tx >= a->tw || ty >= a->th) return;
    int t = ty * a->tw + tx;
    if (a->tileDirty[t]) return;
    a->tileDirty[t] = 1;
    a->dirtyOut[a->dirtyN++] = t;
}

// 升 k (x, y) を回す升に入れる。もう入っていれば起こされた印だけ付ける
static MRP_INLINE void wake_at(WaterFlowArgs* a, int k, int x, int y)
{
    if (a->inList[k] != 0) { a->stir[k] = 1; return; }
    if (x < 1 || y < 1 || x >= a->w - 1 || y >= a->h - 1) return;
    a->inList[k] = 1;
    a->stir[k] = 1;
    a->act[a->actN++] = k;
}

// 座標の分からない升 (割り算は入っていない升の時だけ)
static MRP_INLINE void wake(WaterFlowArgs* a, int k)
{
    if (a->inList[k] != 0) { a->stir[k] = 1; return; }
    int y = k / a->w;
    wake_at(a, k, k - y * a->w, y);
}

static MRP_INLINE void mark(WaterFlowArgs* a, int k, int x, int y)
{
    const int tc = TileCells;
    mark_tile(a, x / tc, y / tc);
    int lx = x % tc, ly = y % tc;
    if (lx == 0) mark_tile(a, x / tc - 1, y / tc);
    else if (lx == tc - 1) mark_tile(a, x / tc + 1, y / tc);
    if (ly == 0) mark_tile(a, x / tc, y / tc - 1);
    else if (ly == tc - 1) mark_tile(a, x / tc, y / tc + 1);
    // 高さが変わった升の隣は流れが変わりうるので、眠っていれば起こす (C# の Mark と同じ順)
    if (x >= 1 && y >= 1 && x < a->w - 1 && y < a->h - 1)
    {
        wake_at(a, k, x, y);
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * a->w;
            if (a->hgt[j] > 0) wake_at(a, j, x + Nx[d], y + Ny[d]);
        }
    }
}

static MRP_INLINE int edge_key(const WaterFlowArgs* a, int k, int d) { return (a->slot[k] - 1) * 4 + d; }

static MRP_INLINE int lip_of(const WaterFlowArgs* a, int k, int d)
{
    return (a->lipMask[k] >> d & 1) != 0 ? a->lipH[edge_key(a, k, d)] : FallLip;
}

static MRP_INLINE int fall_rate(const WaterFlowArgs* a, int k, int d, int h)
{
    int key = edge_key(a, k, d), b = a->barB[key];
    if (b == 0) return weir(h - lip_of(a, k, d));
    int open = b == 255 ? 0 : weir(h - FallLip) * (256 - b);
    int over = a->barH[key] >= BarMax ? 0 : weir(h - imax(FallLip, (int)a->barH[key])) * (b == 255 ? 256 : b);
    return (open + over) / 256;
}

template <bool W>
static MRP_INLINE int sub_out(WaterFlowArgs* a, int k, int d, int g)
{
    if (g <= 0) g = 1;
    int q = share(g, a->sub);
    if (W && q > 0)
    {
        if (a->foN >= a->foCap) { a->foOver++; return q; }
        int* f = a->fo + a->foN * 3; f[0] = k; f[1] = d; f[2] = q; a->foN++;
    }
    return q;
}

static MRP_INLINE void drop_void(WaterFlowArgs* a)
{
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        if (a->lvl[k] != VoidLevel || a->hgt[k] <= 0) continue;
        a->voidAdd += a->hgt[k];
        a->hgt[k] = 0;
        int y = k / a->w;
        mark(a, k, k - y * a->w, y);
    }
}

// 升 k の 4 本の流れ出しを決める。W = false は書かずに流れ出しの合計だけ返す (眠っている升の検査用)
template <bool W>
static MRP_INLINE int flow_cell(WaterFlowArgs* a, int k)
{
    const int subGain = Gain * SubN, subDamp = 256 - (256 - Damp) / SubN;
    int w = a->w;
    int h = a->hgt[k];
    int sum = 0;
    int lk = a->lvl[k];
    int bk = a->bed[k] + a->furn[k];
    int ek = a->edge[k], sm = a->stepMask[k], fm = a->fallMask[k], cm = a->seaMask[k];
    int32_t zk = a->useZ && h > 0 ? base_z(a, k) : NoZ; // 高さを使う流れは全部 h > 0 の時だけ
    int falls = a->foN;
    for (int d = 0; d < 4; d++)
    {
        int j = k + Nx[d] + Ny[d] * w;
        int f = 0;
        if (h > 0 && a->open[j] != 0 && (ek >> d & 1) != 0 && (a->edge[j] >> Opp[d] & 1) != 0)
        {
            int lj = a->lvl[j];
            bool known = lk != NoLevel && lj != NoLevel;
            // 同じ島の同じ段なら高さも同じ (表を引かない)
            int32_t zj = zk == NoZ ? NoZ : lj == lk && a->isl[j] == a->isl[k] ? zk : base_z(a, j);
            if (zj != NoZ)
            {
                // 両側の高さが決まっていれば水面 (床 + 深さ) の差で流す (段の順位の一方通行は使わない)
                int hj = a->hgt[j];
                int diff = (int)((int64_t)zk + h + bk - zj - hj - a->bed[j] - a->furn[j]) - (hj == 0 ? DryHold : WetHold);
                f = a->flux[k * 4 + d] * subDamp / 256 + (diff > 0 ? diff * subGain / 256 : 0);
                if (f < 0) f = 0;
            }
            else if (!known || lj <= lk)
            {
                int hj = a->hgt[j];
                int diff = known && lj < lk ? h - DryHold
                    : h + bk - hj - a->bed[j] - a->furn[j] - (hj == 0 ? DryHold : WetHold);
                f = a->flux[k * 4 + d] * subDamp / 256 + (diff > 0 ? diff * subGain / 256 : 0);
                if (f < 0) f = 0;
            }
        }
        else if (h > 0 && (sm >> d & 1) != 0)
        {
            int t = a->stepTo[edge_key(a, k, d)], ht = a->hgt[t];
            int32_t zt = zk == NoZ ? NoZ : base_z(a, t);
            int diff;
            if (zt != NoZ)
            {
                // 段の向こうの床が dz だけ低い (負なら高い)。低い側は段の高さまで溜まってから高い床とつながる
                int dz = (int)((int64_t)zk + bk - zt - a->bed[t] - a->furn[t]);
                diff = dz > 0 ? h - imax(0, ht - dz) - (ht <= dz ? DryHold : WetHold)
                    : h + dz - ht - (ht == 0 ? DryHold : WetHold);
            }
            else diff = a->lvl[t] < lk
                ? h - imax(0, ht - StepHeight) - (ht <= StepHeight ? DryHold : WetHold)
                : h - StepHeight - ht - (ht == 0 ? DryHold : WetHold);
            if (diff > 0) sum += sub_out<W>(a, k, d, diff * Gain / 256);
        }
        else if ((fm >> d & 1) != 0 && h > FallLip)
        {
            int g = fall_rate(a, k, d, h);
            if (g > 0) sum += sub_out<W>(a, k, d, g);
        }
        else if (h > 0 && (cm >> d & 1) != 0)
        {
            int diff = h + bk - a->seaLevel - WetHold;
            if (diff > 0) sum += sub_out<W>(a, k, d, diff * Gain / 256);
        }
        int q = share(f, a->sub);
        sum += q;
        if (!W) { sum += f; continue; }
        a->flux[k * 4 + d] = f;
        a->move[k * 4 + d] = q;
        if (f > 0) wake(a, j);
    }
    if (W && sum > h)
    {
        for (int d = 0; d < 4; d++)
        {
            a->flux[k * 4 + d] = (int)((int64_t)a->flux[k * 4 + d] * h / sum);
            a->move[k * 4 + d] = (int)((int64_t)a->move[k * 4 + d] * h / sum);
        }
        for (int x = falls; x < a->foN; x++) a->fo[x * 3 + 2] = (int)((int64_t)a->fo[x * 3 + 2] * h / sum);
    }
    return sum;
}

static MRP_INLINE void flow(WaterFlowArgs* a)
{
    a->foN = 0;
    for (int i = 0; i < a->actN; i++) flow_cell<true>(a, a->act[i]);
}

// 眠っている升 (濡れていて回す升に居ない) は流れ出しが 0 で、乾いていく深さでもないはず。外れた升を数える
static MRP_INLINE void check_sleep(WaterFlowArgs* a)
{
    int w = a->w;
    for (int y = 1; y < a->h - 1; y++)
    for (int x = 1; x < w - 1; x++)
    {
        int k = y * w + x;
        if (a->hgt[k] <= 0 || a->inList[k] != 0) continue;
        if (a->hgt[k] < MinDepth || flow_cell<false>(a, k) != 0) a->sleepViol++;
    }
}

static MRP_INLINE void heights(WaterFlowArgs* a)
{
    int w = a->w, n = a->actN; // 途中で起こした升は流れ込みが無い (流れ込みは flow で起こす) ので、この回では回さない
    for (int i = 0; i < n; i++)
    {
        int k = a->act[i];
        int dh = 0;
        for (int d = 0; d < 4; d++)
        {
            dh -= a->move[k * 4 + d];
            int j = k + Nx[d] + Ny[d] * w;
            dh += a->move[j * 4 + Opp[d]];
        }
        // 深さが変わったら 1 回だけ印を付ける (印は k 自身の深さを読まないので、増減と乾きで 2 回付けるのと同じ)
        int hk = a->hgt[k] + dh;
        bool changed = dh != 0;
        if (a->dry && hk > 0 && hk < MinDepth) { hk--; changed = true; }
        if (!changed) continue;
        a->hgt[k] = hk;
        int y = k / w;
        mark(a, k, k - y * w, y);
    }
}

// 1 刻みの小刻み SubN 回 (浸水の口へ注ぐ → 小刻み → 縁を越えた量を縁ごとに集める) をまとめて回す
// (src/Terrain/WaterSim.cs の StepOnce の小刻みのループ・Pour3・SubFalls と同じ結果)
struct WaterStepArgs
{
    WaterFlowArgs f;
    const int32_t* infK; const int32_t* infRate; int32_t infN;
    int32_t dryStep;                // この刻みの最後の小刻みで浅い升を乾かすか
    int32_t* fallStep;              // 縁を越えた量 (升・向き・量の 3 個ずつ・縁ごとに合計)
    int32_t fsN, fsCap, fsOver;
    int32_t* fallAt;                // 縁の枠 × 4 + 向き → fallStep の番号 + 1
    int64_t inflowAdd, seaOutAdd;
};

// 口の周りの開いている升へ等分して入れる。入れた量 (周りが全部閉じていれば 0) を返す
static MRP_INLINE int pour3(WaterFlowArgs* a, int k, int amt)
{
    int w = a->w, n = 0;
    for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++) if (a->open[k + dx + dy * w] != 0) n++;
    if (n == 0) return 0;
    int each = amt / n, rest = amt - each * n;
    for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int j = k + dx + dy * w;
            if (a->open[j] == 0) continue;
            int v = each + (rest > 0 ? 1 : 0);
            if (rest > 0) rest--;
            a->hgt[j] += v;
            wake(a, j);
            int y = j / w;
            mark(a, j, j - y * w, y);
        }
    return amt;
}

static MRP_INLINE void sub_falls(WaterStepArgs* s)
{
    WaterFlowArgs* a = &s->f;
    int w = a->w;
    for (int i = 0; i < a->foN; i++)
    {
        int k = a->fo[i * 3], d = a->fo[i * 3 + 1], a0 = a->fo[i * 3 + 2];
        int amt = imin(a0, a->hgt[k]);
        if (amt <= 0) continue;
        a->hgt[k] -= amt;
        int y = k / w;
        mark(a, k, k - y * w, y);
        if ((a->seaMask[k] >> d & 1) != 0) { s->seaOutAdd += amt; continue; }
        int key = edge_key(a, k, d);
        if ((a->stepMask[k] >> d & 1) != 0)
        {
            int t = a->stepTo[key];
            a->hgt[t] += amt;
            int ty = t / w;
            mark(a, t, t - ty * w, ty);
            wake(a, t);
            continue;
        }
        int at = s->fallAt[key];
        if (at == 0)
        {
            if (s->fsN >= s->fsCap) { s->fsOver++; continue; }
            int32_t* e = s->fallStep + s->fsN * 3; e[0] = k; e[1] = d; e[2] = amt;
            s->fallAt[key] = ++s->fsN;
        }
        else s->fallStep[(at - 1) * 3 + 2] += amt;
    }
}

// 定数が C# と違えば何もせず 0 (C# 版で回す)
MRP_API int32_t mrp_water_step(WaterStepArgs* p)
{
    if (p->f.subN != SubN || p->f.tileCells != TileCells) return 0;
    WaterStepArgs s = *p;
    for (int sub = 0; sub < SubN; sub++)
    {
        for (int i = 0; i < s.infN; i++)
        {
            int amt = share(s.infRate[i], sub);
            if (amt > 0) s.inflowAdd += pour3(&s.f, s.infK[i], amt);
        }
        s.f.sub = sub;
        s.f.dry = sub == SubN - 1 && s.dryStep;
        drop_void(&s.f);
        if (s.f.checkSleep) check_sleep(&s.f);
        flow(&s.f);
        heights(&s.f);
        sub_falls(&s);
    }
    *p = s;
    return 1;
}
