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
// 壁の作り (シェーダの Facade が窓の大きさ・並び・1 階の作りを変える)。色 g = 種類 × 16 + 8
enum Fac { FOffice = 0, FCurtain, FMansion, FHouse, FShopHouse, FPunched, FBigBox, FFactory, FSchool, FHotel };
inline uint8_t FacG(int f) { return (uint8_t)(f * 16 + 8); }

// 船 (幅 44・長さ 48) が大通りを抜けられる縮尺。S = 建物と塔の大きさの倍率
const float S = 3.0f;
const float Pitch = 192.0f;     // 区画の間隔 (道の中心から中心)
const float Extent = 4800.0f;   // 街の広さ (中心からの半分)
const float InnerR = 1500.0f;   // ここより内は区画を細かく分けて密に建てる
const float TileSize = 1200.0f; // メッシュを分ける升 (見えない升は描かれない)
const int Tiles = 8;            // 升の数 (片側)
const float TowerX = 380.0f, TowerZ = 560.0f; // 電波塔の位置
const float TowerK = 1.5f;      // 電波塔の実寸からの倍率 (縦横同じ)
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
    // 川の水面の升 (焼いた街だけ・橋の上は 0)
    std::vector<uint8_t> wet;
    int32_t wetNx = 0, wetNz = 0;
    float wetOrg[2] = { 0, 0 }, wetCell = 8.0f, wetDip = 0;
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

