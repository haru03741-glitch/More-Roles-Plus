using System;
using System.Collections.Generic;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Builtin;

namespace MoreRolesPlus.Roles;

// 配役の公平化の履歴。前の試合で外れた人ほど当たりやすく、前の試合で当たった人は当たりにくくする (連続も禁止はしない)。
// 重みの式は https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/RoleAssignmentFairness.cs に倣う (GPL-3.0):
// 外れが続くと 1 試合ごとに +0.25 (4 試合・2 倍で打ち止め)・前の試合で当たったら半分・同じ役職の連続はさらに半分・0.25〜2 に収める。
// 鍵は Slot.Key (ClientId)。鍵の無い人 (ダミー) は履歴を持たず常に 1。ホストの手元だけで持つ
public sealed class AssignHistory
{
    // インポスターになるか / 第三陣営になるか / 役職 (素のクルーとインポスター以外・第三陣営も含む) が付くか
    public enum Kind { Impostor, Neutral, Role }

    private const int MaxMissed = 4;
    private const double MinWeight = 0.25, MaxWeight = 2.0;

    private struct Entry
    {
        public int Missed;
        public bool WasPicked;
        public int LastRole; // 前の試合で付いた役職の番号 (無ければ -1)
    }

    private readonly Dictionary<(int, Kind), Entry> _entries = new();

    public double Weight(int key, Kind kind, int roleIndex = -1)
    {
        if (key < 0 || !_entries.TryGetValue((key, kind), out var e)) return 1.0;
        double w = 1.0 + 0.25 * Math.Min(MaxMissed, e.Missed);
        if (e.WasPicked) w *= 0.5;
        if (roleIndex >= 0 && e.LastRole == roleIndex) w *= 0.5;
        return Math.Clamp(w, MinWeight, MaxWeight);
    }

    // 素のクルーとインポスター (役職が付かなかったのと同じ扱い)
    public static bool IsPlain(int roleIndex) =>
        roleIndex < 0 || Registry.Roles[roleIndex] is VanillaRole { IsPlain: true };

    // 配った結果を履歴に足す。その種類の当たりが 1 つも無かった試合は、外れの数を動かさない (候補の機会が無かったので)
    public void Record(IReadOnlyList<Slot> roster, List<Pick> plan)
    {
        var roleOf = new Dictionary<byte, int>();
        foreach (var p in plan) roleOf[p.PlayerId] = p.RoleIndex;
        bool anyImpostor = false, anyNeutral = false, anyRole = false;
        foreach (var s in roster)
        {
            anyImpostor |= s.Impostor;
            int r = roleOf.TryGetValue(s.PlayerId, out int v) ? v : -1;
            if (IsPlain(r)) continue;
            anyRole = true;
            anyNeutral |= Registry.Roles[r].Team == Team.Neutral;
        }
        foreach (var s in roster)
        {
            if (s.Key < 0) continue;
            int r = roleOf.TryGetValue(s.PlayerId, out int v) ? v : -1;
            bool plain = IsPlain(r);
            if (anyImpostor) Note(s.Key, Kind.Impostor, s.Impostor, -1);
            // 第三陣営の席はクルーの側から出るので、インポスターは候補に数えない
            if (anyNeutral && !s.Impostor) Note(s.Key, Kind.Neutral, !plain && Registry.Roles[r].Team == Team.Neutral, plain ? -1 : r);
            if (anyRole) Note(s.Key, Kind.Role, !plain, plain ? -1 : r);
        }
    }

    private void Note(int key, Kind kind, bool picked, int role)
    {
        _entries.TryGetValue((key, kind), out var e);
        e.Missed = picked ? 0 : Math.Min(MaxMissed, e.Missed + 1);
        e.WasPicked = picked;
        e.LastRole = picked ? role : -1;
        _entries[(key, kind)] = e;
    }

    // ログ用の 1 行 (鍵ごとに インポスター/第三陣営/役職 の重み)
    public string Describe(IReadOnlyList<Slot> roster)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in roster)
            sb.Append($" {s.PlayerId}:{Weight(s.Key, Kind.Impostor):0.##}/{Weight(s.Key, Kind.Neutral):0.##}/{Weight(s.Key, Kind.Role):0.##}");
        return sb.ToString();
    }
}
