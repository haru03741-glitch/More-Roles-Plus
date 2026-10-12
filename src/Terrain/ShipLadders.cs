using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 船のはしごの上の端と下の端。はしごは動かないので船ごとに 1 回だけ読む
// (GetComponentsInChildren は呼ぶたびに船じゅうを探す。水の升を作る時に 3 か所で使う)
internal static class ShipLadders
{
    private static readonly List<(Vector3 Top, Vector3 Bottom)> Tops = new();
    private static int _gen = -1;
    private static IntPtr _ship;

    // 上の端のはしご (IsTop) と行き先の位置
    internal static List<(Vector3 Top, Vector3 Bottom)> Of(ShipStatus ship)
    {
        if (_gen == GameClock.ShipGen && ship.Pointer == _ship) return Tops;
        Tops.Clear();
        foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
            if (l && l.IsTop && l.Destination) Tops.Add((l.transform.position, l.Destination.transform.position));
        _gen = GameClock.ShipGen;
        _ship = ship.Pointer;
        return Tops;
    }
}