// wcls = 壁の種類 (0 表・1 隣の棟と接する窓の無い壁・2 妻)。シェーダが窓の並べ方を変える。材質の番号の上の位に入れる (材質 + 8 × 種類)
void Quad(Mesh& m, const float* a, const float* b, const float* c, const float* d, const float* n, int mat, const uint8_t* col,
          float u0, float v0, float u1, float v1, int wcls = 0)
{
    int base = (int)m.v.size();
    const float* ps[4] = { a, b, c, d };
    float uvs[4][2] = { { u0, v0 }, { u1, v0 }, { u1, v1 }, { u0, v1 } };
    for (int k = 0; k < 4; k++)
    {
        V vx;
        memcpy(vx.p, ps[k], 12);
        vx.n[0] = Sn(n[0]); vx.n[1] = Sn(n[1]); vx.n[2] = Sn(n[2]); vx.n[3] = (int8_t)(mat + 8 * wcls);
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
// wc = 南・東・北・西の壁の種類 (Quad の wcls・省略は全部表)
void Box(Mesh& m, float x0, float z0, float x1, float z1, float y0, float y1, float ground, int wallMat, int roofMat, const uint8_t* col, bool roof = true,
         const uint8_t* wc = nullptr)
{
    static const uint8_t none[4] = { 0, 0, 0, 0 };
    if (!wc) wc = none;
    float h0 = y0 - ground, h1 = y1 - ground;
    float p[8][3] = {
        { x0, y0, z0 }, { x1, y0, z0 }, { x1, y0, z1 }, { x0, y0, z1 },
        { x0, y1, z0 }, { x1, y1, z0 }, { x1, y1, z1 }, { x0, y1, z1 } };
    float nS[3] = { 0, 0, -1 }, nE[3] = { 1, 0, 0 }, nN[3] = { 0, 0, 1 }, nW[3] = { -1, 0, 0 }, nU[3] = { 0, 1, 0 };
    float wx = x1 - x0, wz = z1 - z0;
    // 南 (z0)・東 (x1)・北 (z1)・西 (x0)。面の巡りは Quad が法線から決める
    Quad(m, p[1], p[0], p[4], p[5], nS, wallMat, col, 0, h0, wx, h1, wc[0]);
    Quad(m, p[2], p[1], p[5], p[6], nE, wallMat, col, 0, h0, wz, h1, wc[1]);
    Quad(m, p[3], p[2], p[6], p[7], nN, wallMat, col, 0, h0, wx, h1, wc[2]);
    Quad(m, p[0], p[3], p[7], p[4], nW, wallMat, col, 0, h0, wz, h1, wc[3]);
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
    int fac;
    float hgt;
    if (U(H(h ^ 0x1234)) < tallP * fall)
    {
        hgt = (70.0f + 70.0f * U(H(h ^ 0x77))) * S;
        float su = U(H(h ^ 0x99));
        fac = su < 0.5f ? FCurtain : (su < 0.8f ? FOffice : (su < 0.92f ? FMansion : FHotel));
    }
    else
    {
        float mid = 14.0f + 26.0f * u * u;
        hgt = (mid * (0.55f + 0.45f * fall) + 4.0f) * S;
        float su = U(H(h ^ 0x99));
        fac = su < 0.3f ? FMansion : (su < 0.45f ? FShopHouse : (su < 0.62f ? FPunched : (su < 0.74f ? FOffice : (su < 0.8f ? FHotel : (su < 0.88f ? FBigBox : FFactory)))));
    }
    if (hgt > MaxH) hgt = MaxH;
    float lit = fac == FFactory ? 0.03f : (fac == FBigBox ? 0.04f + 0.1f * U(H(h ^ 0x4242)) : 0.06f + 0.3f * U(H(h ^ 0x4242)));
    uint8_t col[4] = { (uint8_t)(h >> 8), FacG(fac), (uint8_t)(lit * 255), (uint8_t)(H(h ^ 0xabc) & 7) };
    // 壁の種類: 低い棟は街区の中で隣と背中合わせの面がある (窓の無い壁)。細長い住まいの短い面は妻 (小窓がまばら)
    uint8_t wc[4] = { 0, 0, 0, 0 };
    {
        bool slab = (fac == FMansion || fac == FHotel) && ((x1 - x0) > 1.8f * (z1 - z0) || (z1 - z0) > 1.8f * (x1 - x0));
        bool longX = (x1 - x0) > (z1 - z0);
        for (int f = 0; f < 4; f++)
        {
            bool shortFace = (f & 1) ? longX : !longX; // 東西の面は z の幅・南北の面は x の幅
            if (hgt < 45 * S && U(H(h ^ (0x3131 + f))) < 0.3f) wc[f] = 1;
            else if (slab && shortFace) wc[f] = 2;
        }
    }
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
        Box(m, x0, z0, x1, z1, G, G + hb, G, MWall, MRoof, col, true, wc);
        AddBox(c, x0, z0, x1, z1, G, G + hb, id);
        float ix = w * 0.18f, iz = d * 0.18f;
        Box(m, x0 + ix, z0 + iz, x1 - ix, z1 - iz, G + hb, G + hgt, G, MWall, MRoof, col, true, wc);
        AddBox(c, x0 + ix, z0 + iz, x1 - ix, z1 - iz, G + hb, G + hgt, id);
        x0 += ix; x1 -= ix; z0 += iz; z1 -= iz;
    }
    else
    {
        Box(m, x0, z0, x1, z1, G, G + hgt, G, MWall, MRoof, col, true, wc);
        AddBox(c, x0, z0, x1, z1, G, G + hgt, id);
    }
    // 屋上の機械室
    if (U(H(h ^ 0x7171)) < 0.45f && w > 8 * S && d > 8 * S)
    {
        float mx0 = x0 + w * 0.25f, mz0 = z0 + d * 0.3f;
        // 機械室は窓の無い箱
        uint8_t mc[4] = { col[0], FacG(FFactory), 0, col[3] };
        static const uint8_t blank[4] = { 1, 1, 1, 1 };
        Box(m, mx0, mz0, mx0 + w * 0.3f, mz0 + d * 0.3f, G + hgt, G + hgt + 2.2f * S, G, MWall, MRoof, mc, true, blank);
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

void RadioTower(City& c, Mesh& m, float X, float Z, float k);

// 高架の道路 (広い通りの上・橋脚と縁の灯の列)
void Highway(City& c, bool alongX, float at, float from, float to)
{
    float G = c.ground;
    const float Y = 11.0f * S, Hw = 7.5f * S, Th = 1.6f * S, Wall = 1.2f * S, Thin = 0.5f * S;
    uint8_t col[4] = { 30, FacG(FFactory), 0, 0 };
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
    RadioTower(c, c.mesh[1 + Tiles * Tiles], TowerX, TowerZ, TowerK);
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
    g.wet = c.wet.empty() ? nullptr : c.wet.data();
    g.wetNx = c.wetNx; g.wetNz = c.wetNz;
    g.wetOrg[0] = c.wetOrg[0]; g.wetOrg[1] = c.wetOrg[1];
    g.wetCell = c.wetCell; g.wetDip = c.wetDip;
}

// ---- 焼いた街 (自前の生成器が組んだ .mrpcity から組む・川と運河と橋のある街並み) ----
// データは中心 (電波塔) からの東 x・北 z (m)。横を RS・高さを RH 倍し、中心を電波塔の場所 (TowerX, TowerZ) へ置く。
// 棟は足跡の角柱 (壁は辺ごとの板・屋根は焼いた三角)・当たりは焼いた向き付きの箱。地面は区分ごとの面 (色 g = 区分 + 1)。
const float RS = 3.0f, RH = 1.5f;
const int ChunkCap = 60000; // 1 つの網目の頂点の上限 (C# は 16 ビットの添字で描く)
const float WaterDip = 4.5f * RH;  // 川の水面は岸の地面より低い (護岸の壁が立つ)
const float Parapet = 1.1f * RH;   // 護岸と橋の欄干の地面からの高さ

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
    case GWater: return -WaterDip;
    default: return 0.02f;
    }
}

// 用途 (焼く道具の番号)・高さ・階・足跡の面積 (m²) から壁の作りと窓の点く割合。
// 東京の下町の実物の作り: 高い事務所だけが全面ガラス・大きな商業は窓がほとんど無い壁・
// 小さな事務所や雑居は間を空けた小さな窓・住まいは窓がまばら・マンションはベランダ側が手すりの帯
void RealStyle(int usage, float hReal, int storeys, float area, uint32_t h, int& fac, float& lit)
{
    float r = U(H(h ^ 0x4242));
    switch (usage)
    {
    case 1: fac = hReal > 45 ? FCurtain : (hReal > 20 ? FOffice : FPunched); lit = 0.08f + 0.25f * r; break; // 業務
    case 3: fac = FHotel; lit = 0.15f + 0.3f * r; break;                                                        // 宿泊
    case 2: fac = area > 1500 ? FBigBox : (hReal > 45 ? FCurtain : FShopHouse); lit = 0.05f + 0.2f * r; break; // 商業
    case 8: fac = FSchool; lit = 0.05f + 0.15f * r; break;                                                     // 公共・学校
    case 4: fac = (hReal < 12 || (storeys > 0 && storeys <= 3)) ? FHouse : FMansion; lit = 0.3f + 0.35f * r; break; // 住宅
    case 5: fac = FMansion; lit = 0.28f + 0.35f * r; break;                                                   // 共同住宅
    case 6: fac = FShopHouse; lit = 0.28f + 0.35f * r; break;                                                 // 店舗併用
    case 7: fac = hReal < 12 ? FHouse : FPunched; lit = 0.22f + 0.33f * r; break;                              // 作業所併用
    case 9: case 10: fac = FFactory; lit = 0.03f; break;                                                      // 倉庫・工場
    default: fac = hReal > 30 ? FOffice : FPunched; lit = 0.15f + 0.25f * r; break;
    }
}

// ---- 川・護岸・橋 ----
enum BridgeKind { BGirder = 0, BArch, BTruss, BCable };

// 水面の三角を升へ塗る (升の中心が三角の中なら水)
void WetFill(City& c, const std::vector<float>& gv, const uint8_t* ip, uint32_t ni, uint32_t nv)
{
    if (c.wet.empty()) c.wet.assign((size_t)c.wetNx * c.wetNz, 0);
    const float cs = c.wetCell;
    for (uint32_t t = 0; t + 2 < ni; t += 3)
    {
        uint32_t i3[3]; memcpy(i3, ip + t * 4, 12);
        if (i3[0] >= nv || i3[1] >= nv || i3[2] >= nv) continue;
        float ax = gv[i3[0] * 2], az = gv[i3[0] * 2 + 1], bx = gv[i3[1] * 2], bz = gv[i3[1] * 2 + 1], cx = gv[i3[2] * 2], cz = gv[i3[2] * 2 + 1];
        float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
        if (fabsf(area) < 1e-6f) continue;
        int i0 = (int)floorf((fminf(ax, fminf(bx, cx)) - c.wetOrg[0]) / cs), i1 = (int)floorf((fmaxf(ax, fmaxf(bx, cx)) - c.wetOrg[0]) / cs);
        int j0 = (int)floorf((fminf(az, fminf(bz, cz)) - c.wetOrg[1]) / cs), j1 = (int)floorf((fmaxf(az, fmaxf(bz, cz)) - c.wetOrg[1]) / cs);
        i0 = i0 < 0 ? 0 : i0; j0 = j0 < 0 ? 0 : j0; i1 = i1 >= c.wetNx ? c.wetNx - 1 : i1; j1 = j1 >= c.wetNz ? c.wetNz - 1 : j1;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                float px = c.wetOrg[0] + (i + 0.5f) * cs, pz = c.wetOrg[1] + (j + 0.5f) * cs;
                float w0 = ((bx - px) * (cz - pz) - (bz - pz) * (cx - px)) / area;
                float w1 = ((cx - px) * (az - pz) - (cz - pz) * (ax - px)) / area;
                if (w0 >= 0 && w1 >= 0 && w0 + w1 <= 1) c.wet[(size_t)j * c.wetNx + i] = 1;
            }
    }
}

// 橋の上 (a→b・半幅 hw) の升は水でない (弾は橋の路面で止まる)
void WetClear(City& c, const float* a, const float* b, float hw)
{
    if (c.wet.empty()) return;
    float dx = b[0] - a[0], dz = b[1] - a[1], L = sqrtf(dx * dx + dz * dz);
    if (L < 1e-3f) return;
    dx /= L; dz /= L;
    const float cs = c.wetCell;
    int i0 = (int)floorf((fminf(a[0], b[0]) - hw - c.wetOrg[0]) / cs), i1 = (int)floorf((fmaxf(a[0], b[0]) + hw - c.wetOrg[0]) / cs);
    int j0 = (int)floorf((fminf(a[1], b[1]) - hw - c.wetOrg[1]) / cs), j1 = (int)floorf((fmaxf(a[1], b[1]) + hw - c.wetOrg[1]) / cs);
    i0 = i0 < 0 ? 0 : i0; j0 = j0 < 0 ? 0 : j0; i1 = i1 >= c.wetNx ? c.wetNx - 1 : i1; j1 = j1 >= c.wetNz ? c.wetNz - 1 : j1;
    for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            float px = c.wetOrg[0] + (i + 0.5f) * cs - a[0], pz = c.wetOrg[1] + (j + 0.5f) * cs - a[1];
            float t = px * dx + pz * dz, s = -px * dz + pz * dx;
            if (t >= 0 && t <= L && fabsf(s) <= hw) c.wet[(size_t)j * c.wetNx + i] = 0;
        }
}

