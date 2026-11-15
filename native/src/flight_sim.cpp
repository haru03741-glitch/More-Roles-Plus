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

void SpawnBolt(World* w, int owner, V3 p, V3 v, float life, float dmg, int mine)
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
    b.age = 0; b.life = life; b.owner = owner; b.alive = 1; b.mine = mine; b.dmg = dmg;
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

// 弾が当たった所: 白い閃き → 当たった向きの逆へ跳ねる火花の筋 → 薄い煙
void FxHit(World* w, V3 p, V3 boltV)
{
    static const uint8_t flash0[4] = { 255, 250, 225, 0 }, flash1[4] = { 255, 160, 60, 0 };
    static const uint8_t spark0[4] = { 255, 225, 160, 0 }, spark1[4] = { 255, 90, 30, 0 };
    static const uint8_t smoke0[4] = { 60, 58, 62, 110 }, smoke1[4] = { 20, 20, 24, 0 };
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

// 火花・炎・煙と宇宙の塵の板を書く。丸い板の uv は 0〜1、筋の板は uv.x に 2 を足して形をシェーダへ伝える
void WriteFx(World* w, FrameArgs* a, V3 cam, Q4 camQ, V3 shipV)
{
    const float* P = w->prm;
    int n = 0;
    uint8_t* out = a->fxVerts;
    V3 cr = rot(camQ, v3(1, 0, 0)), cu = rot(camQ, v3(0, 1, 0));
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
        uint8_t col[4];
        for (int k = 0; k < 4; k++) col[k] = (uint8_t)((q.c0[k] + (q.c1[k] - q.c0[k]) * t) * fade);
        V3 p = ld(q.p) + ld(q.v) * w->acc;
        V3 vs[4];
        if (q.kind == 0)
        {
            V3 r = cr * size, u = cu * size;
            vs[0] = p - r - u; vs[1] = p + r - u; vs[2] = p + r + u; vs[3] = p - r + u;
            PutQuad(out, n++, vs, col, uvRound);
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
            uint8_t col[4] = { (uint8_t)(170 * al), (uint8_t)(190 * al), (uint8_t)(230 * al), 0 };
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
}

// 部品の値を信号へ寄せる (固定刻みごと)
void StepParts(World* w, Ship& s, float dt)
{
    s.firePulse *= expf(-10.0f * dt);
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

    s.pitchV = approach(s.pitchV, inPitch * P[PPitchRate] * Deg, P[PTurnResp], dt);
    s.yawV = approach(s.yawV, inYaw * P[PYawRate] * Deg, P[PTurnResp], dt);
    s.rollV = approach(s.rollV, inRoll * P[PRollRate] * Deg, P[PTurnResp], dt);

    s.uturnCd = s.uturnCd > 0 ? s.uturnCd - dt : 0;
    s.dodgeCd = s.dodgeCd > 0 ? s.dodgeCd - dt : 0;
    if (in.uturn && s.uturnT <= 0 && s.uturnCd <= 0 && s.dodgeT <= 0)
    {
        s.uturnT = P[PUTurnTime];
        stq(s.uturnQ0, q);
        Emit(w, EvUTurn, idx, 0, 0, p, 0);
    }
    if (in.dodge && s.dodgeT <= 0 && s.dodgeCd <= 0 && s.uturnT <= 0)
    {
        s.dodgeT = P[PDodgeTime];
        s.dodgeDir = in.dodge > 0 ? 1 : -1;
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
    p = p + v * dt;
    st(s.p, p); st(s.v, v); stq(s.q, q);

    // 撃つ: 砲口の印 (id 0 = 主砲) から全部同時に
    s.fireCd = s.fireCd > 0 ? s.fireCd - dt : 0;
    if (in.fire && s.fireCd <= 0 && s.uturnT <= 0)
    {
        s.fireCd = P[PBoltInterval];
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
            SpawnBolt(w, idx, mp, bv, P[PBoltLife], P[PBoltDamage], 1);
            Emit(w, EvFire, idx, i, 0, mp, 0);
            shots++;
        }
        if (shots == 0)
        {
            SpawnBolt(w, idx, p + fwd * 2.0f, fwd * P[PBoltSpeed] + v, P[PBoltLife], P[PBoltDamage], 1);
            Emit(w, EvFire, idx, -1, 0, p, 0);
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
        for (int k = 0; k < w->shipN; k++)
        {
            Ship& s = w->ship[k];
            if (!s.alive || k == b.owner) continue;
            if (s.dodgeT > 0) continue; // 横転の回避の間は弾を弾く
            const Model& m = w->model[s.model];
            V3 sp = ld(s.p);
            if (SegSphere(a, e, sp, m.bound) < 0) continue;
            Q4 sq = ldq(s.q);
            for (int j = 0; j < m.sphN; j++)
            {
                V3 c = sp + rot(sq, ld(m.sph[j].c));
                float t = SegSphere(a, e, c, m.sph[j].r);
                if (t >= 0 && t < best) { best = t; hitShip = k; hitSector = m.sph[j].sector; }
            }
        }
        if (hitShip >= 0)
        {
            V3 hp = lerp(a, e, best);
            Emit(w, EvHit, b.owner, hitShip, hitSector | (b.mine ? 0x10000 : 0), hp, b.dmg);
            FxHit(w, hp, ld(b.v));
            if (hitShip == local) w->shake += w->prm[PShakeHurt];
            else if (b.owner == local) w->shake += w->prm[PShakeHit];
            b.alive = 0;
            continue;
        }
        st(b.p, e);
        b.age += dt;
        if (b.age >= b.life) b.alive = 0;
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
    V3 want = p - fwd * P[PCamDist] + up * P[PCamHeight];
    V3 cp = ld(w->camP);
    if (!w->camInit) { cp = want; w->camInit = 1; w->camFov = P[PFov]; }
    else cp = lerp(cp, want, 1.0f - expf(-P[PCamLag] * dt));
    // 曲がる向きへ注視点を少しずらす (狙う先を見せる)
    float steerX = P[PYawRate] > 0 ? s.yawV / (P[PYawRate] * Deg) : 0;
    float steerY = P[PPitchRate] > 0 ? s.pitchV / (P[PPitchRate] * Deg) : 0;
    V3 look = p + fwd * P[PCamLook] + (right * steerX + up * steerY) * P[PCamSteer];
    Q4 cq = qlook(look - cp, up);
    st(w->camP, cp);
    stq(w->camQ, cq);
    float fovT = P[PFov];
    if (s.boost > 0 && s.speed > P[PCruise] + 1) fovT += P[PFovBoost] * clampf((s.speed - P[PCruise]) / (P[PBoost] - P[PCruise] + 1e-3f), 0, 1);
    if (s.speed < P[PCruise] - 1) fovT -= P[PFovBoost] * 0.4f * clampf((P[PCruise] - s.speed) / (P[PCruise] - P[PBrake] + 1e-3f), 0, 1);
    w->camFov = approach(w->camFov, fovT, P[PFovResp], dt);
    w->shake *= expf(-P[PShakeDecay] * dt);
    if (w->shake > 3) w->shake = 3;
}

void DrawPose(World* w, Ship& s)
{
    const float* P = w->prm;
    Q4 q = ldq(s.q);
    // 曲がる時に機体を傾ける (見た目だけ)・横転の回避は 1 回転
    float yawN = P[PYawRate] > 0 ? s.yawV / (P[PYawRate] * Deg) : 0;
    s.bank = approach(s.bank, -yawN * P[PBankMax] * Deg, P[PBankResp], Step);
    float spin = 0;
    if (s.dodgeT > 0)
    {
        float u = 1.0f - s.dodgeT / P[PDodgeTime];
        spin = -(float)s.dodgeDir * 2 * Pi * (u * u * (3 - 2 * u));
    }
    stq(s.drawQ, qnorm(qmul(q, qaxis(v3(0, 0, 1), s.bank + spin))));
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
        float L = P[PBoltLen];
        if (b.age * sp < L) L = b.age * sp + 0.2f;
        V3 tail = p - dir * L;
        V3 side = norm(cross(dir, cam - p), v3(1, 0, 0)) * P[PBoltWidth];
        const Ship& o = w->ship[b.owner < 0 ? 0 : b.owner];
        uint8_t col[4];
        if (o.team == 0) { col[0] = 120; col[1] = 255; col[2] = 200; col[3] = 255; }
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
    memcpy(s.drawP, p, 12); memcpy(s.drawQ, q, 16);
    s.hp = s.hpMax = hp;
    s.speed = w->prm[PCruise];
    s.boost = w->prm[PBoostMax];
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
        StepParticles(w, Step);
        for (int k = 0; k < w->shipN; k++)
        {
            Ship& s = w->ship[k];
            if (!s.alive) continue;
            DrawPose(w, s);
            if (!s.remote) Signals(w, s, Step);
            StepParts(w, s, Step);
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
        if (steps == 0 && s.remote) DrawPose(w, s);
        st(s.drawP, s.remote ? ld(s.p) : ld(s.p) + ld(s.v) * w->acc);
    }
    Camera(w, a->local, dt);

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
    st(a->camOut, cpo);
    stq(a->camOut + 3, cq);
    a->camOut[7] = w->camFov;
    WriteBolts(w, a, cpo);
    {
        int li0 = a->local >= 0 && a->local < w->shipN ? a->local : 0;
        WriteFx(w, a, cpo, cq, ld(w->ship[li0].v));
    }

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
}
