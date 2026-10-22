using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// 試合中の役職とアドオンの持ち主。PlayerId (0〜255) で引ける表にしておく (毎フレーム引いても軽いように)
public static class RoleState
{
    private static readonly RoleBase[] ByPlayer = new RoleBase[256];
    private static readonly List<AddonBase>[] AddonsByPlayer = new List<AddonBase>[256];
    private static readonly List<AddonBase> NoAddons = new();
    private static readonly List<RoleBase> Active = new();
    private static readonly List<AddonBase> ActiveAddons = new();

    public static RoleBase Of(byte playerId) => ByPlayer[playerId];
    public static RoleBase Of(PlayerControl p) => p ? ByPlayer[p.PlayerId] : null;

    // その人に付いているアドオン (付いた順・無ければ空)
    public static IReadOnlyList<AddonBase> AddonsOf(byte playerId) => AddonsByPlayer[playerId] ?? NoAddons;

    // 自分の役職 (無ければ null)
    public static RoleBase Local { get; private set; }

    public static IReadOnlyList<RoleBase> All => Active;
    public static IReadOnlyList<AddonBase> AllAddons => ActiveAddons;

    // 役職かアドオンが誰かに付いているか (どちらも無い試合は本編のまま)
    public static bool Any => Active.Count > 0 || ActiveAddons.Count > 0;

    // 今の試合の寿命 (役職の寿命の親)。役職の外で試合中だけ何かを購読する時に使う
    public static Lifespan Match => _match ??= new Lifespan();
    private static Lifespan _match;

    // 第三陣営の役職が誰かに付いている試合か (付いていない試合の勝敗は本編のまま)
    public static bool AnyNeutral { get; private set; }

    internal static void Assign(PlayerControl p, RoleBase proto, IReadOnlyList<AddonBase> addonProtos)
    {
        var role = (RoleBase)Activator.CreateInstance(proto.GetType());
        role.Player = p;
        role.PlayerId = p.PlayerId;
        role.Chance = proto.Chance;
        role.Count = proto.Count;
        ByPlayer[p.PlayerId] = role;
        Active.Add(role);
        if (p.AmOwner) Local = role;
        if (role.Team == Team.Neutral) AnyNeutral = true;
        role.Lifespan = Match.Child();
        EventBinder.Bind(role);
        KillAbility.OnAssigned(role);
        try { role.OnAssigned(); }
        catch (Exception e) { Plugin.Logger.LogError($"{role.Id}.OnAssigned: {e}"); }
        Abilities.OnAssigned(role);
        RoleFrame.OnAssigned(role);
        // アドオンは役職の後 (役職の OnAssigned で作った物を前提にできるように)
        AttachAddons(p, addonProtos, role.Lifespan);
        if (role.IsVanilla) RoleDisplay.OnAddonsOnly(p);
        else RoleDisplay.OnAssigned(role);
        if (Dev.DevGod.On) Dev.DevGod.Refresh();
    }

    // 役職が無い人 (本編の役職のまま) にアドオンだけ付ける。寿命は試合の寿命の子
    internal static void AssignAddonsOnly(PlayerControl p, IReadOnlyList<AddonBase> addonProtos)
    {
        AttachAddons(p, addonProtos, Match);
        RoleDisplay.OnAddonsOnly(p);
    }

    private static void AttachAddons(PlayerControl p, IReadOnlyList<AddonBase> addonProtos, Lifespan parent)
    {
        if (addonProtos == null) return;
        foreach (var ap in addonProtos)
        {
            var addon = (AddonBase)Activator.CreateInstance(ap.GetType());
            addon.Player = p;
            addon.PlayerId = p.PlayerId;
            addon.Chance = ap.Chance;
            addon.Count = ap.Count;
            addon.OnCrew = ap.OnCrew;
            addon.OnImpostor = ap.OnImpostor;
            addon.OnNeutral = ap.OnNeutral;
            (AddonsByPlayer[p.PlayerId] ??= new List<AddonBase>()).Add(addon);
            ActiveAddons.Add(addon);
            addon.Lifespan = parent.Child();
            EventBinder.Bind(addon);
            try { addon.OnAssigned(); }
            catch (Exception e) { Plugin.Logger.LogError($"{addon.Id}.OnAssigned: {e}"); }
            RoleFrame.OnAssigned(addon);
        }
    }

    internal static void Clear(bool gameEnded)
    {
        if (gameEnded && Active.Count > 0) Events<GameEndEvent>.Run(new GameEndEvent { Result = GameEnd.Last });
        foreach (var r in Active) ByPlayer[r.PlayerId] = null;
        foreach (var a in ActiveAddons) AddonsByPlayer[a.PlayerId] = null;
        Active.Clear();
        ActiveAddons.Clear();
        MeetingEndWatch.Reset();
        // 役職の寿命 (購読・作った物) は試合の寿命ごと切る
        _match?.Release();
        _match = null;
        Local = null;
        KillAbility.Clear();
        Abilities.Clear();
        RoleFrame.Clear();
        AnyNeutral = false;
        RoleDisplay.Clear();
        Dev.DevGod.Clear();
    }
}