bool WetAt(const City& c, float x, float z)
{
    if (c.wet.empty()) return false;
    int i = (int)floorf((x - c.wetOrg[0]) / c.wetCell), j = (int)floorf((z - c.wetOrg[1]) / c.wetCell);
    return i >= 0 && j >= 0 && i < c.wetNx && j < c.wetNz && c.wet[(size_t)j * c.wetNx + i];
}

// 護岸: 水面から岸の上の欄干まで立つコンクリートの壁 (水側の面・岸側の欄干の面・欄干の天端)
void Bank(City& c, Chunks& ch, const float* p, int np, uint32_t seed)
{
    float G = c.ground;
    const float T = 0.5f * RS; // 欄干の厚み
    uint8_t col[4] = { (uint8_t)(H(seed * 7919u + 3) >> 8), FacG(FFactory), 0, 0 };
    float run = 0, side = 1;
    for (int k = 0; k + 1 < np; k++)
    {
        float ax = p[k * 2], az = p[k * 2 + 1], bx = p[k * 2 + 2], bz = p[k * 2 + 3];
        float dx = bx - ax, dz = bz - az, L = sqrtf(dx * dx + dz * dz);
        if (L < 1e-3f) continue;
        float nx = dz / L, nz = -dx / L, mx = (ax + bx) * 0.5f, mz = (az + bz) * 0.5f;
        // 水のある側へ向ける (どちらにも水が無い所は前の辺の向きのまま)
        if (WetAt(c, mx + nx * 6, mz + nz * 6)) side = 1;
        else if (WetAt(c, mx - nx * 6, mz - nz * 6)) side = -1;
        nx *= side; nz *= side;
        Mesh& m = ch.At(mx, mz, 12);
        float yb = G - WaterDip - 0.5f, yt = G + Parapet;
        float n1[3] = { nx, 0, nz }, n2[3] = { -nx, 0, -nz }, up[3] = { 0, 1, 0 };
        float a0[3] = { ax, yb, az }, b0[3] = { bx, yb, bz }, b1[3] = { bx, yt, bz }, a1[3] = { ax, yt, az };
        float u0 = run / RS, u1 = (run + L) / RS;
        Quad(m, a0, b0, b1, a1, n1, MWall, col, u0, (yb - G) / RH, u1, (yt - G) / RH, 1);
        float ox = -nx * T, oz = -nz * T;
        float c0[3] = { ax + ox, G, az + oz }, d0[3] = { bx + ox, G, bz + oz }, d1[3] = { bx + ox, yt, bz + oz }, c1[3] = { ax + ox, yt, az + oz };
        Quad(m, c0, d0, d1, c1, n2, MWall, col, u0, 0, u1, Parapet / RH, 1);
        Quad(m, a1, b1, d1, c1, up, MRoof, col, ax, az, bx, bz);
        run += L;
    }
}

// 向きの付いた箱の網目 (中心・向き d・半長 hl・半幅 hw)。底は描かない
void OBoxMesh(Mesh& m, float cx, float cz, const float* d, float hl, float hw, float y0, float y1, int mat, const uint8_t* col, bool top = true)
{
    float n[2] = { -d[1], d[0] };
    float P[4][2];
    for (int k = 0; k < 4; k++)
    {
        float su = (k == 0 || k == 3) ? -1.0f : 1.0f, sv = (k < 2) ? -1.0f : 1.0f;
        P[k][0] = cx + d[0] * hl * su + n[0] * hw * sv; P[k][1] = cz + d[1] * hl * su + n[1] * hw * sv;
    }
    for (int k = 0; k < 4; k++)
    {
        int k1 = (k + 1) & 3;
        float ex = P[k1][0] - P[k][0], ez = P[k1][1] - P[k][1], el = sqrtf(ex * ex + ez * ez);
        if (el < 1e-4f) continue;
        float fn[3] = { ez / el, 0, -ex / el };
        float mx = (P[k][0] + P[k1][0]) * 0.5f - cx, mz = (P[k][1] + P[k1][1]) * 0.5f - cz;
        if (fn[0] * mx + fn[2] * mz < 0) { fn[0] = -fn[0]; fn[2] = -fn[2]; }
        float a[3] = { P[k][0], y0, P[k][1] }, b[3] = { P[k1][0], y0, P[k1][1] }, cc[3] = { P[k1][0], y1, P[k1][1] }, dd[3] = { P[k][0], y1, P[k][1] };
        Quad(m, a, b, cc, dd, fn, mat, col, 0, 0, el, y1 - y0);
    }
    if (top)
    {
        float up[3] = { 0, 1, 0 };
        float a[3] = { P[0][0], y1, P[0][1] }, b[3] = { P[1][0], y1, P[1][1] }, cc[3] = { P[2][0], y1, P[2][1] }, dd[3] = { P[3][0], y1, P[3][1] };
        Quad(m, a, b, cc, dd, up, mat, col, 0, 0, 1, 1);
    }
}

