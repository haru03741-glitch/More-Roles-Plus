using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 落ちる縁と段差の縁の値 (手すりの高さ・縁の上の物・段の向こうの升・1 刻みの落ちる量のまとめ) は、
// 縁のある升 (マップ全体で数百) だけに 4 向きぶんの枠を割り当てて持つ。升 → 枠の番号 + 1 (0 = 枠なし) を _eSlot に。
// 枠の番号は升の作り直しの順で変わるが、値は升と向きで引くので水の結果には出ない
internal static partial class WaterSim
{
    private static int[] _eSlot;
    private static int _slotN;
    private static readonly Stack<int> FreeSlots = new();

    private static void InitSlots(int n)
    {
        _eSlot = new int[n];
        _slotN = 0;
        FreeSlots.Clear();
        const int cap = 256 * 4;
        _lipH = new short[cap];
        _barB = new byte[cap];
        _barH = new short[cap];
        _stepTo = new int[cap];
        _fallAt = new int[cap];
    }

    private static void ClearSlots()
    {
        _eSlot = null;
        _slotN = 0;
        FreeSlots.Clear();
        _lipH = null; _barB = null; _barH = null; _stepTo = null; _fallAt = null;
    }

    // 縁 (k, d) の枠の位置 (枠のある升だけ)
    private static int EdgeKey(int k, int d) => (_eSlot[k] - 1) * 4 + d;

    // 升 k に枠を割り当てる (あればそのまま)
    private static void UseSlot(int k)
    {
        if (_eSlot[k] != 0) return;
        int s = FreeSlots.Count > 0 ? FreeSlots.Pop() : _slotN++;
        if (s * 4 + 4 > _barB.Length)
        {
            int cap = _barB.Length * 2;
            Array.Resize(ref _lipH, cap);
            Array.Resize(ref _barB, cap);
            Array.Resize(ref _barH, cap);
            Array.Resize(ref _stepTo, cap);
            Array.Resize(ref _fallAt, cap);
        }
        _eSlot[k] = s + 1;
    }

    // 升 k の枠を空けて中身を消す (縁の印が消えた時)
    private static void FreeSlot(int k)
    {
        int s = _eSlot[k] - 1;
        if (s < 0) return;
        for (int i = s * 4; i < s * 4 + 4; i++) { _lipH[i] = 0; _barB[i] = 0; _barH[i] = 0; _stepTo[i] = 0; _fallAt[i] = 0; }
        _eSlot[k] = 0;
        FreeSlots.Push(s);
    }
}
