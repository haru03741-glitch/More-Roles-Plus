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
        // アドオンは役職の後 (役職の OnAssigned で作った物を前提にできるように)
        AttachAddons(p, addonProtos, role.Lifespan);
        RoleDisplay.OnAssigned(role);
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
        AnyNeutral = false;
        RoleDisplay.Clear();
        Dev.DevGod.Clear();
    }
}

[Settings(Tab.General, "役職", "Roles", order: -100)]
public static class RoleSettings
{
    public static readonly BoolOpt Enabled = new("More Roles Plus の役職を使う", "Use More Roles Plus roles", true);
    public static readonly BoolOpt GhostsSeeRoles = new("死んだら全員の役職が見える (会議)", "Ghosts see all roles in meetings", true);
    public static readonly BoolOpt ImpostorsSeeRoles = new("インポスター同士は役職が見える (会議)", "Impostors see each other's roles in meetings", true);
}

// 配り方の上限。「なし」は制限なし
[Settings(Tab.General, "配り方", "Assignment", order: -99)]
public static class AssignSettings
{
    private static readonly Text[] Counts = Enumerable.Range(0, 16).Select(i => i == 0 ? new Text("なし", "None") : new Text(i.ToString(), i.ToString())).ToArray();

    public static readonly ChoiceOpt MaxNeutral = new("第三陣営の役職の最大人数", "Max neutral roles", 0, Counts);
    public static readonly ChoiceOpt MaxNeutralKillers = new("キルする第三陣営の最大人数", "Max killing neutral roles", 0, Counts);
    public static readonly ChoiceOpt MaxRoled = new("役職が付く人の最大人数", "Max players with a role", 0, Counts);
    public static readonly IntOpt MaxAddonsPerPlayer = new("1 人に付くアドオンの最大数", "Max addons per player", 1, 0, 3);

    // 出現率の抽選で役職が付かなかった人の扱い。「埋める」は出現率を重みにして、人数の残っている役職から配る
    // (出現率を低くしても、役職を入れた分だけ誰かに付く)。第三陣営を埋めに使うとクルーが減りすぎるので既定は同じ陣営だけ
    public static readonly ChoiceOpt Fill = new("役職が付かなかった人", "Players left without a role", 1,
        new Text("本編のまま", "Keep vanilla"),
        new Text("同じ陣営の役職で埋める", "Fill from same-team roles"),
        new Text("第三陣営も含めて埋める", "Fill, neutrals included"));
}

