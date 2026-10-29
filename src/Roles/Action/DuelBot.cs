using System;
using System.Collections.Generic;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 練習の相手 (ボット)。ホストがチャットの /bot で出すダミーで、ホストの端末だけで考えて動く。行動は人と同じ確かめを通す。
// サムライ = 間合いを詰めて斬る・気が減れば納めて離れて溜める・溜めた居合で踏み込む・斬られかけたら斬り返して弾き合う・鍔迫り合いで連打する。
// インポスター = 追いかけてキルを狙い、待ち時間の間は抜いたサムライから距離を取る・立ち合いで斬られたら回避で飛び退く。
// 強さ (1〜9) で反応の速さ・狙いのぶれ・攻め気・弾きと回避の確率・連打の速さが変わる。1 はほとんど立っているだけ
internal static class DuelBot
{
    internal enum Kind : byte { Samurai, Impostor }

    private sealed class Bot
    {
        public byte Id;
        public Kind Kind;
        public int Level;
        public float NextThink, NextMove, NextStroke, KillReady, CounterAt = -1f, DodgeAt = -1f, DodgeUntil, MashCarry, Charge = -1f;
        public byte Foe = 255;      // 斬り返す・飛び退く相手
        public Vector2 DodgeDir;
        public bool Retreat;
    }

    private static readonly Dictionary<byte, Bot> Bots = new();
    private static readonly List<byte> Gone = new();
    private static readonly System.Random Rng = new();
    private static int _gen = -1;
    private static bool _hooked;
    private const float ThinkEvery = 0.05f, MoveEvery = 0.25f;
    private const float AggroPace = 2.5f;     // 攻め気 1 で 1 回の考えごとに仕掛ける割合の倍率
    private const float Reach = 1.7f;         // ここまで詰めて斬る (斬れる距離の内側)
    private const float ChargeDistance = 2.7f; // 居合を溜める間に保つ距離
    private const float Keep = 3.2f;           // インポスターが待ち時間の間に取る距離
    private const float RegenTo = 75f;          // 納めて離れた時、ここまで気が溜まったら戻る
    private const float FirstKill = 8f;

    internal static int Dodges, Counters;

    internal static bool Has(byte id) => Bots.ContainsKey(id);
    internal static int Count => Bots.Count;

    // 強さごとの値 (t = 0 〜 1)
    private static float T(Bot b) => (b.Level - 1) / 8f;
    private static float Reaction(Bot b) => 0.55f - 0.47f * T(b);
    private static float Aggro(Bot b) => b.Level <= 1 ? 0f : 0.25f + 0.75f * T(b);    // 仕掛けられる時に 1 秒あたり仕掛ける見込み
    private static float Accuracy(Bot b) => 0.3f + 0.65f * T(b);                      // 一振りを相手に向ける確率 (外れは扇の外へ振る)
    private static float ClashChance(Bot b) => b.Level < 3 ? 0f : 0.15f + 0.65f * T(b);
    private static float DodgeChance(Bot b) => b.Level < 3 ? 0f : 0.2f + 0.7f * T(b);
    private static float MashRate(Bot b) => 2f + 9f * T(b);                           // 鍔迫り合いの連打 (回/秒)
    private static float StrokeGap(Bot b) => 1.4f - 1.0f * T(b);
    private static float MaxCharge(Bot b) => b.Level < 3 ? 0f : b.Level < 7 ? 0.8f : 1f; // 溜め切り (不意打ちで一撃) は 7 から

