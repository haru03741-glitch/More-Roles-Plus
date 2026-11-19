#include "mrp_native.h"
#include "flight.h"
#include <math.h>
#include <stdint.h>
#include <string.h>
#include <vector>

// 3D の艦隊戦の夜の街 (飛ぶ範囲の下に広がる景色)。種から区画・ビル・格子の電波塔・高架の道路・航空障害灯を組み、
// 区画ごとの静的なメッシュの頂点と添字を作る (作るのは場面に入る時の 1 回だけ)。
// 同時に当たりの箱 (ビル・塔・高架) を区画の升の表に詰めて持ち、場面の間はシムが借りる (flt::CityCol)。
// 棟ごとに頂点の範囲を覚えておく (壊れた棟だけ書き直すため)。形は種と整数の hash だけで決まり、どの端末でも同じ並びになる。
//
// 頂点 (44 バイト) = 位置 f32x3・法線 i8x3 + 材質 i8・色 u8x4・uv f32x2・崩れ f32x4
//   材質: 0 = ビルの壁 (uv = 壁に沿った長さ・地面からの高さ)・1 = 屋上・2 = 光る鉄骨 (塔)・3 = 地面・4 = 展望台 (uv は壁と同じ)・5 = 高架の道路
//   色: r = 建物の種・g = 様式 (0 事務所 / 1 ガラスの塔 / 2 住まい / 3 消えた建物)・b = 窓の点く割合・a = ちらつきの組 (下 3 ビット) + 0x40 燃えている
//   崩れ: (崩れ始めた時刻 (0 = 立っている)・棟の中心 x・z・棟の高さ)。時刻は C# の街の時計 (_MrpCityClock)
// 灯の頂点 (同じ 44 バイト) = 位置 (灯の中心)・法線 xy = 板の角 (-1/1)・色 = 灯の色 + a = 点滅の組・uv = (大きさ (0 = 消えた), 位相)

