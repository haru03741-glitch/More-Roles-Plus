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

    // Polus の金属の柵 (絵の柱の高さ 0.2): 研究室の北の望遠鏡の台 (北の縁と左右の斜めの縁)、
    // 地震計の塔へ渡る 2 本の通路の両脇と、塔の丸い台の周り。柵の向こうは奈落なので、越えた水は落ちて消える。
    // 帯を壁の面として塗らない (柵の端で面が向こうの空と接し、細い筋が立って見える)。
    // 研究室の斜めの柵は奥へ向いているので、升の段の横向きの縁は柵にせず (越えた水が手前の壁の前に落ちて見える)、奥向きの縁からだけ越えさせる
    private const float PolusRail = 0.2f;
    private static readonly Entry[] Polus =
    {
        new(33.3f, -5.2f, 34.95f, -5f, 2, PolusRail, false, -5.27f), // 絵の柵の根元は当たり判定の線
        new(33f, -5.45f, 33.25f, -5.3f, 2, PolusRail),
        new(35f, -5.45f, 35.25f, -5.3f, 2, PolusRail),
        new(32.75f, -5.7f, 33f, -5.55f, 2, PolusRail),
        new(35.25f, -5.7f, 35.5f, -5.55f, 2, PolusRail),
        // 地震計 (西): 通路 x 4.3〜5.0・丸い台の上の縁 y -3.4
        new(5.05f, -7.2f, 5.45f, -3.5f, 0, PolusRail),
        new(3.8f, -7.2f, 4.45f, -3.5f, 1, PolusRail),
        new(3.8f, -3.95f, 5.45f, -3.5f, 2, PolusRail),
        new(3.8f, -4.45f, 5.45f, -4f, 3, PolusRail),
        // 地震計 (東): 通路 x 23.8〜24.5・丸い台の上の縁 y -2.4
        new(24.3f, -6.45f, 24.95f, -2.5f, 0, PolusRail),
        new(23.3f, -6.45f, 23.95f, -2.5f, 1, PolusRail),
        new(23.3f, -2.95f, 24.95f, -2.5f, 2, PolusRail),
        new(23.3f, -3.45f, 24.95f, -3f, 3, PolusRail),
    };

    internal static IReadOnlyList<Entry> All { get; private set; } = None;

    internal static void Load(bool mira, bool polus)
    {
        var src = mira ? Mira : polus ? Polus : None;
        All = src.Length != 0 && MapFlip.Flipped ? Mirrored(src) : src;
    }

    // 表は反転前の船の座標。左右反転の船では範囲を鏡に映し、左右の縁を入れ替える
    private static Entry[] Mirrored(Entry[] src)
    {
        var o = new Entry[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var e = src[i];
            int dir = e.Dir == 0 ? 1 : e.Dir == 1 ? 0 : e.Dir;
            o[i] = new Entry(MapFlip.Mirror(e.X1), e.Y0, MapFlip.Mirror(e.X0), e.Y1, dir, e.Height, e.Stamp, e.Base);
        }
        return o;
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
