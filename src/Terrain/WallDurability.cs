using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壁の耐久値。壁の区間を世界座標の格子 (CellSize 四方) で区切り、格子ごとに残りの耐久を持つ。
// 切り取りで壁の部品 (EdgeCollider2D) が増えても、場所で数えるので数え直しにならない。
internal static class WallDurability
{
    public const int MaxHp = 3;
    private const float CellSize = 0.5f;

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
        return hp;
    }

    // ホストが決めた耐久をそのまま書く (同期の受け手は自分で数えない)
    public static void Set(Vector2 p, int hp)
    {
        Sync();
        Hp[Key(p)] = hp;
    }

    public static int Remaining(Vector2 p)
    {
        Sync();
        return Hp.TryGetValue(Key(p), out int v) ? v : MaxHp;
    }

    private static long Key(Vector2 p)
    {
        long x = Mathf.FloorToInt(p.x / CellSize), y = Mathf.FloorToInt(p.y / CellSize);
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