[Settings(Tab.General, "RoleSettings.Title", order: -100)]
public static class RoleSettings
{
    public static readonly BoolOpt Enabled = new("RoleSettings.Enabled", true);
    public static readonly BoolOpt GhostsSeeRoles = new("RoleSettings.GhostsSeeRoles", true);
    public static readonly BoolOpt ImpostorsSeeRoles = new("RoleSettings.ImpostorsSeeRoles", true);
}

// 配り方。行は本編の「ゲーム設定」タブに並べる (GameTabRows)。
// 素のクルーとインポスターも役職の 1 つなので、役職の付く人の数は出現率と人数で決まる (VanillaCrewmate / VanillaImpostor)。
// 第三陣営の数は第三陣営の設定 (NeutralKillSettings)・インポスターの数は ImpostorRange と本編の設定にある
[Settings(Tab.General, "AssignSettings.Title", order: -97)]
public static class AssignSettings
{
    public static readonly IntOpt MaxAddonsPerPlayer = new("AssignSettings.MaxAddonsPerPlayer", 1, 0, 3) { OffTab = true };

    // 出現率の抽選で役職が付かなかった人の扱い。「埋める」は出現率を重みにして、人数の残っている役職から配る
    // (出現率を低くしても、役職を入れた分だけ誰かに付く)。第三陣営を埋めに使うとクルーが減りすぎるので既定は同じ陣営だけ
    public static readonly ChoiceOpt Fill = new("AssignSettings.Fill", 1,
        new Text("AssignSettings.KeepVanilla"),
        new Text("AssignSettings.FillFromSameTeamRoles"),
        new Text("AssignSettings.FillNeutralsIncluded")) { OffTab = true };

    // 前の試合で外れた人ほどインポスター・第三陣営・役職に当たりやすくする (AssignHistory)
    public static readonly BoolOpt Fairness = new("AssignSettings.Fairness", true) { OffTab = true };
}

// 配る前の名簿の 1 人 (本編が決めた陣営と土台の役職)
public struct Slot
{
    public byte PlayerId;
    public bool Impostor;
    public RoleTypes BaseRole;
    public int Key; // 公平化の履歴の鍵 (ClientId・ダミーは -1)
}

// 配布案の 1 人分 (役職の番号 = Registry.Roles の添字・アドオンの番号 = Registry.Addons の添字)
public sealed class Pick
{
    public byte PlayerId;
    public int RoleIndex;
    public readonly List<int> Addons = new();
}

// 配り方そのもの。名簿と設定と乱数だけから配布案を決める (本編の物を読まないので、人数や回数を変えて何度でも回せる = ブリッジ assignsim)
public static class AssignPlan
{
    // 配り方の内訳 (確かめ用)
    public sealed class PlanStats
    {
        public int LimitSkips, ConflictSkips, NoSlotSkips, Reserved, FillPicks, NeutralTopUps;
    }

