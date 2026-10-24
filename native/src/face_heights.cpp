#include "mrp_native.h"
#include "digest.h"
#include "capture.h"

// 水のタイルの画素ごとの壁の面の高さ (src/Terrain/FloorMask.cs の FaceHeights と同じ結果) と、
// 壁の絵が抜けた所を面から外す (src/Terrain/WaterHeight.cs の CutHoles と同じ結果)。小数の式の順番は C# と同じにする

struct FaceHeightsArgs
{
    const uint8_t* bits; const uint8_t* face; int32_t w, h; // 床マスクと壁の面のマスク (1 ビット = 1 画素)
    float ox, oy, ppu;
    float x, y, size; int32_t n;                            // タイル (左下 x, y から size 四方を n×n 画素)
    uint8_t* into;                                          // 戻り: 高さ (FaceFloor = 床・255 = 面でない)
    int32_t any;                                            // 戻り: 面の画素があった
};

static const float FaceUnit = 64.0f, FaceMax = 253.0f / 64.0f;
static const uint8_t FaceTop = 253, FaceFloor = 254;

MRP_API void mrp_face_heights(FaceHeightsArgs* a)
{
    const int n = a->n, w = a->w, h = a->h;
    const float ox = a->ox, oy = a->oy, ppu = a->ppu, x = a->x, y = a->y;
    const float step = a->size / (float)(n - 1);
    const int scan = (int)(FaceMax * ppu) + 1;
    const int top = (int)((y + a->size - oy) * ppu);
    int any = 0;
    for (int px = 0; px < n; px++)
    {
        int mx = (int)((x + (float)px * step - ox) * ppu);
        int py = 0;
        if (mx < 0 || mx >= w)
        {
            for (; py < n; py++) a->into[py * n + px] = 255;
            continue;
        }
        // 下から上へ 1 行ずつ、いちばん近い床の行を覚えながら、タイルの画素の行に来たら高さを書く
        int last = (int)0x80000000;
        int my = (int)((y - oy) * ppu) - scan;
        if (my < 0) my = 0;
        int next = (int)((y - oy) * ppu);
        for (; my <= top && my < h && py < n; my++)
        {
            int k = my * w + mx;
            if ((a->bits[k >> 3] & (1 << (k & 7))) != 0) last = my;
            while (py < n && my == next)
            {
                uint8_t v;
                if (last == my) v = FaceFloor;
                else if ((a->face[k >> 3] & (1 << (k & 7))) != 0 && last != (int)0x80000000 && my - last < scan)
                {
                    int z = my - last; // last が無い時に引くと桁あふれするので、有る時だけ
                    float m = (float)z / ppu * FaceUnit;
                    v = (uint8_t)((float)FaceTop < m ? (float)FaceTop : m);
                }
                else v = 255;
                a->into[py * n + px] = v;
                if (v <= FaceTop) any = 1;
                py++;
                next = (int)((y + (float)py * step - oy) * ppu);
            }
        }
        for (; py < n; py++) a->into[py * n + px] = 255;
    }
    a->any = any;
}

struct FaceHolesArgs
{
    uint8_t* face; int32_t n;               // タイルの面の高さ (書き換える)
    float x, y, size;
    const uint8_t* px; int32_t w, h;         // 損傷マスク (RGBA・R = 穴)
    float ox, oy, ppu;
    int32_t holeMin;
};

