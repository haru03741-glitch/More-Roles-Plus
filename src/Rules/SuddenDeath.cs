using System.Collections.Generic;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Rules;

// サドンデス (モードの札で選ぶ): バトルロワイヤル。全員をクルーの側にして、いつもどおり (クルー・第三陣営の) 役職を配り、
// どの役職の人にも本編のキルボタンを持たせて誰でも誰でもキルできるようにする。
// 会議は無し (緊急ボタンも通報も)・タスクでは勝てない・最後の一人が勝ち (全員死んだら引き分け)。
// 本編のモードはクラシックのまま、MRP の配り方と終わりの判定だけを替える
[Settings(Tab.General, "SuddenDeath.Title", order: -59)]
public static class SuddenDeath
{
    public static readonly BoolOpt Enabled = new("SuddenDeath.Enabled", false) { OffTab = true };
    public static readonly IntOpt KillCooldown = new("SuddenDeath.KillCooldown", 10, 0, 60, 5, "s");
    public static readonly BoolOpt Vent = new("SuddenDeath.CanVent", false);

    // モードの札の番号 (本編のモードの番号と重ならない数)
    internal const int ChipIndex = 100;

    public static bool Active => Enabled && GameModeSwitch.IsNormal;

    // ホスト: 全員をクルーの側にする。インポスターの役職の予約は外す (クルーの席には配れない)
    internal static void AllCrew(List<Slot> roster, Dictionary<byte, int> reserved)
    {
        for (int i = 0; i < roster.Count; i++)
            if (roster[i].Impostor) AssignPlan.SetSide(roster, i, false);
        var drop = new List<byte>();
        foreach (var kv in reserved)
            if (kv.Value >= 0 && kv.Value < Registry.Roles.Count && Registry.Roles[kv.Value] is RoleBase { Team: Team.Impostor }) drop.Add(kv.Key);
        foreach (byte id in drop) reserved.Remove(id);
    }

    // ホスト: MRP の役職が当たらなかった人にも本編の役職 (クルーなど) を MRP の役職として付ける (キルボタンを持たせ、勝ち負けに数えるため)
    internal static void FillRoles(List<Slot> roster, List<Pick> plan)
    {
        foreach (var slot in roster)
        {
            var pick = plan.Find(p => p.PlayerId == slot.PlayerId);
            if (pick is { RoleIndex: >= 0 }) continue;
            int idx = Registry.Roles.FindIndex(r => r is Roles.Builtin.VanillaRole v && v.Type == slot.BaseRole);
            if (idx < 0) idx = Registry.Roles.FindIndex(r => r is Roles.Builtin.VanillaCrewmate);
            if (idx < 0) continue;
            if (pick == null) plan.Add(new Pick { PlayerId = slot.PlayerId, RoleIndex = idx });
            else pick.RoleIndex = idx;
        }
    }

    // ホスト: 生き残りが 1 人になったらその人の勝ち・0 人なら引き分け (まだなら null)
    internal static GameResult Decide()
    {
        byte last = 255;
        int alive = 0;
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected || p.IsDead) continue;
            if (RoleState.Of(p.PlayerId) == null) continue; // 観戦の人など
            alive++;
            last = p.PlayerId;
        }
        if (alive > 1) return null;
        var r = new GameResult { Role = alive == 1 ? RoleState.Of(last) : null };
        if (alive == 1) r.Winners.Add(last);
        else r.Draw = true;
        return r;
    }
}
