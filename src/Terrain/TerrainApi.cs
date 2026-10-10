using System;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 地形を壊す時の結果。Ok = 使えた (空振り・断られた時は false で Why に理由)
public readonly struct TerrainResult
{
    public readonly bool Ok;
    public readonly string Why;

    private TerrainResult(bool ok, string why)
    {
        Ok = ok;
        Why = why;
    }

    internal static TerrainResult Done(string what) => new(true, what);
    internal static TerrainResult Fail(string why) => new(false, why);
}

// 役職・機能から地形を壊す入口。同期 (ホストが決めて全員へ配る)・音・粉塵・水漏れ・家具・瓦礫はここから先で全部付く。
// 自分 (LocalPlayer) が壊す物は、その人の役職の TerrainPermit で使えるかを確かめる (客の依頼はホストも同じ判定で確かめる)。
// World〜 はプレイヤーでない壊れ方 (マップの出来事・時間で崩れる等)。ホストと一人の時だけ起こせる。
// 壊れた後に何かしたい時は TerrainBrokenEvent を受ける。
public static class TerrainApi
{
    // マップの出来事などプレイヤーでない破壊の PlayerId
    public const byte World = ResolvedDamage.NoActor;

    public static TerrainPermit LocalPermit
    {
        get
        {
            var lp = PlayerControl.LocalPlayer;
            return lp ? TerrainPermits.Of(lp.PlayerId) : TerrainPermit.None;
        }
    }

    // 向いている方向の壁をハンマーで叩く (振る動きの後に当たる)。force 0..1 = 振りの強さ。aim = 向きの指定 (null = 歩いている向き)
    public static TerrainResult Hammer(float force = 0.5f, Vector2? aim = null)
    {
        if (!Local(TerrainUse.Hammer, 0f, out var lp, out string why)) return TerrainResult.Fail(why);
        string res = HammerSwing.Swing(force, out bool ok, aim);
        if (!ok) return TerrainResult.Fail(res);
        TerrainPermits.Mark(lp.PlayerId, TerrainUse.Hammer);
        return TerrainResult.Done(res);
    }

    // 足元に爆弾を置く (導火線の後に爆発)
    public static TerrainResult Bomb(float radius)
    {
        if (!Local(TerrainUse.Bomb, radius, out var lp, out string why)) return TerrainResult.Fail(why);
        string res = BombFuse.Place(radius, out bool ok);
        if (!ok) return TerrainResult.Fail(res);
        TerrainPermits.Mark(lp.PlayerId, TerrainUse.Bomb);
        return TerrainResult.Done(res);
    }

    // その場で爆発させる (ロケット・自爆など)。pos は自分から MaxBlastDistance 以内。
    // dir と force (0..1) を付けると、その向きへ伸びた涙形に抜ける (0 = 円)
    public static TerrainResult Blast(Vector2 pos, float radius, Vector2 dir = default, float force = 0f)
    {
        if (!Local(TerrainUse.Blast, radius, out var lp, out string why)) return TerrainResult.Fail(why);
        Vector2 me = lp.GetTruePosition();
        float dx = pos.x - me.x, dy = pos.y - me.y;
        if (dx * dx + dy * dy > TerrainSync.MaxBlastDistance * TerrainSync.MaxBlastDistance)
            return TerrainResult.Fail(new Text("遠すぎます", "Too far away"));
        string res = TerrainSync.Request(new DamageEvent(DamageKind.Explosion, pos, dir, radius, Math.Clamp(force, 0f, 1f), Seed()), out bool ok);
        if (!ok) return TerrainResult.Fail(res);
        TerrainPermits.Mark(lp.PlayerId, TerrainUse.Blast);
        return TerrainResult.Done(res);
    }

