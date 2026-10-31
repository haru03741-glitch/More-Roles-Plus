using System;
using System.Collections.Generic;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 練習の相手 (ボット)。ホストがチャットの /bot で出すダミーで、ホストの端末だけで考えて動く。行動は人と同じ確かめを通す。
// サムライ = 間合いを詰めて斬る・離れていれば踏み込んで斬り、斬った後に踏み込みで抜ける・相手の周りを回り、時々横へ踏み込む・気が減れば納めて離れて溜める・
//   溜めた居合で踏み込む・斬られかけたら斬り返して弾き合う・鍔迫り合いで連打する。
// インポスター = 追いかけてキルを狙い、待ち時間の間は抜いたサムライから距離を取る・立ち合いでは相手の周りを回り、抜いたサムライの間合いでは先に横へ飛び退く・斬られたら回避で飛び退く。
// 相手を読む (強さ 3 から): 相手のサムライの振りの勢いと振り回す癖を数え、振り回す相手には届く所のすぐ外で待って空振りさせ (時々寄って振らせる)、
//   気切れの隙に気づいたら踏み込む。斬られると用心して待つ方へ寄る。たまに 2 番手の作戦を選ぶ (読まれにくく)。
// 強さ (1〜9) で反応の速さ・読みの確かさ・覚える速さ・狙いのぶれ・攻め気・弾きと回避の確率・連打の速さが変わる。1 はほとんど立っているだけ
internal static class DuelBot
{
    internal enum Kind : byte { Samurai, Impostor }

    private sealed class Bot
    {
        public byte Id;
        public Kind Kind;
        public int Level;
        public float NextThink, NextMove, NextStroke, KillReady, CounterAt = -1f, DodgeAt = -1f, DodgeUntil, MashCarry, Charge = -1f, GuardAt = -1f, TongueReady;
        public float MoveSpeed, DashReady, DashOutAt = -1f, DodgeReady, NextSide;
        public float Side = 1f;          // 回り込む向き (1 = 左回り・-1 = 右回り)
        public bool StrikeAfterDash;
        public byte Foe = 255;      // 斬り返す・飛び退く相手
        public Vector2 DodgeDir;        // 踏み込み・回避で動く向き (DodgeUntil まで MoveSpeed で)
        public bool Retreat;
        public float DetourSide;        // 塞がれて回り込む側 (0 = 回り込んでいない)
        // 相手の読み: 見ている相手・数えた振り・今の振りの勢い (≒ 1 秒の振りの数)・覚えた振り回しの癖 (0〜1)・斬られた後の用心 (0〜1)・隙に気づく時刻
        public byte Watched = 255;
        public int Seen;
        public float Heat, Spam, Caution, OpenAt = -1f, WhiffUntil, HitAt = -10f;
        public Plan Mode;
        public float ModeUntil, WaitKeep = 2.3f;
        public bool Escape;             // 斬られた後、のけぞりが明けたら踏み込みで抜ける
        public bool Masher;          // 確かめ用: 寄って気の続く限り振り回すだけ (TestMash)
        public float CalmUntil;         // 出した直後はこの時刻まで斬らずに間合いの外を回る
    }

    // 作戦: ふつうに戦う・相手の間合いのすぐ外で待つ (振り回しを空振りさせる)・相手の隙 (気切れ) に踏み込む
    private enum Plan : byte { Engage, Wait, Punish }

    private static readonly Dictionary<byte, Bot> Bots = new();
    private static readonly List<byte> Gone = new();
    private static readonly System.Random Rng = new();
    private static int _gen = -1;
    private static bool _hooked;
    private const float ThinkEvery = 0.05f, MoveEvery = 0.25f;
    private const float AggroPace = 2.5f;     // 攻め気 1 で 1 回の考えごとに仕掛ける割合の倍率
    private const float ReachIn = 0.3f;       // 斬れる距離よりこれだけ内側まで詰めて斬る
    private static float Reach => Samurai.SlashReach - ReachIn;
    private const float ChargeDistance = 2.7f; // 居合を溜める間に保つ距離
    private const float Keep = 3.2f;           // インポスターが待ち時間の間に取る距離
    private static float TongueNear => Samurai.SlashReach - 0.2f; // 立ち合い中のインポスターが詰める距離 (斬りの届く所の内側)
    private const float RegenTo = 75f;          // 納めて離れた時、ここまで気が溜まったら戻る
    private const float FirstKill = 8f;
    private const float SpawnCalm = 3f;        // 出してから斬り始めるまで (相手が構える間)
    private const float RiseCalm = 0.5f;       // 吹っ飛んだ相手が起き上がってから斬り始めるまで

