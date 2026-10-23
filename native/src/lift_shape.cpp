#include "mrp_native.h"
#include "digest.h"

// 家具の当たり判定の形を左右下上の幅だけ太らせる (src/Terrain/FurnitureLift.cs の MakeShape の後半と同じ結果)。
// 画素は、中の画素から見た向きごとの半径の楕円の中に入っていれば中。小数の式の順番は C# と同じにする

struct LiftShapeArgs
{
    const uint8_t* inside; int32_t n;   // 当たり判定の中 (n×n・左下原点)
    int32_t rx, ry;                     // 探す範囲 (画素)
    float padL, padR, padB, padT;       // 広げる幅 (単位)
    float sx, sy;                       // 1 画素の大きさ (単位)
    uint8_t* px;                        // 戻り: RGBA (中 = 白・外 = 0 のまま。0 で渡す)
    uint8_t* shapeIn;                   // 戻り: 中 = 1 (0 で渡す)
};

MRP_API void mrp_lift_shape(LiftShapeArgs* a)
{
    const int n = a->n, rx = a->rx, ry = a->ry;
    for (int y = 0; y < n; y++)
    for (int x = 0; x < n; x++)
    {
        bool hit = false;
        for (int dy = -ry; dy <= ry && !hit; dy++)
        {
            int yy = y + dy;
            if (yy < 0 || yy >= n) continue;
            for (int dx = -rx; dx <= rx; dx++)
            {
                int xx = x + dx;
                if (xx < 0 || xx >= n || a->inside[yy * n + xx] == 0) continue;
                // この画素が家具のどちら側か (中の画素が右 = dx > 0 なら、この画素は左へ広げた所)
                float px0 = dx > 0 ? a->padL : a->padR, py0 = dy > 0 ? a->padB : a->padT;
                float ex = dx == 0 ? 0.0f : (float)dx * a->sx / (px0 > 1e-4f ? px0 : 1e-4f);
                float ey = dy == 0 ? 0.0f : (float)dy * a->sy / (py0 > 1e-4f ? py0 : 1e-4f);
                if (ex * ex + ey * ey <= 1.0f) { hit = true; break; }
            }
        }
        if (!hit) continue;
        a->shapeIn[y * n + x] = 1;
        int i = (y * n + x) * 4;
        a->px[i] = 255; a->px[i + 1] = 255; a->px[i + 2] = 255; a->px[i + 3] = 255;
    }
}

// 指紋を取る版 (digest.h)
MRP_API void mrp_dg_lift_shape(LiftShapeArgs* a)
{
    Dg d(DgLiftShape);
    int64_t n = (int64_t)a->n * a->n;
    d.InV(a->n); d.InV(a->rx); d.InV(a->ry); d.InV(a->padL); d.InV(a->padR); d.InV(a->padB); d.InV(a->padT); d.InV(a->sx); d.InV(a->sy);
    d.In(a->inside, n);
    mrp_lift_shape(a);
    d.Out(a->px, n * 4); d.Out(a->shapeIn, n);
    d.Commit();
}
