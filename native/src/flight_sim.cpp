#include "mrp_native.h"
#include "flight.h"
#include <math.h>
#include <string.h>
#include <new>

// 3D の艦隊戦のシム。固定刻み (60Hz) で自分の船・弾・当たりを進め、フレームの終わりに描く物を詰める。
// 遠くの船は時刻付きの状態の列を少し過去で補間する (切れたら外挿)。弾は撃った時の位置と速さだけで決まるので、通信は発射の出来事だけ

using namespace flt;

namespace
{
const float Step = 1.0f / 60.0f;
const int MaxSteps = 5;
const float Pi = 3.14159265358979f;
const float Deg = Pi / 180.0f;

struct V3 { float x, y, z; };
struct Q4 { float x, y, z, w; };

inline V3 v3(float x, float y, float z) { return { x, y, z }; }
inline V3 ld(const float* p) { return { p[0], p[1], p[2] }; }
inline void st(float* p, V3 v) { p[0] = v.x; p[1] = v.y; p[2] = v.z; }
inline Q4 ldq(const float* p) { return { p[0], p[1], p[2], p[3] }; }
inline void stq(float* p, Q4 q) { p[0] = q.x; p[1] = q.y; p[2] = q.z; p[3] = q.w; }
inline V3 operator+(V3 a, V3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
inline V3 operator-(V3 a, V3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
inline V3 operator*(V3 a, float s) { return { a.x * s, a.y * s, a.z * s }; }
inline float dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline V3 cross(V3 a, V3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
inline float len(V3 a) { return sqrtf(dot(a, a)); }
inline V3 norm(V3 a, V3 fb = { 0, 0, 1 })
{
    float l = len(a);
    return l > 1e-6f ? a * (1.0f / l) : fb;
}
inline V3 lerp(V3 a, V3 b, float t) { return a + (b - a) * t; }
inline float clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
inline float approach(float cur, float target, float rate, float dt) { return cur + (target - cur) * (1.0f - expf(-rate * dt)); }

inline Q4 qmul(Q4 a, Q4 b)
{
    return { a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
             a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
             a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
             a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z };
}
inline Q4 qaxis(V3 axis, float ang)
{
    float s = sinf(ang * 0.5f);
    return { axis.x * s, axis.y * s, axis.z * s, cosf(ang * 0.5f) };
}
inline Q4 qnorm(Q4 q)
{
    float l = sqrtf(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    if (l < 1e-8f) return { 0, 0, 0, 1 };
    float k = 1.0f / l;
    return { q.x * k, q.y * k, q.z * k, q.w * k };
}
// Unity の Quaternion * Vector3 と同じ
inline V3 rot(Q4 q, V3 v)
{
    float x = q.x * 2, y = q.y * 2, z = q.z * 2;
    float xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z;
    float wx = q.w * x, wy = q.w * y, wz = q.w * z;
    return { (1 - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z,
             (xy + wz) * v.x + (1 - (xx + zz)) * v.y + (yz - wx) * v.z,
             (xz - wy) * v.x + (yz + wx) * v.y + (1 - (xx + yy)) * v.z };
}
inline Q4 qslerp(Q4 a, Q4 b, float t)
{
    float d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
    if (d < 0) { b = { -b.x, -b.y, -b.z, -b.w }; d = -d; }
    if (d > 0.9995f)
        return qnorm({ a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t });
    float th = acosf(d), s = sinf(th);
    float wa = sinf((1 - t) * th) / s, wb = sinf(t * th) / s;
    return { a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb };
}
// Unity の Quaternion.LookRotation と同じ (前 = fwd・上はなるべく up)
Q4 qlook(V3 fwd, V3 up)
{
    V3 f = norm(fwd);
    V3 r = cross(up, f);
    if (len(r) < 1e-5f) r = cross(v3(0, 1, 0), f);
    if (len(r) < 1e-5f) r = v3(1, 0, 0);
    r = norm(r);
    V3 u = cross(f, r);
    float m00 = r.x, m01 = u.x, m02 = f.x;
    float m10 = r.y, m11 = u.y, m12 = f.y;
    float m20 = r.z, m21 = u.z, m22 = f.z;
    float tr = m00 + m11 + m22;
    Q4 q;
    if (tr > 0)
    {
        float s = sqrtf(tr + 1.0f) * 2;
        q = { (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s };
    }
    else if (m00 > m11 && m00 > m22)
    {
        float s = sqrtf(1.0f + m00 - m11 - m22) * 2;
        q = { 0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s };
    }
    else if (m11 > m22)
    {
        float s = sqrtf(1.0f + m11 - m00 - m22) * 2;
        q = { (m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s };
    }
    else
    {
        float s = sqrtf(1.0f + m22 - m00 - m11) * 2;
        q = { (m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s };
    }
    return qnorm(q);
}

inline float rnd(World* w)
{
    w->rng = w->rng * 1664525u + 1013904223u;
    return (float)(w->rng >> 8) * (1.0f / 16777216.0f);
}

void Emit(World* w, int kind, int a, int b, int c, V3 p, float f)
{
    if (w->evN >= MaxEvents) return;
    w->ev[w->evN++] = { kind, a, b, c, p.x, p.y, p.z, f };
}

void SpawnBolt(World* w, int owner, V3 p, V3 v, float life, float dmg, int mine, int kind = 0, float size = 1.0f)
{
    // 輪の中で空いた所を探す (全部埋まっていれば一番古い所を上書き)
    int k = w->boltNext;
    for (int i = 0; i < MaxBolts; i++)
    {
        int j = (w->boltNext + i) % MaxBolts;
        if (!w->bolt[j].alive) { k = j; break; }
    }
    w->boltNext = (k + 1) % MaxBolts;
    Bolt& b = w->bolt[k];
    st(b.p, p); st(b.v, v);
    b.age = 0; b.life = life; b.owner = owner; b.alive = 1; b.mine = mine; b.dmg = dmg; b.kind = kind; b.size = size;
}

inline float rr(World* w, float a, float b) { return a + (b - a) * rnd(w); }
inline V3 rdir(World* w)
{
    for (int i = 0; i < 8; i++)
    {
        V3 d = v3(rr(w, -1, 1), rr(w, -1, 1), rr(w, -1, 1));
        float l = len(d);
        if (l > 0.05f && l <= 1.0f) return d * (1.0f / l);
    }
    return v3(0, 1, 0);
}

void Emit(World* w, int kind, V3 p, V3 v, float life, float s0, float s1, float drag, const uint8_t* c0, const uint8_t* c1, float delay = 0)
{
    int k = w->partNext;
    for (int i = 0; i < MaxParticles; i++)
    {
        int j = (w->partNext + i) % MaxParticles;
        if (!w->part[j].alive) { k = j; break; }
    }
    w->partNext = (k + 1) % MaxParticles;
    Particle& q = w->part[k];
    st(q.p, p); st(q.v, v);
    q.age = -delay; q.life = life; q.s0 = s0; q.s1 = s1; q.drag = drag; q.kind = kind; q.alive = 1;
    memcpy(q.c0, c0, 4); memcpy(q.c1, c1, 4);
}

// 一瞬だけ周りを照らす光を置く。同じ key の前の光は置き換える (連射で光が積み上がらない)。
// 空きが無ければ一番明るさの残りが少ない光を上書きする
void AddLight(World* w, V3 p, float r, float cr, float cg, float cb, float life, int ship = -1, V3 lp = { 0, 0, 0 }, int key = -1)
{
    int k = -1;
    float worst = 1e30f;
    for (int i = 0; i < MaxLights; i++)
    {
        FlashLight& L = w->light[i];
        if (key >= 0 && L.alive && L.key == key) { k = i; break; }
        float left = L.alive ? (1.0f - L.age / L.life) * L.r : -1.0f;
        if (left < worst) { worst = left; k = i; }
    }
    FlashLight& L = w->light[k];
    st(L.p, p); st(L.lp, lp);
    L.c[0] = cr; L.c[1] = cg; L.c[2] = cb;
    L.r = r; L.age = 0; L.life = life; L.ship = ship; L.alive = 1; L.key = key;
}

// 弾が当たった所: 白い閃き → 当たった向きの逆へ跳ねる火花の筋 → 薄い煙
void FxHit(World* w, V3 p, V3 boltV)
{
    static const uint8_t flash0[4] = { 255, 250, 225, 0 }, flash1[4] = { 255, 160, 60, 0 };
    static const uint8_t spark0[4] = { 255, 225, 160, 0 }, spark1[4] = { 255, 90, 30, 0 };
    static const uint8_t smoke0[4] = { 60, 58, 62, 110 }, smoke1[4] = { 20, 20, 24, 0 };
    AddLight(w, p, 22.0f, 1.0f, 0.7f, 0.4f, 0.14f);
    V3 back = norm(boltV * -1.0f);
    Emit(w, 0, p, v3(0, 0, 0), 0.11f, 3.2f, 1.2f, 0, flash0, flash1);
    for (int i = 0; i < 10; i++)
    {
        V3 d = norm(back + rdir(w) * 0.9f);
        Emit(w, 1, p, d * rr(w, 28, 75), rr(w, 0.18f, 0.42f), 0.5f, 0.2f, 3.5f, spark0, spark1);
    }
    for (int i = 0; i < 2; i++)
        Emit(w, 0, p + rdir(w) * 0.6f, back * rr(w, 2, 5) + rdir(w), rr(w, 0.5f, 0.8f), 1.4f, 4.5f, 1.5f, smoke0, smoke1, 0.05f);
}

// 船が落ちる爆発: 閃光 → 膨らむ火の玉 → 黒煙 → 散る破片の火花 (どれも中心から外へ動いて冷める)
void FxExplode(World* w, V3 p, float sc)
{
    static const uint8_t flash0[4] = { 255, 255, 240, 0 }, flash1[4] = { 255, 190, 90, 0 };
    static const uint8_t fire0[4] = { 255, 235, 170, 40 }, fire1[4] = { 200, 60, 20, 150 };
    static const uint8_t smoke0[4] = { 40, 36, 40, 210 }, smoke1[4] = { 12, 12, 14, 0 };
    static const uint8_t deb0[4] = { 255, 220, 150, 0 }, deb1[4] = { 255, 80, 20, 0 };
    Emit(w, 0, p, v3(0, 0, 0), 0.2f, 34 * sc, 18 * sc, 0, flash0, flash1);
    AddLight(w, p, 140.0f * sc, 1.0f, 0.62f, 0.3f, 1.1f);
    for (int i = 0; i < 16; i++)
    {
        V3 d = rdir(w);
        Emit(w, 0, p + d * rr(w, 0, 5 * sc), d * rr(w, 6, 20) * sc, rr(w, 0.55f, 1.0f), rr(w, 5, 8) * sc, rr(w, 11, 17) * sc, 1.8f, fire0, fire1, rr(w, 0, 0.12f));
    }
    for (int i = 0; i < 14; i++)
    {
        V3 d = rdir(w);
        Emit(w, 0, p + d * rr(w, 1, 6 * sc), d * rr(w, 3, 9) * sc, rr(w, 1.6f, 2.8f), rr(w, 4.5f, 7) * sc, rr(w, 12, 19) * sc, 0.9f, smoke0, smoke1, rr(w, 0.12f, 0.35f));
    }
    for (int i = 0; i < 44; i++)
    {
        V3 d = rdir(w);
        Emit(w, 1, p + d * rr(w, 0, 3 * sc), d * rr(w, 40, 130) * sc, rr(w, 0.4f, 1.1f), 0.9f * sc, 0.3f * sc, 1.3f, deb0, deb1);
    }
}

// 砲台の光線が届いた所で小惑星が砕ける: 短い閃き → 灰褐色の岩の欠片が光線の向きへ散って回る → 細かい砂の雲 (岩は光らない)
void FxRock(World* w, V3 p, V3 dir)
{
    static const uint8_t flash0[4] = { 255, 230, 190, 0 }, flash1[4] = { 255, 120, 60, 0 };
    static const uint8_t rock0[4] = { 92, 84, 76, 235 }, rock1[4] = { 52, 48, 46, 200 };
    static const uint8_t rockb0[4] = { 120, 108, 92, 235 }, rockb1[4] = { 66, 60, 54, 200 };
    static const uint8_t ember0[4] = { 255, 170, 90, 0 }, ember1[4] = { 200, 50, 20, 0 };
    static const uint8_t dust0[4] = { 38, 35, 33, 110 }, dust1[4] = { 16, 15, 15, 0 };
    Emit(w, 0, p, v3(0, 0, 0), 0.12f, 9.0f, 4.0f, 0, flash0, flash1);
    for (int i = 0; i < 18; i++)
    {
        V3 d = norm(dir * 0.6f + rdir(w));
        float s = rr(w, 0.7f, 2.0f);
        Emit(w, 2, p + d * rr(w, 0, 1.5f), d * rr(w, 6, 22), rr(w, 1.6f, 3.2f), s, s * 0.8f, 0.25f, (i & 1) ? rock0 : rockb0, (i & 1) ? rock1 : rockb1);
    }
    for (int i = 0; i < 10; i++)
    {
        V3 d = norm(dir * 0.4f + rdir(w));
        Emit(w, 1, p, d * rr(w, 30, 70), rr(w, 0.2f, 0.45f), 0.45f, 0.15f, 3.0f, ember0, ember1);
    }
    for (int i = 0; i < 6; i++)
    {
        V3 d = rdir(w);
        Emit(w, 0, p + d * rr(w, 0, 2), d * rr(w, 2, 6) + dir * 3.0f, rr(w, 1.8f, 3.0f), rr(w, 3, 5), rr(w, 8, 12), 0.6f, dust0, dust1, rr(w, 0.03f, 0.15f));
    }
}

// ゴミの口から捨てたゴミ: 箱の紙・袋・水色の容器・暗い屑が口の向きへ押し出され、船の速さを持ったまま回りながら後ろへ離れていく
void FxGarbage(World* w, V3 p, V3 dir, V3 shipV)
{
    static const uint8_t box0[4] = { 196, 158, 106, 245 }, box1[4] = { 150, 120, 84, 235 };
    static const uint8_t bag0[4] = { 96, 98, 106, 245 }, bag1[4] = { 66, 68, 74, 235 };
    static const uint8_t can0[4] = { 120, 210, 230, 245 }, can1[4] = { 80, 150, 170, 235 };
    static const uint8_t junk0[4] = { 72, 58, 52, 245 }, junk1[4] = { 48, 40, 38, 235 };
    static const uint8_t puff0[4] = { 34, 34, 36, 60 }, puff1[4] = { 12, 12, 14, 0 };
    const uint8_t* c0[4] = { box0, bag0, can0, junk0 };
    const uint8_t* c1[4] = { box1, bag1, can1, junk1 };
    for (int i = 0; i < 26; i++)
    {
        V3 d = norm(dir + rdir(w) * 0.45f);
        int k = i % 4;
        float s = rr(w, 0.3f, k == 1 ? 0.75f : 0.55f);
        Emit(w, 3, p + d * rr(w, 0, 0.4f), shipV + d * rr(w, 3.5f, 9.0f), rr(w, 3.0f, 5.0f), s, s, 0.05f, c0[k], c1[k], rr(w, 0, 0.25f));
    }
    // 中の空気が抜ける薄い白いもや (すぐ散る)
    for (int i = 0; i < 4; i++)
    {
        V3 d = norm(dir + rdir(w) * 0.3f);
        Emit(w, 0, p, shipV + d * rr(w, 4, 8), rr(w, 0.5f, 0.9f), 0.8f, 2.6f, 1.5f, puff0, puff1);
    }
}

void StepParticles(World* w, float dt)
{
    for (int i = 0; i < MaxParticles; i++)
    {
        Particle& q = w->part[i];
        if (!q.alive) continue;
        q.age += dt;
        if (q.age < 0) continue;
        if (q.age >= q.life) { q.alive = 0; continue; }
        V3 v = ld(q.v);
        st(q.p, ld(q.p) + v * dt);
        st(q.v, v * expf(-q.drag * dt));
    }
}

inline void PutQuad(uint8_t* out, int n, const V3* vs, const uint8_t* col, const float (*uv)[2])
{
    for (int k = 0; k < 4; k++)
    {
        uint8_t* o8 = out + (size_t)(n * 4 + k) * BoltStride;
        float f3[3] = { vs[k].x, vs[k].y, vs[k].z };
        memcpy(o8, f3, 12);
        memcpy(o8 + 12, col, 4);
        memcpy(o8 + 16, uv[k], 8);
    }
}

// 火花・炎・煙と宇宙の塵の板を書く。丸い板の uv は 0〜1、筋の板は uv.x に 2 を足して形をシェーダへ伝える。
// 固い欠片 (kind 2 = 岩・3 = ゴミ) は uv.x から 4 を引き、uv.y に形の番号 × 2 を足す (シェーダが角ばった形を選ぶ・0 の四角い切れ端はゴミだけ)。板は粒ごとの速さで回す
int WriteFlames(World* w, FrameArgs* a, V3 cam, V3 cr, V3 cu);
int WriteMuzzle(World* w, FrameArgs* a, int n, V3 cam, V3 cr, V3 cu);

void WriteFx(World* w, FrameArgs* a, V3 cam, Q4 camQ, V3 shipV)
{
    const float* P = w->prm;
    uint8_t* out = a->fxVerts;
    V3 cr = rot(camQ, v3(1, 0, 0)), cu = rot(camQ, v3(0, 1, 0));
    // 噴射口の炎を先に書く (粒が枠を使い切っても炎は欠けない)
    int n = WriteFlames(w, a, cam, cr, cu);
    n = WriteMuzzle(w, a, n, cam, cr, cu);
    static const float uvRound[4][2] = { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } };
    static const float uvStreak[4][2] = { { 2, 0 }, { 2, 1 }, { 3, 1 }, { 3, 0 } };
    for (int i = 0; i < MaxParticles && n < a->fxCap; i++)
    {
        const Particle& q = w->part[i];
        if (!q.alive || q.age < 0) continue;
        float t = q.age / q.life;
        float g = 1.0f - (1.0f - t) * (1.0f - t);
        float size = q.s0 + (q.s1 - q.s0) * g;
        float fade = t < 0.6f ? 1.0f : (1.0f - t) / 0.4f;
        V3 p = ld(q.p) + ld(q.v) * w->acc;
        // カメラのすぐ前を抜ける噴射のガスは薄くする (近い板は画面を埋めて白い幕になる・爆発の閃光は撃墜した船を隠すので薄くしない)
        if (q.kind == 4) fade *= clampf((len(p - cam) - size - 4.0f) / 14.0f, 0, 1);
        if (fade <= 0) continue;
        uint8_t col[4];
        for (int k = 0; k < 4; k++) col[k] = (uint8_t)((q.c0[k] + (q.c1[k] - q.c0[k]) * t) * fade);
        V3 vs[4];
        if (q.kind == 0 || q.kind == 4)
        {
            V3 r = cr * size, u = cu * size;
            vs[0] = p - r - u; vs[1] = p + r - u; vs[2] = p + r + u; vs[3] = p - r + u;
            PutQuad(out, n++, vs, col, uvRound);
        }
        else if (q.kind == 2 || q.kind == 3)
        {
            // 番号から決まる向きと回る速さ (-3..3 回転/秒)・形 (4 種)
            uint32_t h = (uint32_t)i * 2654435761u;
            float ph = (float)(h & 1023) * (6.2831853f / 1024.0f);
            float spin = ((float)((h >> 10) & 1023) / 511.5f - 1.0f) * 3.0f * 6.2831853f;
            float ang = ph + spin * q.age, ca = cosf(ang), sa = sinf(ang);
            V3 r = (cr * ca + cu * sa) * size, u = (cu * ca - cr * sa) * size;
            vs[0] = p - r - u; vs[1] = p + r - u; vs[2] = p + r + u; vs[3] = p - r + u;
            float sh = (float)(q.kind == 2 ? 1 + (h >> 20) % 3 : (h >> 20) & 3) * 2.0f;
            float uvc[4][2] = { { -4, sh }, { -3, sh }, { -3, sh + 1 }, { -4, sh + 1 } };
            PutQuad(out, n++, vs, col, uvc);
        }
        else
        {
            V3 v = ld(q.v);
            float sp = len(v);
            V3 dir = norm(v, cu);
            float L = sp * 0.05f + size;
            V3 side = norm(cross(dir, cam - p), cr) * (size * 0.35f);
            V3 tail = p - dir * L;
            vs[0] = tail - side; vs[1] = tail + side; vs[2] = p + side; vs[3] = p - side;
            PutQuad(out, n++, vs, col, uvStreak);
        }
    }
    // 宇宙の塵: カメラの周りの箱の中で巻き戻る点。自分の船の速さの分だけ後ろへ伸ばす (遠いほど薄く)
    float D = P[PDustCell];
    if (D > 1)
    {
        float sp = len(shipV);
        V3 dir = norm(shipV, rot(camQ, v3(0, 0, 1)));
        float L = sp * P[PDustStreak] + 0.25f;
        for (int i = 0; i < MaxDust && n < a->fxCap; i++)
        {
            V3 d = ld(w->dust[i]);
            V3 rel = d - cam;
            rel.x -= D * floorf(rel.x / D + 0.5f);
            rel.y -= D * floorf(rel.y / D + 0.5f);
            rel.z -= D * floorf(rel.z / D + 0.5f);
            float dist = len(rel);
            float near = clampf((dist - 4.0f) / 10.0f, 0, 1), far = clampf(1.0f - (dist - D * 0.25f) / (D * 0.25f), 0, 1);
            float al = near * far * P[PDustAlpha];
            if (al < 0.02f) continue;
            V3 p = cam + rel;
            // 宇宙は星明かりの青白・夜の街は下の赤い明かりを受けた色
            float dr = P[PDustRed];
            uint8_t col[4] = { (uint8_t)((170 + 70 * dr) * al), (uint8_t)((190 - 120 * dr) * al), (uint8_t)((230 - 150 * dr) * al), 0 };
            V3 side = norm(cross(dir, rel * -1.0f), cr) * 0.09f;
            V3 tail = p - dir * L;
            V3 vs[4] = { tail - side, tail + side, p + side, p - side };
            PutQuad(out, n++, vs, col, uvStreak);
        }
    }
    for (int i = n; i < a->fxPrev && i < a->fxCap; i++)
        memset(out + (size_t)i * 4 * BoltStride, 0, 4 * BoltStride);
    a->fxN = n;
}

// 部品の動き = 船の中の座標で p' = rot(q, p * s) + t
struct Xf { Q4 q; V3 t; float s; };

// 船の部品を全部計算する (親が先に並ぶ)。値は partV・回り続ける分は世界の時刻から
void PartXfs(const World* w, const Ship& s, Xf* out)
{
    const Model& m = w->model[s.model];
    float tm = (float)fmod(w->time, 3600.0);
    for (int i = 0; i < m.partN; i++)
    {
        const Part& pt = m.part[i];
        V3 pv = ld(pt.pivot), ax = ld(pt.axis);
        float v = s.partV[i];
        Xf x = { { 0, 0, 0, 1 }, { 0, 0, 0 }, 1.0f };
        if (pt.mode == PmRotate)
        {
            x.q = qaxis(ax, (v + pt.spin * tm) * Deg);
            x.t = pv - rot(x.q, pv);
        }
        else if (pt.mode == PmScale)
        {
            x.s = v;
            x.t = pv - pv * v;
        }
        else x.t = ax * v;
        int pa = pt.parent;
        if (pa >= 0 && pa < i)
        {
            const Xf& P = out[pa];
            x = { qmul(P.q, x.q), rot(P.q, x.t * P.s) + P.t, P.s * x.s };
        }
        out[i] = x;
    }
}

// 部品を動かす信号を船の状態から出す (自分の船は操作の溜まり・遠くの船は向きの差から)
void Signals(World* w, Ship& s, float dt)
{
    const float* P = w->prm;
    float pr = P[PPitchRate] * Deg, yr = P[PYawRate] * Deg, rr_ = P[PRollRate] * Deg;
    float pitchV = s.pitchV, yawV = s.yawV, rollV = s.rollV;
    if (s.remote)
    {
        s.speed = dot(ld(s.v), rot(ldq(s.q), v3(0, 0, 1))); // 遠くの船の速さは届いた速度の前向きの分 (回避の横の速さを加速と読まない)
        // 前の向きからの回りを船の中の軸で測る (StepLocal の回し方の逆: x = -pitch・y = yaw・z = -roll)
        Q4 q = ldq(s.q);
        if (s.prevInit && dt > 1e-4f)
        {
            Q4 a = ldq(s.prevQ);
            Q4 d = qmul({ -a.x, -a.y, -a.z, a.w }, q);
            if (d.w < 0) d = { -d.x, -d.y, -d.z, -d.w };
            float k = 2.0f / dt;
            pitchV = -d.x * k; yawV = d.y * k; rollV = -d.z * k;
        }
        else pitchV = yawV = rollV = 0;
        stq(s.prevQ, q);
        s.prevInit = 1;
    }
    s.sig[SgPitch] = pr > 0 ? clampf(pitchV / pr, -1, 1) : 0;
    s.sig[SgYaw] = yr > 0 ? clampf(yawV / yr, -1, 1) : 0;
    s.sig[SgRoll] = rr_ > 0 ? clampf(rollV / rr_, -1, 1) : 0;
    s.sig[SgBank] = P[PBankMax] > 0 ? clampf(s.bank / (P[PBankMax] * Deg), -1, 1) : 0;
    float cr = P[PCruise];
    s.sig[SgBoost] = P[PBoost] > cr ? clampf((s.speed - cr) / (P[PBoost] - cr), 0, 1) : 0;
    s.sig[SgBrake] = cr > P[PBrake] ? clampf((cr - s.speed) / (cr - P[PBrake]), 0, 1) : 0;
    s.sig[SgFire] = s.firePulse;
    float dg = 0;
    if (s.dodgeT > 0 && P[PDodgeTime] > 0) dg = (float)s.dodgeDir * sinf((1.0f - s.dodgeT / P[PDodgeTime]) * Pi);
    s.sig[SgDodge] = dg;
    s.sig[SgOne] = 1;
    s.sig[SgAimYaw] = s.aux[AxAimYaw];
    s.sig[SgAimPitch] = s.aux[AxAimPitch];
    s.sig[SgShotA] = s.shotPulse[0];
    s.sig[SgShotB] = s.shotPulse[1];
    s.sig[SgHatch] = s.aux[AxHatch];
}

// 部品の値を信号へ寄せる (固定刻みごと)
void StepParts(World* w, Ship& s, float dt)
{
    s.firePulse *= expf(-10.0f * dt);
    s.shotPulse[0] *= expf(-8.0f * dt);
    s.shotPulse[1] *= expf(-8.0f * dt);
    const Model& m = w->model[s.model];
    for (int i = 0; i < m.partN; i++)
    {
        const Part& pt = m.part[i];
        float t = 0;
        for (int k = 0; k < SgCount; k++) t += pt.w[k] * s.sig[k];
        t = clampf(t, pt.lo, pt.hi);
        s.partV[i] = pt.resp > 0 ? approach(s.partV[i], t, pt.resp, dt) : t;
    }
}

// 噴射口 (印の種類 MkEngine) の今の位置と向き (首を振る鐘に付いていればその動きの後)。id 1 = 原子炉の小さな噴射口
static int Nozzles(const World* w, const Ship& s, V3 p, Q4 q, V3* outP, V3* outD, int* outId, int cap)
{
    const Model& m = w->model[s.model];
    Xf xf[MaxParts];
    bool have = false;
    int n = 0;
    for (int i = 0; i < m.mkN && n < cap; i++)
    {
        const Marker& mk = m.mk[i];
        if (mk.kind != MkEngine) continue;
        V3 lp = ld(mk.p), ldv = ld(mk.d);
        int pi = m.mkPart[i];
        if (pi >= 0 && pi < m.partN)
        {
            if (!have) { PartXfs(w, s, xf); have = true; }
            lp = rot(xf[pi].q, lp * xf[pi].s) + xf[pi].t;
            ldv = rot(xf[pi].q, ldv);
        }
        outP[n] = p + rot(q, lp);
        outD[n] = norm(rot(q, ldv), rot(q, v3(0, 0, -1)));
        outId[n] = mk.id;
        n++;
    }
    return n;
}

// 噴射の色 (味方の主エンジン = 青白・原子炉 = 橙・敵 = 赤紫)
// 船へ回りの揺れを足す (船の中の軸・rad/秒)
static inline void Jolt(Ship& s, V3 localAxis)
{
    s.joltV[0] += localAxis.x; s.joltV[1] += localAxis.y; s.joltV[2] += localAxis.z;
}

static void FlameColor(const Ship& s, int id, float* c)
{
    if (s.team != 0) { c[0] = 1.0f; c[1] = 0.32f; c[2] = 0.5f; }
    else if (id == 1) { c[0] = 1.0f; c[1] = 0.68f; c[2] = 0.38f; }
    else { c[0] = 0.45f; c[1] = 0.8f; c[2] = 1.0f; }
}

// 噴射の出力と、噴射口から後ろへ流れて冷める熱いガスの粒 (全部の船・固定刻みごと)。
// 出力は速さの信号から決める (加速 = 1・巡航 = 0.45・減速 = 絞る)。加速の出力が上がり始めた刻みで点火の光と粒の塊
void StepExhaust(World* w, Ship& s, int idx, bool local, float dt)
{
    const float* P = w->prm;
    float b = s.sig[SgBoost], br = s.sig[SgBrake];
    float target = clampf(0.45f + 0.55f * b - 0.37f * br, 0.08f, 1.0f);
    bool lit = b > 0.06f && s.boostPrev <= 0.06f;
    s.boostPrev = b;
    if (lit)
    {
        s.ignite = 1;
        if (local) w->shake += P[PIgniteShake];
        Emit(w, EvIgnite, idx, 0, 0, ld(s.p), 0);
        Jolt(s, v3(-P[PKickBoost], 0, 0)); // 押し出す力が重心の下に掛かり、機首が持ち上がる
    }
    if (br > 0.06f && s.brakePrev <= 0.06f) Jolt(s, v3(P[PKickBoost] * 0.6f, 0, 0)); // 逆噴射で前へつんのめる
    s.brakePrev = br;
    s.thr = approach(s.thr, target, 8.0f, dt);
    s.ignite *= expf(-3.2f * dt);
    float rate = P[PExhRate] * (0.3f + s.thr) * (1.0f + 1.5f * s.ignite);
    s.exhAcc += rate * dt;
    int cnt = (int)s.exhAcc;
    s.exhAcc -= (float)cnt;
    if (cnt > 4) cnt = 4;
    if (cnt == 0 && !lit) return;
    V3 np[8], nd[8]; int nid[8];
    int nn = Nozzles(w, s, ld(s.p), ldq(s.drawQ), np, nd, nid, 8);
    V3 sv = ld(s.v);
    float fw = P[PFlameWidth];
    for (int j = 0; j < nn; j++)
    {
        float R = (nid[j] == 1 ? 0.55f : 1.0f) * fw;
        float c[3]; FlameColor(s, nid[j], c);
        float hot = 0.55f + 0.45f * s.thr + 0.5f * s.ignite;
        // 出た直後は白に近い熱い色 → 冷めると暗い元の色へ (足す光だけ・後ろは隠さない)
        uint8_t c0[4], c1[4];
        for (int k = 0; k < 3; k++)
        {
            c0[k] = (uint8_t)clampf((0.45f + 0.55f * c[k]) * 52.0f * hot, 0, 255);
            c1[k] = (uint8_t)clampf(c[k] * 10.0f, 0, 255);
        }
        c0[3] = 0; c1[3] = 0;
        float sp = P[PExhSpeed] * (0.55f + 0.45f * s.thr);
        for (int i = 0; i < cnt; i++)
        {
            // 刻みの中で出た時刻をずらす (速く飛ぶ間に粒が数珠つなぎにならない)
            float adv = ((float)i + rnd(w)) / (float)cnt * dt;
            V3 v = sv + nd[j] * (sp * rr(w, 0.8f, 1.05f)) + rdir(w) * (R * 2.5f);
            V3 at = np[j] + nd[j] * (R * 0.6f + sp * adv) + rdir(w) * (R * 0.2f);
            float life = P[PExhLife] * rr(w, 0.7f, 1.1f) * (0.55f + 0.55f * s.thr);
            Emit(w, 4, at, v, life, R * 0.75f, R * 2.4f, 0.6f, c0, c1, -adv);
        }
        if (lit)
        {
            // 点火: 溜まっていた燃料が一度に燃えて、太く明るいガスが勢いよく抜ける
            static const uint8_t f1[4] = { 10, 10, 12, 0 };
            static const uint8_t f0[4] = { 95, 95, 92, 0 };
            for (int i = 0; i < 6; i++)
            {
                V3 v = sv + nd[j] * (P[PExhSpeed] * rr(w, 0.7f, 1.1f)) + rdir(w) * (R * 4.0f);
                Emit(w, 4, np[j] + nd[j] * (R * rr(w, 0.5f, 2.0f)), v, rr(w, 0.3f, 0.5f), R * 1.1f, R * 3.2f, 1.5f, f0, f1, rr(w, 0, 0.06f));
            }
        }
    }
}

// 姿勢制御の噴射口 (印の種類 MkRcs・乗る部品の値 = 噴く強さ 0..1) から、冷たいガスが白く吹き出してすぐ散る。
// 曲がる・転ぶ・回避・減速 (前向きの口) の操作と同じ口が噴く (遠くの船も向きの差の信号から)
void StepRcs(World* w, Ship& s, float dt)
{
    const float* P = w->prm;
    const Model& m = w->model[s.model];
    if (P[PRcsRate] <= 0 || m.partN == 0) return;
    Q4 q = ldq(s.drawQ);
    V3 sp = ld(s.p), sv = ld(s.v);
    static const uint8_t g0[4] = { 120, 124, 132, 70 }, g1[4] = { 26, 27, 30, 0 };
    for (int i = 0; i < m.mkN; i++)
    {
        const Marker& mk = m.mk[i];
        if (mk.kind != MkRcs) continue;
        int pi = m.mkPart[i];
        if (pi < 0 || pi >= m.partN) continue;
        float v = s.partV[pi];
        if (v < 0.12f) continue;
        // 口ごとの端数は持たず、刻みごとの確率で出す (14 口 × 端数を船に持たせない)
        float want = P[PRcsRate] * v * dt;
        int cnt = (int)want;
        if (rnd(w) < want - (float)cnt) cnt++;
        if (cnt == 0) continue;
        // 口の部品は口を軸の点にして大きさだけ変わるので、口の位置と向きは動かない
        V3 lp = ld(mk.p), ld_ = ld(mk.d);
        V3 at = sp + rot(q, lp);
        V3 d = norm(rot(q, ld_), rot(q, v3(0, 1, 0)));
        for (int k = 0; k < cnt; k++)
        {
            float adv = rnd(w) * dt;
            V3 vel = sv + d * (P[PRcsSpeed] * rr(w, 0.7f, 1.1f)) + rdir(w) * 2.5f;
            Emit(w, 4, at + d * (0.5f + P[PRcsSpeed] * adv), vel, rr(w, 0.3f, 0.5f), 0.35f, 1.9f * (0.6f + 0.4f * v), 3.0f, g0, g1, -adv);
        }
    }
}

static inline float ss01(float a, float b, float x)
{
    float t = clampf((x - a) / (b - a), 0, 1);
    return t * t * (3 - 2 * t);
}

// 噴射口に付いた炎 (毎フレーム・描く位置から)。軸に沿った板 (シェーダが芯・広がる炎・加速の節を描く) と、
// 噴射口の中の光る面 (カメラへ向けた丸い板 2 枚)。軸の板は軸を正面から見るほど潰れるので薄くし、その分は丸い板と粒が受け持つ
int WriteFlames(World* w, FrameArgs* a, V3 cam, V3 cr, V3 cu)
{
    const float* P = w->prm;
    uint8_t* out = a->fxVerts;
    int n = 0;
    static const float uvRound[4][2] = { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } };
    for (int k = 0; k < w->shipN; k++)
    {
        const Ship& s = w->ship[k];
        if (!s.alive) continue;
        V3 np[8], nd[8]; int nid[8];
        int nn = Nozzles(w, s, ld(s.drawP), ldq(s.drawQ), np, nd, nid, 8);
        for (int j = 0; j < nn && n + 3 <= a->fxCap; j++)
        {
            float R = (nid[j] == 1 ? 0.55f : 1.0f) * P[PFlameWidth];
            float c[3]; FlameColor(s, nid[j], c);
            float I = (0.5f + 0.5f * s.thr + 0.6f * s.ignite) * P[PFlameGlow];
            V3 toCam = norm(cam - np[j], cu);
            float facing = dot(nd[j], toCam);
            V3 sx = cross(nd[j], toCam);
            float sinv = len(sx);
            // 軸の板: 根元 uv.x = 5 → 先 6・uv.y = 横 (0..1) + 出力の段 × 2
            float fade = ss01(0.06f, 0.45f, sinv);
            if (fade > 0.01f)
            {
                float L = P[PFlameLen] * (0.3f + 0.9f * s.thr + 0.35f * s.ignite) * (nid[j] == 1 ? 0.6f : 1.0f);
                V3 side = sx * (1.0f / sinv) * (R * 1.7f);
                V3 tip = np[j] + nd[j] * L;
                V3 vs[4] = { np[j] - side, np[j] + side, tip + side, tip - side };
                float lv = (float)(int)clampf(s.thr * 7.0f + s.ignite * 2.0f + 0.5f, 0, 7) * 2.0f;
                float uv[4][2] = { { 5, lv }, { 5, lv + 1 }, { 6, lv + 1 }, { 6, lv } };
                float g = I * fade * 255.0f;
                uint8_t col[4] = { (uint8_t)clampf(c[0] * g, 0, 255), (uint8_t)clampf(c[1] * g, 0, 255), (uint8_t)clampf(c[2] * g, 0, 255), 0 };
                PutQuad(out, n++, vs, col, uv);
            }
            // 噴射口の中の光: 後ろから見るほど明るい (横からは鐘の縁に隠れる分だけ弱く)
            float f2 = ss01(-0.15f, 0.7f, facing);
            if (f2 > 0.01f)
            {
                V3 at = np[j] + nd[j] * (R * 0.25f);
                float hs = R * (1.5f + 0.5f * s.thr + 0.6f * s.ignite);
                float g = I * f2 * 120.0f;
                uint8_t halo[4] = { (uint8_t)clampf(c[0] * g, 0, 255), (uint8_t)clampf(c[1] * g, 0, 255), (uint8_t)clampf(c[2] * g, 0, 255), 0 };
                V3 r = cr * hs, u = cu * hs;
                V3 vs[4] = { at - r - u, at + r - u, at + r + u, at - r + u };
                PutQuad(out, n++, vs, halo, uvRound);
                float cs = R * 0.8f;
                float gc = I * f2 * 200.0f;
                uint8_t core[4] = { (uint8_t)clampf((0.6f + 0.4f * c[0]) * gc, 0, 255), (uint8_t)clampf((0.6f + 0.4f * c[1]) * gc, 0, 255), (uint8_t)clampf((0.6f + 0.4f * c[2]) * gc, 0, 255), 0 };
                r = cr * cs; u = cu * cs;
                V3 vc[4] = { at - r - u, at + r - u, at + r + u, at - r + u };
                PutQuad(out, n++, vc, core, uvRound);
            }
        }
    }
    return n;
}

static void BoltColor(const World* w, int team, bool big, float* c);

static inline uint32_t Hash32(uint32_t x)
{
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
    return x;
}

// 砲口の閃光 1 つ: 砲口から前へ短く噴く光 (炎と同じ軸の板・出力の段 0 = 芯と広がる炎だけ)・砲口の白い芯・周りのにじみ。
// age = 撃ってからの秒・dur で消える。h1/h2 = 撃つごとに変わる長さと芯の大きさ (毎回同じ形に見せない)
static int PutMuzzleFlash(uint8_t* out, int n, int cap, V3 cam, V3 cr, V3 cu, V3 mp, V3 d, const float* c, float S, float age, float dur, float h1, float h2)
{
    static const float uvRound[4][2] = { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } };
    if (age >= dur || n + 3 > cap) return n;
    float u = age / dur;
    float e = (1.0f - u) * (1.0f - u);
    V3 toCam = norm(cam - mp, cu);
    V3 sx = cross(d, toCam);
    float sinv = len(sx);
    float fade = ss01(0.06f, 0.45f, sinv);
    if (fade > 0.01f)
    {
        // 出た直後に伸び切り (最初の 1/4 で)、あとは明るさだけが落ちる
        float L = S * (7.0f + 5.0f * h1) * (0.55f + 0.45f * clampf(u * 4.0f, 0, 1));
        V3 side = sx * (1.0f / sinv) * (S * 1.6f);
        V3 tip = mp + d * L;
        V3 vs[4] = { mp - side, mp + side, tip + side, tip - side };
        float uv[4][2] = { { 5, 0 }, { 5, 1 }, { 6, 1 }, { 6, 0 } };
        float g = e * fade * 330.0f;
        uint8_t col[4] = { (uint8_t)clampf(c[0] * g, 0, 255), (uint8_t)clampf(c[1] * g, 0, 255), (uint8_t)clampf(c[2] * g, 0, 255), 0 };
        PutQuad(out, n++, vs, col, uv);
    }
    V3 at = mp + d * (0.5f * S);
    float hs = S * 6.0f * (1.0f + 0.3f * u);
    float gh = e * 110.0f;
    uint8_t halo[4] = { (uint8_t)clampf(c[0] * gh, 0, 255), (uint8_t)clampf(c[1] * gh, 0, 255), (uint8_t)clampf(c[2] * gh, 0, 255), 0 };
    V3 r = cr * hs, up = cu * hs;
    V3 vh[4] = { at - r - up, at + r - up, at + r + up, at - r + up };
    PutQuad(out, n++, vh, halo, uvRound);
    float cs = S * (1.5f + 0.8f * h2) * (1.0f + 0.5f * u);
    float gc = e * 255.0f;
    uint8_t core[4] = { (uint8_t)clampf((0.65f + 0.35f * c[0]) * gc, 0, 255), (uint8_t)clampf((0.65f + 0.35f * c[1]) * gc, 0, 255), (uint8_t)clampf((0.65f + 0.35f * c[2]) * gc, 0, 255), 0 };
    r = cr * cs; up = cu * cs;
    V3 vc[4] = { at - r - up, at + r - up, at + r + up, at - r + up };
    PutQuad(out, n++, vc, core, uvRound);
    return n;
}

// 主砲と砲台の砲口の閃光 (毎フレーム・描く姿から。速く飛んでも砲口に付いたまま)
int WriteMuzzle(World* w, FrameArgs* a, int n, V3 cam, V3 cr, V3 cu)
{
    uint8_t* out = a->fxVerts;
    for (int k = 0; k < w->shipN; k++)
    {
        const Ship& s = w->ship[k];
        if (!s.alive) continue;
        float tm = s.mzAge + w->acc, t0 = s.tzAge[0] + w->acc, t1 = s.tzAge[1] + w->acc;
        bool big = s.mzBig > 0.5f;
        float dur = big ? 0.16f : 0.075f;
        if (tm >= dur && t0 >= 0.07f && t1 >= 0.07f) continue;
        const Model& m = w->model[s.model];
        Xf xf[MaxParts];
        PartXfs(w, s, xf);
        V3 sp = ld(s.drawP);
        Q4 q = ldq(s.drawQ);
        float c[3]; BoltColor(w, s.team, big, c);
        static const float red[3] = { 1.0f, 0.22f, 0.16f }; // 本編の武器庫の砲台と同じ赤い光線
        for (int i = 0; i < m.mkN; i++)
        {
            const Marker& mk = m.mk[i];
            bool main = mk.kind == MkMuzzle && mk.id == 0;
            bool tur = mk.kind == MkTurret && (mk.id == 0 || mk.id == 1);
            if (!main && !tur) continue;
            float age = main ? tm : (mk.id == 0 ? t0 : t1);
            float du = main ? dur : 0.07f;
            if (age >= du) continue;
            V3 lp = ld(mk.p), ldv = ld(mk.d);
            int pi = m.mkPart[i];
            if (pi >= 0 && pi < m.partN) { lp = rot(xf[pi].q, lp * xf[pi].s) + xf[pi].t; ldv = rot(xf[pi].q, ldv); }
            V3 mp = sp + rot(q, lp);
            V3 d = norm(rot(q, ldv), rot(q, v3(0, 0, 1)));
            uint32_t h = Hash32((main ? s.mzSeed : 0x9e3779b9u * (uint32_t)(mk.id + 1)) + (uint32_t)i * 7919u + (uint32_t)k * 104729u);
            float h1 = (float)(h & 1023) / 1023.0f, h2 = (float)((h >> 10) & 1023) / 1023.0f;
            n = PutMuzzleFlash(out, n, a->fxCap, cam, cr, cu, mp, d, main ? c : red, main ? (big ? 2.2f : 1.0f) : 0.8f, age, du, h1, h2);
        }
    }
    return n;
}

// 照らす光をシェーダへ (明るい順に OutLights 個)。船に付いた光は描く姿から位置を出す。使わない枠は届く距離 1・色 0
void WriteLights(World* w, float* o)
{
    float score[MaxLights];
    for (int i = 0; i < MaxLights; i++)
    {
        FlashLight& L = w->light[i];
        score[i] = -1;
        if (!L.alive) continue;
        if (L.ship >= 0 && L.ship < MaxShips && w->ship[L.ship].alive)
            st(L.p, ld(w->ship[L.ship].drawP) + rot(ldq(w->ship[L.ship].drawQ), ld(L.lp)));
        float u = clampf((L.age + w->acc) / L.life, 0, 1);
        score[i] = (1.0f - u) * (1.0f - u) * L.r;
    }
    for (int j = 0; j < OutLights; j++)
    {
        int best = -1;
        for (int i = 0; i < MaxLights; i++) if (score[i] > 0 && (best < 0 || score[i] > score[best])) best = i;
        float* d = o + j * 8;
        if (best < 0) { d[0] = d[1] = d[2] = 0; d[3] = 1; d[4] = d[5] = d[6] = d[7] = 0; continue; }
        const FlashLight& L = w->light[best];
        float u = clampf((L.age + w->acc) / L.life, 0, 1);
        float k = (1.0f - u) * (1.0f - u);
        d[0] = L.p[0]; d[1] = L.p[1]; d[2] = L.p[2]; d[3] = L.r;
        d[4] = L.c[0] * k; d[5] = L.c[1] * k; d[6] = L.c[2] * k; d[7] = 0;
        score[best] = -1;
    }
}

// 溜めの見た目 (主砲の砲口ごと): 溜まる間は周りから光の粒が吸い込まれ、満ちたら砲口が脈打って光り続ける
void FxCharge(World* w, const Ship& s, V3 p, Q4 q, V3 v, bool burst)
{
    static const uint8_t in0[4] = { 120, 255, 220, 0 }, in1[4] = { 200, 255, 245, 0 };
    static const uint8_t glow0[4] = { 235, 255, 250, 0 }, glow1[4] = { 110, 255, 210, 0 };
    const Model& m = w->model[s.model];
    bool full = s.charge >= 1;
    if (!full && !burst && rnd(w) > 0.35f + 0.65f * s.charge) return;
    Xf xf[MaxParts];
    bool xfReady = false;
    for (int i = 0; i < m.mkN; i++)
    {
        const Marker& mk = m.mk[i];
        if (mk.kind != MkMuzzle || mk.id != 0) continue;
        V3 lp = ld(mk.p);
        int pi = m.mkPart[i];
        if (pi >= 0 && pi < m.partN)
        {
            if (!xfReady) { PartXfs(w, s, xf); xfReady = true; }
            lp = rot(xf[pi].q, lp * xf[pi].s) + xf[pi].t;
        }
        V3 mp = p + rot(q, lp);
        if (burst)
        {
            // 満ちた瞬間: 砲口で一度大きくはじける (溜まったことを目の端でも分かるように)
            Emit(w, 0, mp, v, 0.22f, 9.0f, 3.0f, 0, glow0, glow1);
            for (int k = 0; k < 6; k++)
                Emit(w, 1, mp, v + rdir(w) * rr(w, 25, 45), rr(w, 0.15f, 0.25f), 0.5f, 0.15f, 3.0f, glow0, in0);
        }
        if (full)
        {
            float pulse = 1.0f + 0.3f * sinf((float)w->time * 18.0f);
            Emit(w, 0, mp, v, 0.06f, 3.6f * pulse, 3.0f * pulse, 0, glow0, glow1);
        }
        else
        {
            const float life = 0.18f;
            V3 from = mp + rdir(w) * (3.5f + 4.0f * (1.0f - s.charge));
            Emit(w, 0, from, v + (mp - from) * (1.0f / life), life, 0.8f, 0.3f, 0, in0, in1);
        }
    }
}

// 回避の最中に敵の弾がすぐ横を抜けた: 弾の向きに流れる白い筋と、抜けた所の短い閃き
void FxGraze(World* w, V3 p, V3 boltV, V3 shipV)
{
    static const uint8_t flash0[4] = { 255, 255, 255, 0 }, flash1[4] = { 140, 255, 220, 0 };
    static const uint8_t streak0[4] = { 230, 255, 250, 0 }, streak1[4] = { 90, 220, 200, 0 };
    Emit(w, 0, p, shipV, 0.18f, 10.0f, 3.0f, 0, flash0, flash1);
    V3 d = norm(boltV);
    for (int i = 0; i < 16; i++)
        Emit(w, 1, p + rdir(w) * 2.0f, shipV + norm(d + rdir(w) * 0.4f) * rr(w, 60, 130), rr(w, 0.2f, 0.4f), 0.8f, 0.2f, 2.5f, streak0, streak1);
}

// 弾の色 (光の筋・砲口の閃光・照らす光で揃える)。0..1
// 敵の弾は宇宙では赤紫・赤い夜の街では赤に埋もれるので琥珀色の白熱 (PEnemyWarm)
static void BoltColor(const World* w, int team, bool big, float* c)
{
    if (big) { c[0] = 0.88f; c[1] = 1.0f; c[2] = 0.96f; }
    else if (team == 0) { c[0] = 0.47f; c[1] = 1.0f; c[2] = 0.78f; }
    else if (w->prm[PEnemyWarm] > 0.5f) { c[0] = 1.0f; c[1] = 0.8f; c[2] = 0.4f; }
    else { c[0] = 1.0f; c[1] = 0.27f; c[2] = 0.47f; }
}

// 砲口から出て残る物 (撃った瞬間に 1 回): 砲口の先で膨らんで冷める薄いガス・前へ散る短い火花。
// どれも船の速さを持って出る (砲口の前に留まって見え、置き去りにならない)。閃光そのものは毎フレーム砲口の位置から書く (WriteMuzzle)
void FxMuzzle(World* w, int team, V3 mp, V3 dir, V3 shipV, bool big)
{
    float c[3]; BoltColor(w, team, big, c);
    float S = big ? 2.2f : 1.0f;
    uint8_t gas0[4] = { (uint8_t)(c[0] * 70), (uint8_t)(c[1] * 70), (uint8_t)(c[2] * 70), 36 }, gas1[4] = { 10, 12, 14, 0 };
    uint8_t sp0[4] = { (uint8_t)(150 + c[0] * 105), (uint8_t)(150 + c[1] * 105), (uint8_t)(150 + c[2] * 105), 0 };
    uint8_t sp1[4] = { (uint8_t)(c[0] * 120), (uint8_t)(c[1] * 120), (uint8_t)(c[2] * 120), 0 };
    int gasN = big ? 5 : 2, spN = big ? 10 : 3;
    for (int i = 0; i < gasN; i++)
        Emit(w, 4, mp + dir * rr(w, 0.6f, 1.6f) * S, shipV + dir * rr(w, 5, 14) * S + rdir(w) * 2.0f, rr(w, 0.25f, 0.4f) * (big ? 1.6f : 1.0f),
             0.5f * S, rr(w, 2.0f, 3.0f) * S, 3.0f, gas0, gas1);
    for (int i = 0; i < spN; i++)
        Emit(w, 1, mp + dir * 0.5f, shipV + norm(dir + rdir(w) * 0.55f) * rr(w, 35, 80) * (big ? 1.4f : 1.0f), rr(w, 0.08f, 0.18f), 0.35f * S, 0.1f, 4.0f, sp0, sp1);
}

// ---- 街の当たり (CityCol の箱の表) ----
// 升の番号の範囲 (x0..x1, z0..z1 を覆う升)。表の外には箱が無い
inline bool CityCells(const CityCol* c, float x0, float z0, float x1, float z1, int& i0, int& j0, int& i1, int& j1)
{
    i0 = (int)floorf((x0 - c->org[0]) / c->cell); i1 = (int)floorf((x1 - c->org[0]) / c->cell);
    j0 = (int)floorf((z0 - c->org[1]) / c->cell); j1 = (int)floorf((z1 - c->org[1]) / c->cell);
    if (i1 < 0 || j1 < 0 || i0 >= c->nx || j0 >= c->nz) return false;
    if (i0 < 0) i0 = 0;
    if (j0 < 0) j0 = 0;
    if (i1 >= c->nx) i1 = c->nx - 1;
    if (j1 >= c->nz) j1 = c->nz - 1;
    return true;
}

// 線分 a→b (太さ r) が最初に当たる箱。返り値 = 0..1 の位置 (当たらなければ -1)・n = 当たった面の向き
float CitySeg(const CityCol* c, V3 a, V3 b, float r, int* hitBox, V3* nOut)
{
    if (!c || fminf(a.y, b.y) - r > c->top) return -1;
    int i0, j0, i1, j1;
    if (!CityCells(c, fminf(a.x, b.x) - r, fminf(a.z, b.z) - r, fmaxf(a.x, b.x) + r, fmaxf(a.z, b.z) + r, i0, j0, i1, j1)) return -1;
    float pa[3] = { a.x, a.y, a.z }, d[3] = { b.x - a.x, b.y - a.y, b.z - a.z };
    float best = 2.0f; int bestBox = -1, bestAx = 1; float bestSg = 1;
    for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            int cell = j * c->nx + i;
            for (int k = c->start[cell]; k < c->start[cell + 1]; k++)
            {
                const CityBox& bx = c->box[c->item[k]];
                float te = -1e30f, tx = 1e30f; int ax = 1; float sg = 1;
                bool miss = false;
                for (int q = 0; q < 3 && !miss; q++)
                {
                    float lo = bx.mn[q] - r, hi = bx.mx[q] + r;
                    if (fabsf(d[q]) < 1e-8f)
                    {
                        if (pa[q] < lo || pa[q] > hi) miss = true;
                        continue;
                    }
                    float inv = 1.0f / d[q];
                    float t0 = (lo - pa[q]) * inv, t1 = (hi - pa[q]) * inv;
                    float s0 = -1;
                    if (t0 > t1) { float tt = t0; t0 = t1; t1 = tt; s0 = 1; }
                    if (t0 > te) { te = t0; ax = q; sg = s0; }
                    if (t1 < tx) tx = t1;
                    if (te > tx) miss = true;
                }
                if (miss || tx < 0 || te > 1) continue;
                float t = te < 0 ? 0 : te;
                if (t < best) { best = t; bestBox = c->item[k]; bestAx = ax; bestSg = sg; }
            }
        }
    if (bestBox < 0) return -1;
    if (hitBox) *hitBox = bestBox;
    if (nOut) { float n[3] = { 0, 0, 0 }; n[bestAx] = bestSg; *nOut = v3(n[0], n[1], n[2]); }
    return best;
}

// 球 (中心 p・半径 r) と一番深く重なる候補の箱。返り値 = 重なりの深さ (0 = 重ならない)・n = 押し出す向き
float CitySphere(const CityCol* c, V3 p, float r, const int* cand, int candN, V3* nOut, int* hitBox)
{
    float best = 0;
    for (int k = 0; k < candN; k++)
    {
        const CityBox& bx = c->box[cand[k]];
        V3 q = v3(clampf(p.x, bx.mn[0], bx.mx[0]), clampf(p.y, bx.mn[1], bx.mx[1]), clampf(p.z, bx.mn[2], bx.mx[2]));
        V3 d = p - q;
        float d2 = dot(d, d);
        if (d2 >= r * r) continue;
        float depth; V3 n;
        if (d2 > 1e-8f) { float dl = sqrtf(d2); n = d * (1.0f / dl); depth = r - dl; }
        else
        {
            // 中心が箱の中: 一番近い面から出す
            float pen[6] = { p.x - bx.mn[0], bx.mx[0] - p.x, p.y - bx.mn[1], bx.mx[1] - p.y, p.z - bx.mn[2], bx.mx[2] - p.z };
            int m = 0;
            for (int f = 1; f < 6; f++) if (pen[f] < pen[m]) m = f;
            float nn[3] = { 0, 0, 0 }; nn[m >> 1] = (m & 1) ? 1.0f : -1.0f;
            n = v3(nn[0], nn[1], nn[2]); depth = pen[m] + r;
        }
        if (depth > best) { best = depth; *nOut = n; *hitBox = cand[k]; }
    }
    return best;
}

// 擦った所の火花: 進む向きへ流れる短い筋と、壁の粉
void FxScrape(World* w, V3 p, V3 n, V3 v, float k)
{
    static const uint8_t spark0[4] = { 255, 230, 170, 0 }, spark1[4] = { 255, 100, 35, 0 };
    static const uint8_t dust0[4] = { 58, 50, 52, 120 }, dust1[4] = { 20, 18, 20, 0 };
    V3 t = v - n * dot(v, n);
    int sparks = 3 + (int)(5 * k);
    for (int i = 0; i < sparks; i++)
    {
        V3 d = norm(n * 0.6f + rdir(w) * 0.7f);
        Emit(w, 1, p, t * 0.55f + d * rr(w, 20, 60), rr(w, 0.15f, 0.35f), 0.45f, 0.15f, 3.0f, spark0, spark1);
    }
    if (rnd(w) < 0.5f + k)
        Emit(w, 0, p + rdir(w) * 1.5f, n * rr(w, 2, 6) + t * 0.2f, rr(w, 0.8f, 1.4f), 3.0f, 9.0f, 1.2f, dust0, dust1);
    AddLight(w, p, 30.0f, 1.0f, 0.6f, 0.3f, 0.08f, -1, v3(0, 0, 0), 900);
}

// 自分の船とビル: 部品の球ごとに重なりを見て押し出す。浅い角度は擦って減速し機首が壁に沿う、正面からは大きく削れて跳ね返る
void CityShip(World* w, Ship& s, int idx, V3& p, Q4& q, float dt)
{
    const CityCol* c = w->city;
    const float* P = w->prm;
    const Model& m = w->model[s.model];
    float R = m.bound;
    s.scrapeT -= dt;
    if (!c || p.y - R > c->top) return;
    int i0, j0, i1, j1;
    if (!CityCells(c, p.x - R, p.z - R, p.x + R, p.z + R, i0, j0, i1, j1)) return;
    // 粗い球に届く箱だけを候補に (同じ箱が隣の升にも入っているので重ねない)
    int cand[48], candN = 0;
    for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            int cell = j * c->nx + i;
            for (int k = c->start[cell]; k < c->start[cell + 1] && candN < 48; k++)
            {
                int b = c->item[k];
                const CityBox& bx = c->box[b];
                V3 qp = v3(clampf(p.x, bx.mn[0], bx.mx[0]), clampf(p.y, bx.mn[1], bx.mx[1]), clampf(p.z, bx.mn[2], bx.mx[2]));
                if (dot(p - qp, p - qp) > R * R) continue;
                bool dup = false;
                for (int u = 0; u < candN; u++) if (cand[u] == b) { dup = true; break; }
                if (!dup) cand[candN++] = b;
            }
        }
    if (candN == 0) return;
    for (int iter = 0; iter < 2; iter++)
    {
        float deep = 0; V3 n = v3(0, 1, 0), hp = p; int box = -1;
        for (int j = 0; j < m.sphN; j++)
        {
            V3 sc = p + rot(q, ld(m.sph[j].c));
            V3 sn = v3(0, 1, 0); int sb = -1;
            float d = CitySphere(c, sc, m.sph[j].r, cand, candN, &sn, &sb);
            if (d > deep) { deep = d; n = sn; box = sb; hp = sc - sn * (m.sph[j].r - d); }
        }
        if (deep <= 0 || box < 0) break;
        p = p + n * deep;
        V3 fwd = rot(q, v3(0, 0, 1));
        float vn = dot(fwd, n) * s.speed;
        if (iter > 0 || vn >= 0) continue;
        float impact = -vn;
        int bld = c->box[box].bld;
        bool crash = impact > P[PCrashSpeed];
        V3 v = fwd * s.speed;
        if (crash)
        {
            // 正面から: 速さを失い、壁から跳ね返って機首が弾かれる
            float k = clampf(impact / (P[PBoost] > 1 ? P[PBoost] : 1), 0, 1);
            s.speed *= 0.3f;
            st(s.bumpV, ld(s.bumpV) + n * (impact * P[PCrashBounce]));
            Jolt(s, v3(rnd(w) - 0.5f, rnd(w) - 0.5f, (rnd(w) - 0.5f) * 2.0f) * (6.0f * k));
            FxExplode(w, hp, 0.18f + 0.25f * k);
            FxScrape(w, hp, n, v, 1);
            float dmg = P[PCrashDamage] * k;
            Emit(w, EvHit, -1, idx, -1, hp, dmg);
            Emit(w, EvScrape, idx, 0, 1, hp, k);
            if (bld >= 0) Emit(w, EvBuilding, idx, bld, 0x30000, hp, dmg * 2.0f);
            w->shake += P[PShakeHurt] * (1.0f + 2.0f * k);
            s.scrapeT = 0.3f;
        }
        else
        {
            // 浅い角度: 機首を壁に沿う向きへ回し、擦れて減速する
            float k = clampf(impact / (P[PCrashSpeed] > 1 ? P[PCrashSpeed] : 1), 0, 1);
            V3 tf = norm(fwd - n * dot(fwd, n), fwd);
            float ang = acosf(clampf(dot(fwd, tf), -1, 1));
            float turn = fminf(ang, P[PScrapeTurn] * Deg * dt);
            if (ang > 1e-4f) q = qnorm(qmul(qaxis(norm(cross(fwd, tf), v3(0, 1, 0)), turn), q));
            s.speed = approach(s.speed, P[PBrake], P[PScrapeDrag] * (0.4f + k), dt);
            if (s.scrapeT <= 0)
            {
                s.scrapeT = 0.1f;
                FxScrape(w, hp, n, v, k);
                float dmg = P[PScrapeDamage] * 0.1f * (0.3f + k);
                Emit(w, EvHit, -1, idx, -1, hp, dmg);
                Emit(w, EvScrape, idx, 0, 0, hp, k);
                if (bld >= 0) Emit(w, EvBuilding, idx, bld, 0x30000, hp, dmg);
                w->shake += P[PShakeHit] * (0.5f + k);
            }
        }
    }
}

// ビルが崩れる間: 地面を這って外へ広がる土煙と、崩れていく上の縁から落ちる欠片。窓の明かりに下から照らされた色
void StepRuins(World* w, float dt)
{
    static const uint8_t dust0[4] = { 62, 44, 46, 200 }, dust1[4] = { 22, 15, 17, 0 };
    static const uint8_t puff0[4] = { 80, 60, 60, 170 }, puff1[4] = { 26, 19, 21, 0 };
    static const uint8_t rock0[4] = { 70, 60, 60, 235 }, rock1[4] = { 40, 34, 36, 200 };
    for (int i = 0; i < MaxRuins; i++)
    {
        Ruin& r = w->ruin[i];
        if (!r.alive) continue;
        r.age += dt;
        float T = r.fallT + 2.5f;
        if (r.age > T) { r.alive = 0; continue; }
        float hx = r.half[0], hz = r.half[1];
        float fall = 0.5f * 60.0f * fmaxf(r.age - 0.35f, 0) * fmaxf(r.age - 0.35f, 0);
        float top = r.c[1] + fmaxf(r.H - fall, 0);
        // 縁を回る点 (周の上の一様な位置)
        auto edge = [&](float y) {
            float u = rnd(w) * 4.0f;
            float x, z;
            if (u < 1) { x = -hx + 2 * hx * u; z = -hz; }
            else if (u < 2) { x = hx; z = -hz + 2 * hz * (u - 1); }
            else if (u < 3) { x = hx - 2 * hx * (u - 2); z = hz; }
            else { x = -hx; z = hz - 2 * hz * (u - 3); }
            return v3(r.c[0] + x, y, r.c[2] + z);
        };
        bool falling = r.age < r.fallT;
        // 土煙: 足元の周りから外へ (崩れている間は濃く、終わると薄れていく)
        float rate = falling ? 70.0f : 70.0f * (1.0f - (r.age - r.fallT) / 2.5f);
        if (rnd(w) < rate * dt)
        {
            V3 p = edge(r.c[1] + rr(w, 2, 12));
            V3 out = norm(v3(p.x - r.c[0], 0, p.z - r.c[2]), v3(1, 0, 0));
            Emit(w, 0, p, out * rr(w, 18, 45) + v3(0, rr(w, 2, 10), 0), rr(w, 3.0f, 5.0f), rr(w, 14, 24), rr(w, 50, 80), 0.7f, dust0, dust1);
        }
        if (falling)
        {
            // 崩れていく上の縁: 欠片が外へこぼれて落ち、粉が噴く
            if (rnd(w) < 30.0f * dt)
            {
                V3 p = edge(top);
                V3 out = norm(v3(p.x - r.c[0], 0, p.z - r.c[2]), v3(1, 0, 0));
                Emit(w, 2, p, out * rr(w, 4, 14) + v3(0, -rr(w, 20, 50), 0), rr(w, 1.0f, 2.0f), rr(w, 1.5f, 4.0f), rr(w, 1.5f, 4.0f), 0.2f, rock0, rock1);
            }
            if (rnd(w) < 18.0f * dt)
            {
                V3 p = edge(top);
                V3 out = norm(v3(p.x - r.c[0], 0, p.z - r.c[2]), v3(1, 0, 0));
                Emit(w, 0, p, out * rr(w, 8, 20) + v3(0, rr(w, 0, 8), 0), rr(w, 1.5f, 2.6f), rr(w, 8, 14), rr(w, 26, 40), 0.9f, puff0, puff1);
            }
        }
    }
}

// 燃えている所: 黒い煙が立ちのぼり、時々火の粉が散る
void StepBurns(World* w, float dt)
{
    static const uint8_t smoke0[4] = { 34, 28, 30, 200 }, smoke1[4] = { 10, 9, 10, 0 };
    static const uint8_t fire0[4] = { 255, 170, 70, 30 }, fire1[4] = { 180, 40, 10, 0 };
    static const uint8_t ember0[4] = { 255, 190, 90, 0 }, ember1[4] = { 255, 70, 20, 0 };
    for (int i = 0; i < MaxBurns; i++)
    {
        Burn& b = w->burn[i];
        if (!b.alive) continue;
        b.age += dt;
        V3 p = ld(b.p), n = ld(b.n);
        bool nearCam = dot(p - ld(w->camP), p - ld(w->camP)) < 900.0f * 900.0f;
        if (rnd(w) < 5.0f * dt)
            Emit(w, 0, p + n * 3.0f + rdir(w) * 2.0f, n * rr(w, 3, 8) + v3(rr(w, -2, 2), rr(w, 10, 18), rr(w, -2, 2)), rr(w, 3.0f, 4.5f), rr(w, 7, 11), rr(w, 30, 46), 0.3f, smoke0, smoke1);
        if (nearCam && rnd(w) < 6.0f * dt)
            Emit(w, 0, p + n * 1.5f + rdir(w) * 2.5f, n * rr(w, 2, 5) + v3(0, rr(w, 4, 9), 0), rr(w, 0.3f, 0.6f), rr(w, 4, 7), rr(w, 2, 3), 1.0f, fire0, fire1);
        if (nearCam && rnd(w) < 2.0f * dt)
            Emit(w, 1, p + n * 2.0f, (n + rdir(w) * 0.6f + v3(0, 1, 0)) * rr(w, 12, 26), rr(w, 0.5f, 1.1f), 0.35f, 0.12f, 1.2f, ember0, ember1);
    }
}

void StepLocal(World* w, Ship& s, int idx, const Input& in, float dt)
{
    const float* P = w->prm;
    Q4 q = ldq(s.q);
    V3 p = ld(s.p);
    V3 fwd = rot(q, v3(0, 0, 1)), up = rot(q, v3(0, 1, 0)), right = rot(q, v3(1, 0, 0));

    float inPitch = clampf(in.pitch, -1, 1), inYaw = clampf(in.yaw, -1, 1), inRoll = clampf(in.roll, -1, 1);
    // 場の外に出たら中へ向けて自動で曲げる (操作は効かない)
    float distC = len(p);
    if (distC > P[PArena])
    {
        V3 toC = norm(p * -1.0f);
        float lx = dot(toC, right), ly = dot(toC, up), lz = dot(toC, fwd);
        float k = P[PArenaTurn];
        inYaw = clampf(lx * k + (lz < 0 ? (lx >= 0 ? 1.0f : -1.0f) : 0), -1, 1);
        inPitch = clampf(ly * k, -1, 1);
        inRoll = 0;
    }
    // 下限 (地面) に近づいたら機首を上げて引き起こす (近いほど操作より強く)
    if (P[PFloorY] > -1e5f && p.y < P[PFloorY] + P[PFloorMargin])
    {
        float k = clampf((P[PFloorY] + P[PFloorMargin] - p.y) / P[PFloorMargin], 0, 1);
        V3 want = norm(v3(fwd.x, 0, fwd.z) + v3(0, 0.7f, 0), v3(0, 1, 0));
        float lx = dot(want, right), ly = dot(want, up);
        inPitch = inPitch + (clampf(ly * 3.0f, -1, 1) - inPitch) * k;
        inYaw = inYaw + (clampf(lx * 3.0f, -1, 1) - inYaw) * k * 0.5f;
    }

    s.pitchV = approach(s.pitchV, inPitch * P[PPitchRate] * Deg, P[PTurnResp], dt);
    s.yawV = approach(s.yawV, inYaw * P[PYawRate] * Deg, P[PTurnResp], dt);
    s.rollV = approach(s.rollV, inRoll * P[PRollRate] * Deg, P[PTurnResp], dt);

    s.uturnCd = s.uturnCd > 0 ? s.uturnCd - dt : 0;
    s.dodgeCd = s.dodgeCd > 0 ? s.dodgeCd - dt : 0;
    // 後ろに敵が付いている (近くて真後ろ寄り) か。付かれている時の回避は宙返りになり、追ってくる相手へ向き直る
    s.tailed = 0;
    for (int k = 0; k < w->shipN; k++)
    {
        const Ship& o = w->ship[k];
        if (k == idx || !o.alive || o.team == s.team) continue;
        V3 rel = ld(o.p) - p;
        float d = len(rel);
        if (d > 1e-3f && d < P[PTailDist] && dot(rel, fwd) < -P[PTailCone] * d) { s.tailed = 1; break; }
    }
    int wantUTurn = in.uturn;
    int wantDodge = in.dodge;
    if (wantDodge && s.tailed && s.uturnCd <= 0) { wantUTurn = 1; wantDodge = 0; }
    if (wantUTurn && s.uturnT <= 0 && s.uturnCd <= 0 && s.dodgeT <= 0)
    {
        s.uturnT = P[PUTurnTime];
        stq(s.uturnQ0, q);
        Emit(w, EvUTurn, idx, 0, 0, p, 0);
    }
    if (wantDodge && s.dodgeT <= 0 && s.dodgeCd <= 0 && s.uturnT <= 0)
    {
        s.dodgeT = P[PDodgeTime];
        s.dodgeDir = wantDodge > 0 ? 1 : -1;
        s.grazed = 0;
        s.dodgeCd = P[PDodgeCool];
        Emit(w, EvDodge, idx, s.dodgeDir, 0, p, 0);
    }

    if (s.uturnT > 0)
    {
        // 宙返りの半分で上へ回って向きを変え、残りで半ひねりして天地を戻す
        float T = P[PUTurnTime];
        s.uturnT -= dt;
        float u = clampf(1.0f - s.uturnT / T, 0, 1);
        float a = clampf(u / 0.7f, 0, 1), b = clampf((u - 0.7f) / 0.3f, 0, 1);
        a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
        Q4 q0 = ldq(s.uturnQ0);
        q = qmul(q0, qmul(qaxis(v3(1, 0, 0), -Pi * a), qaxis(v3(0, 0, 1), Pi * b)));
        if (s.uturnT <= 0) s.uturnCd = P[PUTurnCool];
        s.pitchV = s.yawV = s.rollV = 0;
    }
    else
    {
        q = qmul(q, qaxis(v3(1, 0, 0), -s.pitchV * dt));
        q = qmul(q, qaxis(v3(0, 1, 0), s.yawV * dt));
        q = qmul(q, qaxis(v3(0, 0, 1), -s.rollV * dt));
    }
    q = qnorm(q);
    fwd = rot(q, v3(0, 0, 1)); right = rot(q, v3(1, 0, 0));

    // 速さ: 加速と減速は同じ溜めを使い、離して少し待つと戻る
    float target = P[PCruise];
    bool using_ = false;
    if (s.boost > 0 && s.uturnT <= 0)
    {
        if (in.boost) { target = P[PBoost]; using_ = true; }
        else if (in.brake) { target = P[PBrake]; using_ = true; }
    }
    if (using_)
    {
        s.boost -= P[PBoostDrain] * dt;
        if (s.boost < 0) s.boost = 0;
        s.boostWait = P[PBoostDelay];
    }
    else if (s.boostWait > 0) s.boostWait -= dt;
    else s.boost = clampf(s.boost + P[PBoostRegen] * dt, 0, P[PBoostMax]);
    s.speed = approach(s.speed, target, P[PSpeedResp], dt);

    V3 v = fwd * s.speed;
    if (s.dodgeT > 0)
    {
        float T = P[PDodgeTime];
        float u = 1.0f - s.dodgeT / T;
        float shape = sinf(u * Pi) * (Pi * 0.5f); // 横へずれる量の時間微分 (合計で PDodgeSide)
        v = v + right * ((float)s.dodgeDir * P[PDodgeSide] / T * shape);
        s.dodgeT -= dt;
    }
    // ぶつかって跳ね返る速さ (すぐ減る)
    V3 bump = ld(s.bumpV);
    v = v + bump;
    st(s.bumpV, bump * expf(-5.0f * dt));
    p = p + v * dt;
    if (w->city) CityShip(w, s, idx, p, q, dt);
    // 下限 (街の地面のすぐ上) を抜けない
    if (P[PFloorY] > -1e5f && p.y < P[PFloorY]) p.y = P[PFloorY];
    st(s.p, p); st(s.v, v); stq(s.q, q);

    // 溜め: 撃たずにいると少し待ってから溜まる。満ちた所で押した最初の 1 発が溜め撃ち (同じ砲口から太い弾)。
    // 溜まり切る前に撃つと溜めは捨てる (連射を続けるか、手を止めて一撃を待つか)
    s.fireCd = s.fireCd > 0 ? s.fireCd - dt : 0;
    bool press = in.fire && !s.firePrev;
    s.firePrev = in.fire;
    if (!in.fire && s.uturnT <= 0)
    {
        if (s.chargeWait > 0) s.chargeWait -= dt;
        else if (s.charge < 1)
        {
            s.charge = P[PChargeTime] > 0 ? clampf(s.charge + dt / P[PChargeTime], 0, 1) : 1;
            if (s.charge >= 1) s.chargeBurst = 1;
        }
    }
    if (s.charge > 0) FxCharge(w, s, p, q, v, s.chargeBurst != 0);
    s.chargeBurst = 0;
    // 撃つ: 砲口の印 (id 0 = 主砲) から全部同時に
    if (in.fire && s.fireCd <= 0 && s.uturnT <= 0)
    {
        bool big = press && s.charge >= 1;
        s.fireCd = big ? P[PChargeRecover] : P[PBoltInterval];
        s.charge = 0;
        s.chargeWait = P[PChargeDelay];
        float size = big ? P[PChargeScale] : 1.0f;
        if (big) Jolt(s, v3(-P[PKickCharge], 0, 0)); // 太い弾の反動で機首が跳ね上がる
        const Model& m = w->model[s.model];
        int shots = 0;
        // 照準の線の上に船があれば、その距離へ集める (無ければ PConverge)。翼の先の 2 門が遠くの的の左右へ外れないように
        float range = P[PBoltSpeed] * P[PBoltLife];
        float best = 1e30f;
        for (int k = 0; k < w->shipN; k++)
        {
            const Ship& o = w->ship[k];
            if (k == idx || !o.alive) continue;
            V3 rel = ld(o.p) - p;
            float along = dot(rel, fwd);
            if (along < 10 || along > range) continue;
            if (len(rel - fwd * along) < w->model[o.model].bound && along < best) best = along;
        }
        float conv = best < 1e29f ? best : P[PConverge];
        Xf xf[MaxParts];
        PartXfs(w, s, xf);
        s.firePulse = 1;
        int muzN = 0;
        for (int i = 0; i < m.mkN; i++) if (m.mk[i].kind == MkMuzzle && m.mk[i].id == 0) muzN++;
        // 溜め撃ちの威力は砲口の数で割る (砲口の多い船ほど強くならない)
        float dmg = big ? P[PChargeDamage] / (float)(muzN > 0 ? muzN : 1) : P[PBoltDamage];
        s.mzAge = 0; s.mzBig = big ? 1.0f : 0.0f; s.mzSeed = w->rng;
        for (int i = 0; i < m.mkN; i++)
        {
            const Marker& mk = m.mk[i];
            if (mk.kind != MkMuzzle || mk.id != 0) continue;
            // 砲口が動く部品 (翼) に付いていれば、その動きの後の位置から撃つ
            V3 lp = ld(mk.p), ld_ = ld(mk.d);
            int pi = m.mkPart[i];
            if (pi >= 0 && pi < m.partN) { lp = rot(xf[pi].q, lp * xf[pi].s) + xf[pi].t; ld_ = rot(xf[pi].q, ld_); }
            V3 mp = p + rot(q, lp);
            // 砲口は離れて付いているので、照準の距離 (PConverge) の 1 点へ向けて撃つ
            V3 dir = conv > 1 ? norm(p + fwd * conv - mp, fwd) : norm(rot(q, ld_), fwd);
            V3 bv = dir * P[PBoltSpeed] + v;
            SpawnBolt(w, idx, mp, bv, P[PBoltLife], dmg, 1, 0, size);
            Emit(w, EvFire, idx, i, big ? 1 : 0, mp, 0);
            FxMuzzle(w, s.team, mp, dir, v, big);
            // 砲口の閃光が周りの船体を照らす (砲口の少し前・船と一緒に動く。船体の中に置くと外板を裏から照らして効かない)
            float bc[3]; BoltColor(w, s.team, big, bc);
            V3 llp = lp + ld_ * (big ? 3.0f : 1.5f);
            AddLight(w, p + rot(q, llp), big ? 70.0f : 40.0f, bc[0] * 1.8f, bc[1] * 1.8f, bc[2] * 1.8f, big ? 0.2f : 0.09f, idx, llp, 1000 + idx * MaxMarkers + i);
            shots++;
        }
        if (shots == 0)
        {
            SpawnBolt(w, idx, p + fwd * 2.0f, fwd * P[PBoltSpeed] + v, P[PBoltLife], dmg, 1, 0, size);
            Emit(w, EvFire, idx, -1, big ? 1 : 0, p, 0);
        }
    }
}

// 遠くの船: now - 遅れ の時刻を補間 (位置は速さ付きのエルミート・向きは球面補間)。列が切れたら外挿
void PoseRemote(World* w, Ship& s, double now)
{
    if (s.snapN == 0) return;
    double t = now - w->prm[PInterpDelay];
    int n = s.snapN;
    const Snap* a = nullptr; const Snap* b = nullptr;
    for (int i = 0; i + 1 < n; i++)
        if (s.snap[i].t <= t && t <= s.snap[i + 1].t) { a = &s.snap[i]; b = &s.snap[i + 1]; break; }
    if (a)
    {
        float span = (float)(b->t - a->t);
        float u = span > 1e-5f ? (float)((t - a->t) / span) : 1.0f;
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
        V3 p = ld(a->p) * h00 + ld(a->v) * (h10 * span) + ld(b->p) * h01 + ld(b->v) * (h11 * span);
        st(s.p, p);
        st(s.v, lerp(ld(a->v), ld(b->v), u));
        stq(s.q, qslerp(ldq(a->q), ldq(b->q), u));
        return;
    }
    const Snap& last = s.snap[n - 1];
    if (t < s.snap[0].t)
    {
        st(s.p, ld(s.snap[0].p)); st(s.v, ld(s.snap[0].v)); stq(s.q, ldq(s.snap[0].q));
        return;
    }
    float ex = (float)(t - last.t);
    if (ex > w->prm[PExtrapMax]) ex = w->prm[PExtrapMax];
    st(s.p, ld(last.p) + ld(last.v) * ex);
    st(s.v, ld(last.v));
    stq(s.q, ldq(last.q));
}

// 線分 a→b と球の最初の交点の割合 (無ければ -1)
float SegSphere(V3 a, V3 b, V3 c, float r)
{
    V3 d = b - a, m = a - c;
    float A = dot(d, d);
    if (A < 1e-10f) return dot(m, m) <= r * r ? 0.0f : -1.0f;
    float B = dot(m, d), C = dot(m, m) - r * r;
    if (C <= 0) return 0.0f;
    float disc = B * B - A * C;
    if (disc < 0) return -1.0f;
    float t = (-B - sqrtf(disc)) / A;
    return (t >= 0 && t <= 1) ? t : -1.0f;
}

void StepBolts(World* w, int local, float dt)
{
    for (int i = 0; i < MaxBolts; i++)
    {
        Bolt& b = w->bolt[i];
        if (!b.alive) continue;
        V3 a = ld(b.p), e = a + ld(b.v) * dt;
        float best = 2.0f; int hitShip = -1, hitSector = -1;
        for (int k = 0; k < w->shipN && b.kind == 0; k++)
        {
            Ship& s = w->ship[k];
            if (!s.alive || k == b.owner) continue;
            const Model& m = w->model[s.model];
            V3 sp = ld(s.p);
            if (s.dodgeT > 0)
            {
                // 横転の回避の間は弾を弾く。敵の弾がすぐ横を抜けたら溜めが満ちる (1 回の回避で 1 度だけ)
                bool foe = b.owner < 0 || b.owner >= w->shipN || w->ship[b.owner].team != s.team;
                if (foe && !s.grazed && SegSphere(a, e, sp, m.bound + w->prm[PGrazeDist]) >= 0)
                {
                    s.grazed = 1;
                    s.charge = 1; s.chargeWait = 0; s.chargeBurst = 1;
                    V3 gp = lerp(a, e, 0.5f);
                    Emit(w, EvGraze, k, b.owner, 0, gp, 0);
                    FxGraze(w, gp, ld(b.v), ld(s.v));
                    if (k == local) w->shake += w->prm[PShakeHit];
                }
                continue;
            }
            float br = (b.size - 1.0f) * w->prm[PBoltWidth]; // 太い弾は当たりも太い
            if (SegSphere(a, e, sp, m.bound + br) < 0) continue;
            Q4 sq = ldq(s.q);
            for (int j = 0; j < m.sphN; j++)
            {
                V3 c = sp + rot(sq, ld(m.sph[j].c));
                float t = SegSphere(a, e, c, m.sph[j].r + br);
                if (t >= 0 && t < best) { best = t; hitShip = k; hitSector = m.sph[j].sector; }
            }
        }
        // ビル: 船より手前で当たれば弾はそこで止まり、当たった棟が削れる
        if (w->city)
        {
            int cb = -1; V3 cn;
            float br = (b.size - 1.0f) * w->prm[PBoltWidth];
            float t = CitySeg(w->city, a, e, br, &cb, &cn);
            if (t >= 0 && t < best)
            {
                V3 hp = lerp(a, e, t);
                FxHit(w, hp, ld(b.v));
                if (b.size > 1.5f) FxExplode(w, hp, 0.35f);
                int bld = w->city->box[cb].bld;
                if (bld >= 0 && b.kind == 0) Emit(w, EvBuilding, b.owner, bld, b.mine ? 0x10000 : 0, hp, b.dmg);
                b.alive = 0;
                continue;
            }
        }
        if (hitShip >= 0)
        {
            V3 hp = lerp(a, e, best);
            Emit(w, EvHit, b.owner, hitShip, hitSector | (b.mine ? 0x10000 : 0), hp, b.dmg);
            FxHit(w, hp, ld(b.v));
            if (b.size > 1.5f) FxExplode(w, hp, 0.35f);
            {
                // 当たった所と弾の向きから回りの揺れ (撃たれた船が当たった側から押される)
                Ship& hs = w->ship[hitShip];
                Q4 hq = ldq(hs.q), inv = { -hq.x, -hq.y, -hq.z, hq.w };
                float bd = w->model[hs.model].bound;
                V3 r = rot(inv, hp - ld(hs.p)) * (bd > 1e-3f ? 1.0f / bd : 1.0f);
                V3 f = rot(inv, norm(ld(b.v)));
                Jolt(hs, cross(r, f) * (w->prm[PJoltHit] * b.size));
                V3 kv = ld(hs.knockV) + norm(ld(b.v)) * (w->prm[PKnock] * b.size);
                st(hs.knockV, kv);
            }
            if (hitShip == local) w->shake += w->prm[PShakeHurt];
            else if (b.owner == local) w->shake += w->prm[PShakeHit];
            b.alive = 0;
            continue;
        }
        st(b.p, e);
        b.age += dt;
        if (b.age >= b.life)
        {
            b.alive = 0;
            if (b.kind == 1) FxRock(w, e, norm(ld(b.v)));
        }
    }
}

void Camera(World* w, int local, float dt)
{
    const float* P = w->prm;
    int idx = local >= 0 ? local : 0;
    if (idx >= w->shipN) return;
    Ship& s = w->ship[idx];
    Q4 q = ldq(s.q);
    V3 p = ld(s.drawP); // 描く位置 (刻みの余りを含む) に合わせないと船がカメラに対して揺れる
    V3 fwd = rot(q, v3(0, 0, 1)), up = rot(q, v3(0, 1, 0)), right = rot(q, v3(1, 0, 0));
    // カメラの台が向かう先。宙返りの間は始めの天地を保ったまま横へ回り込み (真上や真下を通らない)、
    // 終わりの向き (始めの逆向き・天地は同じ) へつなぐ。少し引いて輪の全体を見せる
    V3 tf = fwd, tu = up;
    float distK = 1.0f;
    if (s.uturnT > 0 && P[PUTurnTime] > 0)
    {
        float u = clampf(1.0f - s.uturnT / P[PUTurnTime], 0, 1);
        float e = u * u * (3 - 2 * u);
        Q4 q0 = ldq(s.uturnQ0);
        V3 u0 = rot(q0, v3(0, 1, 0));
        tf = rot(qaxis(u0, Pi * e), rot(q0, v3(0, 0, 1)));
        tu = u0;
        distK = 1.0f + P[PUTurnCamBack] * sinf(u * Pi);
    }
    V3 cf = ld(w->camF), cu = ld(w->camU);
    if (!w->camInit) { cf = tf; cu = tu; }
    else
    {
        // 台の向きは船の向きを遅れて追う (曲がると船の横顔と傾きが見える)
        float k = 1.0f - expf(-P[PCamTurn] * dt);
        cf = norm(lerp(cf, tf, k), tf);
        cu = norm(lerp(cu, tu, k), tu);
    }
    st(w->camF, cf); st(w->camU, cu);
    V3 want = p - cf * (P[PCamDist] * distK) + cu * (P[PCamHeight] * distK);
    V3 cp = ld(w->camP);
    if (!w->camInit) { cp = want; w->camInit = 1; w->camFov = P[PFov]; }
    else cp = lerp(cp, want, 1.0f - expf(-P[PCamLag] * dt));
    if (w->city)
    {
        V3 from = p + cu * 4.0f;
        float t = CitySeg(w->city, from, cp, 3.0f, nullptr, nullptr);
        if (t >= 0) cp = lerp(from, cp, fmaxf(t - 0.04f, 0.05f));
        if (P[PFloorY] > -1e5f && cp.y < P[PFloorY] - 10.0f) cp.y = P[PFloorY] - 10.0f;
    }
    // 曲がる向きへ注視点を少しずらす (狙う先を見せる)
    float steerX = s.sig[SgYaw], steerY = s.sig[SgPitch];
    V3 cr = norm(cross(cu, cf), right);
    V3 look = p + cf * P[PCamLook] + (cr * steerX + cu * steerY) * P[PCamSteer];
    Q4 cq = qlook(look - cp, cu);
    st(w->camP, cp);
    stq(w->camQ, cq);
    float fovT = P[PFov];
    if (s.boost > 0 && s.speed > P[PCruise] + 1) fovT += P[PFovBoost] * clampf((s.speed - P[PCruise]) / (P[PBoost] - P[PCruise] + 1e-3f), 0, 1);
    if (s.speed < P[PCruise] - 1) fovT -= P[PFovBoost] * 0.4f * clampf((P[PCruise] - s.speed) / (P[PCruise] - P[PBrake] + 1e-3f), 0, 1);
    w->camFov = approach(w->camFov, fovT, P[PFovResp], dt);
    w->shake *= expf(-P[PShakeDecay] * dt);
    if (w->shake > 3) w->shake = 3;
}

// 減衰のあるばね (半陰的)。姿勢を保つ噴射が機体の慣性を受け止める動き (既定の減衰は行き過ぎない値)
static inline void Spring(float& x, float& v, float target, float freq, float damp, float dt)
{
    v += ((target - x) * freq * freq - v * 2.0f * damp * freq) * dt;
    x += v * dt;
}

// 描く姿 (見た目だけ・当たりと位置は s.q のまま)。固定刻みごと
// dt = 0 は姿だけ今の向きへ合わせ直す (遠くの船で刻みが回らなかったフレーム)
void DrawPose(World* w, Ship& s, int idx, float dt)
{
    const float* P = w->prm;
    Q4 q = ldq(s.q);
    // 曲がる向きへ傾け、機首を引く向きへ少し寄せる。遠くの船も向きの差の信号から同じに動く
    float yawN = s.sig[SgYaw], pitchN = s.sig[SgPitch];
    Spring(s.bank, s.bankV, -yawN * P[PBankMax] * Deg, P[PBankResp], P[PBankDamp], dt);
    Spring(s.lean, s.leanV, pitchN * P[PPitchLean] * Deg, P[PBankResp], P[PBankDamp], dt);
    for (int k = 0; k < 3; k++)
    {
        Spring(s.jolt[k], s.joltV[k], 0, P[PJoltFreq], P[PJoltDamp], dt);
        Spring(s.knock[k], s.knockV[k], 0, P[PJoltFreq], P[PJoltDamp] > 1 ? P[PJoltDamp] : 1.0f, dt);
    }
    // 真っすぐの間も姿勢を保つ噴射の小さなぶれ (曲がっている間は消える)
    s.idleT += dt;
    float calm = 1.0f - clampf(fabsf(yawN) + fabsf(pitchN), 0, 1);
    float sw = P[PIdleSway] * Deg * calm, ph = (float)idx * 1.7f;
    float swayR = sw * (sinf(s.idleT * 1.13f + ph) * 0.7f + sinf(s.idleT * 2.71f + ph * 2.0f) * 0.3f);
    float swayP = sw * 0.6f * sinf(s.idleT * 0.87f + ph * 0.5f);
    // 横転の回避: 押した瞬間に一番速く回り、終わりへ向けて止める (ease-out)・機首を逃げる向きへ振る
    float spin = 0, dyaw = 0;
    if (s.dodgeT > 0 && P[PDodgeTime] > 0)
    {
        float u = clampf(1.0f - s.dodgeT / P[PDodgeTime], 0, 1);
        float e = 1.0f - powf(1.0f - u, P[PDodgeEase] > 1 ? P[PDodgeEase] : 1.0f);
        spin = -(float)s.dodgeDir * 2 * Pi * e;
        dyaw = (float)s.dodgeDir * P[PDodgeYaw] * Deg * sinf(u * Pi) * (1.0f - u * 0.5f);
    }
    Q4 d = qmul(qaxis(v3(0, 1, 0), dyaw + s.jolt[1]), qaxis(v3(1, 0, 0), -s.lean + swayP + s.jolt[0]));
    d = qmul(d, qaxis(v3(0, 0, 1), s.bank + spin + swayR + s.jolt[2]));
    stq(s.poseQ, qnorm(qmul(q, d)));
    stq(s.drawQ, ldq(s.poseQ));
}


void WriteBolts(World* w, FrameArgs* a, V3 cam)
{
    const float* P = w->prm;
    int n = 0;
    uint8_t* out = a->boltVerts;
    for (int i = 0; i < MaxBolts && n < a->boltCap; i++)
    {
        const Bolt& b = w->bolt[i];
        if (!b.alive) continue;
        V3 v = ld(b.v), p = ld(b.p) + v * w->acc;
        float sp = len(v);
        V3 dir = norm(v);
        float L = P[PBoltLen] * (b.size > 1 ? 0.5f + 0.5f * b.size : 1.0f);
        if (b.age * sp < L) L = b.age * sp + 0.2f;
        V3 tail = p - dir * L;
        V3 side = norm(cross(dir, cam - p), v3(1, 0, 0)) * (P[PBoltWidth] * b.size);
        const Ship& o = w->ship[b.owner < 0 ? 0 : b.owner];
        uint8_t col[4];
        if (b.kind == 1) { col[0] = 255; col[1] = 50; col[2] = 40; col[3] = 255; } // 本編の武器庫の砲台と同じ赤い光線
        else if (b.size > 1.5f) { col[0] = 225; col[1] = 255; col[2] = 245; col[3] = 255; } // 溜め撃ちは芯が白く見える明るさ
        else if (o.team == 0) { col[0] = 120; col[1] = 255; col[2] = 200; col[3] = 255; }
        else if (w->prm[PEnemyWarm] > 0.5f) { col[0] = 255; col[1] = 205; col[2] = 105; col[3] = 255; }
        else { col[0] = 255; col[1] = 70; col[2] = 120; col[3] = 255; }
        V3 vs[4] = { tail - side, tail + side, p + side, p - side };
        float uv[4][2] = { { 0, 0 }, { 0, 1 }, { 1, 1 }, { 1, 0 } };
        for (int k = 0; k < 4; k++)
        {
            uint8_t* o8 = out + (size_t)(n * 4 + k) * BoltStride;
            float f3[3] = { vs[k].x, vs[k].y, vs[k].z };
            memcpy(o8, f3, 12);
            memcpy(o8 + 12, col, 4);
            memcpy(o8 + 16, uv[k], 8);
        }
        n++;
    }
    // 前のフレームより減った分は面積 0 の四角にする
    for (int i = n; i < a->boltPrev && i < a->boltCap; i++)
        memset(out + (size_t)i * 4 * BoltStride, 0, 4 * BoltStride);
    a->boltN = n;
}
}

MRP_API World* mrp_flt_create(uint32_t seed)
{
    World* w = new (std::nothrow) World;
    if (!w) return nullptr;
    memset(w, 0, sizeof(World));
    w->rng = seed ? seed : 1u;
    w->camQ[3] = 1;
    for (int i = 0; i < MaxDust; i++)
        for (int k = 0; k < 3; k++) w->dust[i][k] = (rnd(w) - 0.5f) * 1000.0f;
    return w;
}

MRP_API void mrp_flt_destroy(World* w) { delete w; }

// ビルが崩れ始めた (c = 足元の中心・half = 足元の半分の幅 x/z・H = 高さ)。その棟の火元は消す
MRP_API void mrp_flt_ruin(World* w, const float* c, float hx, float hz, float H, int32_t bld)
{
    int k = 0;
    for (int i = 0; i < MaxRuins; i++) if (!w->ruin[i].alive) { k = i; break; } else if (w->ruin[i].age > w->ruin[k].age) k = i;
    Ruin& r = w->ruin[k];
    memcpy(r.c, c, 12); r.half[0] = hx; r.half[1] = hz; r.H = H; r.age = 0; r.alive = 1;
    r.fallT = sqrtf(2.0f * H / 60.0f) + 0.35f;
    for (int i = 0; i < MaxBurns; i++) if (w->burn[i].bld == bld) w->burn[i].alive = 0;
}

// 燃え始めた所 (p = 壁の上の点・n = 壁の外向き)。いっぱいなら古い火元を置き換える
MRP_API void mrp_flt_burn(World* w, const float* p, const float* n, int32_t bld)
{
    Burn& b = w->burn[w->burnNext];
    w->burnNext = (w->burnNext + 1) % MaxBurns;
    memcpy(b.p, p, 12); memcpy(b.n, n, 12); b.age = 0; b.bld = bld; b.alive = 1;
}

// 街の当たりを貸す (null = 外す)。街は場面を出るまで生きている
MRP_API void mrp_flt_city(World* w, const CityCol* c) { if (w) w->city = c; }

MRP_API void mrp_flt_view(World* w, const float* v6) { memcpy(w->view, v6, sizeof(float) * 6); }

// 船が落ちた所の爆発 (撃墜を決めた側が呼ぶ)
MRP_API void mrp_flt_explode(World* w, const float* p, float scale)
{
    FxExplode(w, ld(p), scale);
    w->shake += 1.2f;
}

MRP_API int32_t mrp_flt_size() { return (int32_t)sizeof(FrameArgs); }
MRP_API int32_t mrp_flt_part_size() { return (int32_t)sizeof(Part); }

MRP_API void mrp_flt_params(World* w, const float* v, int32_t n)
{
    if (n > MaxParams) n = MaxParams;
    memcpy(w->prm, v, sizeof(float) * n);
}

MRP_API int32_t mrp_flt_model(World* w, int32_t id, const Sphere* sph, int32_t n, const Marker* mk, int32_t m)
{
    if (id < 0 || id >= MaxModels) return 0;
    Model& md = w->model[id];
    md.sphN = n < MaxSpheres ? n : MaxSpheres;
    md.mkN = m < MaxMarkers ? m : MaxMarkers;
    memcpy(md.sph, sph, sizeof(Sphere) * md.sphN);
    memcpy(md.mk, mk, sizeof(Marker) * md.mkN);
    md.partN = 0;
    for (int i = 0; i < MaxMarkers; i++) md.mkPart[i] = -1;
    float b = 0;
    for (int i = 0; i < md.sphN; i++)
    {
        float d = len(ld(md.sph[i].c)) + md.sph[i].r;
        if (d > b) b = d;
    }
    md.bound = b;
    return md.sphN;
}

// 動く部品と、印が乗る部品 (mkPart は印の数だけ・無ければ null)。mrp_flt_model の後に呼ぶ
MRP_API int32_t mrp_flt_parts(World* w, int32_t id, const Part* parts, int32_t n, const int32_t* mkPart, int32_t mkN)
{
    if (id < 0 || id >= MaxModels) return 0;
    Model& md = w->model[id];
    md.partN = n < 0 ? 0 : (n < MaxParts ? n : MaxParts);
    if (md.partN > 0) memcpy(md.part, parts, sizeof(Part) * md.partN);
    for (int i = 0; i < MaxMarkers; i++) md.mkPart[i] = (mkPart && i < mkN) ? mkPart[i] : -1;
    return md.partN;
}

MRP_API void mrp_flt_spawn(World* w, int32_t idx, int32_t model, int32_t team, int32_t remote, const float* p, const float* q, float hp)
{
    if (idx < 0 || idx >= MaxShips) return;
    Ship& s = w->ship[idx];
    memset(&s, 0, sizeof(Ship));
    s.alive = 1; s.model = model; s.team = team; s.remote = remote;
    memcpy(s.p, p, 12); memcpy(s.q, q, 16);
    memcpy(s.drawP, p, 12); memcpy(s.drawQ, q, 16); memcpy(s.poseQ, q, 16);
    s.hp = s.hpMax = hp;
    s.speed = w->prm[PCruise];
    s.boost = w->prm[PBoostMax];
    s.mzAge = s.tzAge[0] = s.tzAge[1] = 9.0f; // 出た瞬間に砲口が光らない
    if (idx >= w->shipN) w->shipN = idx + 1;
}

MRP_API void mrp_flt_despawn(World* w, int32_t idx)
{
    if (idx < 0 || idx >= MaxShips) return;
    w->ship[idx].alive = 0;
}

MRP_API void mrp_flt_snap(World* w, int32_t idx, double t, const float* p, const float* v, const float* q)
{
    if (idx < 0 || idx >= MaxShips) return;
    Ship& s = w->ship[idx];
    if (s.snapN > 0 && t <= s.snap[s.snapN - 1].t) return; // 遅れて届いた古い状態は捨てる
    if (s.snapN == MaxSnaps)
    {
        memmove(&s.snap[0], &s.snap[1], sizeof(Snap) * (MaxSnaps - 1));
        s.snapN--;
    }
    Snap& sn = s.snap[s.snapN++];
    sn.t = t; memcpy(sn.p, p, 12); memcpy(sn.v, v, 12); memcpy(sn.q, q, 16);
}

MRP_API void mrp_flt_bolt(World* w, int32_t owner, const float* p, const float* v, float life, float dmg)
{
    SpawnBolt(w, owner, ld(p), ld(v), life, dmg, 0);
    // 遠くの船が撃った弾も砲口が光る (主砲は全部の砲口から同時に撃つので船ごとの時刻 1 つ)
    if (owner >= 0 && owner < MaxShips && w->ship[owner].alive && w->ship[owner].mzAge > 0.03f)
    {
        Ship& s = w->ship[owner];
        s.mzAge = 0; s.mzBig = 0; s.mzSeed = w->rng;
    }
}

// 船の中から決める部品の目標 (which = Aux の番号)
MRP_API void mrp_flt_aux(World* w, int32_t idx, int32_t which, float v)
{
    if (idx < 0 || idx >= MaxShips || which < 0 || which >= AuxCount) return;
    w->ship[idx].aux[which] = v;
}

// 印 (kind, id) の今の位置と向き (動く部品に乗っていればその動きの後)。無ければ 0
static int MarkerNow(World* w, const Ship& s, int kind, int id, V3& pos, V3& dir)
{
    const Model& m = w->model[s.model];
    for (int i = 0; i < m.mkN; i++)
    {
        const Marker& mk = m.mk[i];
        if (mk.kind != kind || mk.id != id) continue;
        V3 lp = ld(mk.p), ldv = ld(mk.d);
        int pi = m.mkPart[i];
        if (pi >= 0 && pi < m.partN)
        {
            Xf xf[MaxParts];
            PartXfs(w, s, xf);
            lp = rot(xf[pi].q, lp * xf[pi].s) + xf[pi].t;
            ldv = rot(xf[pi].q, ldv);
        }
        Q4 q = ldq(s.drawQ);
        pos = ld(s.drawP) + rot(q, lp);
        dir = norm(rot(q, ldv), rot(q, v3(0, 0, 1)));
        return 1;
    }
    return 0;
}

// 砲台の砲身 barrel から、砲口の向きへ小惑星を撃つ光線を出す (range 先で小惑星が砕ける)。砲身が後ろへ揺れる
MRP_API int32_t mrp_flt_turret(World* w, int32_t idx, int32_t barrel, float range)
{
    if (idx < 0 || idx >= MaxShips || barrel < 0 || barrel > 1) return 0;
    Ship& s = w->ship[idx];
    if (!s.alive) return 0;
    V3 p, d;
    if (!MarkerNow(w, s, MkTurret, barrel, p, d)) return 0;
    float sp = w->prm[PBoltSpeed] * 0.8f;
    if (sp < 1) sp = 1;
    float travel = range / sp;
    SpawnBolt(w, idx, p, d * sp + ld(s.v), travel, 0, 0, 1);
    s.shotPulse[barrel] = 1;
    s.tzAge[barrel] = 0;
    // 狙った小惑星 (光線が届くまで見えている大きな岩。消え始めが光線の届く時と重なるよう寿命を延ばし、残りは砕けた砂に隠れる)
    static const uint8_t rockA[4] = { 118, 106, 94, 252 }, rockB[4] = { 118, 106, 94, 252 };
    V3 at = p + (d * sp + ld(s.v)) * travel;
    float rs = rr(w, 3.6f, 5.0f);
    Emit(w, 2, at, rdir(w) * 0.8f, travel / 0.6f, rs, rs, 0, rockA, rockB);
    return 1;
}

// ゴミの口からゴミを捨てる
MRP_API int32_t mrp_flt_garbage(World* w, int32_t idx)
{
    if (idx < 0 || idx >= MaxShips) return 0;
    Ship& s = w->ship[idx];
    if (!s.alive) return 0;
    V3 p, d;
    if (!MarkerNow(w, s, MkHatch, 0, p, d)) return 0;
    FxGarbage(w, p, d, ld(s.v));
    return 1;
}

MRP_API void mrp_flt_hp(World* w, int32_t idx, float hp)
{
    if (idx < 0 || idx >= MaxShips) return;
    w->ship[idx].hp = hp;
}

// 船の今の状態 (送る用): 位置 3・速さ 3・四元数 4
MRP_API void mrp_flt_get(World* w, int32_t idx, float* out)
{
    if (idx < 0 || idx >= MaxShips) return;
    Ship& s = w->ship[idx];
    memcpy(out, s.p, 12); memcpy(out + 3, s.v, 12); memcpy(out + 6, s.q, 16);
}

MRP_API void mrp_flt_frame(FrameArgs* a)
{
    World* w = a->w;
    w->evN = 0;
    float dt = a->dt;
    if (dt > 0.1f) dt = 0.1f;
    if (dt < 0) dt = 0;
    if (w->prm[PTimeScale] > 0) dt *= w->prm[PTimeScale];
    w->acc += dt;
    int steps = 0;
    for (int k = 0; k < w->shipN; k++)
        if (w->ship[k].alive && w->ship[k].remote)
        {
            PoseRemote(w, w->ship[k], a->now);
            Signals(w, w->ship[k], dt);
        }
    if (a->in.dodge) w->latchDodge = a->in.dodge;
    if (a->in.uturn) w->latchUTurn = a->in.uturn;
    while (w->acc >= Step && steps < MaxSteps)
    {
        a->in.dodge = w->latchDodge; a->in.uturn = w->latchUTurn;
        w->acc -= Step;
        w->time += Step;
        for (int k = 0; k < w->shipN; k++)
        {
            Ship& s = w->ship[k];
            if (!s.alive || s.remote) continue;
            Input none = {};
            StepLocal(w, s, k, k == a->local ? a->in : none, Step);
        }
        StepBolts(w, a->local, Step);
        StepRuins(w, Step);
        StepBurns(w, Step);
        StepParticles(w, Step);
        for (int k = 0; k < w->shipN; k++)
        {
            Ship& s = w->ship[k];
            if (!s.alive) continue;
            DrawPose(w, s, k, Step);
            if (!s.remote) Signals(w, s, Step);
            StepParts(w, s, Step);
            StepExhaust(w, s, k, k == a->local, Step);
            StepRcs(w, s, Step);
            s.mzAge += Step; s.tzAge[0] += Step; s.tzAge[1] += Step;
        }
        for (int i = 0; i < MaxLights; i++)
        {
            FlashLight& L = w->light[i];
            if (L.alive && (L.age += Step) >= L.life) L.alive = 0;
        }
        steps++;
        // 押した瞬間の入力 (回避・宙返り) は 1 刻みで使い切る (刻みが 0 回のフレームでは次のフレームまで持つ)
        w->latchDodge = 0; w->latchUTurn = 0;
    }
    if (steps == MaxSteps) w->acc = 0;
    a->steps = steps;
    // 描く位置 = 最後の刻み + 刻みの余りの分だけ進めた所 (画面の更新が刻みと合わない時のがたつきを消す)
    for (int k = 0; k < w->shipN; k++)
    {
        Ship& s = w->ship[k];
        if (!s.alive) continue;
        if (steps == 0 && s.remote) DrawPose(w, s, k, 0);
        st(s.drawP, s.remote ? ld(s.p) : ld(s.p) + ld(s.v) * w->acc);
        Q4 pq = ldq(s.poseQ);
        bool posed = pq.x * pq.x + pq.y * pq.y + pq.z * pq.z + pq.w * pq.w > 0.5f; // 最初の刻みの前は未設定
        if (!s.remote && posed) stq(s.drawQ, pq);
        if (!s.remote && posed && s.uturnT <= 0 && w->acc > 0)
        {
            // 描く向きも刻みの余りの分だけ回して進める (画面の更新が刻みより速いと、遅れて追うカメラは滑らかで船の回りだけが段になる)
            Q4 q = ldq(s.q), dq = qmul({ -q.x, -q.y, -q.z, q.w }, ldq(s.poseQ));
            Q4 e = qmul(qmul(q, qaxis(v3(1, 0, 0), -s.pitchV * w->acc)), qaxis(v3(0, 1, 0), s.yawV * w->acc));
            e = qmul(e, qaxis(v3(0, 0, 1), -s.rollV * w->acc));
            stq(s.drawQ, qnorm(qmul(e, dq)));
        }
    }
    Camera(w, a->local, dt);
    // 被弾で押されたずれはカメラが追わない所で足す (カメラごと動くと押されたように見えない)
    for (int k = 0; k < w->shipN; k++)
        if (w->ship[k].alive) st(w->ship[k].drawP, ld(w->ship[k].drawP) + ld(w->ship[k].knock));

    for (int k = 0; k < MaxShips; k++)
    {
        float* o = a->shipOut + k * 8;
        const Ship& s = w->ship[k];
        memcpy(o, s.drawP, 12); memcpy(o + 3, s.drawQ, 16);
        o[7] = (k < w->shipN && s.alive) ? 1.0f : 0.0f;
    }
    if (a->partOut)
    {
        // 部品の動きをシェーダの配列の形 (部品ごとに 8 個) で書く。部品の無い所は動かない形
        for (int k = 0; k < PartShips; k++)
        {
            float* o = a->partOut + (size_t)k * MaxParts * 8;
            const Ship& s = w->ship[k];
            int pn = (k < w->shipN && s.alive) ? w->model[s.model].partN : 0;
            Xf xf[MaxParts];
            if (pn > 0) PartXfs(w, s, xf);
            for (int i = 0; i < MaxParts; i++, o += 8)
            {
                Xf x = i < pn ? xf[i] : Xf{ { 0, 0, 0, 1 }, { 0, 0, 0 }, 1.0f };
                o[0] = x.q.x; o[1] = x.q.y; o[2] = x.q.z; o[3] = x.q.w;
                o[4] = x.t.x; o[5] = x.t.y; o[6] = x.t.z; o[7] = x.s;
            }
        }
    }
    V3 cp = ld(w->camP);
    float sh = w->shake;
    V3 jit = v3((rnd(w) - 0.5f) * sh, (rnd(w) - 0.5f) * sh, 0);
    Q4 cq = ldq(w->camQ);
    V3 cpo = cp + rot(cq, jit);
    if (w->view[2] > 0 && a->local >= 0 && a->local < w->shipN)
    {
        // 確かめ用: 自分の船の周りの決めた角度から見る。板を向ける先も同じカメラにする (炎や粒が横から潰れない)
        const Ship& vs = w->ship[a->local];
        Q4 sq = ldq(vs.q); // 描く姿 (横転・傾き) に付けると回っても見えないので、当たりの向きに付ける
        V3 center = ld(vs.drawP) + rot(sq, v3(w->view[3], w->view[4], w->view[5]));
        Q4 rq = qmul(sq, qmul(qaxis(v3(0, 1, 0), w->view[0] * Deg), qaxis(v3(1, 0, 0), w->view[1] * Deg)));
        cpo = center + rot(rq, v3(0, 0, -w->view[2]));
        cq = qlook(center - cpo, rot(sq, v3(0, 1, 0)));
    }
    st(a->camOut, cpo);
    stq(a->camOut + 3, cq);
    a->camOut[7] = w->camFov;
    WriteBolts(w, a, cpo);
    {
        int li0 = a->local >= 0 && a->local < w->shipN ? a->local : 0;
        WriteFx(w, a, cpo, cq, ld(w->ship[li0].v));
    }
    if (a->lightOut) WriteLights(w, a->lightOut);

    int en = w->evN < a->evCap ? w->evN : a->evCap;
    memcpy(a->evOut, w->ev, sizeof(Event) * en);
    a->evN = en;

    int li = a->local >= 0 && a->local < w->shipN ? a->local : 0;
    const Ship& s = w->ship[li];
    a->hud[0] = s.speed; a->hud[1] = w->prm[PBoostMax] > 0 ? s.boost / w->prm[PBoostMax] : 0;
    a->hud[2] = s.hpMax > 0 ? s.hp / s.hpMax : 0;
    a->hud[3] = w->prm[PDodgeCool] > 0 ? s.dodgeCd / w->prm[PDodgeCool] : 0;
    a->hud[4] = w->prm[PUTurnCool] > 0 ? s.uturnCd / w->prm[PUTurnCool] : 0;
    a->hud[5] = sh;
    a->hud[6] = s.charge;
    a->hud[7] = (float)s.tailed;
    a->hud[8] = s.thr;
    // 姿勢制御の噴射の強さ = 噴いている口の値の大きい方 (音の大きさ)
    float rcs = 0;
    const Model& lm = w->model[s.model];
    for (int i = 0; i < lm.mkN; i++)
    {
        int pi = lm.mkPart[i];
        if (lm.mk[i].kind == MkRcs && pi >= 0 && pi < lm.partN && s.partV[pi] > rcs) rcs = s.partV[pi];
    }
    a->hud[9] = rcs;
}
