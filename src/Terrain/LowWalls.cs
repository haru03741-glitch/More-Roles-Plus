using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 低い壁 (ガラスの手すり・柵)。床の升の縁にあって、水はその高さまで溜まり、越えた分が向こうへこぼれる。
// 水の計算 (堰の高さ・落ちる先) と絵 (手すりの画素の高さ・越えた水の膜) が同じ表を見るので、高さの基準が 1 つになる。
// 範囲 (X0, Y0)-(X1, Y1) は「真ん中がこの中にある升」で、Dir はその升の縁の向き (0 右・1 左・2 奥・3 手前)。
// Height = 床からの高さ (水の深さと同じ物差し)。Stamp = 絵の上でその帯を壁の面として扱う (奥の縁で、向こうが空の不透明な手すり。
// 手前のガラスは向こうの床の水が透けて見えるので扱わない)。
internal static class LowWalls
{
    internal readonly struct Entry
    {
        public readonly float X0, Y0, X1, Y1, Height, Base;
        public readonly int Dir;
        public readonly bool Stamp;
        // Base = 絵の手すりの根元の y (升の縁と絵がずれている所だけ指定。既定は縁)
        public Entry(float x0, float y0, float x1, float y1, int dir, float height, bool stamp = false, float baseY = float.NaN)
        { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; Dir = dir; Height = height; Stamp = stamp; Base = float.IsNaN(baseY) ? (dir == 2 ? y1 : y0) : baseY; }

        // 絵の手すりの上の縁の y (越えた水の膜が出る高さ)
        public float Top => Base + Height;

    }

    private static readonly Entry[] None = System.Array.Empty<Entry>();

    // Mira のガラスの手すり (絵の帯の高さ 0.13): 発射台の南 (左右 2 区間・真ん中は通路)・発射台から東の廊下へ続く渡り廊下
    // (南・北・西の端・北の手すりの外の小さな出っ張り)・バルコニー (長い所の南・皿の区画の西・南・東)。
    // 網のフェンスは網目を水が抜けるので含めない (床の高さから落ちる)
    private const float MiraRail = 0.13f;
    private static readonly Entry[] Mira =
    {
        new(-11.5f, -0.5f, -6.25f, -0.25f, 3, MiraRail),
        new(-2.5f, -0.5f, -0.25f, -0.25f, 3, MiraRail),
        new(-6.25f, -2.75f, 3.75f, -2.5f, 3, MiraRail),
        new(-2.5f, -1f, 2.25f, -0.75f, 2, MiraRail, true),
        new(2.25f, -0.75f, 3.75f, -0.5f, 2, MiraRail, true, -0.75f), // 絵の手すりは西の区間と同じ高さに続く
        new(-6.25f, -2.75f, -6f, -0.75f, 1, MiraRail),
        new(20.5f, -2.75f, 29f, -2.5f, 3, MiraRail),
        new(17.75f, -3.75f, 18f, -2.25f, 1, MiraRail),
        new(17.75f, -3.75f, 20.5f, -3.5f, 3, MiraRail),
        new(20.25f, -3.75f, 20.5f, -2.75f, 0, MiraRail),
    };

    internal static IReadOnlyList<Entry> All { get; private set; } = None;

    internal static void Load(bool mira)
    {
        All = mira ? Mira : None;
    }

    internal static void Clear()
    {
        All = None;
    }

    // 升の真ん中 (px, py) の向き d の縁が低い壁か。あればその高さ
    internal static bool At(float px, float py, int d, out Entry entry)
    {
        var all = All;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e.Dir == d && px >= e.X0 && px <= e.X1 && py >= e.Y0 && py <= e.Y1) { entry = e; return true; }
        }
        entry = default;
        return false;
    }
}