// 橋: 路面は地面の面 (道・歩道) が水の上へ続いて描かれる。ここで組むのは桁の側面と裏・欄干・街灯・橋脚と、
// 作りごとの上の構造 (アーチ = 両側の弓と吊り材・トラス = 両側の三角の骨組み・斜張 = 中央の主塔と斜めの索)
void Bridge(City& c, Mesh& m, int kind, const float* A, const float* B, float W, uint32_t seed)
{
    float G = c.ground;
    float dx = B[0] - A[0], dz = B[1] - A[1], L = sqrtf(dx * dx + dz * dz);
    if (L < 1.0f || W < 1.0f) return;
    float d[2] = { dx / L, dz / L }, n[2] = { -d[1], d[0] };
    float hw = W * 0.5f;
    float Lr = L / RS; // 実寸の長さ
    auto P = [&](float t, float s, float y, float* o) { o[0] = A[0] + d[0] * t + n[0] * s; o[1] = y; o[2] = A[1] + d[1] * t + n[1] * s; };
    const float girder = (kind == BGirder ? 2.2f : 1.4f) * RH;
    uint8_t conc[4] = { (uint8_t)(H(seed ^ 0x77) >> 8), 0, 0, 0 };
    uint8_t rail[4] = { 70, 72, 80, 0 };
    // 桁の側面と裏
    for (int sd = -1; sd <= 1; sd += 2)
    {
        float a0[3], b0[3], b1[3], a1[3];
        P(0, sd * hw, G - girder, a0); P(L, sd * hw, G - girder, b0); P(L, sd * hw, G + 0.15f, b1); P(0, sd * hw, G + 0.15f, a1);
        float fn[3] = { n[0] * sd, 0, n[1] * sd };
        Quad(m, a0, b0, b1, a1, fn, MRoad, conc, 0, 0, L, girder);
    }
    {
        float a[3], b[3], cc[3], dd[3];
        P(0, -hw, G - girder, a); P(L, -hw, G - girder, b); P(L, hw, G - girder, cc); P(0, hw, G - girder, dd);
        float dn[3] = { 0, -1, 0 };
        Quad(m, a, b, cc, dd, dn, MRoad, conc, 0, 0, 1, 1);
    }
    // 欄干: 上と中の手すり・支柱
    for (int sd = -1; sd <= 1; sd += 2)
    {
        float s = sd * (hw - 0.3f);
        float a[3], b[3];
        P(0, s, G + Parapet, a); P(L, s, G + Parapet, b); Beam(m, a, b, 0.22f, rail);
        P(0, s, G + Parapet * 0.5f, a); P(L, s, G + Parapet * 0.5f, b); Beam(m, a, b, 0.12f, rail);
        int posts = (int)(L / 9.0f);
        for (int k = 0; k <= posts; k++)
        {
            float t = L * k / (posts > 0 ? posts : 1);
            P(t, s, G + 0.1f, a); P(t, s, G + Parapet, b); Beam(m, a, b, 0.14f, rail);
        }
    }
    // 街灯: 両側に実寸 25 m ごと (柱と、道へ張り出した腕の先の灯)
    {
        int lamps = (int)(Lr / 25.0f);
        if (lamps < 1) lamps = 1;
        for (int k = 0; k < lamps; k++)
        {
            float t = L * (k + 0.5f) / lamps;
            for (int sd = -1; sd <= 1; sd += 2)
            {
                float s = sd * (hw - 0.8f);
                float a[3], b[3], e[3];
                P(t, s, G, a); P(t, s, G + 9.0f * RH, b); Beam(m, a, b, 0.3f, rail);
                P(t, s - sd * 2.2f * RS, G + 9.2f * RH, e); Beam(m, b, e, 0.2f, rail);
                Lamp(*c.lampMesh, e[0], e[1] - 0.4f, e[2], 1.6f, 255, 196, 140, 0, 0);
            }
        }
    }
    // 橋脚: 長い桁橋は実寸 45 m ほどごと・トラスは 3 径間・アーチと斜張は両端の岸で支える
    {
        int spans = kind == BGirder ? (int)(Lr / 45.0f) + 1 : (kind == BTruss ? 3 : 1);
        if (Lr < 50) spans = 1;
        for (int k = 1; k < spans; k++)
        {
            float t = L * k / spans, ctr[3];
            P(t, 0, 0, ctr);
            OBoxMesh(m, ctr[0], ctr[2], d, 1.3f * RS, hw * 0.78f, G - WaterDip - 0.5f, G - girder, MRoad, conc, false);
        }
    }
    uint8_t steel[4];
    if (kind == BArch)
    {
        // 両側の弓 (放物線・実寸の高さ = 長さの 14%)・吊り材・上の横つなぎ。弓の頂に障害灯
        steel[0] = 112; steel[1] = 142; steel[2] = 178; steel[3] = 0;
        const int Seg = 20;
        float Hh = 0.14f * Lr * RH;
        float s0 = hw - 0.6f;
        float prev[2][3];
        for (int k = 0; k <= Seg; k++)
        {
            float t = L * k / Seg, u = 2.0f * k / Seg - 1.0f, y = G + 0.3f + Hh * (1 - u * u);
            for (int sd = 0; sd < 2; sd++)
            {
                float p[3]; P(t, sd ? s0 : -s0, y, p);
                if (k > 0)
                {
                    Beam(m, prev[sd], p, 1.5f, steel);
                    AddBeamBox(c, prev[sd], p, 1.5f);
                }
                memcpy(prev[sd], p, 12);
            }
            if (k > 0 && k < Seg)
            {
                for (int sd = 0; sd < 2; sd++)
                {
                    float a[3]; P(t, sd ? s0 : -s0, G + 0.2f, a);
                    Beam(m, a, prev[sd], 0.22f, steel);
                }
                if (1 - u * u > 0.55f)
                {
                    Beam(m, prev[0], prev[1], 0.8f, steel);
                    AddBeamBox(c, prev[0], prev[1], 0.8f);
                    if (k % 2 == 0 && k + 2 <= Seg)
                    {
                        float t2 = L * (k + 2) / Seg, u2 = 2.0f * (k + 2) / Seg - 1.0f, y2 = G + 0.3f + Hh * (1 - u2 * u2);
                        float q[3]; P(t2, s0, y2, q);
                        Beam(m, prev[0], q, 0.45f, steel);
                    }
                }
            }
        }
        float top[3]; P(L * 0.5f, 0, G + 0.3f + Hh + 1.5f, top);
        Lamp(*c.lampMesh, top[0], top[1], top[2], 2.6f, 255, 30, 22, 1, 0);
    }
    else if (kind == BTruss)
    {
        // 両側の三角の骨組み (上弦・下弦・斜材・垂直材)・上の横つなぎ。端の格間は斜めの端柱
        steel[0] = 96; steel[1] = 128; steel[2] = 116; steel[3] = 0;
        float Ht = 11.0f * RH;
        int panels = (int)(Lr / 11.0f); if (panels < 4) panels = 4;
        float s0 = hw - 0.6f;
        for (int sd = 0; sd < 2; sd++)
        {
            float s = sd ? s0 : -s0;
            for (int k = 0; k < panels; k++)
            {
                float t0 = L * k / panels, t1 = L * (k + 1) / panels;
                float b0[3], b1[3], u0[3], u1[3];
                P(t0, s, G + 0.3f, b0); P(t1, s, G + 0.3f, b1); P(t0, s, G + Ht, u0); P(t1, s, G + Ht, u1);
                Beam(m, b0, b1, 0.7f, steel);
                if (k == 0) { Beam(m, b0, u1, 1.0f, steel); AddBeamBox(c, b0, u1, 1.0f); continue; }
                if (k == panels - 1) { Beam(m, u0, b1, 1.0f, steel); AddBeamBox(c, u0, b1, 1.0f); continue; }
                Beam(m, u0, u1, 1.0f, steel);
                Beam(m, b0, u0, 0.5f, steel);
                if (k < panels / 2) Beam(m, u0, b1, 0.6f, steel); else Beam(m, b0, u1, 0.6f, steel);
            }
            float a[3], b[3];
            P(L / panels, s, G, a); P(L * (panels - 1) / panels, s, G + Ht, b);
            AddOBox(c, (a[0] + b[0]) * 0.5f, (a[2] + b[2]) * 0.5f, L * (panels - 2) / panels * 0.5f, 0.9f, atan2f(d[1], d[0]), G, G + Ht + 1.0f, -1);
        }
        for (int k = 1; k < panels; k++)
        {
            float t = L * k / panels, a[3], b[3];
            P(t, -s0, G + Ht, a); P(t, s0, G + Ht, b);
            Beam(m, a, b, 0.5f, steel);
            if (k + 1 < panels)
            {
                float e[3]; P(L * (k + 1) / panels, s0, G + Ht, e);
                Beam(m, a, e, 0.3f, steel);
            }
        }
        for (int e = 0; e < 2; e++)
        {
            float p[3]; P(e ? L * (panels - 1) / panels : L / panels, 0, G + Ht + 0.8f, p);
            Lamp(*c.lampMesh, p[0], p[1], p[2], 2.0f, 255, 30, 22, 1, 0);
        }
    }
    else if (kind == BCable)
    {
        // 中央の主塔 (A 字の 2 本の脚が上で合う) と、両側へ扇に張る斜めの索
        steel[0] = 176; steel[1] = 182; steel[2] = 196; steel[3] = 0;
        float Hp = 0.3f * Lr * RH + 6.0f * RH;
        float tm = L * 0.5f;
        float top[3]; P(tm, 0, G + Hp, top);
        for (int sd = -1; sd <= 1; sd += 2)
        {
            float foot[3]; P(tm, sd * (hw + 0.8f), G - girder, foot);
            Beam(m, foot, top, 1.6f, steel);
            AddBeamBox(c, foot, top, 1.6f);
        }
        {
            float a[3], b[3]; P(tm, -(hw + 0.8f), G - girder - 0.2f, a); P(tm, hw + 0.8f, G - girder - 0.2f, b);
            Beam(m, a, b, 1.2f, steel);
        }
        const int Cables = 9;
        for (int dir = -1; dir <= 1; dir += 2)
            for (int k = 0; k < Cables; k++)
            {
                float f = (k + 1.0f) / Cables;
                float t = tm + dir * f * (L * 0.5f - 2.0f * RS);
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    float anchor[3], high[3];
                    P(t, sd * (hw - 0.9f), G + 0.4f, anchor);
                    float hy = Hp * (0.72f + 0.25f * (1 - f));
                    float sh = sd * (hw + 0.8f) * (1 - hy / Hp) * 0.95f; // 脚の上の位置
                    P(tm + dir * 0.6f, sh, G + hy, high);
                    Beam(m, anchor, high, 0.18f, steel);
                }
            }
        Lamp(*c.lampMesh, top[0], top[1] + 1.5f, top[2], 3.0f, 255, 30, 22, 1, 0);
    }
}