    // reserved = 固定指定 (PlayerId → 役職の番号)。枠の数・上限より先に付ける。
    // history があれば、役職を付ける人を公平化の重みで選ぶ (無ければ混ぜた名簿の先頭から = 一様)
    public static List<Pick> Decide(IReadOnlyList<Slot> roster, Random rng, IReadOnlyDictionary<byte, int> reserved = null, PlanStats stats = null, AssignHistory history = null)
    {
        // 出現率 100% の枠を先に、残りは抽選で通った枠を混ぜてから並べる
        var sure = new List<int>();
        var lucky = new List<int>();
        for (int i = 0; i < Registry.Roles.Count; i++)
        {
            var r = Registry.Roles[i];
            int chance = r.Chance;
            if (chance <= 0 || !Usable(r)) continue;
            for (int k = 0; k < r.Count.Value; k++)
            {
                if (chance >= 100) sure.Add(i);
                else if (rng.Next(100) < chance) lucky.Add(i);
            }
        }
        Shuffle(sure, rng);
        Shuffle(lucky, rng);

        var free = new List<Slot>(roster);
        Shuffle(free, rng);

        var plan = new List<Pick>();
        var chosen = new List<RoleBase>();
        var taken = new int[Registry.Roles.Count];
        int neutrals = 0, killers = 0;
        // 陣営ごとの最少〜最多は https://github.com/Gurge44/EndlessHostRoles Modules/CustomRoleSelector.cs に倣う (GPL-3.0)。
        // 第三陣営の最少〜最多と、そのうちキル役の最多 (0 = 出さない)。人数の帯 (TeamSizes) があればその人数の帯
        // (ロビーでは帯の値がゲーム設定の行へ写っているので同じ値。人数を変えて回す時も帯どおりになるように)、無ければゲーム設定の値
        int minNeutral, maxNeutral, maxKillers;
        if (TeamSizes.For(roster.Count) is { } band)
        {
            minNeutral = band.MinNeutrals;
            maxNeutral = band.MaxNeutrals;
            maxKillers = band.MaxNeutralKillers;
        }
        else
        {
            maxNeutral = NeutralKillSettings.MaxNeutrals.Value;
            maxKillers = Math.Min(NeutralKillSettings.MaxKillers.Value, maxNeutral);
            minNeutral = Math.Min(NeutralKillSettings.MinNeutrals.Value, maxNeutral);
        }
        void Take(int slotIndex, int roleIndex)
        {
            var role = Registry.Roles[roleIndex];
            plan.Add(new Pick { PlayerId = free[slotIndex].PlayerId, RoleIndex = roleIndex });
            free.RemoveAt(slotIndex);
            chosen.Add(role);
            taken[roleIndex]++;
            if (role.Team == Team.Neutral)
            {
                neutrals++;
                if (role.IsKiller) killers++;
            }
        }

        bool OverLimit(RoleBase role)
        {
            bool neutral = role.Team == Team.Neutral;
            return (neutral && neutrals >= maxNeutral)
                || (neutral && role.IsKiller && killers >= maxKillers);
        }

        // 第三陣営の最少に足りない分を付けられる役職 (出現率 > 0・人数が残っている・上限と同時に出ない組に掛からない)
        List<int> NeutralPool()
        {
            var pool = new List<int>();
            for (int i = 0; i < Registry.Roles.Count; i++)
            {
                var r = Registry.Roles[i];
                if (r.Team != Team.Neutral || r.Chance <= 0 || taken[i] >= r.Count.Value || !Usable(r)) continue;
                if (OverLimit(r) || Conflicts(r, chosen)) continue;
                pool.Add(i);
            }
            return pool;
        }

        // 第三陣営の最少のために空けておくクルーの席の数 (付けられる第三陣営の役職の残りの数まで)
        int Reserve()
        {
            int need = minNeutral - neutrals;
            if (need <= 0) return 0;
            int room = 0;
            foreach (int i in NeutralPool()) room += Registry.Roles[i].Count.Value - taken[i];
            return Math.Min(need, room);
        }

        int CrewSeats() => free.Count(f => !f.Impostor);

        // 本編の役職がちょうど土台の役職と同じ人を優先し、いなければ同じ陣営の誰か
        int SlotFor(int roleIndex)
        {
            var role = Registry.Roles[roleIndex];
            bool exact = free.Exists(s => s.BaseRole == role.BaseRole);
            bool Fits(Slot s) => exact ? s.BaseRole == role.BaseRole : s.Impostor == (role.Team == Team.Impostor);
            if (history == null) return free.FindIndex(Fits);
            // 公平化: 役職は重みの大きい人へ、素のクルーとインポスターは最近役職に当たった人 (重みの小さい人) へ寄せる
            bool plain = AssignHistory.IsPlain(roleIndex);
            var kind = role.Team == Team.Neutral ? AssignHistory.Kind.Neutral : AssignHistory.Kind.Role;
            double total = 0;
            var weights = new double[free.Count];
            for (int i = 0; i < free.Count; i++)
            {
                if (!Fits(free[i])) continue;
                double w = history.Weight(free[i].Key, kind, roleIndex);
                weights[i] = plain ? 1.0 / w : w;
                total += weights[i];
            }
            if (total <= 0) return -1;
            double roll = rng.NextDouble() * total;
            int last = -1;
            for (int i = 0; i < free.Count; i++)
            {
                if (weights[i] <= 0) continue;
                last = i;
                if ((roll -= weights[i]) < 0) return i;
            }
            return last;
        }

        // 固定指定。本編で決まった陣営と合わない指定は飛ばす
        if (reserved != null)
        {
            foreach (var kv in reserved)
            {
                if (kv.Value < 0 || kv.Value >= Registry.Roles.Count) continue;
                var role = Registry.Roles[kv.Value];
                int at = free.FindIndex(s => s.PlayerId == kv.Key);
                if (at < 0) continue;
                if (free[at].Impostor != (role.Team == Team.Impostor))
                {
                    Plugin.Logger.LogWarning($"roles: reservation {kv.Key}={role.Id} skipped (team differs from the vanilla side)");
                    continue;
                }
                Take(at, kv.Value);
                if (stats != null) stats.Reserved++;
            }
        }

        foreach (int idx in sure.Concat(lucky))
        {
            var role = Registry.Roles[idx];
            if (OverLimit(role))
            {
                if (stats != null) stats.LimitSkips++;
                continue;
            }
            if (Conflicts(role, chosen))
            {
                if (stats != null) stats.ConflictSkips++;
                continue;
            }
            int pick = SlotFor(idx);
            if (pick < 0)
            {
                if (stats != null) stats.NoSlotSkips++;
                continue;
            }
            // クルーの役職が第三陣営の最少のための席を埋めない
            if (role.Team == Team.Crew && !free[pick].Impostor && CrewSeats() <= Reserve())
            {
                if (stats != null) stats.LimitSkips++;
                continue;
            }
            Take(pick, idx);
        }

        // 第三陣営が最少に届かなければ、出現率を重みに第三陣営の役職から足す
        var noSeat = new List<int>(); // 席が無かった役職 (次の候補から外す)
        while (neutrals < minNeutral)
        {
            var pool = NeutralPool();
            pool.RemoveAll(noSeat.Contains);
            int idx = WeightedPick(pool, rng);
            if (idx < 0) break;
            int pick = SlotFor(idx);
            if (pick < 0)
            {
                noSeat.Add(idx);
                continue;
            }
            Take(pick, idx);
            if (stats != null) stats.NeutralTopUps++;
        }

        // 余った人を埋める。出現率を重みに、人数の残っている役職から 1 つずつ引く。
        // 付けられなかった役職は候補から外す (残りが全部付けられない物なら終わる)
        int fill = AssignSettings.Fill.Value;
        if (fill > 0 && free.Count > 0)
        {
            var pool = new List<int>();
            for (int i = 0; i < Registry.Roles.Count; i++)
            {
                var r = Registry.Roles[i];
                if (r.Chance <= 0 || taken[i] >= r.Count.Value || !Usable(r)) continue;
                if (fill == 1 && r.Team == Team.Neutral) continue;
                pool.Add(i);
            }
            while (free.Count > 0 && pool.Count > 0)
            {
                int total = 0;
                foreach (int i in pool) total += Registry.Roles[i].Chance.Value;
                int roll = rng.Next(total);
                int at = 0;
                while (at < pool.Count - 1 && (roll -= Registry.Roles[pool[at]].Chance.Value) >= 0) at++;
                int idx = pool[at];
                var role = Registry.Roles[idx];
                int pick = OverLimit(role) || Conflicts(role, chosen) ? -1 : SlotFor(idx);
                if (pick < 0) { pool.RemoveAt(at); continue; }
                Take(pick, idx);
                if (stats != null) stats.FillPicks++;
                if (taken[idx] >= role.Count.Value) pool.RemoveAt(at);
            }
        }

        if (RoleSettings.Enabled) AssignAddons(roster, plan, rng);
        return plan;
    }

