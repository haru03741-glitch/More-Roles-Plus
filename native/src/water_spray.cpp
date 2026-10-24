#include "mrp_native.h"
#include "digest.h"
#include "capture.h"
#include <math.h>
#include <string.h>

// 噴き出し 1 枚の絵。
// 粒が 1 刻みで動いた線を、濃さ (Dens) と泡 (Foam) の足し込みとして描き、R = 濃さ・G = 泡の割合の RGBA へ詰める。
// 小数の式は書いた順に計算する。
// 消す・詰めるのは粒が触った範囲 (box) と前の刻みの範囲だけ (外の Dens/Foam は 0・out は r=g=b=0 a=255 のまま保たれている)

struct SprayArgs
{
    const int32_t* ox; const int32_t* oy; const int32_t* oh;   // 1 刻み前の位置と高さ
    const int32_t* px; const int32_t* py; const int32_t* ph;   // 今の位置と高さ
    const uint8_t* stuck;                                      // 壁に当たって止まった粒 (bool)
    int32_t count;
    float* dens; float* foam;                                  // n × n の置き場 (中で消す)
    uint8_t* out;                                              // n × n × 4
    int32_t n;
    float orgx, orgy, x0, y0;
    int32_t* box;                                              // 入 = 前の刻みの範囲・出 = 今の範囲 (x0 y0 x1 y1・空は x0 > x1)
};

static const float Ppu = 24.0f;
static const float Hit = 0.6f, Side = 0.2f, Corner = 0.06f;
static const int Unit = 1024, MouthH = 256;

static inline void add(SprayArgs* a, int i, float v, float foam)
{
    a->dens[i] += v;
    a->foam[i] += v * foam;
}

static inline void splat(SprayArgs* a, int* bb, int x, int y, float w, float foam)
{
    int n = a->n;
    if (x < 1 || y < 1 || x >= n - 1 || y >= n - 1) return;
    if (x - 1 < bb[0]) bb[0] = x - 1;
    if (y - 1 < bb[1]) bb[1] = y - 1;
    if (x + 1 > bb[2]) bb[2] = x + 1;
    if (y + 1 > bb[3]) bb[3] = y + 1;
    int c = y * n + x;
    add(a, c, Hit * w, foam);
    add(a, c - 1, Side * w, foam); add(a, c + 1, Side * w, foam); add(a, c - n, Side * w, foam); add(a, c + n, Side * w, foam);
    add(a, c - n - 1, Corner * w, foam); add(a, c - n + 1, Corner * w, foam); add(a, c + n - 1, Corner * w, foam); add(a, c + n + 1, Corner * w, foam);
}

