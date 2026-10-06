using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// マップごとに人の目で付けた注釈 (マップの絵に塗った範囲)。当たり判定の形からは分からないことを上書きする。
//   Keep = 壊れてほしくない物 (家具など)。家具の保護範囲に足す (見た目も当たり判定も残る)
//   Low / High = 周りより一段低い / 高い床。壁の両側で違えば高さの違う床の境として扱う
// 範囲は 1/8 単位の長方形の一覧 (MapNotesData.cs・画像から生成)
internal static partial class MapNotes
{
    private static ShipStatus _ship;
    private static Rect[] _keep = Array.Empty<Rect>();
    private static Rect[] _low = Array.Empty<Rect>();
    private static Rect[] _high = Array.Empty<Rect>();

    public static string Stats => $"notes keep={_keep.Length} low={_low.Length} high={_high.Length}";

    private static void Ensure()
    {
        var ship = ShipStatus.Instance;
        if (_ship == ship) return;
        _ship = ship;
        _keep = _low = _high = Array.Empty<Rect>();
        if (!ship) return;
        // 派生の型から先に調べる (どのマップも ShipStatus を継ぐ)
        if (ship.TryCast<AirshipStatus>() != null) Load(AirshipKeep, AirshipLow, AirshipHigh);
        else if (ship.TryCast<FungleShipStatus>() != null) Load(FungleKeep, FungleLow, FungleHigh);
        else if (ship.TryCast<PolusShipStatus>() != null) Load(PolusKeep, PolusLow, PolusHigh);
        else if (ship.TryCast<MiraShipStatus>() != null) Load(MiraKeep, MiraLow, MiraHigh);
        else if (ship.Type == ShipStatus.MapType.Ship && ship.transform.lossyScale.x > 0f) Load(SkeldKeep, SkeldLow, SkeldHigh); // 左右反転のスケルドは座標が合わないので使わない
    }

    private static void Load(float[] keep, float[] low, float[] high)
    {
        _keep = ToRects(keep);
        _low = ToRects(low);
        _high = ToRects(high);
    }

    private static Rect[] ToRects(float[] v)
    {
        var r = new Rect[v.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = Rect.MinMaxRect(v[i * 4], v[i * 4 + 1], v[i * 4 + 2], v[i * 4 + 3]);
        return r;
    }

    // 円 (中心 c・半径 r) に掛かる「壊れてほしくない物」の範囲を足す
    public static void AddKeep(Vector2 c, float r, List<Rect> list)
    {
        Ensure();
        foreach (var k in _keep)
        {
            float dx = Math.Max(Math.Max(k.xMin - c.x, 0f), c.x - k.xMax);
            float dy = Math.Max(Math.Max(k.yMin - c.y, 0f), c.y - k.yMax);
            if (dx * dx + dy * dy <= r * r) list.Add(k);
        }
    }

    public static bool InKeep(Vector2 p)
    {
        Ensure();
        foreach (var k in _keep) if (k.Contains(p)) return true;
        return false;
    }

    // その点の床の高さ (-1 = 一段低い・1 = 一段高い・0 = 塗っていない)
    public static int Level(Vector2 p)
    {
        Ensure();
        foreach (var k in _low) if (k.Contains(p)) return -1;
        foreach (var k in _high) if (k.Contains(p)) return 1;
        return 0;
    }
}
