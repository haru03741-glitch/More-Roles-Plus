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

    private TerrainPermit(bool hammer, float hammerCd, float blast, float blastCd, float bomb, float bombCd)
    {
        Hammer = hammer;
        HammerCooldown = hammerCd;
        BlastRadius = Math.Clamp(blast, 0f, MaxRadius);
        BlastCooldown = blastCd;
        BombRadius = Math.Clamp(bomb, 0f, MaxRadius);
        BombCooldown = bombCd;
    }

    public static readonly TerrainPermit None = default;

    // 練習・フリープレイ: 何でも使える (待ち時間はボタンと各武器の決まりに任せる)
    public static readonly TerrainPermit All = new(true, 0f, MaxRadius, 0f, MaxRadius, 0f);

    public TerrainPermit WithHammer(float cooldown = 0f) => new(true, cooldown, BlastRadius, BlastCooldown, BombRadius, BombCooldown);
    public TerrainPermit WithBlast(float radius, float cooldown = 0f) => new(Hammer, HammerCooldown, radius, cooldown, BombRadius, BombCooldown);
    public TerrainPermit WithBomb(float radius, float cooldown = 0f) => new(Hammer, HammerCooldown, BlastRadius, BlastCooldown, radius, cooldown);

    public bool Any => Hammer || BlastRadius > 0f || BombRadius > 0f;
}

// 壊し方の種類 (待ち時間を別々に数える単位)
public enum TerrainUse : byte
{
    Hammer,
    Blast,
    Bomb,
    Push,   // 押し: 地形は変えないので役職を問わない (依頼の数の上限だけ)
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
        return role != null ? role.TerrainPermit : TerrainPermit.None;
    }

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

    private static int Key(byte playerId, TerrainUse use) => playerId * 4 + (int)use;

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
        _ => TerrainUse.Blast,
    };
}