    // 配り方で配ってよい役職。幽霊になってから付く役職は本編が配る。MRP の役職を切った時は本編の役職だけ
    private static bool Usable(RoleBase r) => !r.AssignedOnDeath && (r.IsVanilla || RoleSettings.Enabled);

    // 同時に出ない組 (どちらの側に書いてあっても)
    private static bool Conflicts(RoleBase role, List<RoleBase> chosen)
    {
        var mine = role.NotWith;
        var type = role.GetType();
        foreach (var c in chosen)
        {
            if (mine != null && Array.IndexOf(mine, c.GetType()) >= 0) return true;
            var theirs = c.NotWith;
            if (theirs != null && Array.IndexOf(theirs, type) >= 0) return true;
        }
        return false;
    }

    // アドオン。役職と同じく出現率と人数で枠を作り、付けられる人の中から無作為に 1 人へ
    private static void AssignAddons(IReadOnlyList<Slot> roster, List<Pick> plan, Random rng)
    {
        int maxPer = AssignSettings.MaxAddonsPerPlayer.Value;
        if (maxPer <= 0 || Registry.Addons.Count == 0) return;
        var sure = new List<int>();
        var lucky = new List<int>();
        for (int i = 0; i < Registry.Addons.Count; i++)
        {
            var a = Registry.Addons[i];
            int chance = a.Chance;
            if (chance <= 0) continue;
            for (int k = 0; k < a.Count.Value; k++)
            {
                if (chance >= 100) sure.Add(i);
                else if (rng.Next(100) < chance) lucky.Add(i);
            }
        }
        Shuffle(sure, rng);
        Shuffle(lucky, rng);

        var picks = new Dictionary<byte, Pick>();
        foreach (var p in plan) picks[p.PlayerId] = p;
        var candidates = new List<int>();
        foreach (int ai in sure.Concat(lucky))
        {
            var addon = Registry.Addons[ai];
            candidates.Clear();
            for (int i = 0; i < roster.Count; i++)
            {
                var slot = roster[i];
                picks.TryGetValue(slot.PlayerId, out var pick);
                if (pick != null && (pick.Addons.Count >= maxPer || pick.Addons.Contains(ai))) continue;
                RoleBase role = pick != null && pick.RoleIndex >= 0 ? Registry.Roles[pick.RoleIndex] : null;
                var team = role != null ? role.Team : slot.Impostor ? Team.Impostor : Team.Crew;
                if (addon.CanAttach(team, role)) candidates.Add(i);
            }
            if (candidates.Count == 0) continue;
            var target = roster[candidates[rng.Next(candidates.Count)]];
            if (!picks.TryGetValue(target.PlayerId, out var tp))
            {
                // 役職の無い人 (本編のまま) にはアドオンだけの Pick を作る
                tp = new Pick { PlayerId = target.PlayerId, RoleIndex = -1 };
                picks[target.PlayerId] = tp;
                plan.Add(tp);
            }
            tp.Addons.Add(ai);
        }
    }

