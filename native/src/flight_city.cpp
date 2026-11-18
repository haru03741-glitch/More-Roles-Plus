#include "mrp_native.h"
#include <math.h>
#include <stdint.h>
#include <string.h>
#include <vector>

// 3D の艦隊戦の夜の街 (飛ぶ範囲の下に広がる景色)。種から区画・ビル・格子の電波塔・高架の道路・航空障害灯を組み、
// 区画ごとの静的なメッシュの頂点と添字を作る (作るのは場面に入る時の 1 回だけ)。
// ビルは飛べる高さの下限より低く置くので当たりは持たない。形は種と整数の hash だけで決まり、どの端末でも同じ並びになる。
//
// 頂点 (28 バイト) = 位置 f32x3・法線 i8x3 + 材質 i8・色 u8x4・uv f32x2
//   材質: 0 = ビルの壁 (uv = 壁に沿った長さ・地面からの高さ)・1 = 屋上・2 = 光る鉄骨 (塔)・3 = 地面・4 = 展望台 (uv は壁と同じ)・5 = 高架の道路
//   色: r = 建物の種・g = 様式 (0 事務所 / 1 ガラスの塔 / 2 住まい / 3 消えた建物)・b = 窓の点く割合・a = ちらつきの組
// 灯の頂点 (同じ 28 バイト) = 位置 (灯の中心)・法線 xy = 板の角 (-1/1)・色 = 灯の色 + a = 点滅の組・uv = (大きさ, 位相)

