#include "mrp_native.h"
#include "digest.h"

// 床の上の火の 1 刻み (src/Terrain/FireSim.cs の StepCs と同じ結果)。整数だけで計算する。
// mode 0 = 熱の移動と升ごとの計算。熱が新しく届く升のタイルの材質がまだ決まっていなければ、何も書かずにそのタイルを返す
//          (材質は Unity の当たり判定から決めるので C# が決めてから呼び直す)。水を湯気にする・焦げ・壁の熱などは出来事として返す。
// mode 1 = 熱も炎も無い升を一覧から外す。

struct FireStepArgs
{
    int32_t *t, *dt, *fuel; uint8_t *mat, *flag, *burn, *inList, *flareAt, *arcAt; uint16_t* chr;
    uint8_t *tileMat, *tileDirty; int32_t* act;
    const uint8_t *open, *edge; const int32_t* hgt;
    const uint16_t* pdist; const int32_t *pcomp, *ppar, *ppress;   // 吸い出し (Decompression の FlowAt と同じ地図)
    int32_t *ev, *dirty, *missing;                                  // 出力: 出来事 (種類・升・値)・描き直すタイル・材質の要るタイル
    int32_t w, h, tw, th, actN, step, pull, decides, wind, pw, ph, pressN;
    int32_t evCap, evN, dirtyCap, dirtyN, missCap, missN, burning, mode, searMin;
};

namespace
{
const int MaxT = 2047, Cond = 40, Cool = 30, Rise = 48, FadeFuel = 60, BurnUp = 40, BurnDown = 14;
const int SteamT = 100, QuenchDiv = 6, QuenchBase = 20, QuenchHeat = 6, FlareCool = 45, ArcCool = 15;
const int BlowOut = 900, Lean = 48;
const int Full = 1 << 16, ThinAir = Full / 2, PerUnit = 8, Wind1024 = 410, Far = 0xFFFF;
const int TileCells = 16;
const int MatFuel = 5, MatElectric = 6;
const uint8_t FBurning = 1;
const int MBurn[] = { 0, 0, 1, 1, 1, 2, 1 };
const int MIgnite[] = { 9999, 9999, 1500, 650, 450, 380, 750 };
const int MFlame[] = { 0, 0, 1300, 1500, 1400, 1800, 1450 };
const int CharStep[] = { 20, 160, 600 };
const int Nx[] = { 1, -1, 0, 0 };
const int Ny[] = { 0, 0, 1, -1 };
const int Opp[] = { 1, 0, 3, 2 };
const int EvSteam = 1, EvFlare = 2, EvArc = 3, EvChar = 4, EvSear = 5;

inline int imin(int a, int b) { return a < b ? a : b; }
inline int imax(int a, int b) { return a > b ? a : b; }
inline int iabs(int a) { return a < 0 ? -a : a; }

inline int Jitter(int k)
{
    uint32_t h = (uint32_t)k * 2654435761u;
    h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
    return (int)(h >> 25);
}

inline bool Passable(const FireStepArgs* a, int k, int d)
{
    return (a->edge[k] >> d & 1) != 0 && (a->edge[k + Nx[d] + Ny[d] * a->w] >> Opp[d] & 1) != 0;
}

// 壁 (流れて行けない向き) に接している
inline bool HasWall(const FireStepArgs* a, int k)
{
    for (int d = 0; d < 4; d++)
        if (!a->open[k + Nx[d] + Ny[d] * a->w] || !Passable(a, k, d)) return true;
    return false;
}

// Decompression.Strength1024 と同じ折れ線
inline int Strength1024(int d, bool wind)
{
    if (wind) return d <= 3 * PerUnit ? Wind1024 : d <= 4 * PerUnit ? Wind1024 * (4 * PerUnit - d) / PerUnit : 0;
    if (d <= 3 * PerUnit) return 1638 - d * 1024 / (3 * PerUnit);
    if (d <= 6 * PerUnit) return 614 - (d - 3 * PerUnit) * 461 / (3 * PerUnit);
    if (d <= 10 * PerUnit) return 154 - (d - 6 * PerUnit) * 154 / (4 * PerUnit);
    return 0;
}

// Decompression.FlowAt と同じ。水と吸い出しの升はどちらも SolidMap の升 4 個で 1 升なので、火の升 (x, y) をそのまま引く
inline bool FlowAt(const FireStepArgs* a, int x, int y, int* dist, int* press, int* dx, int* dy)
{
    if (x < 1 || y < 1 || x >= a->pw - 1 || y >= a->ph - 1) return false;
    int k = y * a->pw + x;
    int c = a->pcomp[k];
    if (a->pdist[k] == Far || c < 0) return false;
    *dist = a->pdist[k];
    *press = a->ppress[c];
    int t = k;
    for (int i = 0; i < 3 && a->ppar[t] >= 0; i++) t = a->ppar[t];
    *dx = t % a->pw - x;
    *dy = t / a->pw - y;
    return true;
}

inline void Emit(FireStepArgs* a, int kind, int k, int v)
{
    if (a->evN + 3 > a->evCap) return;
    a->ev[a->evN] = kind; a->ev[a->evN + 1] = k; a->ev[a->evN + 2] = v;
    a->evN += 3;
}

inline void MarkTile(FireStepArgs* a, int tx, int ty)
{
    if (tx < 0 || ty < 0 || tx >= a->tw || ty >= a->th) return;
    int t = ty * a->tw + tx;
    if (a->tileDirty[t]) return;
    a->tileDirty[t] = 1;
    if (a->dirtyN < a->dirtyCap) a->dirty[a->dirtyN++] = t;
}

inline void Mark(FireStepArgs* a, int k)
{
    int x = k % a->w, y = k / a->w;
    int tx = x / TileCells, ty = y / TileCells;
    MarkTile(a, tx, ty);
    int lx = x % TileCells, ly = y % TileCells;
    if (lx == 0) MarkTile(a, tx - 1, ty);
    else if (lx == TileCells - 1) MarkTile(a, tx + 1, ty);
    if (ly == 0) MarkTile(a, tx, ty - 1);
    else if (ly >= TileCells - 2) MarkTile(a, tx, ty + 1);
}

inline bool Inside(const FireStepArgs* a, int k)
{
    int x = k % a->w, y = k / a->w;
    return x >= 1 && y >= 1 && x < a->w - 1 && y < a->h - 1;
}

inline int TileOf(const FireStepArgs* a, int k) { return k / a->w / TileCells * a->tw + k % a->w / TileCells; }

// 熱が届くかもしれない升 (熱い側から差がある隣) のタイルで、材質がまだの物を集める。届かない升のタイルを決めても結果は変わらない
int FindMissing(FireStepArgs* a)
{
    int n = 0;
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        int tk = a->t[k];
        if (tk <= 0) continue;
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * a->w;
            if (!a->open[j] || !Passable(a, k, d) || tk - a->t[j] <= 0 || a->inList[j] || !Inside(a, j)) continue;
            int t = TileOf(a, j);
            if (a->tileMat[t]) continue;
            bool dup = false;
            for (int m = 0; m < n; m++) if (a->missing[m] == t) { dup = true; break; }
            if (dup) continue;
            if (n < a->missCap) a->missing[n++] = t;
        }
    }
    return n;
}