MRP_API void mrp_face_cut_holes(FaceHolesArgs* a)
{
    const int n = a->n;
    const float step = a->size / (float)(n - 1);
    for (int py = 0; py < n; py++)
    {
        float wy = a->y + (float)py * step;
        for (int px = 0; px < n; px++)
        {
            int j = py * n + px;
            if (a->face[j] > FaceTop) continue;
            float wx = a->x + (float)px * step;
            int hx = (int)((wx - a->ox) * a->ppu), hy = (int)((wy - a->oy) * a->ppu);
            if (hx < 0 || hy < 0 || hx >= a->w || hy >= a->h) continue;
            if (a->px[(hy * a->w + hx) * 4] >= a->holeMin) a->face[j] = 255;
        }
    }
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_face_heights(FaceHeightsArgs* a)
{
    Dg d(DgFaceHeights);
    // 床と面のマスクは船全体で大きいので入力には入れない
    d.InV(a->w); d.InV(a->h); d.InV(a->ox); d.InV(a->oy); d.InV(a->ppu); d.InV(a->x); d.InV(a->y); d.InV(a->size); d.InV(a->n);
    Cap c(DgFaceHeights);
    int64_t bitsB = ((int64_t)a->w * a->h + 7) >> 3, nn = (int64_t)a->n * a->n;
    c.RawV(*a);
    c.Blob(a->bits, bitsB);
    c.Blob(a->face, bitsB);
    c.Blob(a->into, nn);
    mrp_face_heights(a);
    d.Out(a->into, (int64_t)a->n * a->n); d.OutV(a->any);
    d.Commit();
    c.Blob(a->into, nn);
    c.RawV(*a);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_face_heights と同じ)
void rp_face_heights(Rp& r)
{
    FaceHeightsArgs a, want;
    r.RawV(a);
    int64_t bitsB = ((int64_t)a.w * a.h + 7) >> 3, nn = (int64_t)a.n * a.n;
    int64_t lb, lf, li;
    a.bits = (const uint8_t*)r.Blob(&lb);
    a.face = (const uint8_t*)r.Blob(&lf);
    a.into = (uint8_t*)r.Blob(&li);
    if (!r.ok) return;
    if (lb != bitsB || lf != bitsB || li != nn) { r.Fail("face_heights: input blob sizes differ"); return; }
    mrp_face_heights(&a);
    int64_t wi;
    const void* into = r.Blob(&wi);
    r.RawV(want);
    if (!r.ok) return;
    if (wi != nn) { r.Fail("face_heights: output blob size differs"); return; }
    r.Same("into", a.into, into, nn);
    r.Same("any", &a.any, &want.any, 4);
}
static RpReg reg_face_heights(DgFaceHeights, rp_face_heights);
#endif

MRP_API void mrp_dg_face_cut_holes(FaceHolesArgs* a)
{
    Dg d(DgFaceCutHoles);
    int64_t n = (int64_t)a->n * a->n;
    d.InV(a->n); d.InV(a->x); d.InV(a->y); d.InV(a->size); d.InV(a->w); d.InV(a->h); d.InV(a->ox); d.InV(a->oy); d.InV(a->ppu); d.InV(a->holeMin);
    d.In(a->face, n);
    Cap c(DgFaceCutHoles);
    int64_t pxB = (int64_t)a->w * a->h * 4;
    c.RawV(*a);
    c.Blob(a->px, pxB);
    c.Blob(a->face, n);
    mrp_face_cut_holes(a);
    d.Out(a->face, n);
    d.Commit();
    c.Blob(a->face, n);
    c.End();
}

#if defined(MRP_REPLAY)
// 記録した呼び出しを食わせ直す (塊の順は上の mrp_dg_face_cut_holes と同じ)
void rp_face_cut_holes(Rp& r)
{
    FaceHolesArgs a;
    r.RawV(a);
    int64_t n = (int64_t)a.n * a.n, pxB = (int64_t)a.w * a.h * 4;
    int64_t lp, lf;
    a.px = (const uint8_t*)r.Blob(&lp);
    a.face = (uint8_t*)r.Blob(&lf);
    if (!r.ok) return;
    if (lp != pxB || lf != n) { r.Fail("face_cut_holes: input blob sizes differ"); return; }
    mrp_face_cut_holes(&a);
    int64_t wf;
    const void* face = r.Blob(&wf);
    if (!r.ok) return;
    if (wf != n) { r.Fail("face_cut_holes: output blob size differs"); return; }
    r.Same("face", a.face, face, n);
}
static RpReg reg_face_cut_holes(DgFaceCutHoles, rp_face_cut_holes);
#endif