namespace
{
struct V { float p[3]; int8_t n[4]; uint8_t c[4]; float uv[2]; float k[4]; };
static_assert(sizeof(V) == 44, "city vertex");

enum Mat { MWall = 0, MRoof = 1, MBeam = 2, MGround = 3, MDeck = 4, MRoad = 5 };

// 船 (幅 44・長さ 48) が大通りを抜けられる縮尺。S = 建物と塔の大きさの倍率
const float S = 3.0f;
const float Pitch = 192.0f;     // 区画の間隔 (道の中心から中心)
const float Extent = 4800.0f;   // 街の広さ (中心からの半分)
const float InnerR = 1500.0f;   // ここより内は区画を細かく分けて密に建てる
const float TileSize = 1200.0f; // メッシュを分ける升 (見えない升は描かれない)
const int Tiles = 8;            // 升の数 (片側)
const float TowerX = 380.0f, TowerZ = 560.0f; // 電波塔の位置
const float MaxH = 450.0f;      // 建物の高さの上限

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

// 棟 1 つ: 区画のメッシュの中の頂点の範囲・障害灯の範囲・形の範囲
struct Bld { int32_t mesh, v0, v1, l0, l1; float mn[3], mx[3]; };

struct City
{
    float ground;
    std::vector<Mesh> mesh;
    Mesh* tile[Tiles][Tiles];
    Mesh* lampMesh;
    std::vector<flt::CityBox> box;
    std::vector<Bld> bld;
    std::vector<int32_t> start, item;
    flt::CityCol col;
};

void AddBox(City& c, float x0, float z0, float x1, float z1, float y0, float y1, int bld)
{
    flt::CityBox b;
    b.mn[0] = x0 < x1 ? x0 : x1; b.mx[0] = x0 < x1 ? x1 : x0;
    b.mn[1] = y0 < y1 ? y0 : y1; b.mx[1] = y0 < y1 ? y1 : y0;
    b.mn[2] = z0 < z1 ? z0 : z1; b.mx[2] = z0 < z1 ? z1 : z0;
    b.bld = bld; b.rc = 1; b.rs = 0; b.pv[0] = b.pv[1] = 0; b.pad = 0;
    c.box.push_back(b);
}

// 向きの付いた箱 (中心 cx cz・半幅 hw (向き a の方向)・半奥行 hd)
void AddOBox(City& c, float cx, float cz, float hw, float hd, float a, float y0, float y1, int bld)
{
    flt::CityBox b;
    b.rc = cosf(a); b.rs = sinf(a); b.pv[0] = cx; b.pv[1] = cz;
    b.mn[0] = -hw; b.mx[0] = hw; b.mn[2] = -hd; b.mx[2] = hd;
    b.mn[1] = y0 < y1 ? y0 : y1; b.mx[1] = y0 < y1 ? y1 : y0;
    b.bld = bld; b.pad = 0;
    c.box.push_back(b);
}

// 箱を囲む世界の軸に沿った範囲 (xz)
void BoxWorldXZ(const flt::CityBox& b, float& x0, float& z0, float& x1, float& z1)
{
    x0 = z0 = 1e30f; x1 = z1 = -1e30f;
    for (int k = 0; k < 4; k++)
    {
        float lx = (k & 1) ? b.mx[0] : b.mn[0], lz = (k & 2) ? b.mx[2] : b.mn[2];
        float x = b.pv[0] + b.rc * lx - b.rs * lz, z = b.pv[1] + b.rs * lx + b.rc * lz;
        x0 = fminf(x0, x); x1 = fmaxf(x1, x); z0 = fminf(z0, z); z1 = fmaxf(z1, z);
    }
}

// 細い角柱 (a→b・太さ r) を包む箱
void AddBeamBox(City& c, const float* a, const float* b, float r)
{
    AddBox(c, fminf(a[0], b[0]) - r, fminf(a[2], b[2]) - r, fmaxf(a[0], b[0]) + r, fmaxf(a[2], b[2]) + r,
           fminf(a[1], b[1]) - r, fmaxf(a[1], b[1]) + r, -1);
}

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
        vx.k[0] = vx.k[1] = vx.k[2] = vx.k[3] = 0;
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
        vx.k[0] = vx.k[1] = vx.k[2] = vx.k[3] = 0;
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
float RoadW(int line) { return (line % 4 == 0) ? 96.0f : 30.0f; }

void Building(City& c, uint32_t seed, float x0, float z0, float x1, float z1, float cx, float cz, uint32_t h)
{
    float G = c.ground;
    float r = sqrtf(cx * cx + cz * cz);
    // 高い建物の集まる地区 (雑音) と、中心から離れるほど低く疎らに
    float district = Noise2(cx / 1400.0f, cz / 1400.0f, seed ^ 0x51ed);
    float tallP = district > 0.62f ? (district - 0.62f) * 2.6f : 0.0f;
    // 飛ぶ場の真ん中は高層の谷 (どの種でも低く飛べば高い建物の間を抜けられる)
    if (r < 1300) tallP += 0.45f * (1.0f - r / 1300.0f);
    float fall = r < 2400 ? 1.0f : 1.0f - (r - 2400) / 3600.0f;
    if (fall < 0.25f) fall = 0.25f;
    float u = U(h);
    int style;
    float hgt;
    if (U(H(h ^ 0x1234)) < tallP * fall)
    {
        hgt = (70.0f + 70.0f * U(H(h ^ 0x77))) * S;
        style = U(H(h ^ 0x99)) < 0.55f ? 1 : 0;
    }
    else
    {
        float mid = 14.0f + 26.0f * u * u;
        hgt = (mid * (0.55f + 0.45f * fall) + 4.0f) * S;
        float su = U(H(h ^ 0x99));
        style = su < 0.42f ? 2 : (su < 0.85f ? 0 : 3);
    }
    if (hgt > MaxH) hgt = MaxH;
    float lit = style == 3 ? 0.03f : (0.06f + 0.3f * U(H(h ^ 0x4242)));
    uint8_t col[4] = { (uint8_t)(h >> 8), (uint8_t)(style * 64 + 8), (uint8_t)(lit * 255), (uint8_t)(H(h ^ 0xabc) & 7) };
    Mesh& m = TileOf(c, cx, cz);
    int id = (int)c.bld.size();
    Bld b;
    b.mesh = (int32_t)(&m - c.mesh.data());
    b.v0 = (int32_t)m.v.size();
    b.l0 = (int32_t)c.lampMesh->v.size();
    b.mn[0] = x0; b.mn[1] = G; b.mn[2] = z0;
    b.mx[0] = x1; b.mx[1] = G + hgt; b.mx[2] = z1;
    // 高い建物の一部は上で一段細くなる (段の上の小さな塔)
    float w = x1 - x0, d = z1 - z0;
    if (hgt > 60 * S && U(H(h ^ 0x5151)) < 0.35f)
    {
        float t = 0.55f + 0.25f * U(H(h ^ 0x6161));
        float hb = hgt * t;
        Box(m, x0, z0, x1, z1, G, G + hb, G, MWall, MRoof, col);
        AddBox(c, x0, z0, x1, z1, G, G + hb, id);
        float ix = w * 0.18f, iz = d * 0.18f;
        Box(m, x0 + ix, z0 + iz, x1 - ix, z1 - iz, G + hb, G + hgt, G, MWall, MRoof, col);
        AddBox(c, x0 + ix, z0 + iz, x1 - ix, z1 - iz, G + hb, G + hgt, id);
        x0 += ix; x1 -= ix; z0 += iz; z1 -= iz;
    }
    else
    {
        Box(m, x0, z0, x1, z1, G, G + hgt, G, MWall, MRoof, col);
        AddBox(c, x0, z0, x1, z1, G, G + hgt, id);
    }
    // 屋上の機械室
    if (U(H(h ^ 0x7171)) < 0.45f && w > 8 * S && d > 8 * S)
    {
        float mx0 = x0 + w * 0.25f, mz0 = z0 + d * 0.3f;
        uint8_t mc[4] = { col[0], (uint8_t)(3 * 64 + 8), 0, col[3] };
        Box(m, mx0, mz0, mx0 + w * 0.3f, mz0 + d * 0.3f, G + hgt, G + hgt + 2.2f * S, G, MWall, MRoof, mc);
        AddBox(c, mx0, mz0, mx0 + w * 0.3f, mz0 + d * 0.3f, G + hgt, G + hgt + 2.2f * S, id);
        b.mx[1] = G + hgt + 2.2f * S;
    }
    // 航空障害灯: 高い建物は屋上の四隅で揃って点滅・中ほどの高さにも並ぶ。低い建物は時々屋上に 1 つ
    float top = G + hgt + 0.6f * S;
    float ph = 0.0f;
    if (hgt > 55 * S)
    {
        Lamp(*c.lampMesh, x0, top, z0, 3.2f, 255, 30, 22, 1, ph);
        Lamp(*c.lampMesh, x1, top, z0, 3.2f, 255, 30, 22, 1, ph);
        Lamp(*c.lampMesh, x1, top, z1, 3.2f, 255, 30, 22, 1, ph);
        Lamp(*c.lampMesh, x0, top, z1, 3.2f, 255, 30, 22, 1, ph);
        if (hgt > 95 * S)
        {
            float mh = G + hgt * 0.5f;
            Lamp(*c.lampMesh, x0 - 0.3f * S, mh, z0 - 0.3f * S, 2.2f, 255, 40, 30, 0, 0);
            Lamp(*c.lampMesh, x1 + 0.3f * S, mh, z1 + 0.3f * S, 2.2f, 255, 40, 30, 0, 0);
        }
    }
    else if (U(H(h ^ 0x8181)) < 0.18f)
        Lamp(*c.lampMesh, (x0 + x1) * 0.5f, top, (z0 + z1) * 0.5f, 2.0f, 255, 36, 26, 2, U(H(h ^ 0x9191)) * 6.28f);
    b.v1 = (int32_t)m.v.size();
    b.l1 = (int32_t)c.lampMesh->v.size();
    c.bld.push_back(b);
}

// 格子の電波塔 (高さ 166 × S・脚は下で大きく開き、上へ細くなる)。展望台 2 つとアンテナ。鉄骨は自ら光る。
// 当たりは脚の間の弓より下を脚と弓の箱だけ (船が脚の間を抜けられる)・弓より上は段ごとの箱で塞ぐ
void Tower(City& c)
{
    float G = c.ground;
    Mesh& m = TileOf(c, TowerX, TowerZ);
    uint8_t steel[4] = { 255, 70, 40, 0 };
    auto hw = [](float h) { float t = 1.0f - h / (140.0f * S); return (1.4f + 19.8f * powf(t < 0 ? 0 : t, 1.8f)) * S; };
    const int Lv = 26;
    const float Top = 125.0f * S;
    const float Apex = 32.0f * S; // 脚の間の弓の高さ (船が下をくぐれる)
    float sx[4] = { -1, 1, 1, -1 }, sz[4] = { -1, -1, 1, 1 };
    for (int k = 0; k < Lv; k++)
    {
        float h0 = Top * k / Lv, h1 = Top * (k + 1) / Lv;
        float w0 = hw(h0), w1 = hw(h1);
        float legR = (0.95f - 0.55f * (float)k / Lv) * S;
        if (h0 >= Apex)
            AddBox(c, TowerX - w0 - legR, TowerZ - w0 - legR, TowerX + w0 + legR, TowerZ + w0 + legR, G + h0, G + h1, -1);
        for (int j = 0; j < 4; j++)
        {
            int jn = (j + 1) & 3;
            float a[3] = { TowerX + sx[j] * w0, G + h0, TowerZ + sz[j] * w0 };
            float b[3] = { TowerX + sx[j] * w1, G + h1, TowerZ + sz[j] * w1 };
            Beam(m, a, b, legR, steel);
            if (h0 < Apex) AddBeamBox(c, a, b, legR);
            float an[3] = { TowerX + sx[jn] * w0, G + h0, TowerZ + sz[jn] * w0 };
            float bn[3] = { TowerX + sx[jn] * w1, G + h1, TowerZ + sz[jn] * w1 };
            // 段ごとの横の梁と、面ごとの斜めの筋交い (X)。弓より下は開いた門 (船が脚の間を抜けられる)
            if (h0 < Apex) continue;
            Beam(m, a, an, 0.3f * S, steel);
            Beam(m, a, bn, 0.26f * S, steel);
            Beam(m, an, b, 0.26f * S, steel);
        }
    }
    // 脚の間の弓 (足元から立ち上がって面の真ん中で一番高くなる門の形)
    for (int j = 0; j < 4; j++)
    {
        int jn = (j + 1) & 3;
        const int Seg = 10;
        for (int s = 0; s < Seg; s++)
        {
            float u0 = (float)s / Seg, u1 = (float)(s + 1) / Seg;
            float y0 = Apex * sinf(3.14159f * u0), y1 = Apex * sinf(3.14159f * u1);
            float wa = hw(y0), wb = hw(y1);
            float a[3] = { TowerX + (sx[j] + (sx[jn] - sx[j]) * u0) * wa, G + y0, TowerZ + (sz[j] + (sz[jn] - sz[j]) * u0) * wa };
            float b[3] = { TowerX + (sx[j] + (sx[jn] - sx[j]) * u1) * wb, G + y1, TowerZ + (sz[j] + (sz[jn] - sz[j]) * u1) * wb };
            Beam(m, a, b, 0.55f * S, steel);
            // 斜めの梁を軸に沿った箱 1 つで包むと梁の下まで塞ぐので、4 つに分けて包む
            for (int q = 0; q < 4; q++)
            {
                float pa[3], pb[3];
                for (int e = 0; e < 3; e++) { pa[e] = a[e] + (b[e] - a[e]) * q * 0.25f; pb[e] = a[e] + (b[e] - a[e]) * (q + 1) * 0.25f; }
                AddBeamBox(c, pa, pb, 0.55f * S);
            }
        }
    }
    // 展望台 (窓の帯が光る) と、その上のアンテナの塔
    uint8_t deck[4] = { 77, 8, 200, 0 }; // 様式は使わない (展望台は材質で窓を決める)
    float d1 = 9.5f * S;
    Box(m, TowerX - d1, TowerZ - d1, TowerX + d1, TowerZ + d1, G + 72 * S, G + 79 * S, G, MDeck, MRoof, deck);
    AddBox(c, TowerX - d1, TowerZ - d1, TowerX + d1, TowerZ + d1, G + 72 * S, G + 79 * S, -1);
    float d2 = 4.4f * S;
    Box(m, TowerX - d2, TowerZ - d2, TowerX + d2, TowerZ + d2, G + 121 * S, G + 125.5f * S, G, MDeck, MRoof, deck);
    AddBox(c, TowerX - d2, TowerZ - d2, TowerX + d2, TowerZ + d2, G + 121 * S, G + 125.5f * S, -1);
    float prev[3] = { TowerX, G + Top, TowerZ };
    for (int k = 0; k < 8; k++)
    {
        float h1 = Top + 41.0f * S * (k + 1) / 8.0f;
        float w = (1.3f - 1.05f * (k + 1) / 8.0f) * S;
        float nx[3] = { TowerX, G + h1, TowerZ };
        Box(m, TowerX - w, TowerZ - w, TowerX + w, TowerZ + w, prev[1], nx[1], G, MBeam, MBeam, steel, false);
        memcpy(prev, nx, 12);
    }
    AddBox(c, TowerX - 1.3f * S, TowerZ - 1.3f * S, TowerX + 1.3f * S, TowerZ + 1.3f * S, G + Top, G + Top + 41.0f * S, -1);
    // 塔の灯: てっぺん・展望台の四隅
    Lamp(*c.lampMesh, TowerX, G + Top + 41.5f * S, TowerZ, 4.8f, 255, 26, 20, 1, 0);
    for (int j = 0; j < 4; j++)
    {
        Lamp(*c.lampMesh, TowerX + sx[j] * d1, G + 79.6f * S, TowerZ + sz[j] * d1, 3.0f, 255, 30, 22, 1, 0);
        Lamp(*c.lampMesh, TowerX + sx[j] * d2, G + 126.0f * S, TowerZ + sz[j] * d2, 2.6f, 255, 30, 22, 1, 0);
    }
}

// 高架の道路 (広い通りの上・橋脚と縁の灯の列)
void Highway(City& c, bool alongX, float at, float from, float to)
{
    float G = c.ground;
    const float Y = 11.0f * S, Hw = 7.5f * S, Th = 1.6f * S, Wall = 1.2f * S, Thin = 0.5f * S;
    uint8_t col[4] = { 30, 3 * 64 + 8, 0, 0 };
    for (float s = from; s < to; s += 480.0f)
    {
        float e = s + 480.0f < to ? s + 480.0f : to;
        float x0 = alongX ? s : at - Hw, x1 = alongX ? e : at + Hw;
        float z0 = alongX ? at - Hw : s, z1 = alongX ? at + Hw : e;
        Mesh& m = TileOf(c, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
        Box(m, x0, z0, x1, z1, G + Y - Th, G + Y, G, MRoad, MRoad, col);
        AddBox(c, x0, z0, x1, z1, G + Y - Th, G + Y + Wall, -1);
        // 防音壁の低い縁
        if (alongX)
        {
            Box(m, x0, z0, x1, z0 + Thin, G + Y, G + Y + Wall, G, MRoad, MRoad, col);
            Box(m, x0, z1 - Thin, x1, z1, G + Y, G + Y + Wall, G, MRoad, MRoad, col);
        }
        else
        {
            Box(m, x0, z0, x0 + Thin, z1, G + Y, G + Y + Wall, G, MRoad, MRoad, col);
            Box(m, x1 - Thin, z0, x1, z1, G + Y, G + Y + Wall, G, MRoad, MRoad, col);
        }
        for (float q = s + 60.0f; q < e; q += 120.0f)
        {
            float px = alongX ? q : at, pz = alongX ? at : q;
            float pr = 1.6f * S;
            Box(m, px - pr, pz - pr, px + pr, pz + pr, G, G + Y - Th, G, MRoad, MRoad, col, false);
            AddBox(c, px - pr, pz - pr, px + pr, pz + pr, G, G + Y - Th, -1);
        }
        for (float q = s + 18.0f; q < e; q += 72.0f)
        {
            float ax = alongX ? q : at - Hw + Thin, az = alongX ? at - Hw + Thin : q;
            float bx = alongX ? q : at + Hw - Thin, bz = alongX ? at + Hw - Thin : q;
            Lamp(*c.lampMesh, ax, G + Y + 4.0f * S, az, 1.8f, 255, 70, 34, 0, 0);
            Lamp(*c.lampMesh, bx, G + Y + 4.0f * S, bz, 1.8f, 255, 70, 34, 0, 0);
        }
    }
}

void Build(City& c, uint32_t seed)
{
    float G = c.ground;
    // 地面 = 1 枚の大きな板 (道と街灯の光はシェーダが位置から描く)
    {
        Mesh& m = c.mesh[0];
        float E = Extent + 2400.0f;
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
            if (tdx * tdx + tdz * tdz < 95.0f * S * 95.0f * S) continue; // 塔の足元の公園
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
                    float set = (1.5f + 2.5f * U(H(hl ^ 0x1111))) * S;
                    float x0 = bx0 + lw * a + set, x1 = bx0 + lw * (a + 1) - set;
                    float z0 = bz0 + ld * b + set, z1 = bz0 + ld * (b + 1) - set;
                    // 細い塔は敷地の一部だけを使う
                    float sh = 0.75f + 0.25f * U(H(hl ^ 0x2222));
                    float mxw = (x1 - x0) * (1 - sh) * 0.5f, mzw = (z1 - z0) * (1 - sh) * 0.5f;
                    x0 += mxw; x1 -= mxw; z0 += mzw; z1 -= mzw;
                    if (x1 - x0 < 5 * S || z1 - z0 < 5 * S) continue;
                    Building(c, seed, x0, z0, x1, z1, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f, hl);
                }
        }
    Tower(c);
    Highway(c, true, 4 * Pitch * -1, -3600.0f, 3600.0f);
    Highway(c, false, 4 * Pitch, -3600.0f, 2700.0f);
}

// 箱を区画の升の表へ (箱の覆う升ぜんぶに入れる)
void MakeGrid(City& c)
{
    flt::CityCol& g = c.col;
    g.cell = Pitch;
    g.org[0] = g.org[1] = -Extent - Pitch;
    g.nx = g.nz = (int32_t)((2 * Extent + 2 * Pitch) / Pitch) + 1;
    int cells = g.nx * g.nz;
    std::vector<int32_t> cnt(cells + 1, 0);
    float top = c.ground;
    auto span = [&](const flt::CityBox& b, int& i0, int& j0, int& i1, int& j1)
    {
        float x0, z0, x1, z1; BoxWorldXZ(b, x0, z0, x1, z1);
        i0 = (int)floorf((x0 - g.org[0]) / g.cell); i1 = (int)floorf((x1 - g.org[0]) / g.cell);
        j0 = (int)floorf((z0 - g.org[1]) / g.cell); j1 = (int)floorf((z1 - g.org[1]) / g.cell);
        i0 = i0 < 0 ? 0 : i0; j0 = j0 < 0 ? 0 : j0;
        i1 = i1 >= g.nx ? g.nx - 1 : i1; j1 = j1 >= g.nz ? g.nz - 1 : j1;
    };
    for (const flt::CityBox& b : c.box)
    {
        int i0, j0, i1, j1; span(b, i0, j0, i1, j1);
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) cnt[j * g.nx + i]++;
        if (b.mx[1] > top) top = b.mx[1];
    }
    c.start.assign(cells + 1, 0);
    for (int k = 0; k < cells; k++) c.start[k + 1] = c.start[k] + cnt[k];
    c.item.assign(c.start[cells], 0);
    std::vector<int32_t> fill(c.start.begin(), c.start.end() - 1);
    for (int32_t n = 0; n < (int32_t)c.box.size(); n++)
    {
        int i0, j0, i1, j1; span(c.box[n], i0, j0, i1, j1);
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) c.item[fill[j * g.nx + i]++] = n;
    }
    g.top = top;
    g.box = c.box.data(); g.boxN = (int32_t)c.box.size();
    g.start = c.start.data(); g.item = c.item.data();
}