    internal static int Dodges, Counters, Dashes, Circles, Detours, Waits, Punishes;
    private const float HeatKeep = 0.95f;     // 考えるたびに勢いに掛ける (ThinkEvery 0.05 で約 1 秒で薄れる = 勢い ≒ 1 秒あたりの振り)
    private const float CautionFade = 0.12f;  // 用心が 1 秒に薄れる分
    private const float OpenKi = 10f;         // 相手の気がこれより少なければ (続けて振る一振りの分も無い) 隙とみなす
    private const float WhiffWindow = 0.3f;   // 空振りの振り終わりへ踏み込める秒
    private const float Stick = 0.15f;       // 今の作戦を続ける贔屓 (値が拮抗して作戦が揺れないように)
    private static readonly int WalkMask = Constants.ShipAndObjectsMask | 1 << 12; // 本編の船と物の層に家具 (机) の層を足す
    private const float Body = 0.15f;         // 歩く先を確かめる太さ (体の当たりより少し細く: 机に押し付いていても前の面を拾う)

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

    // 吹っ飛ばされた時に受け身を取る割合 (ボットでなければ 0)
    internal static float TechChance(byte id) => Bots.TryGetValue(id, out var b) && b.Level >= 2 ? 0.1f + 0.8f * T(b) : 0f;
    private static float StrokeGap(Bot b) => 1.4f - 1.0f * T(b);
    private static float DashChance(Bot b) => b.Level < 3 ? 0f : 0.6f + 1.2f * T(b);    // 間合いの少し外にいる時に 1 秒あたり踏み込む見込み
    private static float MaxCharge(Bot b) => b.Level < 3 ? 0f : b.Level < 7 ? 0.8f : 1f; // 溜め切り (不意打ちで一撃) は 7 から

