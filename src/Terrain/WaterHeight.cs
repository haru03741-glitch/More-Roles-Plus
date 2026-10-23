using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の絵のタイルごとの「画素ごとの高さ」を重ねて作る。土台は床マスクの壁の面 (FloorMask.FaceHeights) で、その上に
// 低い壁の帯 (LowWalls) を刻印し、壊れた壁の画素 (DamageMap の穴) を外す。将来の物 (置いた壁など) も同じ所に層を足す。
// 画素の値は FaceHeights と同じ (床 = 254・面 = 高さ × FaceUnit・どちらでもない = 255)
internal static class WaterHeight
{
    private const byte HoleMin = 128;   // 穴の値がこれ以上の画素は壁の絵が抜けている (水を掛けない)

    // 手すりの上に盛り上がった水を描ける高さ (根元から・単位)。越える水は手すりより上に水面があるので、帯の上の何も無い画素にも面を延ばす
    private const float OverMax = 1f;

    // 低い壁の帯 (縁から Height まで) を壁の面として刻印し、その上の何も無い画素 (向こうの空) へ OverMax まで面を延ばす。
    // 高さはどの画素も同じ根元 (縁) から測るので、帯の上の水面の線と隣の壁の水面の線が同じ深さで並ぶ。
    // 延ばす列は床の画素に当たったら止める (その先は向こうの床で、手前の水はそこまで)。刻印した画素があれば true
    internal static bool StampFace(byte[] into, int n, float x, float y, float size)
    {
        bool any = false;
        var all = LowWalls.All;
        float step = size / (n - 1), half = WaterSim.Cell * 0.5f;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.Stamp || e.Dir != 2) continue;
            float edge = e.Base, bx0 = e.X0 - half, bx1 = e.X1 + half, by1 = e.Top, byMax = edge + OverMax;
            if (bx1 <= x || bx0 >= x + size || byMax <= y || edge >= y + size) continue;
            int px0 = Math.Max(0, (int)MathF.Ceiling((bx0 - x) / step)), px1 = Math.Min(n - 1, (int)((bx1 - x) / step));
            int py0 = Math.Max(0, (int)MathF.Ceiling((edge - y) / step)), py1 = Math.Min(n - 1, (int)((by1 - y) / step));
            int pyMax = Math.Min(n - 1, (int)((byMax - y) / step));
            for (int py = py0; py <= py1; py++)
            {
                byte h = (byte)Math.Min(FloorMask.FaceTop, (int)((y + py * step - edge) * FloorMask.FaceUnit));
                for (int px = px0; px <= px1; px++) into[py * n + px] = h;
                any = true;
            }
            for (int px = px0; px <= px1; px++)
            for (int py = Math.Max(py0, py1 + 1); py <= pyMax; py++)
            {
                int j = py * n + px;
                if (into[j] == FloorMask.FaceFloor) break;
                into[j] = (byte)Math.Min(FloorMask.FaceTop, (int)((y + py * step - edge) * FloorMask.FaceUnit));
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
    // 答え合わせ (native verify): 穴を外すのを C# でも写しにやって比べる
    internal static long VerifyHoles, VerifyHoleDiff;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct HolesArgs
    {
        public byte* Face; public int N;
        public float X, Y, Size;
        public byte* Px; public int W, H;
        public float Ox, Oy, Ppu;
        public int HoleMin;
    }

    // C++ (CutHolesCs と同じ結果)
    internal static unsafe void CutHoles(byte[] face, int n, float x, float y, float size)
    {
        if (!DamageMap.HoleMap(out var hp, out int hw, out int hh, out float hox, out float hoy, out float hppu)) return;
        byte[] cs = Native.MrpNative.VerifyIn("WaterHeight") ? (byte[])face.Clone() : null;
        fixed (byte* f = face) fixed (byte* h = hp)
        {
            var a = new HolesArgs { Face = f, N = n, X = x, Y = y, Size = size, Px = h, W = hw, H = hh, Ox = hox, Oy = hoy, Ppu = hppu, HoleMin = HoleMin };
            Native.MrpNative.FaceCutHoles(&a);
        }
        if (cs != null)
        {
            CutHolesCs(cs, n, x, y, size);
            long diff = 0;
            for (int i = 0; i < cs.Length; i++) if (cs[i] != face[i]) diff++;
            VerifyHoles++;
            VerifyHoleDiff += diff;
        }
    }

    // C# 版 (照合用)
    private static void CutHolesCs(byte[] face, int n, float x, float y, float size)
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
