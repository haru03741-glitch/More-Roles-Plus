using System;
using System.Collections.Generic;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ダミーの簡単な自動行動 (ホストの端末だけで考え、行動は本物のプレイヤーと同じ道で送る)。
// クルー = タスクの端末へ歩いて数秒立ち、タスクを終える / キル役 = 端末をうろつき、目撃者がいない時に近くの人をキル /
// 死体のそばを通れば通報 / 会議は話し合いの後に投票。死んだダミーは何もしない。
// 既定ではオンラインの部屋だけで動く (フリープレイやローカルの部屋のダミーは立っているだけの的のまま)
internal static class DummyBrain
{
    public static bool? Forced;
    private static bool On => Forced ?? AmongUsClient.Instance.NetworkMode == NetworkModes.OnlineGame;

    private const long ThinkMs = 200;
    private const long FirstKillMs = 10000;   // 試合の始めの待ち時間 (本編のインポスターと同じ)
    private const float ReportRange = 2.5f;
    private const float WitnessRange = 3f;

    private sealed class Mind
    {
        public long NextThinkMs, WorkUntilMs, KillReadyMs, VoteAtMs;
        public bool HasGoal, Working, Voted;
        public uint TaskId;
        public bool TaskIsReal;
    }

    private static readonly Dictionary<byte, Mind> Minds = new();
    private static readonly Dictionary<byte, byte> KilledBy = new();   // ダミーが殺した人 → 殺したダミー (自分が殺した死体は通報しない)
    private static readonly List<DeadBody> Bodies = new();
    private static readonly List<float> Xs = new(), Ys = new();
    private static readonly System.Random Rng = new();
    private static long _bodiesMs;
    private static bool _wasMeeting;
    private static int _gen = -1;

    // 端末の位置 (タスクの種類ごと)。船ごとに 1 回作る
    private static readonly Dictionary<int, List<Vector2>> Consoles = new();
    private static readonly List<Vector2> AnyConsole = new();