// ---- 実在の街 (3D 都市モデルを焼いた .mrpcity から組む) ----
// データは中心 (東京スカイツリー) からの東 x・北 z (m)。横を RS・高さを RH 倍し、中心を電波塔の場所 (TowerX, TowerZ) へ置く。
// 棟は足跡の角柱 (壁は辺ごとの板・屋根は焼いた三角)・当たりは焼いた向き付きの箱。地面は区分ごとの面 (色 g = 区分 + 1)。
const float RS = 3.0f, RH = 1.5f;
const int ChunkCap = 60000; // 1 つの網目の頂点の上限 (C# は 16 ビットの添字で描く)

// 地面の区分 (焼く道具と同じ番号)
enum GCls { GLot = 0, GVacant, GPark, GParking, GRail, GWater, GRoad1, GWalk, GIsland, GRoad, GCross, GExpwy };

struct Rd
{
    const uint8_t* p; const uint8_t* e; bool ok = true;
    template <class T> T Get()
    {
        T v{};
        if (!ok || p + sizeof(T) > e) { ok = false; return v; }
        memcpy(&v, p, sizeof(T)); p += sizeof(T);
        return v;
    }
    const uint8_t* Take(size_t n)
    {
        if (!ok || p + n > e) { ok = false; return nullptr; }
        const uint8_t* q = p; p += n;
        return q;
    }
};