void Step(FireStepArgs* a)
{
    const int w = a->w;
    int32_t* t_ = a->t; int32_t* dt = a->dt; int32_t* fuel = a->fuel;
    const bool pull = a->pull != 0;

    // 熱の移動: 熱い側の升が差に比例して隣へ渡す
    int count = a->actN;
    for (int i = 0; i < count; i++)
    {
        int k = a->act[i];
        int tk = t_[k];
        if (tk <= 0) continue;
        int leanD = -1, leanS = 0;
        if (pull && (a->flag[k] & FBurning))
        {
            int dist, press, fx, fy;
            if (FlowAt(a, k % w, k / w, &dist, &press, &fx, &fy))
            {
                int s = (int)((int64_t)Strength1024(dist, a->wind != 0) * press / Full);
                if (s > 0 && !(fx == 0 && fy == 0))
                {
                    leanS = s;
                    leanD = iabs(fx) >= iabs(fy) ? (fx > 0 ? 0 : 1) : (fy > 0 ? 2 : 3);
                }
            }
        }
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * w;
            if (!a->open[j] || !Passable(a, k, d)) continue;
            int diff = tk - t_[j];
            if (diff <= 0) continue;
            int c = d == leanD ? Cond + leanS * Lean / 1024 : Cond;
            int f = diff * c / 1024;
            if (f <= 0) continue;
            dt[k] -= f;
            dt[j] += f;
            if (!a->inList[j] && Inside(a, j))
            {
                a->inList[j] = 1;
                a->act[a->actN++] = j;
            }
        }
    }

    // 升ごと: 燃える・冷える・水・空気
    int burning = 0;
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        int t = t_[k] + dt[k];
        dt[k] = 0;
        int m = a->mat[k];
        uint8_t flag = a->flag[k];
        bool on = (flag & FBurning) != 0;
        if (!a->open[k]) { on = false; t = 0; }
        int jit = Jitter(k);
        if (on)
        {
            fuel[k] -= MBurn[m];
            int ch = a->chr[k];
            a->chr[k] = (uint16_t)imin(0xFFFF, ch + MBurn[m]);
            t += (MFlame[m] + jit * 2 - t) * Rise / 256;
            for (int s = 0; s < 3; s++)
                if (ch < CharStep[s] && a->chr[k] >= CharStep[s]) { Mark(a, k); Emit(a, EvChar, k, s); }
            if (a->decides && imin(t, MFlame[m] + 128) > a->searMin && HasWall(a, k)) Emit(a, EvSear, k, t);
            if (fuel[k] <= 0)
            {
                fuel[k] = 0;
                on = false;
            }
        }
        else if (fuel[k] > 0 && t >= MIgnite[m] + jit) on = true;
        t -= t * Cool / 1024 + 1;

        int depth = a->hgt[k];
        if (depth > 0 && t > SteamT)
        {
            if (on && m == MatFuel)
            {
                Emit(a, EvSteam, k, depth);
                if ((uint8_t)(a->step - a->flareAt[k]) >= FlareCool || a->flareAt[k] == 0)
                {
                    a->flareAt[k] = (uint8_t)imax(1, a->step & 255);
                    Emit(a, EvFlare, k, 0);
                }
            }
            else
            {
                int want = (t - SteamT) / QuenchDiv + QuenchBase;
                int e = imin(depth, want);
                Emit(a, EvSteam, k, e);
                t -= e * QuenchHeat / (m == MatElectric && on ? 2 : 1);
                if (on && m == MatElectric && (uint8_t)(a->step - a->arcAt[k]) >= ArcCool)
                {
                    a->arcAt[k] = (uint8_t)a->step;
                    Emit(a, EvArc, k, 0);
                }
            }
        }

        if (pull && (on || t > 0))
        {
            int dist, press, fx, fy;
            if (FlowAt(a, k % w, k / w, &dist, &press, &fx, &fy))
            {
                int s = (int)((int64_t)Strength1024(dist, a->wind != 0) * press / Full);
                if (s > BlowOut) { on = false; t -= t / 4; }
                else if (press < ThinAir && !(flag & FBurning)) on = false;
            }
        }

        if (t < 0) t = 0;
        if (t > MaxT) t = MaxT;
        int oldGlow = t_[k] >> 6;
        t_[k] = t;
        a->flag[k] = on ? (uint8_t)(flag | FBurning) : (uint8_t)(flag & ~FBurning);
        int target = !on ? 0 : fuel[k] >= FadeFuel ? 255 : 64 + 191 * fuel[k] / FadeFuel;
        int b = a->burn[k];
        int nb = b < target ? imin(target, b + BurnUp) : imax(target, b - BurnDown);
        if (nb != b || oldGlow != t >> 6) Mark(a, k);
        a->burn[k] = (uint8_t)nb;
        if (on) burning++;
    }
    a->burning = burning;
}

