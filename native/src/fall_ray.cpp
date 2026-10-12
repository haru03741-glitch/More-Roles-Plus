#include "mrp_native.h"

// 縁を越えた水の行き先を探す光線 (src/Terrain/WaterReach.cs の Target / SideAir と同じ結果)。
// useZ = 0 は島の段の順位で「低い床」を決める (C# と 1 バイト比較できる)。useZ = 1 は床の本当の高さ
// (島の基準の高さ + 段 × 段差) で比べ、高さが決まらない升だけ段の順位で比べる

struct FallRayArgs
{
    const uint8_t* open; const uint8_t* door; const uint8_t* prop; const uint8_t* air; const int8_t* lvl;
    const uint8_t* isl; const int32_t* islBase;
    const int32_t* q;   // 縁の升 × 4 + 向き
    int32_t* out;       // 1 つにつき 5 個: 理由・落ちる先 (−1 = 空・INT32_MIN = 落ちない)・距離・最後に見た升の x・y
    int32_t n, w, h, islN, useZ;
    int32_t fallScan[4];
    int32_t sideScan, airLip;
};

// 理由 (C# の RayWhy と同じ並び)
enum { RScan, RBorder, RDoor, RProp, RFall, RSame, RNoLevel, RAir, RWall, RSide, RAirSide };

static const int NoLevel = -128, VoidLevel = -2, StepHeight = 1024 / 4;
static const int32_t NoTo = INT32_MIN, NoZ = INT32_MIN;
static const int Nx[4] = { 1, -1, 0, 0 }, Ny[4] = { 0, 0, 1, -1 };

static inline int32_t base_z(const FallRayArgs* a, int k)
{
    int l = a->lvl[k];
    if (l == NoLevel || l == VoidLevel) return NoZ;
    int i = a->isl[k];
    if (i == 0 || i >= a->islN || a->islBase[i] == NoZ) return NoZ;
    return a->islBase[i] + l * StepHeight;
}

// 升 c の床が縁の升 k より低いか
static inline bool lower(const FallRayArgs* a, int k, int c)
{
    if (a->useZ)
    {
        int32_t zk = base_z(a, k), zc = base_z(a, c);
        if (zk != NoZ && zc != NoZ) return zc < zk;
    }
    int lc = a->lvl[c];
    return lc != NoLevel && lc < a->lvl[k];
}

static void side_air(const FallRayArgs* a, int x, int y, int d, int k, int& to, int& dist, int& tx, int& ty, bool& seen)
{
    const int w = a->w;
    int best = INT32_MAX, wall = 0, faceBest = INT32_MAX, faceDist = 0, faceX = 0, faceY = 0;
    for (int i = 1; i <= a->sideScan; i++)
    {
        int cx = x + Nx[d] * i;
        if (cx < 1 || cx >= w - 1 || i * i >= best) break;
        int row = y * w + cx;
        if (a->open[row] == 0 && a->air[row] == 0) wall++;
        for (int j = 1; j <= a->fallScan[3] && i * i + j * j < best; j++)
        {
            int cy = y - j;
            if (cy < 1) break;
            int c = cy * w + cx;
            if (a->door[c] != 0) break;
            if (a->open[c] == 0)
            {
                if (a->air[c] == 0) continue;
                if (wall > a->airLip) break;
                if (a->lvl[c] != VoidLevel)
                {
                    if (i * i + j * j < faceBest) { faceBest = i * i + j * j; faceDist = j; faceX = cx; faceY = cy; }
                    continue;
                }
                best = i * i + j * j; to = -1; dist = j; tx = cx; ty = cy;
                break;
            }
            seen = true;
            if (lower(a, k, c)) { best = i * i + j * j; to = c; dist = j; tx = cx; ty = cy; }
            break;
        }
    }
    if (to == NoTo && faceBest != INT32_MAX) { to = -1; dist = faceDist; tx = faceX; ty = faceY; }
}

static int target(const FallRayArgs* a, int k, int d, int& to, int& dist, int& tx, int& ty)
{
    const int w = a->w, h = a->h;
    int x = k % w, y = k / w;
    to = NoTo; dist = 0; tx = x; ty = y;
    int wall = 0, airDist = 0, airX = 0, airY = 0;
    bool walled = false;
    for (int i = 1; i <= a->fallScan[d]; i++)
    {
        int cx = x + Nx[d] * i, cy = y + Ny[d] * i;
        if (cx < 1 || cy < 1 || cx >= w - 1 || cy >= h - 1) return RBorder;
        int c = cy * w + cx;
        if (a->door[c] != 0) return RDoor;
        if (i == 1 && a->prop[c] != 0 && a->open[c] == 0) return RProp;
        tx = cx; ty = cy; dist = i;
        if (a->open[c] == 0)
        {
            if (airDist != 0)
            {
                if (a->lvl[c] == VoidLevel) break;
                continue;
            }
            if (a->air[c] != 0)
            {
                if (wall > a->airLip) { walled = true; wall++; continue; }
                if (a->lvl[c] == VoidLevel) { to = -1; return RAir; }
                airDist = i; airX = cx; airY = cy;
                continue;
            }
            wall++;
            continue;
        }
        if (lower(a, k, c)) { to = c; return RFall; }
        if (airDist != 0) break;
        return a->lvl[c] == NoLevel ? RNoLevel : RSame;
    }
    if (airDist != 0 && Nx[d] == 0) { to = -1; dist = airDist; tx = airX; ty = airY; return RAir; }
    if (Nx[d] == 0) return walled ? RWall : RScan;
    bool seen = false;
    side_air(a, x, y, d, k, to, dist, tx, ty, seen);
    if (airDist != 0 && to < 0) { to = -1; dist = airDist; tx = airX; ty = airY; return RAir; }
    if (to >= 0) return RSide;
    if (to == -1) return RAirSide;
    return seen ? RSame : walled ? RWall : RScan;
}

MRP_API void mrp_fall_ray(FallRayArgs* a)
{
    for (int i = 0; i < a->n; i++)
    {
        int key = a->q[i], to, dist, tx, ty;
        int why = target(a, key >> 2, key & 3, to, dist, tx, ty);
        int32_t* o = a->out + i * 5;
        o[0] = why; o[1] = to; o[2] = dist; o[3] = tx; o[4] = ty;
    }
}
