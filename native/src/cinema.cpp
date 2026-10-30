#include "mrp_native.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_JPEG
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#define STBI_NO_LINEAR
#define STBI_NO_HDR
#define STBI_NO_FAILURE_STRINGS
#include "third_party/stb_image.h"

// 事前に描いた全画面演出の 1 コマを組み立てる (作業スレッドから呼ぶ・Unity には触らない)。
// 背景の絵 (クルーの体と影を黒で描いた物・sRGB) に、体と影の重み (クルーの枠だけの 2ch の PNG) × 本人の色を線形の明るさで足す。
// クルーの塗りは平らな色で、光の足し算・閃光・周辺減光も線形なので、体と影の色を変えた絵は足し算で正しく出る。
// 出力は Unity の LoadRawTextureData にそのまま渡せる RGB24 (下の行から)

struct CineFrameArgs
{
    const uint8_t* base; int32_t baseLen;     // 背景の絵 (JPEG・幅 w × 高さ h)
    const uint8_t* weight; int32_t weightLen; // 体と影の重み (PNG の灰+透明 = 体・影)。無ければ null
    int32_t cropX, cropY;                     // 重みの絵の左上が背景の絵のどこか (画素・上から)
    float body[3], shadow[3];                 // 本人の色 (線形)
    float body2[3], shadow2[3];               // グラデーションの 2 色目 (線形・グラデーションでない時は 1 色目と同じ)
    float gx0, gy0, gsx, gsy;                 // 体の枠 (画素・左上) と 1/幅・1/高さ
    float gwx, gwy, gw0, gpos, gsoft;         // グラデーションの向きと境目 (本編の人形と同じ式・u, v は体の枠の左下 0 → 右上 1)
    int32_t negate;                           // 1 = 色を反転したコマ (体の色が明るいほど暗くなる → 重みを引く)
    uint8_t* out; int32_t outCap;             // 出力 RGB24 (w × h × 3)
    int32_t w, h;                             // 出力: 大きさ
    int32_t ok;                               // 出力: 1 = 組めた
};

static float S2L[256];
static uint8_t L2S[4097];
static bool LutReady;