    // 公平化: インポスターを重みで選び直す (人数は本編が決めた数のまま)。固定指定の人は今の陣営のまま数に入れる。
    // 陣営を替えるべき人の添字を返す (名簿は書き換えない)
    public static List<int> ChooseImpostors(IReadOnlyList<Slot> roster, Random rng, AssignHistory history, IReadOnlyDictionary<byte, int> reserved = null)
    {
        int seats = 0;
        var open = new List<int>();
        for (int i = 0; i < roster.Count; i++)
        {
            bool fixedTeam = reserved != null && reserved.ContainsKey(roster[i].PlayerId);
            if (roster[i].Impostor && !fixedTeam) seats++;
            if (!fixedTeam) open.Add(i);
        }
        var want = new bool[roster.Count];
        while (seats > 0 && open.Count > 0)
        {
            double total = 0;
            foreach (int i in open) total += history.Weight(roster[i].Key, AssignHistory.Kind.Impostor);
            double roll = rng.NextDouble() * total;
            int at = 0;
            while (at < open.Count - 1 && (roll -= history.Weight(roster[open[at]].Key, AssignHistory.Kind.Impostor)) >= 0) at++;
            want[open[at]] = true;
            open.RemoveAt(at);
            seats--;
        }
        var flip = new List<int>();
        for (int i = 0; i < roster.Count; i++)
        {
            if (reserved != null && reserved.ContainsKey(roster[i].PlayerId)) continue;
            if (want[i] != roster[i].Impostor) flip.Add(i);
        }
        return flip;
    }

    // 出現率を重みに 1 つ引く (空なら -1)
    private static int WeightedPick(List<int> pool, Random rng)
    {
        if (pool.Count == 0) return -1;
        int total = 0;
        foreach (int i in pool) total += Registry.Roles[i].Chance.Value;
        int roll = rng.Next(total);
        int at = 0;
        while (at < pool.Count - 1 && (roll -= Registry.Roles[pool[at]].Chance.Value) >= 0) at++;
        return pool[at];
    }

    private static void Shuffle<T>(List<T> l, Random rng)
    {
        for (int i = l.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (l[i], l[j]) = (l[j], l[i]);
        }
    }

