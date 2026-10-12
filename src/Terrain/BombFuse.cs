using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 爆弾: 足元に置く (拳が下ろす PlaceTime 秒) → 導火線 Fuse 秒 (火花と本体が点滅・導火線の音) → 爆発。
// 爆発させるのはホストのタイマー (置いた人が抜けても死んでも爆発する)。ホストは依頼を受けたら全員へ「置かれた」を送り、
// 導火線が尽きたら置いた人の爆発として普通の破壊の結果を配る。一人の時はその場で同じことをする。
// 置かれた爆弾の絵は爆発の結果が届いた時に消す (届かなければ時間で消す)。
internal static class BombFuse
{
    public const float PlaceTime = 0.2f;
    public const float Fuse = 1.5f;
    private const float Cooldown = 2.5f;      // ホスト: 1 人が続けて置ける間隔
    private const float MaxReach = 1.5f + 1.5f; // ホスト: 置く所は本人の足元 (+ ホストに見えている位置の遅れ)
    private const float MaxRadius = 3f;
    private const int MaxBombs = 8;
    // 絵 (Resources/Icons/bomb_placed.png・220 px・本体 124 px): 床では 0.35 単位・原点 = 本体の底 (110, 194)
    private const float BottomPy = 194f, CapPx = 120f, CapPy = 70f, SpritePx = 220f;
    private const float BodyPx = 124f, Diameter = 0.35f;
    private const float HandSize = 0.22f;     // 拳の絵の倍率 (ハンマーと同じ)
    private const float CarryLift = 0.62f;    // 置き始めの拳の高さ (足元から)
    private const float SoundRange = 14f, SoundMuffle = 7f, SoundVolume = 0.8f;
    private const float FrontOffset = 0.32f;  // 体に隠れないよう、向いている側へずらして置く (壁があれば足元)

    private sealed class Bomb
    {
        public GameObject Go;
        public SpriteRenderer Body, Spark;
        public Vector2 Pos;
        public float AppearAt, BoomAt, Expire;
        public bool Shown;
    }

    private struct Timer
    {
        public float At;
        public byte Actor;
        public Vector2 Pos;
        public float Radius;
    }

    private static readonly List<Bomb> Bombs = new();
    private static readonly List<Timer> Timers = new();          // ホスト・一人: 爆発させる時刻
    private static readonly Dictionary<byte, float> LastPlaced = new(); // ホスト: 人ごとの最後に置いた時刻
    private static int _shipGen;
    private static float _ownPlacedAt = -10f;                   // 客: 自分の拳を先に動かした時刻

