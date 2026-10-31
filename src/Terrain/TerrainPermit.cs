using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 地形をどう壊してよいか (役職の能力ごと)。役職は RoleBase.TerrainPermit を上書きして返す:
//   public override TerrainPermit TerrainPermit => TerrainPermit.None.WithHammer(cooldown: 1f);
// ホストが客の依頼を確かめる時にも呼ぶので、設定値だけから決める (その端末の状態を見ない)。
// 待ち時間は秒 (0 = 待たない)。半径は爆発の大きさの上限。
public readonly struct TerrainPermit
{
    public const float MaxRadius = 3f;

    public readonly bool Hammer;
    public readonly float HammerCooldown;
    public readonly float BlastRadius; // 0 = 撃てない (その場で爆発させる: ロケットなど)
    public readonly float BlastCooldown;
    public readonly float BombRadius;  // 0 = 置けない (導火線の後に爆発する爆弾)
    public readonly float BombCooldown;
    public readonly float FireRadius;  // 0 = 火を付けられない・油をまけない
    public readonly float FireCooldown;
    public readonly bool Water;        // 放水 (足元から水を噴き出す)
    public readonly float WaterCooldown;
    public readonly float WallLength;  // 0 = 置けない (仕掛け壁の長さの上限)
    public readonly float WallCooldown;
    public readonly bool WallPush;     // 人に掛かる所にも立て、掛かった人は押し出される (離れた所から立てる役職)
    public readonly float HitReach;    // 打撃を自分からこの距離まで離れた所へ依頼できる (弾で壁を削る役職)。0 = 手元 (ハンマー) だけ

    private TerrainPermit(bool hammer, float hammerCd, float blast, float blastCd, float bomb, float bombCd, float fire = 0f, float fireCd = 0f, bool water = false, float waterCd = 0f, float wall = 0f, float wallCd = 0f, bool wallPush = false, float hitReach = 0f)
    {
        Hammer = hammer;
        HammerCooldown = hammerCd;
        BlastRadius = Math.Clamp(blast, 0f, MaxRadius);
        BlastCooldown = blastCd;
        BombRadius = Math.Clamp(bomb, 0f, MaxRadius);
        BombCooldown = bombCd;
        FireRadius = Math.Clamp(fire, 0f, MaxFireRadius);
        FireCooldown = fireCd;
        Water = water;
        WaterCooldown = waterCd;
        WallLength = Math.Clamp(wall, 0f, MaxWallLength);
        WallCooldown = wallCd;
        WallPush = wallPush;
        HitReach = Math.Clamp(hitReach, 0f, MaxHitReach);
    }

    public const float MaxFireRadius = 2f;
    public const float MaxWallLength = 4f;
    public const float MaxHitReach = 30f;

    public static readonly TerrainPermit None = default;

    // 練習・フリープレイ: 何でも使える (待ち時間はボタンと各武器の決まりに任せる)
    public static readonly TerrainPermit All = new(true, 0f, MaxRadius, 0f, MaxRadius, 0f, MaxFireRadius, 0f, true, 0f, MaxWallLength, 0f, true, MaxHitReach);

    public TerrainPermit WithHammer(float cooldown = 0f) => new(true, cooldown, BlastRadius, BlastCooldown, BombRadius, BombCooldown, FireRadius, FireCooldown, Water, WaterCooldown, WallLength, WallCooldown, WallPush, HitReach);
    public TerrainPermit WithBlast(float radius, float cooldown = 0f) => new(Hammer, HammerCooldown, radius, cooldown, BombRadius, BombCooldown, FireRadius, FireCooldown, Water, WaterCooldown, WallLength, WallCooldown, WallPush, HitReach);
    public TerrainPermit WithBomb(float radius, float cooldown = 0f) => new(Hammer, HammerCooldown, BlastRadius, BlastCooldown, radius, cooldown, FireRadius, FireCooldown, Water, WaterCooldown, WallLength, WallCooldown, WallPush, HitReach);
    public TerrainPermit WithFire(float radius, float cooldown = 0f) => new(Hammer, HammerCooldown, BlastRadius, BlastCooldown, BombRadius, BombCooldown, radius, cooldown, Water, WaterCooldown, WallLength, WallCooldown, WallPush, HitReach);
    public TerrainPermit WithWater(float cooldown = 0f) => new(Hammer, HammerCooldown, BlastRadius, BlastCooldown, BombRadius, BombCooldown, FireRadius, FireCooldown, true, cooldown, WallLength, WallCooldown, WallPush, HitReach);

    public TerrainPermit WithWall(float length, float cooldown = 0f, bool push = false) => new(Hammer, HammerCooldown, BlastRadius, BlastCooldown, BombRadius, BombCooldown, FireRadius, FireCooldown, Water, WaterCooldown, length, cooldown, push, HitReach);

    // 離れた所の壁を叩ける (待ち時間 = 叩きの待ち時間)。手元を叩く打撃もできる
    public TerrainPermit WithRangedHit(float reach, float cooldown = 0f) => new(true, cooldown, BlastRadius, BlastCooldown, BombRadius, BombCooldown, FireRadius, FireCooldown, Water, WaterCooldown, WallLength, WallCooldown, WallPush, reach);

    public bool Any => Hammer || BlastRadius > 0f || BombRadius > 0f || FireRadius > 0f || Water || WallLength > 0f;
}