// 下面も描く箱 (台の床板: 下をくぐる船から裏が見える)
void SlabBox(Mesh& m, float x0, float z0, float x1, float z1, float y0, float y1, float ground, int mat, const uint8_t* col)
{
    Box(m, x0, z0, x1, z1, y0, y1, ground, mat, mat, col, true);
    float a[3] = { x0, y0, z0 }, b[3] = { x1, y0, z0 }, cc[3] = { x1, y0, z1 }, d[3] = { x0, y0, z1 };
    float dn[3] = { 0, -1, 0 };
    Quad(m, a, b, cc, d, dn, mat, col, x0, z0, x1, z1);
}

// 電波塔 (実寸で高さ 545 m)。四角い 4 本脚の鉄骨の格子が上へ指数的に細る。下の太い所の脚は 2 本の弦材と綴り材の
// 組み立て柱・面ごとに X の筋交いと、交点から脚へ渡す補剛材・3 段ごとに水平の筋交い。高さ 70 m の第一の台より下は
// 脚の間が開いた門 (船がくぐれる・当たりは脚だけ)。第一の台から展望台まではコンクリートのエレベーターの芯。
// 200 m に 2 層の八角の展望台・290 m に小さな展望台・250 m と 330 m の台にマイクロ波の円筒のアンテナ。
// 380 m から上は円筒のマストとパネルアンテナの段。高光度の航空障害灯を付けるので赤白には塗らず、鉄骨は下から照らされた白銀。
// k = 実寸からゲームの単位への倍率 (縦横同じ・建物の横の縮尺に引きずられず細い塔のまま)
void RadioTower(City& c, Mesh& m, float X, float Z, float k)
{
    const float G = c.ground;
    const float Hl = 380.0f, Plat = 70.0f, Deck = 200.0f;
    auto W = [](float h) { return 3.5f + 37.0f * expf(-h / 120.0f); };
    static const uint8_t steel[4] = { 198, 210, 228, 1 }, mast[4] = { 182, 190, 204, 1 }, radome[4] = { 214, 214, 218, 1 };
    auto BandCol = [&](float) -> const uint8_t* { return steel; };
    const float sx[4] = { -1, 1, 1, -1 }, sz[4] = { -1, -1, 1, 1 };
    auto Pt = [&](int j, float h, float f, float* o) { float w = W(h) * f; o[0] = X + sx[j] * w * k; o[1] = G + h * k; o[2] = Z + sz[j] * w * k; };
    auto B = [&](const float* a, const float* b, float r) { Beam(m, a, b, r * k, BandCol(((a[1] + b[1]) * 0.5f - G) / k)); };
    // 段の高さ: 面の幅に合わせた格間 (下は大きく上は細かく)。台と展望台の高さは段の境にそろえる
    std::vector<float> hs;
    for (float h = 0; h < Hl - 3.0f;)
    {
        hs.push_back(h);
        float st = 1.3f * W(h); st = st < 7 ? 7 : (st > 45 ? 45 : st);
        float nh = h + st;
        static const float marks[] = { Plat, Deck, 250.0f, 290.0f, 330.0f };
        for (float mk : marks) if (h < mk - 3.0f && nh > mk - 3.0f) { nh = mk; break; }
        h = nh;
    }
    hs.push_back(Hl);
    for (size_t i = 0; i + 1 < hs.size(); i++)
    {
        float h0 = hs[i], h1 = hs[i + 1], w0 = W(h0);
        float rl = 0.45f + 0.03f * w0;
        for (int j = 0; j < 4; j++)
        {
            float a[3], b[3];
            Pt(j, h0, 1, a); Pt(j, h1, 1, b);
            if (w0 > 14.0f)
            {
                // 組み立て柱: 外の弦材と、対角に内へ寄せた弦材を綴り材のジグザグでつなぐ
                float ai[3], bi[3];
                Pt(j, h0, 1 - 0.16f, ai); Pt(j, h1, 1 - 0.16f, bi);
                B(a, b, rl * 0.6f); B(ai, bi, rl * 0.6f);
                const int Lace = 4;
                for (int q = 0; q < Lace; q++)
                {
                    float t0 = (float)q / Lace, t1 = (float)(q + 1) / Lace, p0[3], p1[3];
                    for (int e = 0; e < 3; e++) { p0[e] = (q & 1 ? ai : a)[e] + ((q & 1 ? bi : b)[e] - (q & 1 ? ai : a)[e]) * t0; p1[e] = (q & 1 ? a : ai)[e] + ((q & 1 ? b : bi)[e] - (q & 1 ? a : ai)[e]) * t1; }
                    B(p0, p1, 0.22f);
                }
            }
            else B(a, b, rl);
            if (h1 <= Plat)
            {
                // 門の中は脚だけに当たる (斜めの脚を 3 つの箱で包む)
                for (int q = 0; q < 3; q++)
                {
                    float pa[3], pb[3];
                    for (int e = 0; e < 3; e++) { pa[e] = a[e] + (b[e] - a[e]) * q / 3.0f; pb[e] = a[e] + (b[e] - a[e]) * (q + 1) / 3.0f; }
                    AddBeamBox(c, pa, pb, (rl + 1.2f) * k);
                }
            }
        }
        if (h1 > Plat) AddBox(c, X - w0 * 0.85f * k, Z - w0 * 0.85f * k, X + w0 * 0.85f * k, Z + w0 * 0.85f * k, G + h0 * k, G + h1 * k, -1);
        if (h0 < Plat) continue; // 門: 面は開いたまま
        for (int j = 0; j < 4; j++)
        {
            int jn = (j + 1) & 3;
            float a[3], b[3], an[3], bn[3];
            Pt(j, h0, 1, a); Pt(j, h1, 1, b); Pt(jn, h0, 1, an); Pt(jn, h1, 1, bn);
            B(a, an, 0.25f + 0.01f * w0);
            float rd = 0.2f + 0.009f * w0;
            B(a, bn, rd); B(an, b, rd);
            if (w0 > 8.0f)
            {
                // 交点から両脚の中ほどへ渡す補剛材 (菱形)
                float cp[3], ma[3], mb[3];
                for (int e = 0; e < 3; e++) { cp[e] = (a[e] + b[e] + an[e] + bn[e]) * 0.25f; ma[e] = (a[e] + b[e]) * 0.5f; mb[e] = (an[e] + bn[e]) * 0.5f; }
                B(cp, ma, 0.15f); B(cp, mb, 0.15f);
            }
        }
        if (i % 3 == 0)
        {
            float p0[3], p2[3], p1[3], p3[3];
            Pt(0, h0, 1, p0); Pt(2, h0, 1, p2); Pt(1, h0, 1, p1); Pt(3, h0, 1, p3);
            B(p0, p2, 0.18f); B(p1, p3, 0.18f);
        }
    }
    // 円柱と八角柱 (Sides 面・rot = 最初の面の向き)。側面は傾きのぶん法線を上下へ寄せる
    auto Cyl = [&](float r0, float r1, float h0, float h1, int sides, float rot, int mat, const uint8_t* col, bool capTop, bool capBot)
    {
        float vs = mat == MDeck ? 7.6f / (h1 - h0) : k; // 展望台の窓は 1 層 = 2 段の窓
        float slope = (r0 - r1) / (h1 - h0);
        for (int s = 0; s < sides; s++)
        {
            float t0 = rot + 6.2831853f * s / sides, t1 = rot + 6.2831853f * (s + 1) / sides, tm = (t0 + t1) * 0.5f;
            float a[3] = { X + cosf(t0) * r0 * k, G + h0 * k, Z + sinf(t0) * r0 * k };
            float b[3] = { X + cosf(t1) * r0 * k, G + h0 * k, Z + sinf(t1) * r0 * k };
            float d[3] = { X + cosf(t1) * r1 * k, G + h1 * k, Z + sinf(t1) * r1 * k };
            float e[3] = { X + cosf(t0) * r1 * k, G + h1 * k, Z + sinf(t0) * r1 * k };
            float nl = sqrtf(1 + slope * slope);
            float n[3] = { cosf(tm) / nl, slope / nl, sinf(tm) / nl };
            float arc = 2.0f * r0 * sinf(3.14159265f / sides);
            Quad(m, a, b, d, e, n, mat, col, arc * vs * s, 0, arc * vs * (s + 1), (h1 - h0) * vs);
            float uv0[2] = { 0, 0 };
            if (capTop)
            {
                float ctr[3] = { X, G + h1 * k, Z }, up[3] = { 0, 1, 0 };
                Tri(m, ctr, e, d, up, mat == MDeck ? MRoof : mat, col, uv0, uv0, uv0);
            }
            if (capBot)
            {
                float ctr[3] = { X, G + h0 * k, Z }, dn[3] = { 0, -1, 0 };
                Tri(m, ctr, a, b, dn, mat == MDeck ? MRoof : mat, col, uv0, uv0, uv0);
            }
        }
        float rr = (r0 > r1 ? r0 : r1) * k;
        AddBox(c, X - rr, Z - rr, X + rr, Z + rr, G + h0 * k, G + h1 * k, -1);
    };
    // 横向きの円筒 (マイクロ波のアンテナ: 白い覆いの付いた太鼓)。中心 p・水平の向き (dx, dz)
    auto Drum = [&](const float* p, float dx, float dz, float r, float len)
    {
        const int Sides = 12;
        float u[3] = { -dz, 0, dx }, up[3] = { 0, 1, 0 };
        float f[3] = { p[0] + dx * len * 0.5f * k, p[1], p[2] + dz * len * 0.5f * k }, bk[3] = { p[0] - dx * len * 0.5f * k, p[1], p[2] - dz * len * 0.5f * k };
        float uv0[2] = { 0, 0 };
        float fn[3] = { dx, 0, dz }, bn[3] = { -dx, 0, -dz };
        for (int s = 0; s < Sides; s++)
        {
            float t0 = 6.2831853f * s / Sides, t1 = 6.2831853f * (s + 1) / Sides, tm = (t0 + t1) * 0.5f;
            float o0[3], o1[3], n[3];
            for (int e = 0; e < 3; e++)
            {
                o0[e] = (u[e] * cosf(t0) + up[e] * sinf(t0)) * r * k;
                o1[e] = (u[e] * cosf(t1) + up[e] * sinf(t1)) * r * k;
                n[e] = u[e] * cosf(tm) + up[e] * sinf(tm);
            }
            float a[3] = { bk[0] + o0[0], bk[1] + o0[1], bk[2] + o0[2] }, b[3] = { bk[0] + o1[0], bk[1] + o1[1], bk[2] + o1[2] };
            float d[3] = { f[0] + o1[0], f[1] + o1[1], f[2] + o1[2] }, e[3] = { f[0] + o0[0], f[1] + o0[1], f[2] + o0[2] };
            Quad(m, a, b, d, e, n, MBeam, radome, 0, 0, 1, 1);
            Tri(m, f, e, d, fn, MBeam, radome, uv0, uv0, uv0);
            Tri(m, bk, b, a, bn, MBeam, radome, uv0, uv0, uv0);
        }
    };
    // 足元: 脚ごとの台座と小さな建物 (入口と券売所)
    {
        uint8_t hall[4] = { 141, FacG(FShopHouse), 140, 3 };
        uint8_t conc[4] = { 90, 0, 0, 0 };
        for (int j = 0; j < 4; j++)
        {
            float cx = X + sx[j] * W(0) * k, cz = Z + sz[j] * W(0) * k;
            Box(m, cx - 5 * k, cz - 5 * k, cx + 5 * k, cz + 5 * k, G, G + 4 * k, G, MRoad, MRoad, conc);
            float ox = cx + sx[j] * 9 * k, oz = cz + sz[j] * 9 * k;
            Box(m, ox - 9 * k, oz - 9 * k, ox + 9 * k, oz + 9 * k, G, G + 11 * k, G, MWall, MRoof, hall);
            AddBox(c, ox - 9 * k, oz - 9 * k, ox + 9 * k, oz + 9 * k, G, G + 11 * k, -1);
        }
    }
    // 第一の台 (70 m): 鉄骨の床板と手すり
    {
        float w = W(Plat) * 1.02f * k;
        SlabBox(m, X - w, Z - w, X + w, Z + w, G + (Plat - 4) * k, G + Plat * k, G, MBeam, BandCol(Plat - 1));
        AddBox(c, X - w, Z - w, X + w, Z + w, G + (Plat - 4) * k, G + Plat * k, -1);
        for (int j = 0; j < 4; j++)
        {
            int jn = (j + 1) & 3;
            float a[3] = { X + sx[j] * w, G + (Plat + 1.1f) * k, Z + sz[j] * w }, b[3] = { X + sx[jn] * w, G + (Plat + 1.1f) * k, Z + sz[jn] * w };
            B(a, b, 0.12f);
        }
    }
    // エレベーターの芯 (第一の台から展望台の下まで)
    uint8_t core[4] = { 77, 0, 0, 0 };
    Cyl(4.2f, 4.2f, Plat, Deck - 8, 12, 0, MRoad, core, false, false);
    // 展望台 (200 m): 下の受け・2 層の八角のガラス・それぞれの屋根の縁
    uint8_t glass[4] = { 91, FacG(FCurtain), 255, 0 };
    uint8_t roofc[4] = { 60, 0, 0, 0 };
    const float oct = 3.14159265f / 8;
    Cyl(11, 20, Deck - 8, Deck - 1, 8, oct, MRoad, core, false, true);
    Cyl(20, 20, Deck - 1, Deck + 8, 8, oct, MDeck, glass, false, false);
    Cyl(20.8f, 20.8f, Deck + 8, Deck + 9.5f, 8, oct, MRoof, roofc, true, true);
    Cyl(16.5f, 16.5f, Deck + 9.5f, Deck + 16.5f, 8, oct, MDeck, glass, false, false);
    Cyl(17.5f, 15.0f, Deck + 16.5f, Deck + 19, 8, oct, MRoof, roofc, true, false);
    // 小さな展望台 (290 m)
    Cyl(6, 9, 285, 290, 8, oct, MRoad, core, false, true);
    Cyl(9, 9, 290, 296, 8, oct, MDeck, glass, false, false);
    Cyl(9.6f, 8.0f, 296, 297.5f, 8, oct, MRoof, roofc, true, false);
    // マイクロ波の台 (250 m・330 m): 格子の外へ張り出した床と、四方を向く円筒のアンテナ
    for (int lv = 0; lv < 2; lv++)
    {
        float h = lv == 0 ? 250.0f : 330.0f;
        float w = (W(h) + 3.0f) * k;
        SlabBox(m, X - w, Z - w, X + w, Z + w, G + (h - 1.2f) * k, G + h * k, G, MBeam, BandCol(h - 1));
        AddBox(c, X - w, Z - w, X + w, Z + w, G + (h - 1.2f) * k, G + (h + 6) * k, -1);
        float r = lv == 0 ? 2.3f : 1.6f, len = lv == 0 ? 1.7f : 1.2f;
        int per = lv == 0 ? 2 : 1;
        for (int j = 0; j < 4; j++)
            for (int q = 0; q < per; q++)
            {
                // 面の向き (0 = -z・1 = +x・2 = +z・3 = -x) に少しずつ振る
                float th = -1.5707963f + 1.5707963f * j + (per == 2 ? (q ? 0.3f : -0.25f) : 0.15f * (j - 1.5f));
                float dx = cosf(th), dz = sinf(th);
                float along = per == 2 ? (q ? 0.45f : -0.45f) * W(h) : 0;
                float p[3] = { X + dx * (W(h) + 1.4f) * k - dz * along * k, G + (h + r + 0.4f) * k, Z + dz * (W(h) + 1.4f) * k + dx * along * k };
                Drum(p, dx, dz, r, len);
                float foot[3] = { p[0], G + h * k, p[2] };
                B(foot, p, 0.15f);
            }
    }
    // 格子のてっぺんの台 (380 m)
    {
        float w = (W(Hl) + 1.5f) * k;
        SlabBox(m, X - w, Z - w, X + w, Z + w, G + (Hl - 1.5f) * k, G + Hl * k, G, MBeam, steel);
    }
    // マスト: 10 m ごとの継ぎ目 (継ぎ手ごとに少し明るさが違う)・途中に細くなる段・最後は細い避雷の竿
    for (int s = 0; s < 14; s++)
    {
        float h0 = Hl + s * 10.0f, h1 = h0 + 10.0f;
        float r = h0 < 470 ? 2.6f : 1.7f;
        Cyl(r, r, h0, h1, 10, 0, MBeam, (s & 1) ? steel : mast, s == 8 || s == 13, false);
    }
    {
        float a[3] = { X, G + 520 * k, Z }, b[3] = { X, G + 545 * k, Z };
        Beam(m, a, b, 0.45f * k, steel);
        AddBeamBox(c, a, b, 0.6f * k);
    }
    // パネルアンテナ: 3 段 × 8 枚 (392〜425 m)
    for (int t = 0; t < 3; t++)
        for (int s = 0; s < 8; s++)
        {
            float th = 6.2831853f * s / 8 + t * 0.2f;
            float cx = X + cosf(th) * 3.4f * k, cz = Z + sinf(th) * 3.4f * k;
            float d[2] = { -sinf(th), cosf(th) };
            float y0 = G + (392 + t * 11.5f) * k;
            OBoxMesh(m, cx, cz, d, 0.75f * k, 0.25f * k, y0, y0 + 8.5f * k, MBeam, radome);
        }
    // 航空障害灯: てっぺん・マストの段・格子のてっぺんの四隅・台と展望台・中ほどの脚
    Lamp(*c.lampMesh, X, G + 546 * k, Z, 4.2f, 255, 30, 22, 1, 0);
    for (int s = 0; s < 3; s++)
    {
        float th = 6.2831853f * s / 3 + 0.4f;
        Lamp(*c.lampMesh, X + cosf(th) * 2.3f * k, G + 471 * k, Z + sinf(th) * 2.3f * k, 3.0f, 255, 30, 22, 1, 0);
    }
    for (int j = 0; j < 4; j++)
    {
        float hs4[4] = { Hl + 0.6f, 330.6f, 250.6f, Plat + 0.6f };
        for (float h : hs4)
        {
            float w = (W(h) + (h == 330.6f || h == 250.6f ? 3.0f : 0.5f)) * k;
            Lamp(*c.lampMesh, X + sx[j] * w, G + h * k, Z + sz[j] * w, 2.8f, 255, 30, 22, 1, 0);
        }
        float hm = 135.0f;
        Lamp(*c.lampMesh, X + sx[j] * (W(hm) + 0.8f) * k, G + hm * k, Z + sz[j] * (W(hm) + 0.8f) * k, 2.6f, 255, 30, 22, 1, 0);
    }
    for (int s = 0; s < 8; s++)
    {
        float th = oct + 6.2831853f * s / 8;
        Lamp(*c.lampMesh, X + cosf(th) * 17.6f * k, G + (Deck + 19.3f) * k, Z + sinf(th) * 17.6f * k, 2.4f, 255, 30, 22, 1, 0);
        if (s % 2 == 0) Lamp(*c.lampMesh, X + cosf(th) * 9.7f * k, G + 297.8f * k, Z + sinf(th) * 9.7f * k, 2.2f, 255, 30, 22, 1, 0);
    }
}