    // 確かめ用: 名簿を作って何度も配り、役職ごとに付いた回数を数える。
    // 同じ顔ぶれで続けて遊んだことにして、人ごとの当たりの偏りも数える (公平化が ON なら履歴を引き継ぐ・OFF ならインポスターは一様に選ぶ)
    public static string Simulate(int players, int impostors, int runs, int seed, IReadOnlyDictionary<byte, int> reserved = null)
    {
        var roster = new List<Slot>();
        for (int i = 0; i < players; i++)
            roster.Add(new Slot { PlayerId = (byte)i, Key = i });
        var rng = new Random(seed);
        var history = AssignSettings.Fairness ? new AssignHistory() : null;
        var roleHits = new int[Registry.Roles.Count];
        var addonHits = new int[Registry.Addons.Count];
        int withRole = 0, withAddon = 0, doubled = 0, neutralMax = 0, neutralMin = int.MaxValue;
        var stats = new PlanStats();
        // 人ごとの インポスター / 第三陣営 / 役職 の回数と、役職の付かない試合が続いた最長
        var impHits = new int[players];
        var neuHits = new int[players];
        var specHits = new int[players];
        var dry = new int[players];
        var dryMax = new int[players];
        var order = Enumerable.Range(0, players).ToList();
        for (int n = 0; n < runs; n++)
        {
            // 本編のインポスター選び (一様) の代わりに陣営を振り直してから配る
            Shuffle(order, rng);
            for (int k = 0; k < players; k++) SetSide(roster, order[k], k < impostors);
            // 固定指定の人は役職の陣営へ寄せる (本番の MatchReservedTeams と同じく、予約の無い反対側の人と入れ替える)
            if (reserved != null)
            {
                foreach (var kv in reserved)
                {
                    if (kv.Key >= players || kv.Value < 0 || kv.Value >= Registry.Roles.Count) continue;
                    bool want = Registry.Roles[kv.Value].Team == Team.Impostor;
                    if (roster[kv.Key].Impostor == want) continue;
                    int partner = roster.FindIndex(s => s.Impostor == want && !reserved.ContainsKey(s.PlayerId));
                    if (partner >= 0) SetSide(roster, partner, !want);
                    SetSide(roster, kv.Key, want);
                }
            }
            if (history != null)
                foreach (int i in ChooseImpostors(roster, rng, history, reserved)) SetSide(roster, i, !roster[i].Impostor);
            var plan = Decide(roster, rng, reserved, stats, history);
            history?.Record(roster, plan);
            int neutrals = 0;
            var special = new bool[players];
            foreach (var p in plan)
            {
                if (p.RoleIndex >= 0)
                {
                    withRole++;
                    roleHits[p.RoleIndex]++;
                    if (Registry.Roles[p.RoleIndex].Team == Team.Neutral) { neutrals++; neuHits[p.PlayerId]++; }
                }
                if (!AssignHistory.IsPlain(p.RoleIndex)) special[p.PlayerId] = true;
                withAddon += p.Addons.Count;
                if (p.Addons.Count != p.Addons.Distinct().Count()) doubled++;
                foreach (int a in p.Addons) addonHits[a]++;
            }
            for (int i = 0; i < players; i++)
            {
                if (roster[i].Impostor) impHits[i]++;
                if (special[i]) { specHits[i]++; dry[i] = 0; }
                else dryMax[i] = Math.Max(dryMax[i], ++dry[i]);
            }
            neutralMax = Math.Max(neutralMax, neutrals);
            neutralMin = Math.Min(neutralMin, neutrals);
        }
        var sb = new System.Text.StringBuilder();
        sb.Append($"players={players} impostors={impostors} runs={runs} seed={seed} roles/run={(double)withRole / runs:0.00} addons/run={(double)withAddon / runs:0.00}");
        sb.Append($"\nlimitSkips/run={(double)stats.LimitSkips / runs:0.00} conflictSkips/run={(double)stats.ConflictSkips / runs:0.00} noSlotSkips/run={(double)stats.NoSlotSkips / runs:0.00} reserved/run={(double)stats.Reserved / runs:0.00} fill/run={(double)stats.FillPicks / runs:0.00} neutralTopUps/run={(double)stats.NeutralTopUps / runs:0.00} neutrals={neutralMin}..{neutralMax} band={(TeamSizes.For(players) is { } b ? b.Players.ToString() : "-")} duplicateAddonRuns={doubled}");
        for (int i = 0; i < roleHits.Length; i++)
            if (roleHits[i] > 0) sb.Append($"\n{Registry.Roles[i].Id} {roleHits[i]} ({100.0 * roleHits[i] / runs:0.#}%/run)");
        for (int i = 0; i < addonHits.Length; i++)
            if (addonHits[i] > 0) sb.Append($"\n+{Registry.Addons[i].Id} {addonHits[i]} ({100.0 * addonHits[i] / runs:0.#}%/run)");
        sb.Append($"\nfairness={(history != null ? "on" : "off")} perPlayer impostor={Spread(impHits)} neutral={Spread(neuHits)} role={Spread(specHits)} longestNoRole={dryMax.Min()}..{dryMax.Max()} avg={dryMax.Average():0.0}");
        return sb.ToString();
    }

    internal static void SetSide(List<Slot> roster, int at, bool impostor)
    {
        var s = roster[at];
        s.Impostor = impostor;
        s.BaseRole = impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;
        roster[at] = s;
    }

    // 人ごとの回数の 最少..最多 と標準偏差
    private static string Spread(int[] v)
    {
        double avg = v.Average();
        double sd = Math.Sqrt(v.Average(x => (x - avg) * (x - avg)));
        return $"{v.Min()}..{v.Max()} sd={sd:0.0}";
    }
}

// ホストが役職を決めて全員に配る。本編が陣営 (クルー / インポスター) を決めた直後に、
// 同じ陣営の人の中から出現率と人数に従って選ぶ。陣営の人数は変えない。
internal static class RoleAssigner
{
    private static readonly System.Random Rng = new();

    // ロビーでホストが決めた固定指定 (PlayerId → 役職の番号)。次の試合の配布で使い切り、ロビーに戻ったら消す
    internal static readonly Dictionary<byte, int> Reserved = new();

