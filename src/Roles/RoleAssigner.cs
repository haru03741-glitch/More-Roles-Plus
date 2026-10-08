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
        if (addonProtos != null)
        {
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
                addon.Lifespan = role.Lifespan.Child();
                EventBinder.Bind(addon);
                try { addon.OnAssigned(); }
                catch (Exception e) { Plugin.Logger.LogError($"{addon.Id}.OnAssigned: {e}"); }
            }
        }
        RoleDisplay.OnAssigned(role);
        if (Dev.DevGod.On) Dev.DevGod.Refresh();
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
    public static List<Pick> Decide(IReadOnlyList<Slot> roster, Random rng)
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
        foreach (int idx in sure.Concat(lucky))
        {
            var role = Registry.Roles[idx];
            bool wantImpostor = role.Team == Team.Impostor;
            // 本編の役職がちょうど土台の役職と同じ人を優先し、いなければ同じ陣営の誰か
            int pick = free.FindIndex(s => s.BaseRole == role.BaseRole);
            if (pick < 0) pick = free.FindIndex(s => s.Impostor == wantImpostor);
            if (pick < 0) continue;
            plan.Add(new Pick { PlayerId = free[pick].PlayerId, RoleIndex = idx });
            free.RemoveAt(pick);
        }
        return plan;
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
    public static string Simulate(int players, int impostors, int runs, int seed)
    {
        var roster = new List<Slot>();
        for (int i = 0; i < players; i++)
            roster.Add(new Slot { PlayerId = (byte)i, Impostor = i < impostors, BaseRole = i < impostors ? RoleTypes.Impostor : RoleTypes.Crewmate });
        var rng = new Random(seed);
        var roleHits = new int[Registry.Roles.Count];
        var addonHits = new int[Registry.Addons.Count];
        int withRole = 0, withAddon = 0;
        for (int n = 0; n < runs; n++)
        {
            var plan = Decide(roster, rng);
            withRole += plan.Count;
            foreach (var p in plan)
            {
                roleHits[p.RoleIndex]++;
                withAddon += p.Addons.Count;
                foreach (int a in p.Addons) addonHits[a]++;
            }
        }
        var sb = new System.Text.StringBuilder();
        sb.Append($"players={players} impostors={impostors} runs={runs} seed={seed} roles/run={(double)withRole / runs:0.00} addons/run={(double)withAddon / runs:0.00}");
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

    public static void AssignAndSend()
    {
        OptionSync.RestoreOwn();
        var plan = new List<Pick>();
        if (RoleSettings.Enabled)
        {
            int seed = Rng.Next();
            plan = AssignPlan.Decide(Roster(), new Random(seed));
            Plugin.Logger.LogInfo($"roles: seed {seed}");
        }

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
                w.WritePacked(p.RoleIndex);
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
                var p = new Pick { PlayerId = r.ReadByte(), RoleIndex = r.ReadPackedInt32() };
                int na = r.ReadByte();
                for (int k = 0; k < na; k++)
                {
                    int a = r.ReadPackedInt32();
                    if (a >= 0 && a < Registry.Addons.Count) p.Addons.Add(a);
                }
                if (p.RoleIndex >= 0 && p.RoleIndex < Registry.Roles.Count) plan.Add(p);
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
            RoleState.Assign(player, Registry.Roles[p.RoleIndex], addons);
        }
        Plugin.Logger.LogInfo($"roles: {string.Join(", ", plan.Select(x => $"{x.PlayerId}={Registry.Roles[x.RoleIndex].Id}{string.Concat(x.Addons.Select(a => "+" + Registry.Addons[a].Id))}"))}");
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
        GameEnd.Reset();
        MatchLog.Reset();
    }
}