static void InitLut()
{
    if (LutReady) return;
    for (int i = 0; i < 256; i++)
    {
        float c = i / 255.0f;
        S2L[i] = c <= 0.04045f ? c / 12.92f : powf((c + 0.055f) / 1.055f, 2.4f);
    }
    for (int i = 0; i <= 4096; i++)
    {
        float l = i / 4096.0f;
        float s = l <= 0.0031308f ? l * 12.92f : 1.055f * powf(l, 1.0f / 2.4f) - 0.055f;
        int v = (int)(s * 255.0f + 0.5f);
        L2S[i] = (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
    LutReady = true;
}

static inline uint8_t ToS(float l)
{
    if (l <= 0.0f) return 0;
    if (l >= 1.0f) return 255;
    return L2S[(int)(l * 4096.0f + 0.5f)];
}

MRP_API void mrp_cine_frame(CineFrameArgs* a)
{
    a->ok = 0;
    InitLut();
    int w, h, n;
    uint8_t* base = stbi_load_from_memory(a->base, a->baseLen, &w, &h, &n, 3);
    if (!base) return;
    a->w = w; a->h = h;
    if ((int64_t)w * h * 3 > a->outCap) { stbi_image_free(base); return; }
    // 下の行から並べ直す (Unity のテクスチャは左下が原点)
    for (int y = 0; y < h; y++) memcpy(a->out + (size_t)(h - 1 - y) * w * 3, base + (size_t)y * w * 3, (size_t)w * 3);
    stbi_image_free(base);
    if (a->weight && a->weightLen > 0)
    {
        int ww, wh;
        uint8_t* wt = stbi_load_from_memory(a->weight, a->weightLen, &ww, &wh, &n, 2);
        if (!wt) return;
        bool grad = a->gwx != 0.0f || a->gwy != 0.0f || a->gw0 != 0.0f;
        float soft = a->gsoft > 0.02f ? a->gsoft : 0.02f;
        for (int y = 0; y < wh; y++)
        {
            int sy = a->cropY + y;
            if (sy < 0 || sy >= h) continue;
            uint8_t* row = a->out + (size_t)(h - 1 - sy) * w * 3;
            const uint8_t* wr = wt + (size_t)y * ww * 2;
            float v = 1.0f - (sy + 0.5f - a->gy0) * a->gsy;
            for (int x = 0; x < ww; x++)
            {
                int sx = a->cropX + x;
                if (sx < 0 || sx >= w) continue;
                int kb = wr[x * 2], ks = wr[x * 2 + 1];
                if ((kb | ks) == 0) continue;
                float fb = kb / 255.0f, fs = ks / 255.0f;
                const float* b = a->body;
                const float* s = a->shadow;
                float bm[3], sm[3];
                if (grad)
                {
                    float u = (sx + 0.5f - a->gx0) * a->gsx;
                    float r = u * a->gwx + v * a->gwy + a->gw0;
                    float t = (r - a->gpos) / soft + 0.5f;
                    t = t < 0.0f ? 0.0f : t > 1.0f ? 1.0f : t;
                    for (int c = 0; c < 3; c++)
                    {
                        bm[c] = a->body[c] + (a->body2[c] - a->body[c]) * t;
                        sm[c] = a->shadow[c] + (a->shadow2[c] - a->shadow[c]) * t;
                    }
                    b = bm; s = sm;
                }
                uint8_t* px = row + (size_t)sx * 3;
                if (a->negate) { fb = -fb; fs = -fs; }
                for (int c = 0; c < 3; c++) px[c] = ToS(S2L[px[c]] + b[c] * fb + s[c] * fs);
            }
        }
        stbi_image_free(wt);
    }
    a->ok = 1;
}

// 入りと出の光 (黒の上に描いた加算の光の JPEG) を、普通の半透明の合成で近い見た目になる RGBA32 にする:
// 不透明度 = 明るい成分・色 = 光 / 不透明度 → 画面 = 光 + 下 × (1 − 不透明度)。暗い光の所は下がほとんど残る
struct CineLightArgs
{
    const uint8_t* img; int32_t imgLen;
    uint8_t* out; int32_t outCap;   // RGBA32 (下の行から)
    int32_t w, h, ok;
};

MRP_API void mrp_cine_light(CineLightArgs* a)
{
    a->ok = 0;
    int w, h, n;
    uint8_t* px = stbi_load_from_memory(a->img, a->imgLen, &w, &h, &n, 3);
    if (!px) return;
    a->w = w; a->h = h;
    if ((int64_t)w * h * 4 > a->outCap) { stbi_image_free(px); return; }
    for (int y = 0; y < h; y++)
    {
        const uint8_t* src = px + (size_t)y * w * 3;
        uint8_t* dst = a->out + (size_t)(h - 1 - y) * w * 4;
        for (int x = 0; x < w; x++)
        {
            int r = src[x * 3], g = src[x * 3 + 1], b = src[x * 3 + 2];
            int m = r > g ? (r > b ? r : b) : (g > b ? g : b);
            uint8_t* d = dst + x * 4;
            if (m == 0) { d[0] = d[1] = d[2] = d[3] = 0; continue; }
            d[0] = (uint8_t)(r * 255 / m); d[1] = (uint8_t)(g * 255 / m); d[2] = (uint8_t)(b * 255 / m); d[3] = (uint8_t)m;
        }
    }
    stbi_image_free(px);
    a->ok = 1;
}

// 1 チャンネルの PNG (コマの一部を切り抜いた物) を全画面の 1 バイトの面へ広げる。切り抜きの外は fill。
// 使い道 = 事前に描いたコマの不透明度・被害者より手前の物の割合 (Unity の R8 テクスチャへそのまま渡す・下の行から)
struct CinePlaneArgs
{
    const uint8_t* img; int32_t imgLen;   // PNG (灰 1ch)。無ければ null = 全部 fill
    int32_t cropX, cropY;                 // 切り抜きの左上が全画面のどこか (画素・上から)
    int32_t fill;                         // 切り抜きの外の値
    uint8_t* out; int32_t w, h;           // 出力 (w × h・下の行から)
    int32_t ok;
};

MRP_API void mrp_cine_plane(CinePlaneArgs* a)
{
    a->ok = 0;
    if (!a->out || a->w <= 0 || a->h <= 0) return;
    memset(a->out, a->fill & 0xFF, (size_t)a->w * a->h);
    if (a->img && a->imgLen > 0)
    {
        int pw, ph, n;
        uint8_t* px = stbi_load_from_memory(a->img, a->imgLen, &pw, &ph, &n, 1);
        if (!px) return;
        for (int y = 0; y < ph; y++)
        {
            int sy = a->cropY + y;
            if (sy < 0 || sy >= a->h) continue;
            int x0 = a->cropX < 0 ? -a->cropX : 0;
            int x1 = a->cropX + pw > a->w ? a->w - a->cropX : pw;
            if (x1 > x0) memcpy(a->out + (size_t)(a->h - 1 - sy) * a->w + a->cropX + x0, px + (size_t)y * pw + x0, (size_t)(x1 - x0));
        }
        stbi_image_free(px);
    }
    a->ok = 1;
}

// 着せ替えの絵の頂点を、立ち絵の空間の格子 (コマごとに画面のどこへ行くか) から双線形で写す。
// 飛ぶ時は、写した頂点の重心を中心に回転・拡大 (2x2) して中心を動かす (着せ替えはそれぞれ自分の中心で回る)
struct CineWarpArgs
{
    const int16_t* grid; int32_t gw, gh; // 格子の点の画面の位置 (x y を交互・× 1/16384・左上の点から行ごと・画面の左上 = 0 右下 = 1)
    float gx0, gy0, gstep;               // 格子の左上の点の立ち絵の座標と間隔 (画素)
    const float* in; int32_t n;          // 頂点の立ち絵の座標 (x y を交互)
    float fly[6];                        // 回転・拡大 (a b c d) と中心の移動 (dx dy)。飛ばない時は 1 0 0 1 0 0
    float sx, sy, ox, oy;                // 画面の座標 → 出力の座標 (x' = x × sx + ox)
    float* out; int32_t stride;          // 出力 (stride 個の float ごとに x y を書く・Vector3 なら 3)
};

MRP_API void mrp_cine_warp(CineWarpArgs* a)
{
    const float q = 1.0f / 16384.0f;
    float inv = 1.0f / a->gstep;
    int gw = a->gw, gh = a->gh;
    float cx = 0.0f, cy = 0.0f;
    float* tmp = (float*)malloc(sizeof(float) * 2 * (size_t)(a->n > 0 ? a->n : 1));
    for (int i = 0; i < a->n; i++)
    {
        float fx = (a->in[i * 2] - a->gx0) * inv, fy = (a->in[i * 2 + 1] - a->gy0) * inv;
        int ix = (int)floorf(fx), iy = (int)floorf(fy);
        ix = ix < 0 ? 0 : ix > gw - 2 ? gw - 2 : ix;
        iy = iy < 0 ? 0 : iy > gh - 2 ? gh - 2 : iy;
        float tx = fx - ix, ty = fy - iy;  // 格子の外は端の升を伸ばして外へ延ばす
        const int16_t* p00 = a->grid + ((size_t)iy * gw + ix) * 2;
        const int16_t* p10 = p00 + 2;
        const int16_t* p01 = p00 + (size_t)gw * 2;
        const int16_t* p11 = p01 + 2;
        for (int c = 0; c < 2; c++)
        {
            float top = p00[c] + (p10[c] - p00[c]) * tx;
            float bot = p01[c] + (p11[c] - p01[c]) * tx;
            tmp[i * 2 + c] = (top + (bot - top) * ty) * q;
        }
        cx += tmp[i * 2]; cy += tmp[i * 2 + 1];
    }
    if (a->n > 0) { cx /= a->n; cy /= a->n; }
    const float* f = a->fly;
    for (int i = 0; i < a->n; i++)
    {
        float dx = tmp[i * 2] - cx, dy = tmp[i * 2 + 1] - cy;
        float x = cx + f[0] * dx + f[1] * dy + f[4];
        float y = cy + f[2] * dx + f[3] * dy + f[5];
        float* o = a->out + (size_t)i * a->stride;
        o[0] = x * a->sx + a->ox;
        o[1] = y * a->sy + a->oy;
    }
    free(tmp);
}