    public static void AssignAndSend()
    {
        OptionSync.RestoreOwn();
        // 本編の役職も MRP が配る (MRP の役職を切っていても本編の役職は配る)
        int seed = Rng.Next();
        var rng = new Random(seed);
        var vanilla = Roster();
        var roster = new List<Slot>(vanilla);
        MatchReservedTeams(roster);
        // 切った間の古い履歴を入れ直した時に持ち出さない
        if (!AssignSettings.Fairness) _history = null;
        var history = AssignSettings.Fairness ? CurrentHistory() : null;
        if (history != null)
        {
            Plugin.Logger.LogInfo($"roles: fairness weights (pid:imp/neutral/role){history.Describe(roster)}");
            foreach (int i in AssignPlan.ChooseImpostors(roster, rng, history, Reserved)) AssignPlan.SetSide(roster, i, !roster[i].Impostor);
        }
        SendTeamChanges(vanilla, roster);
        var plan = AssignPlan.Decide(roster, rng, Reserved, null, history);
        history?.Record(roster, plan);
        Plugin.Logger.LogInfo($"roles: seed {seed}");
        Reserved.Clear();

        // 役職の処理が設定値を読むので、配る前に揃える (1 通にまとめて順番どおり届ける)
        using (Remote.Batch())
        {
            OptionSync.SendAll();
            Send(plan);
        }
    }

    // 公平化の履歴は同じ部屋の同じホストの間だけ持つ (部屋かホストが替われば最初から)
    private static AssignHistory _history;
    private static int _historyGame;

    public static void OnHostChanged() => _history = null;

    private static AssignHistory CurrentHistory()
    {
        int game = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
        if (_history == null || _historyGame != game)
        {
            _history = new AssignHistory();
            _historyGame = game;
        }
        return _history;
    }

    private static List<Slot> Roster()
    {
        var list = new List<Slot>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.notRealPlayer || p.Data == null || p.Data.Disconnected || !p.Data.Role) continue;
            if (Spectate.IsChosen(p.PlayerId)) continue; // 観戦の人には役職を配らない
            list.Add(new Slot { PlayerId = p.PlayerId, Impostor = RoleManager.IsImpostorRole(p.Data.Role.Role), BaseRole = p.Data.Role.Role, Key = p.isDummy ? -1 : p.Data.ClientId });
        }
        return list;
    }

    // 予約した役職と本編が決めた陣営が違う人は、予約の無い反対側の人と陣営を入れ替える (始まりの場面から予約どおりにする)。
    // 入れ替える相手がいなければその人の陣営だけ変える
    private static void MatchReservedTeams(List<Slot> roster)
    {
        if (Reserved.Count == 0) return;
        var rng = new Random();
        for (int i = 0; i < roster.Count; i++)
        {
            if (!Reserved.TryGetValue(roster[i].PlayerId, out int idx) || idx < 0 || idx >= Registry.Roles.Count) continue;
            bool impostor = Registry.Roles[idx].Team == Team.Impostor;
            if (roster[i].Impostor == impostor) continue;
            var partners = new List<int>();
            for (int k = 0; k < roster.Count; k++)
                if (roster[k].Impostor == impostor && !Reserved.ContainsKey(roster[k].PlayerId)) partners.Add(k);
            if (partners.Count > 0) AssignPlan.SetSide(roster, partners[rng.Next(partners.Count)], !impostor);
            AssignPlan.SetSide(roster, i, impostor);
            Plugin.Logger.LogInfo($"roles: reservation {roster[i].PlayerId}={Registry.Roles[idx].Id} moved team ({(partners.Count > 0 ? "swapped" : "no partner")})");
        }
    }

    // 本編が決めた陣営から最終的に変わった人だけに 1 回ずつ本編の役職を送る (予約合わせと公平化で同じ人を 2 回動かしても 1 本)。
    // 陣営が元に戻った人は本編の役職のまま
    private static void SendTeamChanges(List<Slot> vanilla, List<Slot> roster)
    {
        for (int i = 0; i < roster.Count; i++)
        {
            if (roster[i].Impostor == vanilla[i].Impostor)
            {
                roster[i] = vanilla[i];
                continue;
            }
            var p = Player(roster[i].PlayerId);
            if (p) p.RpcSetRole(roster[i].BaseRole, true);
        }
    }

    private static readonly RemoteCall<List<Pick>> Assign = new("Roles.Assign", Route.HostToAll,
        (w, plan) =>
        {
            w.Write((byte)plan.Count);
            foreach (var p in plan)
            {
                w.Write(p.PlayerId);
                w.WritePacked(p.RoleIndex + 1);
                w.Write((byte)p.Addons.Count);
                foreach (int a in p.Addons) w.WritePacked(a);
            }
        },
        r =>
        {
            int n = r.ReadByte();
            var plan = new List<Pick>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new Pick { PlayerId = r.ReadByte(), RoleIndex = r.ReadPackedInt32() - 1 };
                int na = r.ReadByte();
                for (int k = 0; k < na; k++)
                {
                    int a = r.ReadPackedInt32();
                    if (a >= 0 && a < Registry.Addons.Count) p.Addons.Add(a);
                }
                if (p.RoleIndex >= -1 && p.RoleIndex < Registry.Roles.Count) plan.Add(p);
            }
            return plan;
        },
        (_, plan) => Apply(plan));

    private static void Send(List<Pick> plan)
    {
        // 土台の役職を合わせる (本編の RPC は先に届く)
        foreach (var p in plan)
        {
            var player = Player(p.PlayerId);
            if (p.RoleIndex < 0) continue;
            var role = Registry.Roles[p.RoleIndex];
            if (player && player.Data.Role.Role != role.BaseRole) player.RpcSetRole(role.BaseRole, true);
        }
        Assign.Send(plan);
        Apply(plan);
    }

    // 試験用 (ホスト): target に役職を付け直して全員に配る。他の人の役職とアドオンはそのまま。土台の役職も合わせる。
    // addons = その人に付けるアドオン (null なら今付いている物を残す)
    internal static void Give(PlayerControl target, RoleBase proto, IReadOnlyList<AddonBase> addons = null)
    {
        var plan = new List<Pick>();
        foreach (var r in RoleState.All)
        {
            if (r.PlayerId == target.PlayerId || !r.Player) continue;
            var keep = new Pick { PlayerId = r.PlayerId, RoleIndex = Registry.Roles.FindIndex(x => x.Id == r.Id) };
            foreach (var a in RoleState.AddonsOf(r.PlayerId)) keep.Addons.Add(Registry.Addons.FindIndex(x => x.Id == a.Id));
            plan.Add(keep);
        }
        // 役職の無い人のアドオンも残す
        foreach (var a in RoleState.AllAddons)
        {
            if (a.PlayerId == target.PlayerId || RoleState.Of(a.PlayerId) != null || !a.Player) continue;
            var keep = plan.Find(x => x.PlayerId == a.PlayerId);
            if (keep == null) plan.Add(keep = new Pick { PlayerId = a.PlayerId, RoleIndex = -1 });
            keep.Addons.Add(Registry.Addons.FindIndex(x => x.Id == a.Id));
        }
        var mine = new Pick { PlayerId = target.PlayerId, RoleIndex = Registry.Roles.IndexOf(proto) };
        if (addons != null) foreach (var a in addons) mine.Addons.Add(Registry.Addons.IndexOf(a));
        else foreach (var a in RoleState.AddonsOf(target.PlayerId)) mine.Addons.Add(Registry.Addons.FindIndex(x => x.Id == a.Id));
        plan.Add(mine);
        Send(plan);
    }

    private static PlayerControl Player(byte pid) => GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;

    private static void Apply(List<Pick> plan)
    {
        RoleState.Clear(false);
        GameEnd.Reset();
        var addons = new List<AddonBase>();
        foreach (var p in plan)
        {
            var player = Player(p.PlayerId);
            if (!player) continue;
            addons.Clear();
            foreach (int a in p.Addons) addons.Add(Registry.Addons[a]);
            if (p.RoleIndex < 0) RoleState.AssignAddonsOnly(player, addons);
            else RoleState.Assign(player, Registry.Roles[p.RoleIndex], addons);
        }
        Plugin.Logger.LogInfo($"roles: {string.Join(", ", plan.Select(x => $"{x.PlayerId}={(x.RoleIndex < 0 ? "-" : Registry.Roles[x.RoleIndex].Id)}{string.Concat(x.Addons.Select(a => "+" + Registry.Addons[a].Id))}"))}");
    }
}