    public static void Tick()
    {
        var all = Dummy.All;
        if (all.Count == 0) return;
        var client = AmongUsClient.Instance;
        if (!client.AmHost || !On || client.IsGameOver || !ShipStatus.Instance || !GameData.Instance)
        {
            if (Minds.Count > 0) Reset();
            return;
        }
        if (_gen != GameClock.ShipGen) { Reset(); _gen = GameClock.ShipGen; BuildConsoles(); }
        long now = Environment.TickCount64;
        var hud = MeetingHud.Instance;
        bool meeting = hud || ExileController.Instance;
        if (meeting != _wasMeeting)
        {
            _wasMeeting = meeting;
            foreach (var pc in all) if (pc) Dummy.StopWalk(pc);
            foreach (var m in Minds.Values)
            {
                m.HasGoal = m.Working = m.Voted = false;
                m.VoteAtMs = 0;
                // 会議の後はキルの待ち時間が最初から (本編と同じ)
                if (!meeting) m.KillReadyMs = now + KillCooldownMs();
            }
        }
        if (hud) { Votes(hud, all, now); return; }
        if (meeting) return;
        if (now >= _bodiesMs)
        {
            _bodiesMs = now + 500;
            Bodies.Clear();
            foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>()) Bodies.Add(b);
        }
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected || pc == DummyPilot.Possessed) continue;
            if (!Minds.TryGetValue(pc.PlayerId, out var m))
                Minds[pc.PlayerId] = m = new Mind { KillReadyMs = now + FirstKillMs };
            if (now < m.NextThinkMs) continue;
            m.NextThinkMs = now + ThinkMs;
            if (Think(pc, m, now)) return; // 会議を開いた・キルした時はこのフレームの残りを考えない
        }
    }

    private static bool Think(PlayerControl pc, Mind m, long now)
    {
        if (!pc.CanMove) return false;
        var pos = pc.GetTruePosition();
        bool killer = IsKiller(pc);

        // 死体の通報
        for (int i = 0; i < Bodies.Count; i++)
        {
            var b = Bodies[i];
            if (!b || (KilledBy.TryGetValue(b.ParentId, out byte k) && k == pc.PlayerId)) continue;
            var bp = b.TruePosition;
            if (Dist(bp.x, bp.y, pos.x, pos.y) > ReportRange) continue;
            var info = GameData.Instance.GetPlayerById(b.ParentId);
            if (info == null) continue;
            Dummy.StopWalk(pc);
            Dummy.Report(pc, info);
            return true;
        }

        // キル
        if (killer && now >= m.KillReadyMs)
        {
            var target = KillTarget(pc, pos.x, pos.y);
            if (target)
            {
                Dummy.StopWalk(pc);
                string why = Dummy.Kill(pc, target);
                if (why == null)
                {
                    KilledBy[target.PlayerId] = pc.PlayerId;
                    m.KillReadyMs = now + KillCooldownMs();
                    m.HasGoal = m.Working = false;
                    return true;
                }
                m.KillReadyMs = now + 1000; // 断られたら少し待ってから
            }
        }

        // タスクの最中
        if (m.Working)
        {
            if (now < m.WorkUntilMs) return false;
            m.Working = false;
            m.HasGoal = false;
            if (m.TaskIsReal && !killer) pc.RpcCompleteTask(m.TaskId);
            return false;
        }
        if (Dummy.IsWalking(pc.PlayerId)) return false;
        bool stuck = Dummy.TakeStuck(pc.PlayerId);
        if (m.HasGoal && !stuck)
        {
            // 着いた: 数秒立ってから終える
            m.Working = true;
            m.WorkUntilMs = now + 2000 + Rng.Next(2500);
            return false;
        }
        PickGoal(pc, m, pos.x, pos.y, killer);
        return false;
    }

    private static void PickGoal(PlayerControl pc, Mind m, float x, float y, bool killer)
    {
        m.HasGoal = false;
        // クルーは終わっていないタスク、キル役はどこかの端末
        var tasks = pc.Data.Tasks;
        for (int tries = 0; tries < 4; tries++)
        {
            Vector2 at;
            m.TaskIsReal = false;
            if (!killer && tasks != null && tasks.Count > 0)
            {
                var open = new List<NetworkedPlayerInfo.TaskInfo>();
                foreach (var t in tasks) if (t != null && !t.Complete) open.Add(t);
                if (open.Count == 0) { if (!Random(out at)) return; }
                else
                {
                    var t = open[Rng.Next(open.Count)];
                    m.TaskId = t.Id;
                    m.TaskIsReal = true;
                    if (!ConsoleFor(t.TypeId, out at)) continue;
                }
            }
            else if (killer && Rng.Next(2) == 0 && Prey(pc, out at)) { }
            else if (!Random(out at)) return;
            if (!DummyNav.Path(x, y, at.x, at.y, Xs, Ys)) continue;
            Dummy.WalkPath(pc, Xs.ToArray(), Ys.ToArray());
            m.HasGoal = true;
            return;
        }
    }

    private static void Votes(MeetingHud hud, List<PlayerControl> all, long now)
    {
        // 話し合いの間は投票できない (本編の人と同じ)
        var st = hud.state;
        if (st != MeetingHud.MeetingStates.NotVoted && st != MeetingHud.MeetingStates.Voted) return;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc || pc.Data == null || pc.Data.IsDead || pc == DummyPilot.Voter) continue;
            // 試合の始めすぐの会議だと、まだ考えたことのないダミーもいる
            if (!Minds.TryGetValue(pc.PlayerId, out var m)) Minds[pc.PlayerId] = m = new Mind { KillReadyMs = now + KillCooldownMs() };
            if (m.Voted) continue;
            if (m.VoteAtMs == 0) { m.VoteAtMs = now + 2000 + Rng.Next(4000); continue; }
            if (now < m.VoteAtMs) continue;
            m.Voted = true;
            Dummy.Vote(pc, VoteTarget(pc));
        }
    }

    private static byte VoteTarget(PlayerControl self)
    {
        if (Rng.Next(10) < 4) return 253; // スキップ
        bool imp = self.Data.Role && self.Data.Role.IsImpostor;
        var cands = new List<byte>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p == self || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
            if (imp && p.Data.Role && p.Data.Role.IsImpostor) continue;
            cands.Add(p.PlayerId);
        }
        return cands.Count == 0 ? (byte)253 : cands[Rng.Next(cands.Count)];
    }

    private static PlayerControl KillTarget(PlayerControl self, float x, float y)
    {
        float reach = GameManager.Instance.LogicOptions.GetKillDistance();
        bool imp = self.Data.Role && self.Data.Role.IsImpostor;
        PlayerControl best = null;
        float bestD = float.MaxValue;
        int near = 0;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p == self || p.Data == null || p.Data.IsDead || p.Data.Disconnected || p.inVent) continue;
            if (imp && p.Data.Role && p.Data.Role.IsImpostor) continue;
            var q = p.GetTruePosition();
            float d = Dist(q.x, q.y, x, y);
            if (d <= WitnessRange) near++;
            if (d <= reach && d < bestD) { bestD = d; best = p; }
        }
        // 目撃者 (相手のほかに近くにいる人) がいたらやめる
        return best && near <= 1 ? best : null;
    }

    // キル役のうろつき先: 誰か (仲間のインポスターでない人) の今いる所
    private static bool Prey(PlayerControl self, out Vector2 at)
    {
        at = default;
        bool imp = self.Data.Role && self.Data.Role.IsImpostor;
        var cands = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p == self || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
            if (imp && p.Data.Role && p.Data.Role.IsImpostor) continue;
            cands.Add(p);
        }
        if (cands.Count == 0) return false;
        at = cands[Rng.Next(cands.Count)].GetTruePosition();
        return true;
    }

    private static bool IsKiller(PlayerControl pc) =>
        (pc.Data.Role && pc.Data.Role.IsImpostor) || KillAbility.UsesFor(pc);

    private static long KillCooldownMs()
    {
        float cd = GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown);
        return (long)(cd * 1000f);
    }

    private static void BuildConsoles()
    {
        Consoles.Clear();
        AnyConsole.Clear();
        var ship = ShipStatus.Instance;
        foreach (var c in ship.AllConsoles)
        {
            if (!c || c.TaskTypes == null) continue;
            Vector2 p = c.transform.position;
            AnyConsole.Add(p);
            foreach (var tt in c.TaskTypes)
            {
                if (!Consoles.TryGetValue((int)tt, out var list)) Consoles[(int)tt] = list = new List<Vector2>();
                list.Add(p);
            }
        }
    }

    private static bool ConsoleFor(byte typeId, out Vector2 at)
    {
        at = default;
        var task = ShipStatus.Instance.GetTaskById(typeId);
        if (!task || !Consoles.TryGetValue((int)task.TaskType, out var list) || list.Count == 0) return false;
        at = list[Rng.Next(list.Count)];
        return true;
    }

    private static bool Random(out Vector2 at)
    {
        at = default;
        if (AnyConsole.Count == 0) return false;
        at = AnyConsole[Rng.Next(AnyConsole.Count)];
        return true;
    }

    private static float Dist(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // テスト用: ダミーごとの今の状態
    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        long now = Environment.TickCount64;
        foreach (var (id, m) in Minds)
            sb.Append($" [{id} goal={m.HasGoal} work={m.Working} walk={Dummy.IsWalking(id)} task={(m.TaskIsReal ? m.TaskId.ToString() : "-")} kill={Math.Max(0, m.KillReadyMs - now) / 1000}s voted={m.Voted}]");
        return $"minds={Minds.Count}{sb}";
    }

    private static void Reset()
    {
        Minds.Clear();
        KilledBy.Clear();
        Bodies.Clear();
        _wasMeeting = false;
    }
}
