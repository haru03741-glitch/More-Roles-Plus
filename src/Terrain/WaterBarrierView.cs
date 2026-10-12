using System;
using MoreRolesPlus.Bridge;

namespace MoreRolesPlus.Terrain;

// 確認用 (water barrier): 止める絵の画素と、落ちる縁ごとの「止まる割合」を絵にする
internal static partial class WaterSim
{
    // 中心 (cx, cy) から半径 r の範囲を Screens/barrier.ppm へ。灰 = 水の升・黄 = 止める絵・
    // 落ちる縁 = 緑 (全部抜ける) 〜 赤 (全部止まる)・止まる高さが縁の手すり (LowWalls) の高さより高い縁は白い点
    private static string BarrierView(float cx, float cy, float r)
    {
        if (!_ready) return "ERR water barrier: no grid";
        if (!WaterBarrier.Active) return $"ERR water barrier: {WaterBarrier.Stats}";
        float ppu = WaterBarrier.Ppu;
        int w = (int)(2 * r * ppu), h = w;
        float x0 = cx - r, y0 = cy - r;
        var rgb = new byte[w * h * 3];
        for (int py = 0; py < h; py++)
        for (int px = 0; px < w; px++)
        {
            float wx = x0 + (px + 0.5f) / ppu, wy = y0 + (py + 0.5f) / ppu;
            int i = (py * w + px) * 3;
            if (WaterBarrier.At(wx, wy)) { rgb[i] = 255; rgb[i + 1] = 230; rgb[i + 2] = 40; continue; }
            int gx = (int)MathF.Floor((wx - _org.x) / _cell), gy = (int)MathF.Floor((wy - _org.y) / _cell);
            if (gx >= 0 && gy >= 0 && gx < _w && gy < _h && _open[gy * _w + gx] != 0) { rgb[i] = 70; rgb[i + 1] = 70; rgb[i + 2] = 80; }
        }
        int edges = 0, blockedAll = 0, partial = 0;
        int ax = Math.Max(1, (int)((x0 - _org.x) / _cell)), bx = Math.Min(_w - 2, (int)((x0 + 2 * r - _org.x) / _cell));
        int ay = Math.Max(1, (int)((y0 - _org.y) / _cell)), by = Math.Min(_h - 2, (int)((y0 + 2 * r - _org.y) / _cell));
        for (int y = ay; y <= by; y++)
        for (int x = ax; x <= bx; x++)
        {
            int k = y * _w + x;
            if (_fallMask[k] == 0) continue;
            for (int d = 0; d < 4; d++)
            {
                if ((_fallMask[k] >> d & 1) == 0) continue;
                float ex0 = _org.x + x * _cell, ey0 = _org.y + y * _cell, ex1 = ex0 + _cell, ey1 = ey0 + _cell;
                if (d == 0) ex0 = ex1; else if (d == 1) ex1 = ex0; else if (d == 2) ey0 = ey1; else ey1 = ey0;
                WaterBarrier.Edge(ex0, ey0, ex1, ey1, d, out int blocked, out float height);
                edges++;
                if (blocked >= 256) blockedAll++; else if (blocked > 0) partial++;
                byte cr = (byte)Math.Min(255, blocked), cg = (byte)(255 - Math.Min(255, blocked));
                int p0x = (int)((ex0 - x0) * ppu), p1x = (int)((ex1 - x0) * ppu), p0y = (int)((ey0 - y0) * ppu), p1y = (int)((ey1 - y0) * ppu);
                for (int py = Math.Max(0, p0y - 1); py <= Math.Min(h - 1, p1y + 1); py++)
                for (int px = Math.Max(0, p0x - 1); px <= Math.Min(w - 1, p1x + 1); px++)
                {
                    int i = (py * w + px) * 3;
                    rgb[i] = cr; rgb[i + 1] = cg; rgb[i + 2] = 0;
                }
                float lip = (_lipMask[k] >> d & 1) != 0 ? _lipH[EdgeKey(k, d)] / (float)Full : 0f;
                if (blocked > 0 && height > lip)
                {
                    int mx = (p0x + p1x) / 2, my = (p0y + p1y) / 2;
                    if (mx >= 0 && my >= 0 && mx < w && my < h) { int i = (my * w + mx) * 3; rgb[i] = rgb[i + 1] = rgb[i + 2] = 255; }
                }
            }
        }
        string path = System.IO.Path.Combine(TestBridge.ScreensDir, "barrier.ppm");
        TerrainReview.WriteRgb(path, rgb, w, h);
        // 全升の縁の止まる割合と高さ・手すりの高さ・段差の行き先の指紋 (端末どうしで同じになるか比べる)
        uint hash = 2166136261u;
        for (int i = 0; i < _eSlot.Length * 4; i++)
        {
            int e = _eSlot[i >> 2] == 0 ? -1 : EdgeKey(i >> 2, i & 3);
            hash = (hash ^ (e < 0 ? 0u : _barB[e]) ^ (uint)(e < 0 ? 0 : (ushort)_barH[e]) << 8) * 16777619u;
            if (e >= 0 && (_lipMask[i >> 2] >> (i & 3) & 1) != 0) hash = (hash ^ (uint)(ushort)_lipH[e]) * 16777619u;
            if (e >= 0 && (_stepMask[i >> 2] >> (i & 3) & 1) != 0) hash = (hash ^ (uint)_stepTo[e]) * 16777619u;
        }
        return $"OK water barrier {WaterBarrier.Stats} edges={edges} blocked={blockedAll} partial={partial} hash={hash:x8} -> {path}";
    }
}