    // 自分の足元に爆弾を置く。返り値は結果の説明
    public static string Place(float radius, out bool ok)
    {
        ok = false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) return new Text("生きている時だけ使えます", "Only while alive");
        if (Bombs.Count + Timers.Count >= MaxBombs) return new Text("置ける数の上限です", "Too many bombs");
        Vector2 at = lp.GetTruePosition();
        var front = new Vector2(at.x + (lp.cosmetics && lp.cosmetics.FlipX ? -FrontOffset : FrontOffset), at.y);
        if (!PhysicsHelpers.AnythingBetween(at, front, Constants.ShipAndObjectsMask, false)) at = front;
        float r = Math.Clamp(radius, 0.3f, MaxRadius);
        ok = true;
        if (TerrainSync.IsGuest())
        {
            // 拳は押した時から動かす (爆弾の絵はホストの知らせで出す)
            _ownPlacedAt = Time.time;
            PlayHand(lp.PlayerId, at);
            TerrainSync.SendPlace(at, r);
        }
        else Accept(lp.PlayerId, at, r);
        return new Text($"爆弾を置いた (半径 {r:0.0}・{PlaceTime + Fuse:0.0} 秒後に爆発)", $"Bomb placed (radius {r:0.0}, explodes in {PlaceTime + Fuse:0.0}s)");
    }

    // ホスト: 客の置く依頼を受けない理由 (受けるなら null)
    internal static string HostRefuse(PlayerControl sender, Vector2 pos, float radius)
    {
        var data = sender.Data;
        if (data == null || data.IsDead || data.Disconnected) return "not alive";
        Vector2 me = sender.GetTruePosition();
        float dx = pos.x - me.x, dy = pos.y - me.y;
        if (dx * dx + dy * dy > MaxReach * MaxReach) return "too far";
        if (radius > MaxRadius) return "too large";
        if (Bombs.Count + Timers.Count >= MaxBombs) return "too many";
        if (LastPlaced.TryGetValue(sender.PlayerId, out float last) && Time.time - last < Cooldown) return "too soon";
        return null;
    }

    // ホスト・一人: 置かれたことにして、全員へ知らせ、導火線のタイマーを積む
    internal static void Accept(byte actor, Vector2 pos, float radius)
    {
        pos = TerrainWire.Q(pos);
        radius = TerrainWire.QSize(radius);
        LastPlaced[actor] = Time.time;
        Timers.Add(new Timer { At = Time.time + PlaceTime + Fuse, Actor = actor, Pos = pos, Radius = radius });
        TerrainSync.BroadcastBomb(actor, pos, radius);
        Show(actor, pos, radius);
    }

    // 全員の手元: 置く拳を見せて、少し後に爆弾の絵を出す
    internal static void Show(byte actor, Vector2 pos, float radius)
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            bool mine = lp && lp.PlayerId == actor && Time.time - _ownPlacedAt < 1f;
            float appear = Time.time + PlaceTime;
            if (mine) appear = Math.Max(Time.time, _ownPlacedAt + PlaceTime); // 自分の拳はもう動いている
            else PlayHand(actor, pos);
            if (Bombs.Count >= MaxBombs) return;
            var b = Create(pos);
            if (b == null) return;
            b.AppearAt = appear;
            b.BoomAt = appear + Fuse;
            b.Expire = b.BoomAt + 2f;
            Bombs.Add(b);
        }
        catch (Exception e) { Plugin.Logger.LogError($"[BombFuse] show: {e}"); }
    }

    // 爆発の結果を適用した直後: その場所の爆弾の絵を消す
    internal static void OnApplied(in ResolvedDamage r)
    {
        if (r.Kind != DamageKind.Explosion || Bombs.Count == 0) return;
        for (int i = Bombs.Count - 1; i >= 0; i--)
        {
            float dx = Bombs[i].Pos.x - r.Position.x, dy = Bombs[i].Pos.y - r.Position.y;
            if (dx * dx + dy * dy > 0.09f) continue;
            Destroy(Bombs[i]);
            Bombs.RemoveAt(i);
            break;
        }
    }

    // 毎フレーム。爆弾もタイマーも無ければ試合の切り替わりを見るだけ
    public static void Tick()
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            Clear();
        }
        if (Bombs.Count == 0 && Timers.Count == 0) return;

        float now = Time.time;
        if (MeetingHud.Instance) { Clear(); return; }

        for (int i = Timers.Count - 1; i >= 0; i--)
        {
            var t = Timers[i];
            if (now < t.At) continue;
            Timers.RemoveAt(i);
            TerrainSync.RequestAs(new DamageEvent(DamageKind.Explosion, t.Pos, Vector2.zero, t.Radius, 0f, (ushort)Environment.TickCount), t.Actor);
        }

        for (int i = Bombs.Count - 1; i >= 0; i--)
        {
            var b = Bombs[i];
            if (!b.Go || now >= b.Expire)
            {
                Destroy(b);
                Bombs.RemoveAt(i);
                continue;
            }
            if (now < b.AppearAt) continue;
            if (!b.Shown)
            {
                b.Shown = true;
                b.Go.SetActive(true);
                BreakNoise.PlayAt("noise_fuse", b.Pos, SoundRange, SoundMuffle, SoundVolume);
            }
            // 爆発が近いほど速く点滅する (本体は赤く・火花はちらつく)
            float u = FxMath.Clamp01((now - b.AppearAt) / Fuse);
            float phase = (now - b.AppearAt) * (3f + 9f * u);
            float pulse = 0.5f + 0.5f * FxMath.Sin(phase * 2f * FxMath.PI);
            float red = pulse * (0.25f + 0.5f * u);
            b.Body.color = FxMath.Rgba(1f, 1f - red, 1f - red, 1f);
            float flick = 0.75f + 0.25f * FxMath.Sin(now * 61f) * FxMath.Sin(now * 37f + 1f);
            b.Spark.color = FxMath.Rgba(1f, 1f, 1f, flick);
            float s = 1f + 0.06f * pulse * u;
            b.Go.transform.localScale = FxMath.V3(s, s, 1f);
        }
    }

    private static void Clear()
    {
        foreach (var b in Bombs) Destroy(b);
        Bombs.Clear();
        Timers.Clear();
        LastPlaced.Clear();
    }

    private static Bomb Create(Vector2 pos)
    {
        Sprite body = Roles.ButtonIcons.Load("bomb_placed", FloorPpu, FxMath.V2(0.5f, 1f - BottomPy / SpritePx));
        Sprite spark = Roles.ButtonIcons.Load("bomb_spark", FloorPpu, FxMath.V2(0.5f, 1f - BottomPy / SpritePx));
        if (!body || !spark) return null;
        var go = new GameObject("MrpBomb") { layer = 0 };
        go.SetActive(false);
        // 奥行きは本編のクルーと同じ数え方 (y/1000)。置いた人の足に重なっても見えるよう少し手前
        go.transform.position = FxMath.V3(pos.x, pos.y, pos.y / 1000f - 0.0005f);
        var b = new Bomb { Go = go, Pos = pos, Body = go.AddComponent<SpriteRenderer>() };
        b.Body.sprite = body;
        var sgo = new GameObject("MrpBombSpark") { layer = 0 };
        sgo.transform.SetParent(go.transform, false);
        sgo.transform.localPosition = FxMath.V3(0f, 0f, -0.0001f);
        b.Spark = sgo.AddComponent<SpriteRenderer>();
        b.Spark.sprite = spark;
        return b;
    }

    private static void Destroy(Bomb b)
    {
        if (b.Go) UnityEngine.Object.Destroy(b.Go);
    }

    // 爆弾を下げた拳: 胴の高さから足元へ下ろして放す
    private static void PlayHand(byte actor, Vector2 pos)
    {
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(actor) : null;
        if (info == null) return;
        // 拳が口金を持つ (絵の原点 = 口金)。床に着いた時に爆弾の底が pos に来る高さ
        float bottom = (BottomPy - CapPy) / CarryPpu * HandSize;
        var keys = new[]
        {
            new FxHands.Key(0.00f, 0f, alpha: 0f, dy: CarryLift),
            new FxHands.Key(0.05f, 0f, dy: CarryLift - 0.04f),
            new FxHands.Key(PlaceTime, 0f, dy: bottom),
            new FxHands.Key(PlaceTime + 0.02f, 0f, alpha: 0f, dy: bottom + 0.05f),
        };
        Sprite carry = Roles.ButtonIcons.Load("bomb_placed", CarryPpu, FxMath.V2(CapPx / SpritePx, 1f - CapPy / SpritePx));
        FxHands.Play(keys, pos, info.DefaultOutfit.ColorId, HandSize, false, 0f, pos.y / 1000f - 0.001f, carry);
    }

    private static float FloorPpu => BodyPx / Diameter;
    private static float CarryPpu => BodyPx * HandSize / Diameter; // 拳の倍率がかかっても同じ大きさになる

    internal static string Describe() => $"bombs={Bombs.Count} timers={Timers.Count}";
}