    // プレイヤーでない爆発 (ホストと一人の時だけ)
    public static TerrainResult WorldBlast(Vector2 pos, float radius, Vector2 dir = default, float force = 0f)
    {
        if (TerrainSync.IsGuest()) return TerrainResult.Fail("host only");
        radius = Math.Clamp(radius, 0.1f, TerrainPermit.MaxRadius);
        string res = TerrainSync.RequestAs(new DamageEvent(DamageKind.Explosion, pos, dir, radius, Math.Clamp(force, 0f, 1f), Seed()), World, out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    // プレイヤーでない打撃 (from から dir の向きの壁を叩く。ホストと一人の時だけ)
    public static TerrainResult WorldHit(Vector2 from, Vector2 dir, float force = 0.5f)
    {
        if (TerrainSync.IsGuest()) return TerrainResult.Fail("host only");
        if (dir.sqrMagnitude < 1e-6f) return TerrainResult.Fail("no direction");
        string res = TerrainSync.RequestAs(new DamageEvent(DamageKind.Blunt, from, dir.normalized, 0f, Math.Clamp(force, 0f, 1f), Seed()), World, out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    // 炎に焼かれ続けた壁 (from から dir の向きの壁の耐久を 1 削る。ホストと一人の時だけ)
    public static TerrainResult WorldBurn(Vector2 from, Vector2 dir)
    {
        if (TerrainSync.IsGuest()) return TerrainResult.Fail("host only");
        if (dir.sqrMagnitude < 1e-6f) return TerrainResult.Fail("no direction");
        string res = TerrainSync.RequestAs(new DamageEvent(DamageKind.Burn, from, dir.normalized, 0f, 0.5f, Seed()), World, out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    // 押し (ロケットの反動・衝撃波など): 壁は壊さず、pos から dir の向きへ length までの扇形の水と小物を押す。
    // force 0..1 = 強さ。pos は自分から MaxBlastDistance 以内
    public static TerrainResult Push(Vector2 pos, Vector2 dir, float length, float force = 1f)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) return TerrainResult.Fail(new Text("生きている時だけ使えます", "Only while alive"));
        if (!GameClock.ShipAlive) return TerrainResult.Fail(new Text("試合の中だけ使えます", "Only during a game"));
        if (dir.sqrMagnitude < 1e-6f) return TerrainResult.Fail("no direction");
        Vector2 me = lp.GetTruePosition();
        float dx = pos.x - me.x, dy = pos.y - me.y;
        if (dx * dx + dy * dy > TerrainSync.MaxBlastDistance * TerrainSync.MaxBlastDistance)
            return TerrainResult.Fail(new Text("遠すぎます", "Too far away"));
        length = Math.Clamp(length, 0.1f, TerrainSync.MaxPushReach);
        string res = TerrainSync.Request(new DamageEvent(DamageKind.Push, pos, dir.normalized, length, Math.Clamp(force, 0f, 1f), Seed()), out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    // プレイヤーでない押し (ホストと一人の時だけ)
    public static TerrainResult WorldPush(Vector2 pos, Vector2 dir, float length, float force = 1f)
    {
        if (TerrainSync.IsGuest()) return TerrainResult.Fail("host only");
        if (dir.sqrMagnitude < 1e-6f) return TerrainResult.Fail("no direction");
        length = Math.Clamp(length, 0.1f, TerrainSync.MaxPushReach);
        string res = TerrainSync.RequestAs(new DamageEvent(DamageKind.Push, pos, dir.normalized, length, Math.Clamp(force, 0f, 1f), Seed()), World, out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    // 火を付ける (火炎瓶・火炎放射など): pos の半径 radius の床に熱を足す。heat 0..1 = 強さ (1 = 金属の床の塗装まで一瞬燃える)。
    // 燃え広がるかは床の材質次第 (木・草・油・配線はよく燃え、金属の床は燃えない)
    public static TerrainResult Ignite(Vector2 pos, float radius, float heat = 1f) => Fire(DamageKind.Ignite, pos, radius, heat);

    // 油をまく: pos の半径 radius の床を油にする (amount 0..1)。火が付くとよく燃え、水を掛けると噴き上がる
    public static TerrainResult Spill(Vector2 pos, float radius, float amount = 1f) => Fire(DamageKind.Spill, pos, radius, amount);

    private static TerrainResult Fire(DamageKind kind, Vector2 pos, float radius, float force)
    {
        radius = Math.Clamp(radius, 0.1f, TerrainSync.MaxFireRadius);
        if (!Local(TerrainUse.Fire, radius, out var lp, out string why)) return TerrainResult.Fail(why);
        Vector2 me = lp.GetTruePosition();
        float dx = pos.x - me.x, dy = pos.y - me.y;
        if (dx * dx + dy * dy > TerrainSync.MaxBlastDistance * TerrainSync.MaxBlastDistance)
            return TerrainResult.Fail(new Text("遠すぎます", "Too far away"));
        string res = TerrainSync.Request(new DamageEvent(kind, pos, Vector2.zero, radius, Math.Clamp(force, 0f, 1f), Seed()), out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    // プレイヤーでない火と油 (ホストと一人の時だけ・燃える仕掛けなど)
    public static TerrainResult WorldIgnite(Vector2 pos, float radius, float heat = 1f) => WorldFire(DamageKind.Ignite, pos, radius, heat);
    public static TerrainResult WorldSpill(Vector2 pos, float radius, float amount = 1f) => WorldFire(DamageKind.Spill, pos, radius, amount);

    private static TerrainResult WorldFire(DamageKind kind, Vector2 pos, float radius, float force)
    {
        if (TerrainSync.IsGuest()) return TerrainResult.Fail("host only");
        radius = Math.Clamp(radius, 0.1f, TerrainSync.MaxFireRadius);
        string res = TerrainSync.RequestAs(new DamageEvent(kind, pos, Vector2.zero, radius, Math.Clamp(force, 0f, 1f), Seed()), World, out bool ok);
        return ok ? TerrainResult.Done(res) : TerrainResult.Fail(res);
    }

    private static bool Local(TerrainUse use, float radius, out PlayerControl lp, out string why)
    {
        lp = PlayerControl.LocalPlayer;
        why = null;
        if (!lp || lp.Data == null || lp.Data.IsDead) { why = new Text("生きている時だけ使えます", "Only while alive"); return false; }
        if (!GameClock.ShipAlive) { why = new Text("試合の中だけ使えます", "Only during a game"); return false; }
        string bad = TerrainPermits.Check(lp.PlayerId, use, radius, host: false);
        if (bad == null) return true;
        why = bad == "cooling down" ? new Text("まだ使えません", "Not ready yet") : new Text("この役職では使えません", "Your role can't do that");
        Plugin.Logger.LogDebug($"terrain {use} refused locally: {bad}");
        return false;
    }

    private static ushort Seed() => (ushort)Environment.TickCount;
}
