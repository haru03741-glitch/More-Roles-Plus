using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の絵のタイルごとの「画素ごとの高さ」を重ねて作る。土台は床マスクの壁の面 (FloorMask.FaceHeights) で、その上に
// 低い壁の帯 (LowWalls) を刻印し、壊れた壁の画素 (DamageMap の穴) を外す。将来の物 (置いた壁など) も同じ所に層を足す。
// 画素の値は FaceHeights と同じ (床 = 254・面 = 高さ × FaceUnit・どちらでもない = 255)
internal static class WaterHeight
{
    private const byte HoleMin = 128;   // 穴の値がこれ以上の画素は壁の絵が抜けている (水を掛けない)

    // 低い壁の帯 (縁から Height まで) を壁の面として刻印する。刻印した画素があれば true
    internal static bool StampFace(byte[] into, int n, float x, float y, float size)
    {
        bool any = false;
        var all = LowWalls.All;
        float step = size / (n - 1), half = WaterSim.Cell * 0.5f;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.Stamp || e.Dir != 2) continue;
            float edge = e.Base, bx0 = e.X0 - half, bx1 = e.X1 + half, by1 = e.Top;
            if (bx1 <= x || bx0 >= x + size || by1 <= y || edge >= y + size) continue;
            int px0 = Math.Max(0, (int)MathF.Ceiling((bx0 - x) / step)), px1 = Math.Min(n - 1, (int)((bx1 - x) / step));
            int py0 = Math.Max(0, (int)MathF.Ceiling((edge - y) / step)), py1 = Math.Min(n - 1, (int)((by1 - y) / step));
            for (int py = py0; py <= py1; py++)
            {
                byte h = (byte)Math.Min(FloorMask.FaceTop, (int)((y + py * step - edge) * FloorMask.FaceUnit));
                for (int px = px0; px <= px1; px++) into[py * n + px] = h;
                any = true;
            }
        }
        return any;
    }

    // 床の切り抜き (255 = 床) から低い壁の帯を外す
    internal static void StampFloor(byte[] into, int n, float x, float y, float size)
    {
        var all = LowWalls.All;
        float step = size / (n - 1), half = WaterSim.Cell * 0.5f;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.Stamp || e.Dir != 2) continue;
            float edge = e.Base, bx0 = e.X0 - half, bx1 = e.X1 + half, by1 = e.Top;
            if (bx1 <= x || bx0 >= x + size || by1 <= y || edge >= y + size) continue;
            int px0 = Math.Max(0, (int)MathF.Ceiling((bx0 - x) / step)), px1 = Math.Min(n - 1, (int)((bx1 - x) / step));
            int py0 = Math.Max(0, (int)MathF.Ceiling((edge - y) / step)), py1 = Math.Min(n - 1, (int)((by1 - y) / step));
            for (int py = py0; py <= py1; py++)
            for (int px = px0; px <= px1; px++) into[py * n + px] = 0;
        }
    }

    // 壊れた壁の画素 (穴) は面でなくなる。穴の地図と面の画素は同じ密度 (16 画素/単位) なので画素ごとに引く
    internal static void CutHoles(byte[] face, int n, float x, float y, float size)
    {
        if (!DamageMap.HasHoles) return;
        float step = size / (n - 1);
        for (int py = 0; py < n; py++)
        {
            float wy = y + py * step;
            for (int px = 0; px < n; px++)
            {
                int j = py * n + px;
                if (face[j] > FloorMask.FaceTop) continue;
                if (DamageMap.HoleByteAt(x + px * step, wy) >= HoleMin) face[j] = 255;
            }
        }
    }
}