[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SelectRolesPatch
{
    // 本編には陣営 (クルー / インポスター) だけを決めさせる
    public static void Prefix()
    {
        if (!AmongUsClient.Instance.AmHost || !Options.GameModeSwitch.IsNormal) return;
        try { Builtin.VanillaRoles.PrepareVanillaSelection(); }
        catch (Exception e) { Plugin.Logger.LogError($"vanilla role rates: {e}"); }
    }

    public static void Postfix()
    {
        if (!AmongUsClient.Instance.AmHost || !Options.GameModeSwitch.IsNormal) return;
        try { Spectate.AfterVanillaSelection(); }
        catch (Exception e) { Plugin.Logger.LogError($"spectate: {e}"); }
        try { RoleAssigner.AssignAndSend(); }
        catch (Exception e) { Plugin.Logger.LogError($"role assignment failed: {e}"); }
    }
}

// Based on https://github.com/AU-Avengers/TOU-Mira TownOfUs/Patches/RoleManagerPatches.cs SetRolePatch (GPL-3.0)
// 本編の役職の付け直し (RpcSetRole の「上書き可」) を効かせる。本編は一度役職が決まった人には
// 上書き可でも付け直さないので、上書き可の時だけ決まった印を外す。全員の端末で受け取った時にも通る
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CoSetRole))]
internal static class OverrideRolePatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(1)] bool canOverride)
    {
        if (canOverride) __instance.roleAssigned = false;
    }
}

[HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
internal static class GameEndPatch
{
    public static void Postfix() => RoleState.Clear(true);
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class LobbyClearPatch
{
    public static void Postfix()
    {
        RoleState.Clear(false);
        RoleAssigner.Reserved.Clear();
        GameEnd.Reset();
        MatchLog.Reset();
    }
}