    private static bool Roll(float p) => Rng.NextDouble() < p;
    private static float Rand(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

    // ---- 出す・消す (チャットの /bot) ----

    internal static string Add(Kind kind, int level)
    {
        var proto = Commands.HostActions.FindRole(kind == Kind.Samurai ? "Samurai" : "Impostor");
        if (proto == null) return null;
        var pc = Dummy.Spawn(Bots.Count, $"BOT Lv{level}");
        RoleAssigner.Give(pc, proto);
        if (_gen != GameClock.ShipGen) Bots.Clear();
        _gen = GameClock.ShipGen;
        Hook();
        float now = Time.time;
        Bots[pc.PlayerId] = new Bot { Id = pc.PlayerId, Kind = kind, Level = Math.Clamp(level, 1, 9), KillReady = now + FirstKill };
        return pc.Data.PlayerName;
    }

    // 全部止める (ダミーは立っているだけになる)
    internal static int Stop()
    {
        int n = Bots.Count;
        foreach (var b in Bots.Values)
        {
            var pc = Player(b.Id);
            if (pc) Dummy.StopWalk(pc);
        }
        Bots.Clear();
        return n;
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnHit += OnHit;
    }

    private static PlayerControl Player(byte id) => GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;

    // ---- 毎フレーム (ホストだけ・ボットがいる時だけ) ----

    internal static void Tick()
    {
        if (Bots.Count == 0) return;
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || client.IsGameOver || !ShipStatus.Instance || !GameData.Instance || _gen != GameClock.ShipGen)
        {
            Bots.Clear();
            return;
        }
        if (MeetingHud.Instance || ExileController.Instance) return;
        float now = Time.time, dt = Time.deltaTime;
        foreach (var b in Bots.Values)
        {
            var pc = Player(b.Id);
            if (!pc || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected || !pc.MyPhysics) { Gone.Add(b.Id); continue; }
            // 回避の飛び退きの間は向きと速さを毎フレーム書く
            if (now < b.DodgeUntil)
            {
                pc.MyPhysics.body.velocity = FxMath.V2(b.DodgeDir.x * DuelDodge.Speed, b.DodgeDir.y * DuelDodge.Speed);
                continue;
            }
            if (b.Kind == Kind.Samurai && Samurai.BotLocked(b.Id))
            {
                Dummy.StopWalk(pc);
                b.MashCarry += MashRate(b) * dt;
                int n = (int)b.MashCarry;
                b.MashCarry -= n;
                Samurai.BotMash(b.Id, n);
                continue;
            }
            if (b.CounterAt >= 0f && now >= b.CounterAt) Counter(b, pc);
            if (b.DodgeAt >= 0f && now >= b.DodgeAt) { b.DodgeAt = -1f; DodgeAway(b, pc, now); }
            if (now < b.NextThink) continue;
            b.NextThink = now + ThinkEvery;
            var target = Target(b, pc);
            if (!target) { Dummy.StopWalk(pc); continue; }
            if (b.Kind == Kind.Samurai) { if (RoleState.Of(pc) is Samurai s) ThinkSamurai(b, pc, s, target, now); }
            else ThinkImpostor(b, pc, target, now);
        }
        if (Gone.Count == 0) return;
        foreach (var id in Gone) Bots.Remove(id);
        Gone.Clear();
    }