MRP_API void mrp_water_spray(SprayArgs* a)
{
    int n = a->n;
    int* box = a->box;
    for (int y = box[1]; y <= box[3]; y++)
    {
        memset(a->dens + y * n + box[0], 0, sizeof(float) * (box[2] - box[0] + 1));
        memset(a->foam + y * n + box[0], 0, sizeof(float) * (box[2] - box[0] + 1));
    }
    int bb[4] = { n, n, -1, -1 };
    const float inv = 1.0f / Unit;
    for (int p = 0; p < a->count; p++)
    {
        float ax = (a->orgx + (float)a->ox[p] * inv - a->x0) * Ppu, ay = (a->orgy + (float)(a->oy[p] + a->oh[p]) * inv - a->y0) * Ppu;
        int hph = a->ph[p] > 0 ? a->ph[p] : 0;
        float bx = (a->orgx + (float)a->px[p] * inv - a->x0) * Ppu, by = (a->orgy + (float)(a->py[p] + hph) * inv - a->y0) * Ppu;
        if ((ax < 0 && bx < 0) || (ay < 0 && by < 0) || (ax >= n && bx >= n) || (ay >= n && by >= n)) continue;
        float h = (float)a->ph[p] * (1.0f / MouthH);
        float foam = a->stuck[p] ? 0.9f : h > 0.9f ? 0.45f : h < 0.15f ? 0.3f : 0.03f;
        float dx = bx - ax, dy = by - ay;
        int steps = (int)ceilf(sqrtf(dx * dx + dy * dy));
        if (steps < 1) steps = 1;
        float w = 1.0f / steps * (float)(steps < 3 ? steps : 3);
        for (int k = 0; k <= steps; k++)
        {
            float f = (float)k / steps;
            splat(a, bb, (int)(ax + dx * f), (int)(ay + dy * f), w, foam);
        }
    }
    // 前の範囲と今の範囲を覆う四角を詰め直す (前の刻みだけ描いた所を 0 へ戻す)
    int u[4] = { bb[0], bb[1], bb[2], bb[3] };
    if (box[0] <= box[2])
    {
        if (box[0] < u[0]) u[0] = box[0];
        if (box[1] < u[1]) u[1] = box[1];
        if (box[2] > u[2]) u[2] = box[2];
        if (box[3] > u[3]) u[3] = box[3];
    }
    int ux0 = u[0], uy0 = u[1], ux1 = u[2], uy1 = u[3];
    for (int y = uy0; y <= uy1; y++)
    for (int x = ux0; x <= ux1; x++)
    {
        int i = y * n + x;
        float d = a->dens[i];
        uint8_t* o = a->out + i * 4;
        if (d > 0.0f)
        {
            float r = d * 255.0f;
            o[0] = (uint8_t)(r < 255.0f ? r : 255.0f);
            float g = a->foam[i] / d * 255.0f;
            o[1] = (uint8_t)(g < 255.0f ? g : 255.0f);
        }
        else { o[0] = 0; o[1] = 0; }
    }
    box[0] = bb[0]; box[1] = bb[1]; box[2] = bb[2]; box[3] = bb[3];
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_water_spray(SprayArgs* a)
{
    Dg d(DgWaterSpray);
    int64_t c = a->count, nn = (int64_t)a->n * a->n;
    d.InV(a->count); d.InV(a->n); d.InV(a->orgx); d.InV(a->orgy); d.InV(a->x0); d.InV(a->y0);
    d.In(a->ox, c * 4); d.In(a->oy, c * 4); d.In(a->oh, c * 4); d.In(a->px, c * 4); d.In(a->py, c * 4); d.In(a->ph, c * 4); d.In(a->stuck, c);
    d.In(a->box, 16); d.In(a->dens, nn * 4); d.In(a->foam, nn * 4); d.In(a->out, nn * 4);
    Cap cp(DgWaterSpray);
    cp.RawV(*a);
    cp.Blob(a->ox, c * 4); cp.Blob(a->oy, c * 4); cp.Blob(a->oh, c * 4);
    cp.Blob(a->px, c * 4); cp.Blob(a->py, c * 4); cp.Blob(a->ph, c * 4);
    cp.Blob(a->stuck, c);
    cp.Blob(a->box, 16); cp.Blob(a->dens, nn * 4); cp.Blob(a->foam, nn * 4); cp.Blob(a->out, nn * 4);
    mrp_water_spray(a);
    d.Out(a->box, 16); d.Out(a->dens, nn * 4); d.Out(a->foam, nn * 4); d.Out(a->out, nn * 4);
    d.Commit();
    cp.Blob(a->box, 16); cp.Blob(a->dens, nn * 4); cp.Blob(a->foam, nn * 4); cp.Blob(a->out, nn * 4);
    cp.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_water_spray と同じ)
void rp_water_spray(Rp& r)
{
    SprayArgs a;
    r.RawV(a);
    int64_t c = a.count, nn = (int64_t)a.n * a.n;
    int64_t l[11];
    a.ox = (const int32_t*)r.Blob(&l[0]); a.oy = (const int32_t*)r.Blob(&l[1]); a.oh = (const int32_t*)r.Blob(&l[2]);
    a.px = (const int32_t*)r.Blob(&l[3]); a.py = (const int32_t*)r.Blob(&l[4]); a.ph = (const int32_t*)r.Blob(&l[5]);
    a.stuck = (const uint8_t*)r.Blob(&l[6]);
    a.box = (int32_t*)r.Blob(&l[7]); a.dens = (float*)r.Blob(&l[8]); a.foam = (float*)r.Blob(&l[9]); a.out = (uint8_t*)r.Blob(&l[10]);
    if (!r.ok) return;
    const int64_t size[11] = { c * 4, c * 4, c * 4, c * 4, c * 4, c * 4, c, 16, nn * 4, nn * 4, nn * 4 };
    for (int k = 0; k < 11; k++)
        if (l[k] != size[k]) { r.Fail("water_spray: input blob %d is %lld bytes, want %lld", k, (long long)l[k], (long long)size[k]); return; }
    mrp_water_spray(&a);
    const char* names[4] = { "box", "dens", "foam", "out" };
    const void* got[4] = { a.box, a.dens, a.foam, a.out };
    for (int k = 0; k < 4; k++)
    {
        int64_t pl;
        const void* post = r.Blob(&pl);
        if (!r.ok) return;
        if (pl != size[7 + k]) { r.Fail("water_spray: output blob %s size differs", names[k]); return; }
        r.Same(names[k], got[k], post, pl);
    }
}
static RpReg reg_water_spray(DgWaterSpray, rp_water_spray);
#endif