void Compact(FireStepArgs* a)
{
    int o = 0;
    for (int i = 0; i < a->actN; i++)
    {
        int k = a->act[i];
        if (a->t[k] == 0 && a->burn[k] == 0 && (a->flag[k] & FBurning) == 0) { a->inList[k] = 0; continue; }
        a->act[o++] = k;
    }
    a->actN = o;
}
}

MRP_API void mrp_fire_step(FireStepArgs* a)
{
    a->evN = 0; a->dirtyN = 0; a->missN = 0;
    if (a->mode == 1) { Compact(a); return; }
    a->missN = FindMissing(a);
    if (a->missN > 0) return;
    Step(a);
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_fire_step(FireStepArgs* a)
{
    Dg d(DgFireStep);
    int64_t n = (int64_t)a->w * a->h, tn = (int64_t)a->tw * a->th, pn = (int64_t)a->pw * a->ph;
    d.InV(a->w); d.InV(a->h); d.InV(a->tw); d.InV(a->th); d.InV(a->actN); d.InV(a->step); d.InV(a->pull); d.InV(a->decides); d.InV(a->wind);
    d.InV(a->pw); d.InV(a->ph); d.InV(a->pressN); d.InV(a->evCap); d.InV(a->dirtyCap); d.InV(a->missCap); d.InV(a->mode); d.InV(a->searMin);
    d.In(a->t, n * 4); d.In(a->fuel, n * 4); d.In(a->mat, n); d.In(a->flag, n); d.In(a->burn, n); d.In(a->inList, n);
    d.In(a->flareAt, n); d.In(a->arcAt, n); d.In(a->chr, n * 2); d.In(a->tileMat, tn); d.In(a->tileDirty, tn);
    d.In(a->act, (int64_t)a->actN * 4); d.In(a->open, n); d.In(a->edge, n); d.In(a->hgt, n * 4);
    if (a->pull) { d.In(a->pdist, pn * 2); d.In(a->pcomp, pn * 4); d.In(a->ppar, pn * 4); d.In(a->ppress, (int64_t)a->pressN * 4); }
    mrp_fire_step(a);
    d.Out(a->t, n * 4); d.Out(a->fuel, n * 4); d.Out(a->flag, n); d.Out(a->burn, n); d.Out(a->inList, n);
    d.Out(a->flareAt, n); d.Out(a->arcAt, n); d.Out(a->chr, n * 2); d.Out(a->tileDirty, tn);
    d.OutV(a->actN); d.Out(a->act, (int64_t)a->actN * 4);
    d.OutV(a->evN); d.Out(a->ev, (int64_t)a->evN * 4);
    d.OutV(a->dirtyN); d.Out(a->dirty, (int64_t)a->dirtyN * 4);
    d.OutV(a->missN); d.Out(a->missing, (int64_t)a->missN * 4);
    d.OutV(a->burning);
    d.Commit();
}