// 升ごとに今詰めている網目。頂点が上限を超えたら同じ升に新しい網目を足す
struct Chunks
{
    City* c;
    int cur[Tiles * Tiles];
    Mesh& At(float x, float z, int need)
    {
        int i = (int)floorf((x + Extent) / TileSize), j = (int)floorf((z + Extent) / TileSize);
        i = i < 0 ? 0 : (i >= Tiles ? Tiles - 1 : i);
        j = j < 0 ? 0 : (j >= Tiles ? Tiles - 1 : j);
        int& k = cur[i * Tiles + j];
        if (k < 0 || ((int)c->mesh[k].v.size() + need > ChunkCap && c->mesh.size() + 2 < c->mesh.capacity()))
        {
            c->mesh.emplace_back();
            k = (int)c->mesh.size() - 1;
        }
        return c->mesh[k];
    }
};

void Tri(Mesh& m, const float* a, const float* b, const float* cc, const float* n, int mat, const uint8_t* col, const float* uva, const float* uvb, const float* uvc)
{
    int base = (int)m.v.size();
    const float* ps[3] = { a, b, cc };
    const float* uvs[3] = { uva, uvb, uvc };
    for (int k = 0; k < 3; k++)
    {
        V vx;
        memcpy(vx.p, ps[k], 12);
        vx.n[0] = Sn(n[0]); vx.n[1] = Sn(n[1]); vx.n[2] = Sn(n[2]); vx.n[3] = (int8_t)mat;
        memcpy(vx.c, col, 4);
        vx.uv[0] = uvs[k][0]; vx.uv[1] = uvs[k][1];
        vx.k[0] = vx.k[1] = vx.k[2] = vx.k[3] = 0;
        m.v.push_back(vx);
        Grow(m, ps[k]);
    }
    float e1[3] = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, e2[3] = { cc[0] - a[0], cc[1] - a[1], cc[2] - a[2] };
    float cy = e1[2] * e2[0] - e1[0] * e2[2];
    bool fwd = cy * n[1] > 0;
    m.i.push_back(base);
    m.i.push_back(fwd ? base + 1 : base + 2);
    m.i.push_back(fwd ? base + 2 : base + 1);
}

