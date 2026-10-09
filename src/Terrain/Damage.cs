using UnityEngine;

namespace MoreRolesPlus.Terrain;

public enum DamageKind : byte
{
    Explosion = 1, // 爆発 (ロケットランチャーなど): 円の範囲の壁をまとめて抜き、外側の輪にはひびを入れる
    Blunt = 2,     // 打撃 (ハンマーなど): 叩いた壁 1 枚に耐久ダメージ。0 になった所だけ四角く抜ける
    Push = 3,      // 押し (反動・衝撃波など): 壁は壊さない。起点から向きの先の扇形の中の水と小物を押す
}

// 1 回の破壊の依頼 (武器・役職から TerrainSync.Request へ渡す)。ホストが ResolvedDamage に決めて全員へ配る
internal readonly struct DamageEvent
{
    public readonly DamageKind Kind;
    public readonly Vector2 Position;
    public readonly Vector2 Direction; // 打撃 = 振った向き / 爆発 = 吹き出す向き (力 0 なら使わない)
    public readonly float Size;        // 爆発の半径 / 押しの届く長さ
    public readonly float Force;       // 0..1。打撃 = 振りの強さ / 爆発 = 向きへの偏り (0 = 全方位の円) / 押しの強さ
    public readonly ushort Seed;

    public DamageEvent(DamageKind kind, Vector2 position, Vector2 direction, float size, float force, ushort seed)
    {
        Kind = kind;
        Position = position;
        Direction = direction;
        Size = size;
        Force = force;
        Seed = seed;
    }
}

// ホストが決めた 1 回の破壊の結果。全員がこれを同じ順で適用する。
// 打撃は「どの壁に当たったか・耐久がいくつ残ったか」をホストが決めて配る (当たり判定や格子の境目の判断を
// 各端末でやり直すと、PC と Android の浮動小数の差で叩いた回数の数え方がずれるため)
internal readonly struct ResolvedDamage
{
    public readonly DamageKind Kind;
    public readonly Vector2 Position; // 爆発 = 爆心 / 打撃 = 壁に当たった点
    public readonly Vector2 Normal;   // 打撃: 叩いた面の法線 (爆発では未使用)
    public readonly Vector2 Direction; // 振った向き / 吹き出す向き (法線とは別。斜めに叩くと斜めに抜ける)
    public readonly float Force;      // 0..1
    public readonly float Size;       // 爆発 = 半径 / 打撃 = 壁の厚み (叩いた面から奥の面まで + 余白・法線方向)
    public readonly sbyte Hp;         // 打撃: 叩いた後の耐久 (0 以下 = 抜ける)
    public readonly ushort Seed;
    // 大きな瓦礫の止まる所 (ホストが自分で適用した時に決めて、同じ電文で配る。ホストが適用する時は使わない)
    public readonly RubbleLanding[] Landings;
    public readonly byte Actor;       // 壊した人の PlayerId (NoActor = 分からない・コマンド等)。手の色と自分の振りの見分けに使う
    public const byte NoActor = 255;
    public readonly ushort Tick;      // ホストが決めた時の試合の刻み (GameClock の下位 16 ビット)。水の計算を全員同じ刻みで始めるため

    public ResolvedDamage(DamageKind kind, Vector2 position, Vector2 normal, Vector2 direction, float force, float size, sbyte hp, ushort seed,
        RubbleLanding[] landings = null, byte actor = NoActor, ushort tick = 0)
    {
        Kind = kind;
        Position = position;
        Normal = normal;
        Direction = direction;
        Force = force;
        Size = size;
        Hp = hp;
        Seed = seed;
        Landings = landings;
        Actor = actor;
        Tick = tick;
    }

    public ResolvedDamage WithLandings(RubbleLanding[] landings) =>
        new(Kind, Position, Normal, Direction, Force, Size, Hp, Seed, landings, Actor, Tick);

    public ResolvedDamage WithActor(byte actor) =>
        new(Kind, Position, Normal, Direction, Force, Size, Hp, Seed, Landings, actor, Tick);

    public ResolvedDamage WithTick(ushort tick) =>
        new(Kind, Position, Normal, Direction, Force, Size, Hp, Seed, Landings, Actor, tick);
}

// 壊れ方の型。武器ごとの違いはここの値で表す
internal sealed class DamageProfile
{
    public int WallDamage;          // 当たった壁の耐久をいくつ削るか
    public int StrongDamage;        // 打撃: 力が StrongForce 以上の振りで削る耐久
    public float StrongForce;
    public float OuterRingScale;    // 爆発: この倍率の輪までは「抜けないがひびが入る」
    public bool Scorch;             // 焦げを付けるか
    public float BreachLength;      // 打撃: 抜ける壁の区間の長さ (世界単位)
    public float BreachDepth;       // 打撃: 壁の厚み方向にどこまで抜くか (部屋と部屋の隙間を含む)
    public float Reach;             // 打撃: 叩いた位置から壁を探す距離
    public float MaxSlantDeg;       // 打撃: 斜めに抜ける角度の上限 (面の法線から)。これより寝た振りは上限で止める
    public float ForceLength;       // 打撃: 力 1 で抜ける区間が何倍まで伸びるか (力 0.5 で BreachLength ちょうど・力 0 で 0.7 倍)
    public float ConeStretch;       // 爆発: 力 1 で向きの先へ半径の何倍まで伸びるか
    public float ConeShrink;        // 爆発: 力 1 で後ろ側の半径を何割縮めるか (偏った分だけ全体は小さく)
    public float CrackStretch;      // 爆発: 力 1 で割れ目が向きに沿って何倍まで伸びるか (から 1 を引いた値)
    public float CrackBias;         // 爆発: 力 1 で向きの先ほど大きく割れる割合

    public static readonly DamageProfile Explosion = new()
    {
        WallDamage = 99,
        OuterRingScale = 1.6f,
        Scorch = true,
        ConeStretch = 1.2f,
        ConeShrink = 0.3f,
        CrackStretch = 0.7f,
        CrackBias = 0.45f,
    };

    public static readonly DamageProfile Blunt = new()
    {
        WallDamage = 1,
        StrongDamage = 2,
        StrongForce = 0.8f,
        Scorch = false,
        BreachLength = 1.1f,
        BreachDepth = 1.1f,
        Reach = 1.2f,
        MaxSlantDeg = 50f,
        ForceLength = 1.6f,
    };

    public static DamageProfile Of(DamageKind kind) => kind == DamageKind.Explosion ? Explosion : Blunt;
}