// 壊し方の種類 (待ち時間を別々に数える単位)
public enum TerrainUse : byte
{
    Hammer,
    Blast,
    Bomb,
    Push,   // 押し: 地形は変えないので役職を問わない (依頼の数の上限だけ)
    Fire,   // 点火・油をまく
    Water,  // 放水
    Wall,   // 仕掛け壁を立てる (TerrainPermits.Key が 1 人 8 種までを前提にしている)
}

// その人が今壊してよいか。自分の端末 (使う前) とホスト (客の依頼を受ける時) の両方で同じ判定をする。
// 使った時刻は端末ごとに持つ (ホストが替わると新しいホストは客の前回を知らず、1 回だけ待ち時間なしで通る。結果はホストが決めて配るので同期は割れない)
internal static class TerrainPermits
{
    // ホストの判定は通信の遅れの分だけ待ち時間を甘くする
    private const float HostCooldownSlack = 0.8f;

    private static readonly Dictionary<int, float> LastUse = new();
    private static int _shipGen = -1;

    // 確かめ用 (ブリッジの permit コマンドからだけ・この端末だけ): 練習・フリープレイの「何でも使える」を切る (null = 本当の判定)
    internal static bool? SandboxOverride;

    public static TerrainPermit Of(byte playerId)
    {
        if (SandboxOverride ?? Roles.PracticeMatch.Sandbox) return TerrainPermit.All;
        var role = Roles.RoleState.Of(playerId);
        var p = role != null ? role.TerrainPermit : TerrainPermit.None;
        // 構築士の設計台を使っている人は、その間だけ人を押し出す壁を立てられる (全員の端末で同じ判定)
        if (!p.WallPush && Lent != null && Lent(playerId)) p = p.WithWall(TerrainPermit.MaxWallLength, push: true);
        return p;
    }

    // 役職の外から一時的に壁の許可を貸すか (構築士の設計台)
    internal static Func<byte, bool> Lent;

    // 確かめて、使えるならその場で使ったと記録する (ホストが客の依頼を受ける時)。使えない理由 (使えるなら null)
    public static string TryUse(byte playerId, TerrainUse use, float radius, bool host)
    {
        string bad = Check(playerId, use, radius, host);
        if (bad == null) Mark(playerId, use);
        return bad;
    }

    // 使えない理由 (使えるなら null)。記録はしない (使えたら Mark)。host = 客の依頼を確かめる時 (待ち時間を少し甘くする)
    public static string Check(byte playerId, TerrainUse use, float radius, bool host)
    {
        Reset();

        var p = Of(playerId);
        float cooldown;
        switch (use)
        {
            case TerrainUse.Hammer:
                if (!p.Hammer) return "no hammer";
                cooldown = p.HammerCooldown;
                break;
            case TerrainUse.Push:
                return null;
            case TerrainUse.Water:
                if (!p.Water) return "no water";
                cooldown = p.WaterCooldown;
                break;
            case TerrainUse.Wall:
                if (p.WallLength <= 0f) return "no wall";
                if (radius > TerrainWire.QSize(p.WallLength) + 0.01f) return $"wall too long {radius:0.0}>{p.WallLength:0.0}";
                cooldown = p.WallCooldown;
                break;
            case TerrainUse.Fire:
                if (p.FireRadius <= 0f) return "no fire";
                if (radius > TerrainWire.QSize(p.FireRadius) + 0.01f) return $"fire too large {radius:0.0}>{p.FireRadius:0.0}";
                cooldown = p.FireCooldown;
                break;
            case TerrainUse.Blast:
                if (p.BlastRadius <= 0f) return "no blast";
                if (radius > TerrainWire.QSize(p.BlastRadius) + 0.01f) return $"blast too large {radius:0.0}>{p.BlastRadius:0.0}";
                cooldown = p.BlastCooldown;
                break;
            default:
                if (p.BombRadius <= 0f) return "no bomb";
                if (radius > TerrainWire.QSize(p.BombRadius) + 0.01f) return $"bomb too large {radius:0.0}>{p.BombRadius:0.0}";
                cooldown = p.BombCooldown;
                break;
        }
        if (cooldown > 0f)
        {
            float need = host ? cooldown * HostCooldownSlack : cooldown;
            if (LastUse.TryGetValue(Key(playerId, use), out float last) && Time.time - last < need) return "cooling down";
        }
        return null;
    }

    public static void Mark(byte playerId, TerrainUse use)
    {
        Reset();
        LastUse[Key(playerId, use)] = Time.time;
    }

    private static int Key(byte playerId, TerrainUse use) => playerId * 8 + (int)use;

    // 試合の船が替わったら待ち時間を忘れる
    private static void Reset()
    {
        int gen = GameClock.ShipGen;
        if (gen != _shipGen) { _shipGen = gen; LastUse.Clear(); }
    }

    internal static string Describe(byte playerId)
    {
        var p = Of(playerId);
        return $"sandbox={(SandboxOverride?.ToString() ?? "auto")}/{Roles.PracticeMatch.Sandbox} hammer={p.Hammer}/{p.HammerCooldown:0.#}s blast={p.BlastRadius:0.#}/{p.BlastCooldown:0.#}s bomb={p.BombRadius:0.#}/{p.BombCooldown:0.#}s";
    }

    public static TerrainUse UseOf(in DamageEvent e) => e.Kind switch
    {
        DamageKind.Blunt => TerrainUse.Hammer,
        DamageKind.Push => TerrainUse.Push,
        DamageKind.Ignite or DamageKind.Spill or DamageKind.Burn => TerrainUse.Fire,
        DamageKind.Water or DamageKind.Flood => TerrainUse.Water,
        DamageKind.Wall => TerrainUse.Wall,
        _ => TerrainUse.Blast,
    };
}