// 区分ごとの高さ (地面からの差)。遠くの重なりはシェーダが深さを区分の順にずらして防ぐ
float GLift(int g)
{
    switch (g)
    {
    case GWalk: return 0.3f;    // 縁石の分だけ上
    case GIsland: return 0.4f;
    case GRoad: case GCross: return 0.06f;
    case GRoad1: return 0.04f;
    default: return 0.02f;
    }
}

// 用途 (焼く道具の番号) と高さから壁の様式と窓の点く割合
void RealStyle(int usage, float hReal, uint32_t h, int& style, float& lit)
{
    switch (usage)
    {
    case 1: case 3: style = hReal > 45 ? 1 : 0; lit = 0.08f + 0.25f * U(H(h ^ 0x4242)); break; // 業務・宿泊
    case 2: case 8: style = hReal > 45 ? 1 : 0; lit = 0.05f + 0.2f * U(H(h ^ 0x4242)); break;  // 商業・公共
    case 4: case 5: case 6: case 7: style = 2; lit = 0.2f + 0.3f * U(H(h ^ 0x4242)); break;    // 住まい
    case 9: case 10: style = 3; lit = 0.03f; break;                                            // 倉庫・工場
    default: style = hReal > 30 ? 0 : 2; lit = 0.1f + 0.2f * U(H(h ^ 0x4242)); break;
    }
}

