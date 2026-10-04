using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// 試合中の役職の持ち主。PlayerId (0〜255) で引ける表にしておく (毎フレーム引いても軽いように)
public static class RoleState
{
    private static readonly RoleBase[] ByPlayer = new RoleBase[256];
    private static readonly List<RoleBase> Active = new();

    public static RoleBase Of(byte playerId) => ByPlayer[playerId];
    public static RoleBase Of(PlayerControl p) => p ? ByPlayer[p.PlayerId] : null;

    // 自分の役職 (無ければ null)
    public static RoleBase Local { get; private set; }

    public static IReadOnlyList<RoleBase> All => Active;

    internal static void Assign(PlayerControl p, RoleBase proto)
    {
        var role = (RoleBase)Activator.CreateInstance(proto.GetType());
        role.Player = p;
        role.PlayerId = p.PlayerId;
        role.Chance = proto.Chance;
        role.Count = proto.Count;
        ByPlayer[p.PlayerId] = role;
        Active.Add(role);
        if (p.AmOwner) Local = role;
        try { role.OnAssigned(); }
        catch (Exception e) { Plugin.Logger.LogError($"{role.Id}.OnAssigned: {e}"); }
        RoleDisplay.OnAssigned(role);
    }

    internal static void Clear(bool gameEnded)
    {
        foreach (var r in Active)
        {
            if (gameEnded)
            {
                try { r.OnGameEnd(); }
                catch (Exception e) { Plugin.Logger.LogError($"{r.Id}.OnGameEnd: {e}"); }
            }
            ByPlayer[r.PlayerId] = null;
        }
        Active.Clear();
        Local = null;
        RoleDisplay.Clear();
    }
}

[Settings(Tab.General, "役職", "Roles", order: -100)]
public static class RoleSettings
{
    public static readonly BoolOpt Enabled = new("More Roles Plus の役職を使う", "Use More Roles Plus roles", true);
}

// ホストが役職を決めて全員に配る。本編が陣営 (クルー / インポスター) を決めた直後に、
// 同じ陣営の人の中から出現率と人数に従って選ぶ。陣営の人数は変えない。
internal static class RoleAssigner
{
    private static readonly System.Random Rng = new();

    public static void AssignAndSend()
    {
        OptionSync.RestoreOwn();
        var plan = new List<(PlayerControl player, int roleIndex)>();
        if (RoleSettings.Enabled) plan = Decide();

        OptionSync.SendAll(); // 役職の処理が設定値を読むので、配る前に揃える
        var w = Rpc.Start(Rpc.Roles);
        w.Write(Registry.Fingerprint);
        w.Write((byte)plan.Count);
        foreach (var (p, idx) in plan)
        {
            w.Write(p.PlayerId);
            w.WritePacked(idx);
        }
        Rpc.Finish(w);
        Apply(plan);
    }

    private static List<(PlayerControl, int)> Decide()
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
                else if (Rng.Next(100) < chance) lucky.Add(i);
            }
        }
        Shuffle(sure);
        Shuffle(lucky);

        var free = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (p && !p.notRealPlayer && p.Data != null && !p.Data.Disconnected && p.Data.Role) free.Add(p);
        }
        Shuffle(free);

        var plan = new List<(PlayerControl, int)>();
        foreach (int idx in sure.Concat(lucky))
        {
            var role = Registry.Roles[idx];
            bool wantImpostor = role.Team == Team.Impostor;
            // 本編の役職がちょうど土台の役職と同じ人を優先し、いなければ同じ陣営の誰か
            int pick = free.FindIndex(p => p.Data.Role.Role == role.BaseRole);
            if (pick < 0) pick = free.FindIndex(p => RoleManager.IsImpostorRole(p.Data.Role.Role) == wantImpostor);
            if (pick < 0) continue;
            var target = free[pick];
            free.RemoveAt(pick);
            if (target.Data.Role.Role != role.BaseRole) target.RpcSetRole(role.BaseRole, true);
            plan.Add((target, idx));
        }
        return plan;
    }

    // 試験用: 自分 (ホスト) だけに役職を付け直す。土台の役職も合わせる
    internal static void GiveLocal(RoleBase proto)
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp.Data.Role.Role != proto.BaseRole) lp.RpcSetRole(proto.BaseRole, true);
        Apply(new List<(PlayerControl, int)> { (lp, Registry.Roles.IndexOf(proto)) });
    }

    public static void Receive(PlayerControl sender, MessageReader r)
    {
        if (!Rpc.FromHost(sender) || AmongUsClient.Instance.AmHost) return;
        uint fp = r.ReadUInt32();
        if (fp != Registry.Fingerprint) return;
        int n = r.ReadByte();
        var plan = new List<(PlayerControl, int)>(n);
        for (int i = 0; i < n; i++)
        {
            byte pid = r.ReadByte();
            int idx = r.ReadPackedInt32();
            var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
            if (p && idx >= 0 && idx < Registry.Roles.Count) plan.Add((p, idx));
        }
        Apply(plan);
    }

    private static void Apply(List<(PlayerControl player, int roleIndex)> plan)
    {
        RoleState.Clear(false);
        foreach (var (p, idx) in plan) RoleState.Assign(p, Registry.Roles[idx]);
        Plugin.Logger.LogInfo($"roles: {string.Join(", ", plan.Select(x => $"{x.player.PlayerId}={Registry.Roles[x.roleIndex].Id}"))}");
    }

    private static void Shuffle<T>(List<T> l)
    {
        for (int i = l.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (l[i], l[j]) = (l[j], l[i]);
        }
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
    public static void Postfix() => RoleState.Clear(false);
}
