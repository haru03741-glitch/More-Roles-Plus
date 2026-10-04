using UnityEngine;

namespace MoreRolesPlus.Terrain;

internal enum DamageKind : byte
{
    Explosion = 1, // 爆発 (ロケットランチャーなど): 円の範囲の壁をまとめて抜き、外側の輪にはひびを入れる
    Blunt = 2,     // 打撃 (ハンマーなど): 叩いた壁 1 枚に耐久ダメージ。0 になった所だけ四角く抜ける
}

// 1 回の破壊。同期する時はこの値 (種類・位置・向き・強さ・乱数の種) だけを送れば全員が同じ結果を作れる
internal readonly struct DamageEvent
{
    public readonly DamageKind Kind;
    public readonly Vector2 Position;
    public readonly Vector2 Direction; // 打撃の向き (爆発では未使用)
    public readonly float Size;        // 爆発の半径
    public readonly ushort Seed;

    public DamageEvent(DamageKind kind, Vector2 position, Vector2 direction, float size, ushort seed)
    {
        Kind = kind;
        Position = position;
        Direction = direction;
        Size = size;
        Seed = seed;
    }
}

// 壊れ方の型。武器ごとの違いはここの値で表す
internal sealed class DamageProfile
{
    public int WallDamage;          // 当たった壁の耐久をいくつ削るか
    public float OuterRingScale;    // 爆発: この倍率の輪までは「抜けないがひびが入る」
    public bool Scorch;             // 焦げを付けるか
    public float BreachLength;      // 打撃: 抜ける壁の区間の長さ (世界単位)
    public float BreachDepth;       // 打撃: 壁の厚み方向にどこまで抜くか (部屋と部屋の隙間を含む)
    public float Reach;             // 打撃: 叩いた位置から壁を探す距離

    public static readonly DamageProfile Explosion = new()
    {
        WallDamage = 99,
        OuterRingScale = 1.6f,
        Scorch = true,
    };

    public static readonly DamageProfile Blunt = new()
    {
        WallDamage = 1,
        Scorch = false,
        BreachLength = 1.1f,
        BreachDepth = 1.1f,
        Reach = 1.2f,
    };

    public static DamageProfile Of(DamageKind kind) => kind == DamageKind.Explosion ? Explosion : Blunt;
}