// 東京スカイツリー (高さ 634 m)。足元は一辺 68 m の三角で、上へ行くほど円に近づき細る鉄骨の格子・中心の柱・
// 2 つの展望台 (350 m・450 m)・上のアンテナ。鉄骨は淡い青白に照らされる。当たりは高さごとの箱
void Skytree(City& c, Mesh& m, float X, float Z)
{
    float G = c.ground;
    uint8_t steel[4] = { 200, 225, 255, 1 }; // a = 1: 背の高い塔 (高さによる暗さを緩く)
    auto size = [](float h)
    {
        static const float hs[] = { 0, 100, 200, 300, 400, 497 };
        static const float rs[] = { 27, 23.5f, 21, 19.5f, 17.5f, 13 };
        for (int k = 0; k < 5; k++)
            if (h <= hs[k + 1]) { float t = (h - hs[k]) / (hs[k + 1] - hs[k]); return rs[k] + (rs[k + 1] - rs[k]) * t; }
        return 13.0f;
    };
    // 断面: 足元は三角 (頂点が 3 方向)・300 m で円
    auto rad = [&](float h, float th)
    {
        float t = h / 300.0f; t = t > 1 ? 1 : t;
        float a = fmodf(th + 3.14159265f / 6.0f, 2.0943951f) - 1.0471976f; // 三角の辺の中心からの角
        float tri = 0.5f / cosf(a);                                         // 内接円 0.5 の三角
        float triAvg = 0.6046f;                                             // 三角の半径の平均 (同じ大きさに見えるように割る)
        return size(h) * ((1 - t) * tri / triAvg + t);
    };
    const int Cols = 12, Lv = 33;
    const float Top = 497.0f;
    auto P = [&](int j, int k, float* o)
    {
        float h = Top * k / Lv, th = 6.2831853f * j / Cols;
        float r = rad(h, th);
        o[0] = X + cosf(th) * r * RS; o[1] = G + h * RH; o[2] = Z + sinf(th) * r * RS;
    };
    for (int k = 0; k < Lv; k++)
    {
        float br = (1.6f - 0.8f * k / Lv);
        for (int j = 0; j < Cols; j++)
        {
            float a[3], b[3], d[3], e[3];
            P(j, k, a); P(j, k + 1, b); P(j + 1, k + 1, d); P(j + 1, k, e);
            Beam(m, a, b, br, steel);
            if ((j + k) & 1) Beam(m, a, d, br * 0.6f, steel); else Beam(m, e, b, br * 0.6f, steel);
            if (k % 3 == 0) Beam(m, a, e, br * 0.7f, steel);
        }
        float h0 = Top * k / Lv, h1 = Top * (k + 1) / Lv;
        float hw = size(h0) * 0.8f * RS;
        AddBox(c, X - hw, Z - hw, X + hw, Z + hw, G + h0 * RH, G + h1 * RH, -1);
    }
    // 円柱 (中心の柱・展望台・アンテナ): 側面 Sides 枚・上下の蓋
    auto Cyl = [&](float r0, float r1, float h0, float h1, int mat, const uint8_t* col, bool caps)
    {
        const int Sides = 24;
        for (int s = 0; s < Sides; s++)
        {
            float t0 = 6.2831853f * s / Sides, t1 = 6.2831853f * (s + 1) / Sides, tm = (t0 + t1) * 0.5f;
            float a[3] = { X + cosf(t0) * r0 * RS, G + h0 * RH, Z + sinf(t0) * r0 * RS };
            float b[3] = { X + cosf(t1) * r0 * RS, G + h0 * RH, Z + sinf(t1) * r0 * RS };
            float d[3] = { X + cosf(t1) * r1 * RS, G + h1 * RH, Z + sinf(t1) * r1 * RS };
            float e[3] = { X + cosf(t0) * r1 * RS, G + h1 * RH, Z + sinf(t0) * r1 * RS };
            float n[3] = { cosf(tm), 0, sinf(tm) };
            float arc = 6.2831853f * r0 * RH / Sides; // 窓の升は高さの縮尺にそろえる
            Quad(m, a, b, d, e, n, mat, col, arc * s, 0, arc * (s + 1), (h1 - h0) * RH);
            if (caps)
            {
                float ctr0[3] = { X, G + h0 * RH, Z }, ctr1[3] = { X, G + h1 * RH, Z };
                float up[3] = { 0, 1, 0 }, dn[3] = { 0, -1, 0 };
                float uv0[2] = { 0, 0 };
                uint8_t roofC[4] = { col[0], col[1], 0, col[3] };
                Tri(m, ctr1, e, d, up, MRoof, roofC, uv0, uv0, uv0);
                Tri(m, ctr0, a, b, dn, MRoof, roofC, uv0, uv0, uv0);
            }
        }
        float hw = (r0 > r1 ? r0 : r1) * 0.8f * RS;
        AddBox(c, X - hw, Z - hw, X + hw, Z + hw, G + h0 * RH, G + h1 * RH, -1);
    };
    uint8_t core[4] = { 77, 3 * 64 + 8, 6, 0 };
    uint8_t deck[4] = { 91, 1 * 64 + 8, 255, 0 };
    Cyl(4.5f, 4.5f, 0, 375, MRoof, core, false);
    Cyl(27, 27, 340, 352, MDeck, deck, true);   // 天望デッキ
    Cyl(25, 22, 352, 356, MRoof, core, true);
    Cyl(17.5f, 17.5f, 440, 452, MDeck, deck, true); // 天望回廊
    Cyl(9, 6.5f, 497, 560, MBeam, steel, true);  // ゲイン塔
    Cyl(5, 1.0f, 560, 634, MBeam, steel, true);
    // 航空障害灯: てっぺん・アンテナの途中・2 つの展望台の縁
    Lamp(*c.lampMesh, X, G + 635 * RH, Z, 4.0f, 255, 30, 22, 1, 0);
    for (int s = 0; s < 3; s++)
    {
        float th = 6.2831853f * s / 3 + 0.5f;
        Lamp(*c.lampMesh, X + cosf(th) * 6.5f * RS, G + 560 * RH, Z + sinf(th) * 6.5f * RS, 3.2f, 255, 30, 22, 1, 0);
        Lamp(*c.lampMesh, X + cosf(th) * 27.5f * RS, G + 353 * RH, Z + sinf(th) * 27.5f * RS, 3.2f, 255, 30, 22, 1, 0);
        Lamp(*c.lampMesh, X + cosf(th) * 18 * RS, G + 453 * RH, Z + sinf(th) * 18 * RS, 2.6f, 255, 30, 22, 1, 0);
    }
}