namespace
{
struct V { float p[3]; int8_t n[4]; uint8_t c[4]; float uv[2]; };
static_assert(sizeof(V) == 28, "city vertex");

enum Mat { MWall = 0, MRoof = 1, MBeam = 2, MGround = 3, MDeck = 4, MRoad = 5 };

const float Pitch = 64.0f;      // 区画の間隔 (道の中心から中心)
const float Extent = 3200.0f;   // 街の広さ (中心からの半分)
const float InnerR = 1150.0f;   // ここより内は区画を細かく分けて密に建てる
const float TileSize = 800.0f;  // メッシュを分ける升 (見えない升は描かれない)
const int Tiles = 8;            // 升の数 (片側)
const float TowerX = 380.0f, TowerZ = 560.0f; // 電波塔の位置
const float MaxH = 150.0f;      // 建物の高さの上限 (飛べる下限より下に収める)

inline uint32_t H(uint32_t x)
{
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
    return x;
}
inline uint32_t H3(int a, int b, uint32_t s) { return H((uint32_t)a * 73856093u ^ H((uint32_t)b * 19349663u ^ s)); }
inline float U(uint32_t h) { return (float)(h & 0xffffff) * (1.0f / 16777216.0f); }

// 粗い升の値をなめらかにつないだ雑音 (0..1)。高い建物の集まる地区を決める
float Noise2(float x, float z, uint32_t s)
{
    int ix = (int)floorf(x), iz = (int)floorf(z);
    float fx = x - ix, fz = z - iz;
    fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
    float a = U(H3(ix, iz, s)), b = U(H3(ix + 1, iz, s)), c = U(H3(ix, iz + 1, s)), d = U(H3(ix + 1, iz + 1, s));
    return (a + (b - a) * fx) * (1 - fz) + (c + (d - c) * fx) * fz;
}

struct Mesh
{
    std::vector<V> v;
    std::vector<int32_t> i;
    float mn[3] = { 1e30f, 1e30f, 1e30f }, mx[3] = { -1e30f, -1e30f, -1e30f };
    int kind = 0; // 0 = 街の形・1 = 灯の板
};

struct City
{
    float ground;
    std::vector<Mesh> mesh;
    Mesh* tile[Tiles][Tiles];
    Mesh* lampMesh;
};

void Grow(Mesh& m, const float* p)
{
    for (int k = 0; k < 3; k++) { if (p[k] < m.mn[k]) m.mn[k] = p[k]; if (p[k] > m.mx[k]) m.mx[k] = p[k]; }
}

int8_t Sn(float x) { float v = x * 127.0f; return (int8_t)(v > 127 ? 127 : (v < -127 ? -127 : (v < 0 ? v - 0.5f : v + 0.5f))); }

void Quad(Mesh& m, const float* a, const float* b, const float* c, const float* d, const float* n, int mat, const uint8_t* col,
          float u0, float v0, float u1, float v1)
{
    int base = (int)m.v.size();
    const float* ps[4] = { a, b, c, d };
    float uvs[4][2] = { { u0, v0 }, { u1, v0 }, { u1, v1 }, { u0, v1 } };
    for (int k = 0; k < 4; k++)
    {
        V vx;
        memcpy(vx.p, ps[k], 12);
        vx.n[0] = Sn(n[0]); vx.n[1] = Sn(n[1]); vx.n[2] = Sn(n[2]); vx.n[3] = (int8_t)mat;
        memcpy(vx.c, col, 4);
        vx.uv[0] = uvs[k][0]; vx.uv[1] = uvs[k][1];
        m.v.push_back(vx);
        Grow(m, ps[k]);
    }
    // Unity は画面で時計回りが表 = cross(b - a, c - a) が法線の向き。法線に合わせて巡りを決める
    float e1[3] = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, e2[3] = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
    float cx = e1[1] * e2[2] - e1[2] * e2[1], cy = e1[2] * e2[0] - e1[0] * e2[2], cz = e1[0] * e2[1] - e1[1] * e2[0];
    bool fwd = cx * n[0] + cy * n[1] + cz * n[2] > 0;
    int32_t t[6] = { base, base + 1, base + 2, base, base + 2, base + 3 };
    if (!fwd) { t[1] = base + 2; t[2] = base + 1; t[4] = base + 3; t[5] = base + 2; }
    m.i.insert(m.i.end(), t, t + 6);
}

// 軸に沿った箱 (底は描かない)。壁の uv = 壁に沿った長さ・地面からの高さ
void Box(Mesh& m, float x0, float z0, float x1, float z1, float y0, float y1, float ground, int wallMat, int roofMat, const uint8_t* col, bool roof = true)
{
    float h0 = y0 - ground, h1 = y1 - ground;
    float p[8][3] = {
        { x0, y0, z0 }, { x1, y0, z0 }, { x1, y0, z1 }, { x0, y0, z1 },
        { x0, y1, z0 }, { x1, y1, z0 }, { x1, y1, z1 }, { x0, y1, z1 } };
    float nS[3] = { 0, 0, -1 }, nE[3] = { 1, 0, 0 }, nN[3] = { 0, 0, 1 }, nW[3] = { -1, 0, 0 }, nU[3] = { 0, 1, 0 };
    float wx = x1 - x0, wz = z1 - z0;
    // 南 (z0)・東 (x1)・北 (z1)・西 (x0)。面の巡りは Quad が法線から決める
    Quad(m, p[1], p[0], p[4], p[5], nS, wallMat, col, 0, h0, wx, h1);
    Quad(m, p[2], p[1], p[5], p[6], nE, wallMat, col, 0, h0, wz, h1);
    Quad(m, p[3], p[2], p[6], p[7], nN, wallMat, col, 0, h0, wx, h1);
    Quad(m, p[0], p[3], p[7], p[4], nW, wallMat, col, 0, h0, wz, h1);
    if (roof) Quad(m, p[7], p[6], p[5], p[4], nU, roofMat, col, x0, z0, x1, z1);
}

// 2 点を結ぶ細い角柱 (塔の鉄骨)。4 面だけ (端は塞がない)
void Beam(Mesh& m, const float* a, const float* b, float r, const uint8_t* col)
{
    float d[3] = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
    float L = sqrtf(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
    if (L < 1e-4f) return;
    for (int k = 0; k < 3; k++) d[k] /= L;
    float up[3] = { 0, 1, 0 };
    if (fabsf(d[1]) > 0.9f) { up[0] = 1; up[1] = 0; }
    float s[3] = { d[1] * up[2] - d[2] * up[1], d[2] * up[0] - d[0] * up[2], d[0] * up[1] - d[1] * up[0] };
    float sl = sqrtf(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]);
    for (int k = 0; k < 3; k++) s[k] /= sl;
    float t[3] = { d[1] * s[2] - d[2] * s[1], d[2] * s[0] - d[0] * s[2], d[0] * s[1] - d[1] * s[0] };
    float dirs[4][3];
    for (int k = 0; k < 3; k++) { dirs[0][k] = s[k]; dirs[1][k] = t[k]; dirs[2][k] = -s[k]; dirs[3][k] = -t[k]; }
    for (int f = 0; f < 4; f++)
    {
        const float* e0 = dirs[f];
        const float* e1 = dirs[(f + 1) & 3];
        float n[3] = { e0[0] + e1[0], e0[1] + e1[1], e0[2] + e1[2] };
        float nl = sqrtf(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        for (int k = 0; k < 3; k++) n[k] /= nl;
        float p0[3], p1[3], p2[3], p3[3];
        for (int k = 0; k < 3; k++)
        {
            p0[k] = a[k] + e0[k] * r; p1[k] = a[k] + e1[k] * r;
            p2[k] = b[k] + e1[k] * r; p3[k] = b[k] + e0[k] * r;
        }
        Quad(m, p1, p0, p3, p2, n, MBeam, col, 0, 0, 1, L);
    }
}

// 灯 1 つ = カメラへ向ける板 (シェーダが角を広げる)。cls = 点滅の組 (0 点きっぱなし・1 高い建物の同期した点滅・2 ゆっくり明滅)
void Lamp(Mesh& m, float x, float y, float z, float size, uint8_t r, uint8_t g, uint8_t b, int cls, float phase)
{
    int base = (int)m.v.size();
    static const int8_t cx[4] = { -127, 127, 127, -127 }, cy[4] = { -127, -127, 127, 127 };
    for (int k = 0; k < 4; k++)
    {
        V vx;
        vx.p[0] = x; vx.p[1] = y; vx.p[2] = z;
        vx.n[0] = cx[k]; vx.n[1] = cy[k]; vx.n[2] = 0; vx.n[3] = 0;
        vx.c[0] = r; vx.c[1] = g; vx.c[2] = b; vx.c[3] = (uint8_t)cls;
        vx.uv[0] = size; vx.uv[1] = phase;
        m.v.push_back(vx);
    }
    float p[3] = { x, y, z };
    float lo[3] = { x - size, y - size, z - size }, hi[3] = { x + size, y + size, z + size };
    Grow(m, lo); Grow(m, hi); (void)p;
    int32_t t[6] = { base, base + 3, base + 2, base, base + 2, base + 1 };
    m.i.insert(m.i.end(), t, t + 6);
}

Mesh& TileOf(City& c, float x, float z)
{
    int i = (int)floorf((x + Extent) / TileSize), j = (int)floorf((z + Extent) / TileSize);
    i = i < 0 ? 0 : (i >= Tiles ? Tiles - 1 : i);
    j = j < 0 ? 0 : (j >= Tiles ? Tiles - 1 : j);
    return *c.tile[i][j];
}

// 区画の道の幅: 4 本ごとに広い通り
float RoadW(int line) { return (line % 4 == 0) ? 18.0f : 10.0f; }

void Building(City& c, uint32_t seed, float x0, float z0, float x1, float z1, float cx, float cz, uint32_t h)
{
    float G = c.ground;
    float r = sqrtf(cx * cx + cz * cz);
    // 高い建物の集まる地区 (雑音) と、中心から離れるほど低く疎らに
    float district = Noise2(cx / 700.0f, cz / 700.0f, seed ^ 0x51ed);
    float tallP = district > 0.62f ? (district - 0.62f) * 2.6f : 0.0f;
    float fall = r < 1600 ? 1.0f : 1.0f - (r - 1600) / 2400.0f;
    if (fall < 0.25f) fall = 0.25f;
    float u = U(h);
    int style;
    float hgt;
    if (U(H(h ^ 0x1234)) < tallP * fall)
    {
        hgt = 70.0f + 70.0f * U(H(h ^ 0x77));
        style = U(H(h ^ 0x99)) < 0.55f ? 1 : 0;
    }
    else
    {
        float mid = 14.0f + 26.0f * u * u;
        hgt = mid * (0.55f + 0.45f * fall) + 4.0f;
        float su = U(H(h ^ 0x99));
        style = su < 0.42f ? 2 : (su < 0.85f ? 0 : 3);
    }
    if (hgt > MaxH) hgt = MaxH;
    float lit = style == 3 ? 0.03f : (0.06f + 0.3f * U(H(h ^ 0x4242)));
    uint8_t col[4] = { (uint8_t)(h >> 8), (uint8_t)(style * 64 + 8), (uint8_t)(lit * 255), (uint8_t)(H(h ^ 0xabc) & 7) };
    Mesh& m = TileOf(c, cx, cz);
    // 高い建物の一部は上で一段細くなる (段の上の小さな塔)
    float w = x1 - x0, d = z1 - z0;
    if (hgt > 60 && U(H(h ^ 0x5151)) < 0.35f)
    {
        float t = 0.55f + 0.25f * U(H(h ^ 0x6161));
        float hb = hgt * t;
        Box(m, x0, z0, x1, z1, G, G + hb, G, MWall, MRoof, col);
        float ix = w * 0.18f, iz = d * 0.18f;
        Box(m, x0 + ix, z0 + iz, x1 - ix, z1 - iz, G + hb, G + hgt, G, MWall, MRoof, col);
        x0 += ix; x1 -= ix; z0 += iz; z1 -= iz;
    }
    else Box(m, x0, z0, x1, z1, G, G + hgt, G, MWall, MRoof, col);
    // 屋上の機械室
    if (U(H(h ^ 0x7171)) < 0.45f && w > 8 && d > 8)
    {
        float mx0 = x0 + w * 0.25f, mz0 = z0 + d * 0.3f;
        uint8_t mc[4] = { col[0], (uint8_t)(3 * 64 + 8), 0, col[3] };
        Box(m, mx0, mz0, mx0 + w * 0.3f, mz0 + d * 0.3f, G + hgt, G + hgt + 2.2f, G, MWall, MRoof, mc);
    }
    // 航空障害灯: 高い建物は屋上の四隅で揃って点滅・中ほどの高さにも並ぶ。低い建物は時々屋上に 1 つ
    float top = G + hgt + 0.6f;
    float ph = 0.0f;
    if (hgt > 55)
    {
        Lamp(*c.lampMesh, x0, top, z0, 1.6f, 255, 30, 22, 1, ph);
        Lamp(*c.lampMesh, x1, top, z0, 1.6f, 255, 30, 22, 1, ph);
        Lamp(*c.lampMesh, x1, top, z1, 1.6f, 255, 30, 22, 1, ph);
        Lamp(*c.lampMesh, x0, top, z1, 1.6f, 255, 30, 22, 1, ph);
        if (hgt > 95)
        {
            float mh = G + hgt * 0.5f;
            Lamp(*c.lampMesh, x0 - 0.3f, mh, z0 - 0.3f, 1.1f, 255, 40, 30, 0, 0);
            Lamp(*c.lampMesh, x1 + 0.3f, mh, z1 + 0.3f, 1.1f, 255, 40, 30, 0, 0);
        }
    }
    else if (U(H(h ^ 0x8181)) < 0.18f)
        Lamp(*c.lampMesh, (x0 + x1) * 0.5f, top, (z0 + z1) * 0.5f, 1.0f, 255, 36, 26, 2, U(H(h ^ 0x9191)) * 6.28f);
}

// 格子の電波塔 (高さ 166・脚は下で大きく開き、上へ細くなる)。展望台 2 つとアンテナ。鉄骨は自ら光る
void Tower(City& c)
{
    float G = c.ground;
    Mesh& m = TileOf(c, TowerX, TowerZ);
    uint8_t steel[4] = { 255, 70, 40, 0 };
    auto hw = [](float h) { float t = 1.0f - h / 140.0f; return 1.4f + 19.8f * powf(t < 0 ? 0 : t, 1.8f); };
    const int Lv = 26;
    const float Top = 125.0f;
    float sx[4] = { -1, 1, 1, -1 }, sz[4] = { -1, -1, 1, 1 };
    for (int k = 0; k < Lv; k++)
    {
        float h0 = Top * k / Lv, h1 = Top * (k + 1) / Lv;
        float w0 = hw(h0), w1 = hw(h1);
        float legR = 0.95f - 0.55f * (float)k / Lv;
        for (int j = 0; j < 4; j++)
        {
            int jn = (j + 1) & 3;
            float a[3] = { TowerX + sx[j] * w0, G + h0, TowerZ + sz[j] * w0 };
            float b[3] = { TowerX + sx[j] * w1, G + h1, TowerZ + sz[j] * w1 };
            Beam(m, a, b, legR, steel);
            float an[3] = { TowerX + sx[jn] * w0, G + h0, TowerZ + sz[jn] * w0 };
            float bn[3] = { TowerX + sx[jn] * w1, G + h1, TowerZ + sz[jn] * w1 };
            // 段ごとの横の梁と、面ごとの斜めの筋交い (X)
            if (k > 1) Beam(m, a, an, 0.3f, steel);
            Beam(m, a, bn, 0.26f, steel);
            Beam(m, an, b, 0.26f, steel);
        }
    }
    // 脚の間の弓 (足元から立ち上がって面の真ん中で一番高くなる門の形)
    for (int j = 0; j < 4; j++)
    {
        int jn = (j + 1) & 3;
        const int Seg = 10;
        const float Apex = 21.0f;
        for (int s = 0; s < Seg; s++)
        {
            float u0 = (float)s / Seg, u1 = (float)(s + 1) / Seg;
            float y0 = Apex * sinf(3.14159f * u0), y1 = Apex * sinf(3.14159f * u1);
            float wa = hw(y0), wb = hw(y1);
            float a[3] = { TowerX + (sx[j] + (sx[jn] - sx[j]) * u0) * wa, G + y0, TowerZ + (sz[j] + (sz[jn] - sz[j]) * u0) * wa };
            float b[3] = { TowerX + (sx[j] + (sx[jn] - sx[j]) * u1) * wb, G + y1, TowerZ + (sz[j] + (sz[jn] - sz[j]) * u1) * wb };
            Beam(m, a, b, 0.55f, steel);
        }
    }
    // 展望台 (窓の帯が光る) と、その上のアンテナの塔
    uint8_t deck[4] = { 77, 8, 200, 0 }; // 様式は使わない (展望台は材質で窓を決める)
    float d1 = 9.5f;
    Box(m, TowerX - d1, TowerZ - d1, TowerX + d1, TowerZ + d1, G + 72, G + 79, G, MDeck, MRoof, deck);
    float d2 = 4.4f;
    Box(m, TowerX - d2, TowerZ - d2, TowerX + d2, TowerZ + d2, G + 121, G + 125.5f, G, MDeck, MRoof, deck);
    float prev[3] = { TowerX, G + Top, TowerZ };
    for (int k = 0; k < 8; k++)
    {
        float h1 = Top + 41.0f * (k + 1) / 8.0f;
        float w = 1.3f - 1.05f * (k + 1) / 8.0f;
        float nx[3] = { TowerX, G + h1, TowerZ };
        Box(m, TowerX - w, TowerZ - w, TowerX + w, TowerZ + w, prev[1], nx[1], G, MBeam, MBeam, steel, false);
        memcpy(prev, nx, 12);
    }
    // 塔の灯: てっぺん・展望台の四隅
    Lamp(*c.lampMesh, TowerX, G + Top + 41.5f, TowerZ, 2.4f, 255, 26, 20, 1, 0);
    for (int j = 0; j < 4; j++)
    {
        Lamp(*c.lampMesh, TowerX + sx[j] * d1, G + 79.6f, TowerZ + sz[j] * d1, 1.5f, 255, 30, 22, 1, 0);
        Lamp(*c.lampMesh, TowerX + sx[j] * d2, G + 126.0f, TowerZ + sz[j] * d2, 1.3f, 255, 30, 22, 1, 0);
    }
}

// 高架の道路 (広い通りの上・橋脚と縁の灯の列)
void Highway(City& c, bool alongX, float at, float from, float to)
{
    float G = c.ground;
    const float Y = 11.0f, Hw = 7.5f, Th = 1.6f;
    uint8_t col[4] = { 30, 3 * 64 + 8, 0, 0 };
    for (float s = from; s < to; s += 160.0f)
    {
        float e = s + 160.0f < to ? s + 160.0f : to;
        float x0 = alongX ? s : at - Hw, x1 = alongX ? e : at + Hw;
        float z0 = alongX ? at - Hw : s, z1 = alongX ? at + Hw : e;
        Mesh& m = TileOf(c, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
        Box(m, x0, z0, x1, z1, G + Y - Th, G + Y, G, MRoad, MRoad, col);
        // 防音壁の低い縁
        if (alongX)
        {
            Box(m, x0, z0, x1, z0 + 0.5f, G + Y, G + Y + 1.2f, G, MRoad, MRoad, col);
            Box(m, x0, z1 - 0.5f, x1, z1, G + Y, G + Y + 1.2f, G, MRoad, MRoad, col);
        }
        else
        {
            Box(m, x0, z0, x0 + 0.5f, z1, G + Y, G + Y + 1.2f, G, MRoad, MRoad, col);
            Box(m, x1 - 0.5f, z0, x1, z1, G + Y, G + Y + 1.2f, G, MRoad, MRoad, col);
        }
        for (float q = s + 20.0f; q < e; q += 40.0f)
        {
            float px = alongX ? q : at, pz = alongX ? at : q;
            Box(m, px - 1.6f, pz - 1.6f, px + 1.6f, pz + 1.6f, G, G + Y - Th, G, MRoad, MRoad, col, false);
        }
        for (float q = s + 6.0f; q < e; q += 24.0f)
        {
            float ax = alongX ? q : at - Hw + 0.3f, az = alongX ? at - Hw + 0.3f : q;
            float bx = alongX ? q : at + Hw - 0.3f, bz = alongX ? at + Hw - 0.3f : q;
            Lamp(*c.lampMesh, ax, G + Y + 4.0f, az, 0.9f, 255, 70, 34, 0, 0);
            Lamp(*c.lampMesh, bx, G + Y + 4.0f, bz, 0.9f, 255, 70, 34, 0, 0);
        }
    }
}

void Build(City& c, uint32_t seed)
{
    float G = c.ground;
    // 地面 = 1 枚の大きな板 (道と街灯の光はシェーダが位置から描く)
    {
        Mesh& m = c.mesh[0];
        float E = Extent + 1600.0f;
        float a[3] = { -E, G, -E }, b[3] = { E, G, -E }, cc[3] = { E, G, E }, d[3] = { -E, G, E };
        float n[3] = { 0, 1, 0 };
        uint8_t col[4] = { 0, 0, 0, 0 };
        Quad(m, d, cc, b, a, n, MGround, col, 0, 0, 1, 1);
    }
    int N = (int)(Extent / Pitch);
    for (int i = -N; i < N; i++)
        for (int j = -N; j < N; j++)
        {
            float bx0 = i * Pitch + RoadW(i) * 0.5f, bx1 = (i + 1) * Pitch - RoadW(i + 1) * 0.5f;
            float bz0 = j * Pitch + RoadW(j) * 0.5f, bz1 = (j + 1) * Pitch - RoadW(j + 1) * 0.5f;
            float cx = (bx0 + bx1) * 0.5f, cz = (bz0 + bz1) * 0.5f;
            float r = sqrtf(cx * cx + cz * cz);
            if (r > Extent) continue;
            float tdx = cx - TowerX, tdz = cz - TowerZ;
            if (tdx * tdx + tdz * tdz < 95.0f * 95.0f) continue; // 塔の足元の公園
            uint32_t h = H3(i, j, seed);
            if (r > InnerR && U(h) > 0.55f) continue;
            // 内側は区画を 1〜2 × 1〜2 の敷地に分ける
            int nx = 1, nz = 1;
            if (r <= InnerR)
            {
                uint32_t k = H(h ^ 0x2468) & 3;
                nx = (k & 1) ? 2 : 1; nz = (k & 2) ? 2 : 1;
            }
            float lw = (bx1 - bx0) / nx, ld = (bz1 - bz0) / nz;
            for (int a = 0; a < nx; a++)
                for (int b = 0; b < nz; b++)
                {
                    uint32_t hl = H(h ^ (uint32_t)(a * 31 + b * 7 + 1));
                    float set = 1.5f + 2.5f * U(H(hl ^ 0x1111));
                    float x0 = bx0 + lw * a + set, x1 = bx0 + lw * (a + 1) - set;
                    float z0 = bz0 + ld * b + set, z1 = bz0 + ld * (b + 1) - set;
                    // 細い塔は敷地の一部だけを使う
                    float sh = 0.75f + 0.25f * U(H(hl ^ 0x2222));
                    float mxw = (x1 - x0) * (1 - sh) * 0.5f, mzw = (z1 - z0) * (1 - sh) * 0.5f;
                    x0 += mxw; x1 -= mxw; z0 += mzw; z1 -= mzw;
                    if (x1 - x0 < 5 || z1 - z0 < 5) continue;
                    Building(c, seed, x0, z0, x1, z1, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f, hl);
                }
        }
    Tower(c);
    Highway(c, true, 4 * Pitch * -1, -2400.0f, 2400.0f);
    Highway(c, false, 4 * Pitch * 3, -2400.0f, 1800.0f);
}
}

MRP_API void* mrp_flt_city_create(uint32_t seed, float ground)
{
    City* c = new City();
    c->ground = ground;
    // 先に全部の枠を作る (後から足すと vector が伸びて升の指す先が動く)。0 = 地面・升ごと・最後 = 灯
    c->mesh.resize(2 + Tiles * Tiles);
    for (int i = 0; i < Tiles; i++)
        for (int j = 0; j < Tiles; j++) c->tile[i][j] = &c->mesh[1 + i * Tiles + j];
    c->lampMesh = &c->mesh[1 + Tiles * Tiles];
    c->lampMesh->kind = 1;
    Build(*c, seed);
    return c;
}

// 作った区画の数。mesh 0 = 地面、続いて升ごとの街、最後に灯
MRP_API int32_t mrp_flt_city_count(void* p) { return p ? (int32_t)((City*)p)->mesh.size() : 0; }

// 区画 k の大きさ (頂点の数・添字の数・種類・範囲 6 個)
MRP_API int32_t mrp_flt_city_info(void* p, int32_t k, int32_t* vn, int32_t* in, float* bounds)
{
    City* c = (City*)p;
    if (!c || k < 0 || k >= (int32_t)c->mesh.size()) return -1;
    Mesh& m = c->mesh[k];
    *vn = (int32_t)m.v.size(); *in = (int32_t)m.i.size();
    memcpy(bounds, m.mn, 12); memcpy(bounds + 3, m.mx, 12);
    return m.kind;
}

MRP_API void mrp_flt_city_fill(void* p, int32_t k, uint8_t* verts, int32_t* idx)
{
    City* c = (City*)p;
    if (!c || k < 0 || k >= (int32_t)c->mesh.size()) return;
    Mesh& m = c->mesh[k];
    memcpy(verts, m.v.data(), m.v.size() * sizeof(V));
    memcpy(idx, m.i.data(), m.i.size() * sizeof(int32_t));
}

MRP_API void mrp_flt_city_destroy(void* p) { delete (City*)p; }