    // 一番近い、生きている人 (ダミーとボット以外)
    private static PlayerControl Target(Bot b, PlayerControl self)
    {
        Vector2 me = self.GetTruePosition();
        PlayerControl best = null;
        float bestD = 81f;
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            if (info == null || info.IsDead || info.Disconnected || info.PlayerId == b.Id || Bots.ContainsKey(info.PlayerId)) continue;
            var pc = info.Object;
            if (!pc || pc.inVent || pc.isDummy) continue;
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = pc; }
        }
        return best;
    }

    // ---- サムライ ----

    private static void ThinkSamurai(Bot b, PlayerControl pc, Samurai s, PlayerControl target, float now)
    {
        if (Aggro(b) <= 0f) { Dummy.StopWalk(pc); return; }
        Vector2 me = pc.GetTruePosition(), at = target.GetTruePosition();
        float dx = at.x - me.x, dy = at.y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        float ki = s.BotKi;
        // 気が減ったら納めて離れ、溜まるまで待つ
        if (ki < Samurai.StrokeCost + 2f) b.Retreat = true;
        else if (b.Retreat && ki >= RegenTo) b.Retreat = false;
        if (b.Retreat)
        {
            s.BotSheathe();
            Away(b, pc, me, dx, dy, d, ChargeDistance + 0.5f, now);
            return;
        }
        if (!s.BotDrawn)
        {
            // 納めている: 居合を溜めてから踏み込む (溜めの目標は仕掛けるたびに決め直す)
            if (b.Charge < 0f) b.Charge = MaxCharge(b) * Rand(0.5f, 1f);
            float charge = FxMath.Clamp01((s.BotSheathedFor - 0.12f) / Samurai.ChargeTime);
            bool ready = s.BotSheathedFor > 0.5f && charge >= b.Charge;
            if (!ready)
            {
                if (d < ChargeDistance - 0.3f) Away(b, pc, me, dx, dy, d, ChargeDistance, now);
                else if (d > ChargeDistance + 0.5f) Toward(b, pc, at, now);
                else Dummy.StopWalk(pc);
                return;
            }
            if (d > Reach) { Toward(b, pc, at, now); return; }
            if (!Roll(Aggro(b) * ThinkEvery * AggroPace)) return;
            Strike(b, pc, s, dx, dy, now, FxMath.Min(charge, MaxCharge(b)));
            b.Charge = -1f;
            return;
        }
        if (d > Reach) { Toward(b, pc, at, now); return; }
        if (d < 0.7f) Away(b, pc, me, dx, dy, d, 1.2f, now);
        else Dummy.StopWalk(pc);
        if (now >= b.NextStroke && Roll(Aggro(b) * ThinkEvery * AggroPace)) Strike(b, pc, s, dx, dy, now, 0f);
    }

    // 相手の向きへ斬る。掃引の真ん中が狙った向きに来るよう始めの角度を決める。狙いを外す時は扇が相手に掛からない向きへ振る
    private static void Strike(Bot b, PlayerControl pc, Samurai s, float dx, float dy, float now, float charge)
    {
        float sweep = (Roll(0.5f) ? 1f : -1f) * Rand(80f, 130f);
        float miss = Roll(Accuracy(b)) ? Rand(-10f, 10f) : (Roll(0.5f) ? 1f : -1f) * (FxMath.Abs(sweep) * 0.5f + Rand(45f, 80f));
        float aim = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg + miss;
        float a0 = aim - sweep * 0.5f;
        if (a0 > 180f) a0 -= 360f;
        else if (a0 < -180f) a0 += 360f;
        float secs = 0.16f + 0.14f * (1f - T(b)) + Rand(0f, 0.05f);
        Dummy.StopWalk(pc);
        s.BotStroke(a0, sweep, secs, charge);
        b.NextStroke = now + StrokeGap(b) + secs;
    }

    // ホスト: サムライの斬りがボットに当たる前に弾き合いを待っている (Samurai.Clash)。反応が間に合えば斬り返す
    internal static void OnHeld(byte bot, byte attacker, float window)
    {
        if (!Bots.TryGetValue(bot, out var b) || b.Kind != Kind.Samurai || b.CounterAt >= 0f) return;
        float react = Reaction(b) * Rand(0.6f, 1.1f);
        if (react >= window - 0.02f || !Roll(ClashChance(b))) return;
        b.CounterAt = Time.time + react;
        b.Foe = attacker;
    }

    private static void Counter(Bot b, PlayerControl pc)
    {
        b.CounterAt = -1f;
        var o = Player(b.Foe);
        if (!o || RoleState.Of(pc) is not Samurai s || s.BotKi < -10f) return;
        Vector2 me = pc.GetTruePosition(), at = o.GetTruePosition();
        Counters++;
        Strike(b, pc, s, at.x - me.x, at.y - me.y, Time.time, 0f);
    }

    // ---- インポスター ----

    private static void ThinkImpostor(Bot b, PlayerControl pc, PlayerControl target, float now)
    {
        if (Aggro(b) <= 0f) { Dummy.StopWalk(pc); return; }
        Vector2 me = pc.GetTruePosition(), at = target.GetTruePosition();
        float dx = at.x - me.x, dy = at.y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (now >= b.KillReady)
        {
            if (d > KillAbility.Reach(pc) * 0.85f) { Toward(b, pc, at, now); return; }
            Dummy.StopWalk(pc);
            if (!Roll(Aggro(b) * ThinkEvery * AggroPace)) return;
            if (Dummy.Kill(pc, target) == null)
                b.KillReady = now + FxMath.Max(GameManager.Instance.LogicOptions.GetKillCooldown(), 2f);
            else b.KillReady = now + 1f;
            return;
        }
        // 待ち時間の間: 抜いたサムライからは距離を取り、それ以外は付かず離れず
        bool threat = RoleState.Of(target) is Samurai ts && ts.BotDrawn;
        if (threat && d < Keep) Away(b, pc, me, dx, dy, d, Keep + 0.5f, now);
        else if (d > Keep + 1.5f) Toward(b, pc, at, now);
        else Dummy.StopWalk(pc);
    }

    // 立ち合いで斬られたボット (インポスター) は、反応が間に合えば飛び退く
    private static void OnHit(byte a, byte t, int hp)
    {
        if (a == t || hp <= 0 || !Bots.TryGetValue(t, out var b) || b.Kind != Kind.Impostor) return;
        if (b.DodgeAt >= 0f || !Roll(DodgeChance(b))) return;
        b.DodgeAt = Time.time + Reaction(b) * 0.5f;
        b.Foe = a;
    }

    private static void DodgeAway(Bot b, PlayerControl pc, float now)
    {
        var o = Player(b.Foe);
        if (!o || !DuelDodge.BotDodge(b.Id)) return;
        Vector2 me = pc.GetTruePosition(), at = o.GetTruePosition();
        float dx = me.x - at.x, dy = me.y - at.y, l = FxMath.Sqrt(dx * dx + dy * dy);
        if (l < 0.01f) { dx = 1f; dy = 0f; l = 1f; }
        Dummy.StopWalk(pc);
        Dodges++;
        b.DodgeDir = FxMath.V2(dx / l, dy / l);
        b.DodgeUntil = now + DuelDodge.BotDodgeTime;
    }

    // ---- 歩き (歩き先は MoveEvery ごとに決め直す) ----

    private static void Toward(Bot b, PlayerControl pc, Vector2 at, float now)
    {
        if (now < b.NextMove && Dummy.IsWalking(b.Id)) return;
        b.NextMove = now + MoveEvery;
        Dummy.WalkTo(pc, at.x, at.y);
    }

    // 相手 (me から dx, dy の所) から離れて距離 keep の所へ
    private static void Away(Bot b, PlayerControl pc, Vector2 me, float dx, float dy, float d, float keep, float now)
    {
        if (now < b.NextMove && Dummy.IsWalking(b.Id)) return;
        b.NextMove = now + MoveEvery;
        if (d < 0.01f) { dx = 1f; dy = 0f; d = 1f; }
        float k = (keep - d) / d;
        Dummy.WalkTo(pc, me.x - dx * k, me.y - dy * k);
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"bots={Bots.Count} dodges={Dodges} counters={Counters}");
        foreach (var b in Bots.Values)
        {
            sb.Append($" [{b.Id} {b.Kind} Lv{b.Level} retreat={b.Retreat} charge={b.Charge:0.00} kill={FxMath.Max(b.KillReady - Time.time, 0f):0.0}s");
            if (RoleState.Of(b.Id) is Samurai s) sb.Append($" ki={s.BotKi:0} drawn={s.BotDrawn}");
            sb.Append(']');
        }
        return sb.ToString();
    }
}