// 配る前の名簿の 1 人 (本編が決めた陣営と土台の役職)
public struct Slot
{
    public byte PlayerId;
    public bool Impostor;
    public RoleTypes BaseRole;
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
        public int LimitSkips, ConflictSkips, NoSlotSkips, Reserved, FillPicks;
    }

    // reserved = 固定指定 (PlayerId → 役職の番号)。枠の数・上限より先に付ける
    public static List<Pick> Decide(IReadOnlyList<Slot> roster, Random rng, IReadOnlyDictionary<byte, int> reserved = null, PlanStats stats = null)
    {
        // 出現率 100% の枠を先に、残りは抽選で通った枠を混ぜてから並べる
        var sure = new List<int>();
        var lucky = new List<int>();
        for (int i = 0; i < Registry.Roles.Count; i++)
        {
            var r = Registry.Roles[i];
            int chance = r.Chance;
            if (chance <= 0) continue;
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
        int maxNeutral = AssignSettings.MaxNeutral.Value;
        int maxKillers = AssignSettings.MaxNeutralKillers.Value;
        int maxRoled = AssignSettings.MaxRoled.Value;

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
            return (maxRoled > 0 && plan.Count >= maxRoled)
                || (neutral && maxNeutral > 0 && neutrals >= maxNeutral)
                || (neutral && role.IsKiller && maxKillers > 0 && killers >= maxKillers);
        }

        // 本編の役職がちょうど土台の役職と同じ人を優先し、いなければ同じ陣営の誰か
        int SlotFor(RoleBase role)
        {
            int pick = free.FindIndex(s => s.BaseRole == role.BaseRole);
            if (pick < 0) pick = free.FindIndex(s => s.Impostor == (role.Team == Team.Impostor));
            return pick;
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
            int pick = SlotFor(role);
            if (pick < 0)
            {
                if (stats != null) stats.NoSlotSkips++;
                continue;
            }
            Take(pick, idx);
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
                if (r.Chance <= 0 || taken[i] >= r.Count.Value) continue;
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
                int pick = OverLimit(role) || Conflicts(role, chosen) ? -1 : SlotFor(role);
                if (pick < 0) { pool.RemoveAt(at); continue; }
                Take(pick, idx);
                if (stats != null) stats.FillPicks++;
                if (taken[idx] >= role.Count.Value) pool.RemoveAt(at);
                if (maxRoled > 0 && plan.Count >= maxRoled) break;
            }
        }

        AssignAddons(roster, plan, rng);
        return plan;
    }

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

    private static void Shuffle<T>(List<T> l, Random rng)
    {
        for (int i = l.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (l[i], l[j]) = (l[j], l[i]);
        }
    }

    // 確かめ用: 名簿を作って何度も配り、役職ごとに付いた回数を数える
    public static string Simulate(int players, int impostors, int runs, int seed, IReadOnlyDictionary<byte, int> reserved = null)
    {
        var roster = new List<Slot>();
        for (int i = 0; i < players; i++)
            roster.Add(new Slot { PlayerId = (byte)i, Impostor = i < impostors, BaseRole = i < impostors ? RoleTypes.Impostor : RoleTypes.Crewmate });
        var rng = new Random(seed);
        var roleHits = new int[Registry.Roles.Count];
        var addonHits = new int[Registry.Addons.Count];
        int withRole = 0, withAddon = 0, doubled = 0, neutralMax = 0;
        var stats = new PlanStats();
        for (int n = 0; n < runs; n++)
        {
            var plan = Decide(roster, rng, reserved, stats);
            int neutrals = 0;
            foreach (var p in plan)
            {
                if (p.RoleIndex >= 0)
                {
                    withRole++;
                    roleHits[p.RoleIndex]++;
                    if (Registry.Roles[p.RoleIndex].Team == Team.Neutral) neutrals++;
                }
                withAddon += p.Addons.Count;
                if (p.Addons.Count != p.Addons.Distinct().Count()) doubled++;
                foreach (int a in p.Addons) addonHits[a]++;
            }
            neutralMax = Math.Max(neutralMax, neutrals);
        }
        var sb = new System.Text.StringBuilder();
        sb.Append($"players={players} impostors={impostors} runs={runs} seed={seed} roles/run={(double)withRole / runs:0.00} addons/run={(double)withAddon / runs:0.00}");
        sb.Append($"\nlimitSkips/run={(double)stats.LimitSkips / runs:0.00} conflictSkips/run={(double)stats.ConflictSkips / runs:0.00} noSlotSkips/run={(double)stats.NoSlotSkips / runs:0.00} reserved/run={(double)stats.Reserved / runs:0.00} fill/run={(double)stats.FillPicks / runs:0.00} maxNeutral={neutralMax} duplicateAddonRuns={doubled}");
        for (int i = 0; i < roleHits.Length; i++)
            if (roleHits[i] > 0) sb.Append($"\n{Registry.Roles[i].Id} {roleHits[i]} ({100.0 * roleHits[i] / runs:0.#}%/run)");
        for (int i = 0; i < addonHits.Length; i++)
            if (addonHits[i] > 0) sb.Append($"\n+{Registry.Addons[i].Id} {addonHits[i]} ({100.0 * addonHits[i] / runs:0.#}%/run)");
        return sb.ToString();
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
        var plan = new List<Pick>();
        if (RoleSettings.Enabled)
        {
            int seed = Rng.Next();
            plan = AssignPlan.Decide(Roster(), new Random(seed), Reserved);
            Plugin.Logger.LogInfo($"roles: seed {seed}");
        }
        Reserved.Clear();

        // 役職の処理が設定値を読むので、配る前に揃える (1 通にまとめて順番どおり届ける)
        using (Remote.Batch())
        {
            OptionSync.SendAll();
            Send(plan);
        }
    }

    private static List<Slot> Roster()
    {
        var list = new List<Slot>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.notRealPlayer || p.Data == null || p.Data.Disconnected || !p.Data.Role) continue;
            list.Add(new Slot { PlayerId = p.PlayerId, Impostor = RoleManager.IsImpostorRole(p.Data.Role.Role), BaseRole = p.Data.Role.Role });
        }
        return list;
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
    public static void Postfix()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        try { RoleAssigner.AssignAndSend(); }
        catch (Exception e) { Plugin.Logger.LogError($"role assignment failed: {e}"); }
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