bool BuildReal(City& c, const uint8_t* data, int32_t len)
{
    Rd r{ data, data + len };
    const uint8_t* magic = r.Take(4);
    uint32_t ver = r.Get<uint32_t>();
    if (!magic || memcmp(magic, "MRPC", 4) != 0 || ver != 2) return false;
    r.Get<double>(); r.Get<double>(); r.Get<float>();
    uint32_t nb = r.Get<uint32_t>(), ng = r.Get<uint32_t>();
    if (!r.ok) return false;
    float G = c.ground;
    Chunks ch; ch.c = &c;
    for (int k = 0; k < Tiles * Tiles; k++) ch.cur[k] = -1;
    // 地面の下敷き (敷地の色): 1 枚の大きな板
    {
        Mesh& m = c.mesh[0];
        float E = Extent + 2400.0f;
        float a[3] = { -E, G, -E }, b[3] = { E, G, -E }, cc[3] = { E, G, E }, d[3] = { -E, G, E };
        float n[3] = { 0, 1, 0 };
        uint8_t col[4] = { 0, GLot + 1, 0, 0 };
        Quad(m, d, cc, b, a, n, MGround, col, d[0] - TowerX, d[2] - TowerZ, cc[0] - TowerX, a[2] - TowerZ);
    }
    std::vector<float> xz;
    for (uint32_t bi = 0; bi < nb && r.ok; bi++)
    {
        uint16_t np = r.Get<uint16_t>();
        int16_t base = r.Get<int16_t>();
        uint16_t hgt = r.Get<uint16_t>();
        uint8_t usage = r.Get<uint8_t>(), storeys = r.Get<uint8_t>(), nbox = r.Get<uint8_t>(), ntri = r.Get<uint8_t>();
        const uint8_t* pts = r.Take((size_t)np * 4);
        const uint8_t* tri = r.Take((size_t)ntri * 3);
        const uint8_t* bx = r.Take((size_t)nbox * 10);
        if (!r.ok || np < 3) break;
        xz.resize((size_t)np * 2);
        float mn[2] = { 1e30f, 1e30f }, mx[2] = { -1e30f, -1e30f };
        for (int k = 0; k < np; k++)
        {
            int16_t q[2]; memcpy(q, pts + k * 4, 4);
            xz[k * 2] = TowerX + q[0] * 0.1f * RS;
            xz[k * 2 + 1] = TowerZ + q[1] * 0.1f * RS;
            for (int a = 0; a < 2; a++) { mn[a] = fminf(mn[a], xz[k * 2 + a]); mx[a] = fmaxf(mx[a], xz[k * 2 + a]); }
        }
        float hReal = hgt * 0.1f;
        // 地面の起伏はならして平らな地面に建てる (足元は少し埋める)
        (void)base;
        float y0 = G - 1.0f, y1 = G + hReal * RH;
        uint32_t h = H(bi * 2654435761u ^ 0x5eed);
        int style; float lit;
        RealStyle(usage, hReal, h, style, lit);
        uint8_t col[4] = { (uint8_t)(h >> 8), (uint8_t)(style * 64 + 8), (uint8_t)(lit * 255), (uint8_t)(H(h ^ 0xabc) & 7) };
        float cx = (mn[0] + mx[0]) * 0.5f, cz = (mn[1] + mx[1]) * 0.5f;
        Mesh& m = ch.At(cx, cz, np * 4 + ntri * 3);
        Bld b;
        b.mesh = (int32_t)(&m - c.mesh.data());
        b.v0 = (int32_t)m.v.size();
        b.l0 = (int32_t)c.lampMesh->v.size();
        b.mn[0] = mn[0]; b.mn[1] = G; b.mn[2] = mn[1];
        b.mx[0] = mx[0]; b.mx[1] = y1; b.mx[2] = mx[1];
        // 窓の升: 横は高さの縮尺にそろえ (縦横の比を実物のまま)・縦は階の数があれば 1 階 = 3.8 に合わせる
        float vs = storeys > 0 ? 3.8f * storeys / ((y1 - G) > 1 ? (y1 - G) : 1) : 1.0f;
        float us = RH / RS;
        float run = 0;
        for (int k = 0; k < np; k++)
        {
            int k1 = (k + 1) % np;
            float ax = xz[k * 2], az = xz[k * 2 + 1], bxp = xz[k1 * 2], bzp = xz[k1 * 2 + 1];
            float dx = bxp - ax, dz = bzp - az, L = sqrtf(dx * dx + dz * dz);
            if (L < 1e-3f) continue;
            float n[3] = { dz / L, 0, -dx / L }; // 反時計回りの足跡の外向き
            float p0[3] = { ax, y0, az }, p1[3] = { bxp, y0, bzp }, p2[3] = { bxp, y1, bzp }, p3[3] = { ax, y1, az };
            Quad(m, p0, p1, p2, p3, n, MWall, col, run * us, (y0 - G) * vs, (run + L) * us, (y1 - G) * vs);
            run += L;
        }
        {
            float up[3] = { 0, 1, 0 };
            for (int t = 0; t < ntri; t++)
            {
                int i0 = tri[t * 3], i1 = tri[t * 3 + 1], i2 = tri[t * 3 + 2];
                if (i0 >= np || i1 >= np || i2 >= np) continue;
                float a[3] = { xz[i0 * 2], y1, xz[i0 * 2 + 1] }, bb[3] = { xz[i1 * 2], y1, xz[i1 * 2 + 1] }, cc[3] = { xz[i2 * 2], y1, xz[i2 * 2 + 1] };
                float ua[2] = { a[0], a[2] }, ub[2] = { bb[0], bb[2] }, uc[2] = { cc[0], cc[2] };
                Tri(m, a, bb, cc, up, MRoof, col, ua, ub, uc);
            }
        }
        for (int k = 0; k < nbox; k++)
        {
            int16_t q[5]; memcpy(q, bx + k * 10, 10);
            AddOBox(c, TowerX + q[0] * 0.1f * RS, TowerZ + q[1] * 0.1f * RS, q[2] * 0.1f * RS, q[3] * 0.1f * RS, q[4] * 1e-4f, G, y1, (int)c.bld.size());
        }
        // 航空障害灯: 60 m を超える棟は一つ目の箱の四隅
        if (hReal > 60 && nbox > 0)
        {
            int16_t q[5]; memcpy(q, bx, 10);
            float ca = cosf(q[4] * 1e-4f), sa = sinf(q[4] * 1e-4f);
            float qx = TowerX + q[0] * 0.1f * RS, qz = TowerZ + q[1] * 0.1f * RS, hw = q[2] * 0.1f * RS * 0.9f, hd = q[3] * 0.1f * RS * 0.9f;
            for (int s = 0; s < 4; s++)
            {
                float u = (s & 1) ? hw : -hw, v = (s & 2) ? hd : -hd;
                Lamp(*c.lampMesh, qx + ca * u - sa * v, y1 + 1.5f, qz + sa * u + ca * v, 3.2f, 255, 30, 22, 1, 0);
            }
        }
        else if (U(H(h ^ 0x8181)) < 0.04f)
            Lamp(*c.lampMesh, cx, y1 + 1.2f, cz, 2.0f, 255, 36, 26, 2, U(H(h ^ 0x9191)) * 6.28f);
        b.v1 = (int32_t)m.v.size();
        b.l1 = (int32_t)c.lampMesh->v.size();
        c.bld.push_back(b);
    }
    // 地面の面: 区分ごと (区分 u8・頂点の数・添字の数・頂点 i16x2 dm・添字 u32)
    std::vector<float> gv;
    for (uint32_t gi = 0; gi < ng && r.ok; gi++)
    {
        uint8_t g = r.Get<uint8_t>();
        uint32_t nv = r.Get<uint32_t>(), ni = r.Get<uint32_t>();
        const uint8_t* vp = r.Take((size_t)nv * 4);
        const uint8_t* ip = r.Take((size_t)ni * 4);
        if (!r.ok) break;
        if (g == GExpwy || g == GLot) continue;
        gv.resize((size_t)nv * 2);
        for (uint32_t k = 0; k < nv; k++)
        {
            int16_t q[2]; memcpy(q, vp + k * 4, 4);
            gv[k * 2] = TowerX + q[0] * 0.1f * RS; gv[k * 2 + 1] = TowerZ + q[1] * 0.1f * RS;
        }
        float y = G + GLift(g);
        float up[3] = { 0, 1, 0 };
        for (uint32_t t = 0; t + 2 < ni; t += 3)
        {
            uint32_t i3[3]; memcpy(i3, ip + t * 4, 12);
            if (i3[0] >= nv || i3[1] >= nv || i3[2] >= nv) continue;
            float a[3] = { gv[i3[0] * 2], y, gv[i3[0] * 2 + 1] }, b[3] = { gv[i3[1] * 2], y, gv[i3[1] * 2 + 1] }, cc[3] = { gv[i3[2] * 2], y, gv[i3[2] * 2 + 1] };
            float ua[2] = { a[0] - TowerX, a[2] - TowerZ }, ub[2] = { b[0] - TowerX, b[2] - TowerZ }, uc[2] = { cc[0] - TowerX, cc[2] - TowerZ };
            uint8_t col[4] = { (uint8_t)(H(t ^ (g << 24)) >> 24), (uint8_t)(g + 1), 0, 0 };
            Mesh& m = ch.At((a[0] + b[0] + cc[0]) / 3, (a[2] + b[2] + cc[2]) / 3, 3);
            Tri(m, a, b, cc, up, MGround, col, ua, ub, uc);
        }
    }
    {
        c.mesh.emplace_back();
        Skytree(c, c.mesh.back(), TowerX, TowerZ);
    }
    return true;
}
}