    private static bool Roll(float p) => Rng.NextDouble() < p;
    private static float Rand(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

    private const float MashGap = 0.22f;      // 振り回すだけのボットの振りの間

    // 確かめ用: サムライのボットを「寄って振り回すだけ」に切り替える (振り回す相手への読みを試す)
    internal static bool TestMash(byte id)
    {
        if (!Bots.TryGetValue(id, out var b) || b.Kind != Kind.Samurai) return false;
        b.Masher = !b.Masher;
        return true;
    }

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
        Bots[pc.PlayerId] = new Bot { Id = pc.PlayerId, Kind = kind, Level = Math.Clamp(level, 1, 9), KillReady = now + FirstKill, CalmUntil = now + SpawnCalm };
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
            // 吹っ飛ばされている間は体の速さを DuelTumble に任せ、考えるのも止める
            if (DuelTumble.Busy(b.Id)) continue;
            // 踏み込み・回避の飛び退きの間は向きと速さを毎フレーム書く
            if (now < b.DodgeUntil)
            {
                pc.MyPhysics.body.velocity = FxMath.V2(b.DodgeDir.x * b.MoveSpeed, b.DodgeDir.y * b.MoveSpeed);
                continue;
            }
            if (b.Kind == Kind.Samurai && Samurai.BotLocked(b.Id))
            {
                Dummy.StopWalk(pc);
                pc.MyPhysics.body.velocity = Samurai.BotLockPull(b.Id, pc);
                b.MashCarry += MashRate(b) * dt;
                int n = (int)b.MashCarry;
                b.MashCarry -= n;
                Samurai.BotMash(b.Id, n);
                continue;
            }
            if (b.CounterAt >= 0f && now >= b.CounterAt) Counter(b, pc);
            if (b.GuardAt >= 0f && now >= b.GuardAt) { b.GuardAt = -1f; DuelTongue.HostPress(pc); }
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

    // 一番近い、生きている人 (ダミーとボット以外)。同じ立ち合いの組の相手 (ボット同士の乱戦も) がいればそちらを先に
    private static PlayerControl Target(Bot b, PlayerControl self)
    {
        Vector2 me = self.GetTruePosition();
        PlayerControl best = null;
        float bestD = 81f;
        bool bestMate = false;
        bool imp = self.Data.Role && self.Data.Role.IsImpostor;
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            if (info == null || info.IsDead || info.Disconnected || info.PlayerId == b.Id) continue;
            bool mate = Duel.SameGroup(b.Id, info.PlayerId) && !(imp && info.Role && info.Role.IsImpostor);
            if (!mate && (bestMate || Bots.ContainsKey(info.PlayerId))) continue;
            var pc = info.Object;
            if (!pc || pc.inVent || !mate && pc.isDummy) continue;
            if (mate && !bestMate) { bestMate = true; bestD = float.MaxValue; }
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = pc; }
        }
        return best;
    }

    // ---- 相手を読む (考えるたび) ----

    // 相手のサムライの振りを数える: 勢い (今どれだけ振っているか) と癖 (間合いで振り回すか) を持ち、気切れの隙には反応の遅れの後に気づく
    private static void Read(Bot b, PlayerControl target, float d, float now)
    {
        b.Heat *= HeatKeep;
        b.Caution = FxMath.Max(b.Caution - CautionFade * ThinkEvery, 0f);
        if (RoleState.Of(target) is not Samurai ts) { b.Watched = 255; b.OpenAt = -1f; return; }
        int seen = ts.BotSeenStrokes;
        if (b.Watched != target.PlayerId) { b.Watched = target.PlayerId; b.Seen = seen; }
        int n = seen - b.Seen;
        b.Seen = seen;
        bool near = d < Samurai.SlashReach + 1f;
        if (n > 0)
        {
            b.Heat += n;
            if (near)
            {
                b.Spam += Learn(b) * n * (1f - b.Spam);
                // 間合いで振られたら、反応の遅れの後に作戦を選び直す
                b.ModeUntil = FxMath.Min(b.ModeUntil, now + Reaction(b));
                // 届く所のすぐ外で空振りさせたら、振り終わりへ踏み込む (強さ 4 から)
                if (d > Samurai.SlashReach && b.Level >= 4 && now >= b.WhiffUntil && Roll(0.2f + 0.5f * T(b)))
                {
                    b.OpenAt = now + Reaction(b) * 0.6f;
                    b.WhiffUntil = b.OpenAt + WhiffWindow;
                }
            }
        }
        // 抜いたまま 1 秒ほど振らずに構えている間は、癖を少しずつ忘れる
        else if (near && ts.BotDrawn && b.Heat < 0.5f) b.Spam = FxMath.Max(b.Spam - Learn(b) * 0.1f, 0f);
        bool open = ts.ShownSpent || ts.BotDrawn && ts.ShownKi < OpenKi || now < b.WhiffUntil;
        if (!open) b.OpenAt = -1f;
        else if (b.OpenAt < 0f) b.OpenAt = now + Reaction(b) * Rand(0.8f, 1.3f);
    }

    // 癖を覚える速さ (1 振りあたり)。強さ 3 から
    private static float Learn(Bot b) => b.Level < 3 ? 0f : 0.03f + 0.09f * T(b);

    // 作戦を選ぶ。隙に気づいたら踏み込み、それ以外は時々「待つ」と「戦う」を値で比べ直す (たまに 2 番手を選んで読まれにくくする)
    private static void Decide(Bot b, float d, float now)
    {
        if (b.Level < 3) { b.Mode = Plan.Engage; return; }
        if (b.OpenAt >= 0f && now >= b.OpenAt)
        {
            if (b.Mode != Plan.Punish) Punishes++;
            b.Mode = Plan.Punish;
            return;
        }
        if (b.Mode == Plan.Punish) { b.Mode = Plan.Engage; b.ModeUntil = 0f; }
        if (now < b.ModeUntil) return;
        b.ModeUntil = now + Rand(0.35f, 0.8f);
        // 間合いで振り回されている・振り回されて斬られた直後は、値で比べずにすぐ待つ (2 番手も選ばない)
        if (b.Heat >= 2f && d < Samurai.SlashReach + 1f || now - b.HitAt < 1f && b.Heat >= 1f)
        {
            if (b.Mode != Plan.Wait) Waits++;
            b.Mode = Plan.Wait;
            b.WaitKeep = Samurai.SlashReach + Rand(0.5f, 1f);
            return;
        }
        // 待つ値 = 今の勢い・覚えた癖・斬られた後の用心。読みの確かさは強さで
        float wait = (FxMath.Min(b.Heat / 2.5f, 1f) * 0.6f + b.Spam * 0.7f + b.Caution * 0.5f) * (0.5f + 0.5f * T(b));
        float engage = 0.35f;
        if (b.Mode == Plan.Wait) wait += Stick;
        else engage += Stick;
        bool w = wait > engage;
        if (Roll(0.12f - 0.08f * T(b))) w = !w;
        if (!w) { b.Mode = Plan.Engage; return; }
        if (b.Mode != Plan.Wait) Waits++;
        b.Mode = Plan.Wait;
        // 待つ距離 = 相手の届く所の少し外。強さ 5 からは時々届く所のすぐ外まで寄って振らせる (誘い)
        bool bait = b.Level >= 5 && Roll(0.2f + 0.2f * T(b));
        b.WaitKeep = Samurai.SlashReach + (bait ? 0.1f : Rand(0.5f, 1f));
        if (bait) b.ModeUntil = now + Rand(0.25f, 0.45f);
    }

    // ---- サムライ ----

    private static void ThinkSamurai(Bot b, PlayerControl pc, Samurai s, PlayerControl target, float now)
    {
        if (Aggro(b) <= 0f) { Dummy.StopWalk(pc); return; }
        Vector2 me = pc.GetTruePosition(), at = target.GetTruePosition();
        float dx = at.x - me.x, dy = at.y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (b.Masher)
        {
            Toward(b, pc, at, now);
            if (d <= Samurai.SlashReach + 1f && now >= b.NextStroke)
            {
                Strike(b, pc, s, dx, dy, now, 0f);
                b.NextStroke = now + MashGap;
            }
            return;
        }
        // 出した直後と、相手が吹っ飛んでいる (受けも避けもできない) 間と起き上がった直後は斬らずに間合いの外を回る (居合を溜める間も同じ距離)
        if (DuelTumble.Busy(target.PlayerId)) b.CalmUntil = FxMath.Max(b.CalmUntil, now + RiseCalm);
        if (now < b.CalmUntil)
        {
            if (d < ChargeDistance - 0.3f) Away(b, pc, me, dx, dy, d, ChargeDistance, now);
            else Circle(b, pc, me, at, d, ChargeDistance, now);
            return;
        }
        Read(b, target, d, now);
        Decide(b, d, now);
        // 気切れの間は何もできないので離れる
        if (s.BotSpent)
        {
            Away(b, pc, me, dx, dy, d, ChargeDistance + 0.5f, now);
            return;
        }
        if (b.Escape)
        {
            b.Escape = false;
            if (d < Samurai.SlashReach + 0.5f && DashBot(b, pc, s, Turn(-dx, -dy, b.Side * Rand(20f, 60f)), 2.5f, now)) return;
        }
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
            // 立ち合いの外の相手に溜め切りを当てると一撃なので、ボットはそこまで溜めない (最初の一太刀で立ち合いを始める)
            float max = Duel.InDuel(target.PlayerId) ? MaxCharge(b) : FxMath.Min(MaxCharge(b), Samurai.FullCharge - 0.05f);
            if (b.Charge < 0f) b.Charge = max * Rand(0.5f, 1f);
            float charge = FxMath.Clamp01((s.BotSheathedFor - 0.12f) / Samurai.ChargeTime);
            // 相手の隙には溜め切らずに居合で踏み込む
            bool punish = b.Mode == Plan.Punish;
            bool ready = s.BotSheathedFor > 0.5f && (charge >= b.Charge || punish);
            if (!ready)
            {
                // 振り回す相手の前では、溜める間も相手の届く所の外にいる
                float hold = b.Mode == Plan.Wait ? FxMath.Max(ChargeDistance, b.WaitKeep) : ChargeDistance;
                if (d < hold - 0.3f) Away(b, pc, me, dx, dy, d, hold, now);
                else if (d > hold + 0.5f) Toward(b, pc, at, now);
                else Circle(b, pc, me, at, d, hold, now);
                return;
            }
            if (d > Reach) { Toward(b, pc, at, now); return; }
            if (!punish && !Roll(Aggro(b) * ThinkEvery * AggroPace)) return;
            Strike(b, pc, s, dx, dy, now, FxMath.Min(charge, max));
            b.Charge = -1f;
            return;
        }
        // 踏み込んだ直後: 届けばそのまま斬り、斬った後は時々踏み込みで抜ける
        if (b.StrikeAfterDash)
        {
            b.StrikeAfterDash = false;
            if (d <= Reach + ReachIn)
            {
                Strike(b, pc, s, dx, dy, now, 0f);
                if (Roll(0.3f + 0.4f * T(b))) b.DashOutAt = b.NextStroke - StrokeGap(b) + 0.05f;
                return;
            }
        }
        if (b.DashOutAt >= 0f && now >= b.DashOutAt)
        {
            b.DashOutAt = -1f;
            if (DashBot(b, pc, s, Turn(-dx, -dy, b.Side * Rand(30f, 60f)), 9f, now)) return;
        }
        // 隙 (相手の気切れ): 踏み込んで間を空けずに斬る
        if (b.Mode == Plan.Punish)
        {
            if (d > Reach)
            {
                if (d < Reach + 3.2f && now >= b.DashReady && DashBot(b, pc, s, FxMath.V2(dx, dy), d - 1.1f, now))
                {
                    b.StrikeAfterDash = true;
                    return;
                }
                Toward(b, pc, at, now);
                return;
            }
            // 重なるほど近いと刀が向きを取れないので、少し離れながら斬る
            if (d < 0.7f) Away(b, pc, me, dx, dy, d, 1.2f, now);
            else if (now >= b.NextStroke - StrokeGap(b) * 0.6f) Strike(b, pc, s, dx, dy, now, 0f);
            return;
        }
        // 待つ: 相手の届く所の外を回り、振り回しを空振りさせる。届く所へ入られたら踏み込みで斜め後ろへ抜ける
        if (b.Mode == Plan.Wait)
        {
            if (d < Samurai.SlashReach + 0.4f && b.Level >= 5 && now >= b.DashReady
                && DashBot(b, pc, s, Turn(-dx, -dy, b.Side * Rand(20f, 50f)), b.WaitKeep - d + 0.3f, now))
                return;
            // 壁を背にしたら横へ抜ける (下がり続けると隅に詰まり、壁越しになって刀が届かない)
            if (d > 0.01f && Blocked(me, -dx / d, -dy / d, 1f))
            {
                if (b.Level >= 5 && now >= b.DashReady && DashBot(b, pc, s, Turn(-dx, -dy, b.Side * Rand(90f, 120f)), 2.5f, now)) return;
                Circle(b, pc, me, at, d, b.WaitKeep, now, 90f);
            }
            // 届く所の中なら回らずにまっすぐ下がる (回ると間合いに長く残る)
            else if (d < Samurai.SlashReach) Away(b, pc, me, dx, dy, d, b.WaitKeep, now);
            else Circle(b, pc, me, at, d, b.WaitKeep, now);
            return;
        }
        if (d > Reach)
        {
            if (d < Reach + 3.2f && now >= b.DashReady && Roll(DashChance(b) * ThinkEvery)
                && DashBot(b, pc, s, FxMath.V2(dx, dy), d - 1.1f, now))
            {
                b.StrikeAfterDash = true;
                return;
            }
            Toward(b, pc, at, now);
            return;
        }
        // 間合いの中で気に余裕があれば、時々横へ踏み込んで回り込む
        if (d >= 0.7f && now >= b.DashReady && s.BotKi > 70f && Roll(0.6f * T(b) * ThinkEvery)
            && DashBot(b, pc, s, Turn(dx, dy, b.Side * Rand(70f, 110f)), 9f, now))
            return;
        if (d < 0.7f) Away(b, pc, me, dx, dy, d, 1.2f, now);
        else Circle(b, pc, me, at, d, FxMath.Max(d, 1.3f), now);
        if (now >= b.NextStroke && Roll(Aggro(b) * ThinkEvery * AggroPace)) Strike(b, pc, s, dx, dy, now, 0f);
    }

    // 向き (x, y) へ踏み込む。進むのは dist まで (相手を突き抜けないように)
    private static bool DashBot(Bot b, PlayerControl pc, Samurai s, Vector2 dir, float dist, float now)
    {
        float l = FxMath.Sqrt(dir.x * dir.x + dir.y * dir.y);
        if (l < 0.01f || dist < 0.3f) return false;
        // 机や壁に当たって止まる向きへは踏み込まない (歩きの回り道に任せる)
        float go = FxMath.Min(dist, Samurai.BotDashTime * Samurai.BotDashSpeed);
        // 触れている壁は Blocked が数えないので、行き先との間に壁が無いかも見る (速い踏み込みは触れている壁を抜ける)
        Vector2 me = pc.GetTruePosition();
        if (Blocked(me, dir.x / l, dir.y / l, go)
            || PhysicsHelpers.AnythingBetween(me, FxMath.V2(me.x + dir.x / l * (go + Body), me.y + dir.y / l * (go + Body)), WalkMask, false)
            || !s.BotDash()) return false;
        Dummy.StopWalk(pc);
        Dashes++;
        b.DodgeDir = FxMath.V2(dir.x / l, dir.y / l);
        b.MoveSpeed = Samurai.BotDashSpeed;
        b.DodgeUntil = now + FxMath.Min(Samurai.BotDashTime, dist / Samurai.BotDashSpeed);
        b.DashReady = now + Rand(0.8f, 2.4f) - 0.5f * T(b);
        return true;
    }

    // (x, y) を deg 度回した向き
    private static Vector2 Turn(float x, float y, float deg)
    {
        float r = deg * FxMath.Deg2Rad, c = FxMath.Cos(r), sn = FxMath.Sin(r);
        return FxMath.V2(x * c - y * sn, x * sn + y * c);
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
        // 振り回す癖を読んでいるほど、振りを待ち構えて早く確かに斬り返す
        float read = b.Watched == attacker ? b.Spam : 0f;
        float react = Reaction(b) * Rand(0.6f, 1.1f) * (1f - 0.5f * read);
        float chance = ClashChance(b);
        if (react >= window - 0.02f || !Roll(chance + (1f - chance) * 0.6f * read)) return;
        b.CounterAt = Time.time + react;
        b.Foe = attacker;
    }

    // 立ち合いの相手のサムライの斬りが保留された (ホスト)。読みが当たれば口で受ける
    internal static void OnHeldGuard(byte bot, byte attacker, float window)
    {
        if (!Bots.TryGetValue(bot, out var b) || b.Kind != Kind.Impostor || b.GuardAt >= 0f) return;
        float read = b.Watched == attacker ? b.Spam : 0f;
        float chance = ClashChance(b) * 0.7f;
        if (!Roll(chance + (1f - chance) * 0.5f * read)) return;
        b.GuardAt = Time.time + Rand(0.02f, window - 0.03f);
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
        Read(b, target, d, now);
        Decide(b, d, now);
        // サムライと立ち合っている間は斬りの届く所まで踏み込んで舌を撃つ (届かない所に構えると練習にならない)
        if (Duel.SameGroup(pc.PlayerId, target.PlayerId) && DuelTongue.Engages(pc, target.PlayerId))
        {
            // 隙 (相手の気切れ): 間を詰めて待ち時間が明け次第に舌を撃つ
            if (b.Mode == Plan.Punish)
            {
                if (d > TongueNear - 0.4f) Toward(b, pc, at, now);
                else Dummy.StopWalk(pc);
                if (now >= b.TongueReady) Tongue(b, pc, now);
                return;
            }
            // 待つ: 刀の届く所の外 (舌は届く) を回り、時々そこから舌を撃つ
            if (b.Mode == Plan.Wait)
            {
                Circle(b, pc, me, at, d, FxMath.Min(b.WaitKeep, DuelTongue.Reach - 0.4f), now);
                if (now >= b.TongueReady && Roll(Aggro(b) * ThinkEvery * AggroPace * 0.5f)) Tongue(b, pc, now);
                return;
            }
            if (d > TongueNear) { Toward(b, pc, at, now); return; }
            if (d < TongueNear - 0.7f) { Away(b, pc, me, dx, dy, d, TongueNear - 0.3f, now); }
            else Circle(b, pc, me, at, d, TongueNear - 0.2f, now);
            // 抜いたサムライの間合いにいる間は、斬られる前に時々横へ飛び退く (回避の回数は斬られた後の分と共有)
            if (b.Level >= 3 && now >= b.DodgeReady && d < 2.6f && RoleState.Of(target) is Samurai sam && sam.BotDrawn
                && Roll(DodgeChance(b) * 0.4f * ThinkEvery))
            {
                b.Foe = target.PlayerId;
                b.DodgeReady = now + Rand(1.5f, 3f);
                if (DodgeAway(b, pc, now, b.Side * Rand(50f, 80f))) return;
            }
            if (now >= b.TongueReady && Roll(Aggro(b) * ThinkEvery * AggroPace)) Tongue(b, pc, now);
            return;
        }
        // 相手のサムライには本編の一撃のキルを使わず、舌の届く所で立ち合いを挑む
        if (RoleState.Of(target) is Samurai && !Duel.InDuel(pc.PlayerId) && (!Duel.InDuel(target.PlayerId) || Duel.HasRoom(target.PlayerId)))
        {
            if (d > DuelTongue.Reach * 0.9f) { Toward(b, pc, at, now); return; }
            Duel.HostJoin(pc.PlayerId, target.PlayerId);
            b.TongueReady = now + 0.5f;
            return;
        }
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

    private static void Tongue(Bot b, PlayerControl pc, float now)
    {
        DuelTongue.HostPress(pc);
        b.TongueReady = now + DuelTongue.Cooldown + Rand(0.1f, 1.2f - 0.9f * T(b));
    }

    // 立ち合いで斬られたボットは用心を増やす (強いほど)。インポスターは反応が間に合えば飛び退く
    private static void OnHit(byte a, byte t, int hp)
    {
        if (a == t || hp <= 0 || !Bots.TryGetValue(t, out var b)) return;
        if (b.Level >= 3)
        {
            b.Caution = FxMath.Min(b.Caution + 0.25f + 0.35f * T(b), 1f);
            b.HitAt = Time.time;
            b.ModeUntil = FxMath.Min(b.ModeUntil, Time.time + Reaction(b) * 0.5f);
        }
        // サムライはのけぞりが明けたら踏み込みで抜けて、続けて斬られるのを避ける (強さ 4 から)
        if (b.Kind == Kind.Samurai && b.Level >= 4 && Roll(0.3f + 0.6f * T(b))) { b.Escape = true; b.Foe = a; }
        if (b.Kind != Kind.Impostor) return;
        if (b.DodgeAt >= 0f || !Roll(DodgeChance(b))) return;
        b.DodgeAt = Time.time + Reaction(b) * 0.5f;
        b.Foe = a;
    }

    // 相手から離れる向きを turn 度回した向きへ飛び退く
    private static bool DodgeAway(Bot b, PlayerControl pc, float now, float turn = 0f)
    {
        var o = Player(b.Foe);
        if (!o || !DuelDodge.BotDodge(b.Id)) return false;
        Vector2 me = pc.GetTruePosition(), at = o.GetTruePosition();
        var dir = Turn(me.x - at.x, me.y - at.y, turn);
        float l = FxMath.Sqrt(dir.x * dir.x + dir.y * dir.y);
        if (l < 0.01f) { dir = FxMath.V2(1f, 0f); l = 1f; }
        Dummy.StopWalk(pc);
        Dodges++;
        b.DodgeDir = FxMath.V2(dir.x / l, dir.y / l);
        b.MoveSpeed = DuelDodge.Speed;
        b.DodgeUntil = now + DuelDodge.BotDodgeTime;
        return true;
    }

    // ---- 歩き (歩き先は MoveEvery ごとに決め直す) ----

    private static void Toward(Bot b, PlayerControl pc, Vector2 at, float now)
    {
        if (now < b.NextMove && Dummy.IsWalking(b.Id)) return;
        b.NextMove = now + MoveEvery;
        Go(b, pc, pc.GetTruePosition(), at.x, at.y);
    }

    // 相手の周りを回る: 相手から見た自分の向きを少し回した、距離 keep の所へ歩く。回る向きはときどき変える。強さ 2 から
    private static void Circle(Bot b, PlayerControl pc, Vector2 me, Vector2 at, float d, float keep, float now, float turn = 35f)
    {
        if (b.Level < 2) { Dummy.StopWalk(pc); return; }
        if (now < b.NextMove && Dummy.IsWalking(b.Id)) return;
        b.NextMove = now + MoveEvery;
        if (now >= b.NextSide)
        {
            b.Side = Roll(0.5f) ? 1f : -1f;
            b.NextSide = now + Rand(0.8f, 2.5f);
        }
        if (d < 0.01f) d = 0.01f;
        Circles++;
        var u = Turn((me.x - at.x) / d, (me.y - at.y) / d, b.Side * turn);
        Go(b, pc, me, at.x + u.x * keep, at.y + u.y * keep);
    }

    // 相手 (me から dx, dy の所) から離れて距離 keep の所へ
    private static void Away(Bot b, PlayerControl pc, Vector2 me, float dx, float dy, float d, float keep, float now)
    {
        if (now < b.NextMove && Dummy.IsWalking(b.Id)) return;
        b.NextMove = now + MoveEvery;
        if (d < 0.01f) { dx = 1f; dy = 0f; d = 1f; }
        float k = (keep - d) / d;
        Go(b, pc, me, me.x - dx * k, me.y - dy * k);
    }

    // (x, y) へ歩く。まっすぐ歩くと机や壁に押し付いたまま止まるので、塞がれていたら向きを少しずつ回して通れる向きへ少し歩く。
    // 回る側は塞がれている間は変えない (丸い机は縁をなぞって回り込み、左右に迷わない)
    private static void Go(Bot b, PlayerControl pc, Vector2 me, float x, float y)
    {
        float dx = x - me.x, dy = y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        float step = FxMath.Min(d, 1.2f);
        if (d < 0.05f || !Blocked(me, dx / d, dy / d, step)) { b.DetourSide = 0f; Dummy.WalkTo(pc, x, y); return; }
        if (b.DetourSide == 0f) b.DetourSide = Roll(0.5f) ? 1f : -1f;
        for (int i = 1; i <= 5; i++)
        for (int k = 0; k < 2; k++)
        {
            float side = k == 0 ? b.DetourSide : -b.DetourSide;
            var u = Turn(dx / d, dy / d, side * 30f * i);
            if (Blocked(me, u.x, u.y, step)) continue;
            b.DetourSide = side;
            Detours++;
            Dummy.WalkTo(pc, me.x + u.x * step, me.y + u.y * step);
            return;
        }
        Dummy.WalkTo(pc, x, y);
    }

    // me から向き (ux, uy) へ len 進む間に、壁か机の当たり判定があるか。始めから触れている当たりは数えない
    private static bool Blocked(Vector2 me, float ux, float uy, float len)
    {
        foreach (var h in Physics2D.CircleCastAll(me, Body, FxMath.V2(ux, uy), len, WalkMask))
            if (h.collider && !h.collider.isTrigger && h.distance > 0.02f) return true;
        return false;
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"bots={Bots.Count} dodges={Dodges} counters={Counters} dashes={Dashes} circles={Circles} detours={Detours} waits={Waits} punishes={Punishes} faced={DuelFacing.Turned}");
        foreach (var b in Bots.Values)
        {
            sb.Append($" [{b.Id} {b.Kind} Lv{b.Level} plan={b.Mode} heat={b.Heat:0.0} spam={b.Spam:0.00} caution={b.Caution:0.00} retreat={b.Retreat} charge={b.Charge:0.00} kill={FxMath.Max(b.KillReady - Time.time, 0f):0.0}s");
            if (RoleState.Of(b.Id) is Samurai s) sb.Append($" ki={s.BotKi:0} drawn={s.BotDrawn}");
            sb.Append(']');
        }
        return sb.ToString();
    }
}