bool BuildReal(City& c, const uint8_t* data, int32_t len)
{
    Rd r{ data, data + len };
    const uint8_t* magic = r.Take(4);
    uint32_t ver = r.Get<uint32_t>();
    if (!magic || memcmp(magic, "MRPC", 4) != 0 || ver != 4) return false;
    r.Get<double>(); r.Get<double>();
    float half = r.Get<float>();
    uint32_t nb = r.Get<uint32_t>(), ng = r.Get<uint32_t>();
    if (!r.ok || !(half > 10 && half < 5000)) return false;
    float G = c.ground;
    Chunks ch; ch.c = &c;
    for (int k = 0; k < Tiles * Tiles; k++) ch.cur[k] = -1;
    // 地面の下敷き (敷地の色): 焼いた広さの外だけを囲む 4 枚 (中は敷地の面があり、川の所は穴になっている)
    float sx0 = TowerX - half * RS, sx1 = TowerX + half * RS, sz0 = TowerZ - half * RS, sz1 = TowerZ + half * RS;
    {
        Mesh& m = c.mesh[0];
        float E = Extent + 2400.0f;
        float n[3] = { 0, 1, 0 };
        uint8_t col[4] = { 0, GLot + 1, 0, 0 };
        float rects[4][4] = { { -E, -E, E, sz0 }, { -E, sz1, E, E }, { -E, sz0, sx0, sz1 }, { sx1, sz0, E, sz1 } };
        for (auto& q : rects)
        {
            if (q[2] <= q[0] || q[3] <= q[1]) continue;
            float a[3] = { q[0], G, q[1] }, b[3] = { q[2], G, q[1] }, cc[3] = { q[2], G, q[3] }, d[3] = { q[0], G, q[3] };
            Quad(m, d, cc, b, a, n, MGround, col, d[0] - TowerX, d[2] - TowerZ, cc[0] - TowerX, a[2] - TowerZ);
        }
    }
    c.wetCell = 8.0f;
    c.wetOrg[0] = sx0; c.wetOrg[1] = sz0;
    c.wetNx = (int32_t)ceilf((sx1 - sx0) / c.wetCell); c.wetNz = (int32_t)ceilf((sz1 - sz0) / c.wetCell);
    c.wetDip = WaterDip;
    std::vector<float> xz;
    for (uint32_t bi = 0; bi < nb && r.ok; bi++)
    {
        uint16_t np = r.Get<uint16_t>();
        int16_t base = r.Get<int16_t>();
        uint16_t hgt = r.Get<uint16_t>();
        uint8_t usage = r.Get<uint8_t>(), storeys = r.Get<uint8_t>(), nbox = r.Get<uint8_t>(), ntri = r.Get<uint8_t>();
        const uint8_t* pts = r.Take((size_t)np * 4);
        const uint8_t* wcl = r.Take((size_t)np); // 辺ごとの壁の種類
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
        float area = 0; // 足跡の面積 (実寸の m²)
        for (int k = 0; k < np; k++)
        {
            int k1 = (k + 1) % np;
            area += xz[k * 2] * xz[k1 * 2 + 1] - xz[k1 * 2] * xz[k * 2 + 1];
        }
        area = fabsf(area) * 0.5f / (RS * RS);
        int fac; float lit;
        RealStyle(usage, hReal, storeys, area, h, fac, lit);
        uint8_t col[4] = { (uint8_t)(h >> 8), FacG(fac), (uint8_t)(lit * 255), (uint8_t)(H(h ^ 0xabc) & 7) };
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
            int wk = wcl && wcl[k] <= 2 ? wcl[k] : 0;
            Quad(m, p0, p1, p2, p3, n, MWall, col, run * us, (y0 - G) * vs, (run + L) * us, (y1 - G) * vs, wk);
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
        if (g == GExpwy) continue;
        gv.resize((size_t)nv * 2);
        for (uint32_t k = 0; k < nv; k++)
        {
            int16_t q[2]; memcpy(q, vp + k * 4, 4);
            gv[k * 2] = TowerX + q[0] * 0.1f * RS; gv[k * 2 + 1] = TowerZ + q[1] * 0.1f * RS;
        }
        float y = G + GLift(g);
        if (g == GWater) WetFill(c, gv, ip, ni, nv);
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
    // 護岸: 水の縁の線 (点の数 u16・点 i16x2 dm)
    uint32_t nl = r.Get<uint32_t>();
    std::vector<float> lp;
    for (uint32_t li = 0; li < nl && r.ok; li++)
    {
        uint16_t np = r.Get<uint16_t>();
        const uint8_t* pp = r.Take((size_t)np * 4);
        if (!r.ok) break;
        lp.resize((size_t)np * 2);
        for (int k = 0; k < np; k++)
        {
            int16_t q[2]; memcpy(q, pp + k * 4, 4);
            lp[k * 2] = TowerX + q[0] * 0.1f * RS; lp[k * 2 + 1] = TowerZ + q[1] * 0.1f * RS;
        }
        Bank(c, ch, lp.data(), np, li);
    }
    // 橋: 作り u8・予備 u8・幅 u16 dm・両端 i16x4 dm。水の升から橋の上を抜いてから組む
    uint32_t nbr = r.Get<uint32_t>();
    const uint8_t* bp = r.Take((size_t)nbr * 12);
    if (bp)
    {
        for (uint32_t k = 0; k < nbr; k++)
        {
            const uint8_t* e = bp + k * 12;
            uint16_t w; int16_t q[4]; memcpy(&w, e + 2, 2); memcpy(q, e + 4, 8);
            float a[2] = { TowerX + q[0] * 0.1f * RS, TowerZ + q[1] * 0.1f * RS }, b[2] = { TowerX + q[2] * 0.1f * RS, TowerZ + q[3] * 0.1f * RS };
            WetClear(c, a, b, w * 0.1f * RS * 0.5f + 1.0f);
        }
        for (uint32_t k = 0; k < nbr; k++)
        {
            const uint8_t* e = bp + k * 12;
            uint16_t w; int16_t q[4]; memcpy(&w, e + 2, 2); memcpy(q, e + 4, 8);
            float a[2] = { TowerX + q[0] * 0.1f * RS, TowerZ + q[1] * 0.1f * RS }, b[2] = { TowerX + q[2] * 0.1f * RS, TowerZ + q[3] * 0.1f * RS };
            Bridge(c, ch.At((a[0] + b[0]) * 0.5f, (a[1] + b[1]) * 0.5f, 8000), e[0], a, b, w * 0.1f * RS, k);
        }
    }
    {
        c.mesh.emplace_back();
        RadioTower(c, c.mesh.back(), TowerX, TowerZ, TowerK);
    }
    return true;
}
}

// 焼いた街 (.mrpcity の中身・ほどいた後) から作る。形が読めなければ null
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
    // 先に全部の枠を作る (後から足すと vector が伸びて升の指す先が動く)。0 = 地面・升ごと・電波塔・最後 = 灯
    c->mesh.resize(3 + Tiles * Tiles);
    for (int i = 0; i < Tiles; i++)
        for (int j = 0; j < Tiles; j++) c->tile[i][j] = &c->mesh[1 + i * Tiles + j];
    c->lampMesh = &c->mesh[2 + Tiles * Tiles];
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