// 実在の街 (焼いた .mrpcity の中身・ほどいた後) から作る。形が読めなければ null
MRP_API void* mrp_flt_city_create_real(const uint8_t* data, int32_t len, float ground)
{
    if (!data || len < 32) return nullptr;
    City* c = new City();
    c->ground = ground;
    // 網目は升ごとに必要なだけ足す (足しても動かないよう先に枠を取る)。0 = 地面の下敷き・最後 = 灯
    c->mesh.reserve(4096);
    c->mesh.resize(1);
    for (int i = 0; i < Tiles; i++)
        for (int j = 0; j < Tiles; j++) c->tile[i][j] = nullptr;
    Mesh lamps;
    lamps.kind = 1;
    c->lampMesh = &lamps;
    if (!BuildReal(*c, data, len)) { delete c; return nullptr; }
    c->mesh.push_back(std::move(lamps));
    c->lampMesh = &c->mesh.back();
    MakeGrid(*c);
    return c;
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
    MakeGrid(*c);
    return c;
}

// 区画 k の頂点の先頭 (壊れた棟の範囲を C# がそのままメッシュへ書き直す)
MRP_API const uint8_t* mrp_flt_city_verts(void* p, int32_t k)
{
    City* c = (City*)p;
    if (!c || k < 0 || k >= (int32_t)c->mesh.size()) return nullptr;
    return (const uint8_t*)c->mesh[k].v.data();
}

// 崩れた瓦礫の山の高さ (棟の高さ H から)。シェーダの式と同じ
inline float RubbleTop(float H) { float r = 0.08f * H + 4.0f; return r < 28.0f ? r : 28.0f; }

// 棟 k の状態を頂点へ書く。burn = 燃えている (窓の一部が火・残りは消える)・t0 > 0 = その時刻に崩れ始めた
// (障害灯を消す)。書いた後は C# が mrp_flt_city_bld の範囲をメッシュへ写す。当たりの箱は崩れ落ちた後に mrp_flt_city_flatten で下げる
MRP_API int32_t mrp_flt_city_mark(void* p, int32_t k, int32_t burn, float t0)
{
    City* c = (City*)p;
    if (!c || k < 0 || k >= (int32_t)c->bld.size()) return 0;
    const Bld& b = c->bld[k];
    Mesh& m = c->mesh[b.mesh];
    float cx = (b.mn[0] + b.mx[0]) * 0.5f, cz = (b.mn[2] + b.mx[2]) * 0.5f, H = b.mx[1] - b.mn[1];
    for (int32_t i = b.v0; i < b.v1; i++)
    {
        V& v = m.v[i];
        if (burn && !(v.c[3] & 0x40)) { v.c[3] |= 0x40; v.c[2] = (uint8_t)(v.c[2] / 3); }
        if (t0 > 0) { v.k[0] = t0; v.k[1] = cx; v.k[2] = cz; v.k[3] = H; }
    }
    if (t0 > 0)
        for (int32_t i = b.l0; i < b.l1; i++) c->lampMesh->v[i].uv[0] = 0;
    return 1;
}

// 崩れ落ちた棟 k の当たりの箱を瓦礫の高さへ下げる (上の段は地面の下へ)
MRP_API int32_t mrp_flt_city_flatten(void* p, int32_t k)
{
    City* c = (City*)p;
    if (!c || k < 0 || k >= (int32_t)c->bld.size()) return 0;
    const Bld& b = c->bld[k];
    float H = b.mx[1] - b.mn[1];
    {
        float top = b.mn[1] + RubbleTop(H);
        for (flt::CityBox& bx : c->box)
            if (bx.bld == k)
            {
                if (bx.mn[1] >= top) { bx.mn[1] = bx.mx[1] = b.mn[1] - 1000.0f; bx.bld = -2; } // 上の段は消える (地面の下へ)
                else if (bx.mx[1] > top) bx.mx[1] = top;
            }
    }
    return 1;
}

// シムへ貸す当たりの表 (街を消すまで有効)
MRP_API const flt::CityCol* mrp_flt_city_col(void* p) { return p ? &((City*)p)->col : nullptr; }

// 棟の数と、棟 k の頂点の範囲 (区画の番号・始め・終わり・障害灯の始め・終わり) と形の範囲 6 個
MRP_API int32_t mrp_flt_city_blds(void* p) { return p ? (int32_t)((City*)p)->bld.size() : 0; }
MRP_API int32_t mrp_flt_city_bld(void* p, int32_t k, int32_t* r5, float* b6)
{
    City* c = (City*)p;
    if (!c || k < 0 || k >= (int32_t)c->bld.size()) return 0;
    const Bld& b = c->bld[k];
    r5[0] = b.mesh; r5[1] = b.v0; r5[2] = b.v1; r5[3] = b.l0; r5[4] = b.l1;
    memcpy(b6, b.mn, 12); memcpy(b6 + 3, b.mx, 12);
    return 1;
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
