using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壁の耐久値。壁の区間を世界座標の格子 (CellSize 四方) で区切り、格子ごとに残りの耐久を持つ。
// 切り取りで壁の部品 (EdgeCollider2D) が増えても、場所で数えるので数え直しにならない。
internal static class WallDurability
{
    public const int MaxHp = 3;
    private const float CellSize = 0.5f;
    private const float BoundaryShift = 1f / 512f; // 格子の点は p/0.5 = m/256。ずらすと (2m+1)/512 で整数に来ない

    private static readonly Dictionary<long, int> Hp = new();
    private static ShipStatus _ship;

    // 叩いた位置の耐久を削り、残りを返す (0 以下 = 抜ける)
    public static int Hit(Vector2 p, int damage)
    {
        Sync();
        long key = Key(p);
        int hp = Hp.TryGetValue(key, out int v) ? v : MaxHp;
        hp -= damage;
        Hp[key] = hp;
        TerrainDigest.Hp(key, hp);
        return hp;
    }

    // ホストが決めた耐久をそのまま書く (同期の受け手は自分で数えない)
    public static void Set(Vector2 p, int hp)
    {
        Sync();
        long key = Key(p);
        Hp[key] = hp;
        TerrainDigest.Hp(key, hp);
    }

    public static int Remaining(Vector2 p)
    {
        Sync();
        return Hp.TryGetValue(Key(p), out int v) ? v : MaxHp;
    }

    // 耐久の格子の番号 (同じ格子を叩いた打撃は同じ壁の所)
    internal static long CellKey(Vector2 p) => Key(p);
    internal static long CellKey(float x, float y) => Key(x, y);

    private static long Key(Vector2 p) => Key(p.x, p.y);

    private static long Key(float px, float py)
    {
        // 境目は 1/512 の格子 (蓋・壁の線を揃える格子) の点と点の間へずらす。格子に乗った線の上の点が境目に来ると、
        // PC と Android の浮動小数の末尾の差で別の格子に数えられ、耐久が端末ごとに割れる
        long x = (long)MathF.Floor(px / CellSize + BoundaryShift), y = (long)MathF.Floor(py / CellSize + BoundaryShift);
        return (x << 32) ^ (y & 0xffffffffL);
    }

    // マップが変わったら数え直す
    private static void Sync()
    {
        var ship = ShipStatus.Instance;
        if (_ship == ship) return;
        _ship = ship;
        Hp.Clear();
    }
}
