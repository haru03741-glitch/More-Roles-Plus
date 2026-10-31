using System;
using MoreRolesPlus.Net;

namespace MoreRolesPlus.Roles;

// 立ち合い (アクション役職の斬り合い) と、その間だけの体力。
// 攻撃が当たると、攻撃した人と当たった人が同じ組 (戦い) に入り、当たった人の体力が減る。0 で倒れる (倒すのは呼んだ役職)。
// 組は最大 MaxMembers 人の乱戦。戦っている人に当てた/当てられた戦える人は、その組に加わる (空きが無ければ体力だけ減る)。
// 倒れた・皆から離れたまま時間が経った人は組を抜けて全快に戻り、残りが 1 人になったら組は畳まれる。会議で全部畳む。
// 体力と組はホストが持ち、変わるたびに全員へ配る
internal static class Duel
{
    internal const int MaxHp = 100;
    internal const int MaxMembers = 4;
    internal const int MaxGroups = 16;
    private const float LeaveDistance = 7f;   // 組の誰からもこれより離れて
    private const int LeaveMs = 4000;         // この時間が経ったら抜ける
    private const int CheckMs = 250;

    // 全員の端末で持つ写し (ホストは正本)。組の番号は 1〜MaxGroups (0 = 組に入っていない)
    private static readonly byte[] Hp = new byte[256];
    private static readonly byte[] Group = new byte[256];
    private static readonly byte[] Members = new byte[(MaxGroups + 1) * MaxMembers]; // 組ごとに入った順
    private static readonly byte[] Count = new byte[MaxGroups + 1];
    private static readonly long[] FarSince = new long[256];
    private static readonly long[] LastHit = new long[256]; // ホスト: 最後に当たった時刻 (組の外の体力を戻す)
    private static readonly byte[] Moving = new byte[MaxMembers];
    private static long _nextCheck;
    private static int _groups;

    // 組に入った・抜けた・体力の変化 (全員の端末で)。HUD などが聞く。
    // OnJoin の fresh = 二人で新しい組ができた (二人とも fresh で来る)。途中から加わった人は false
    internal static event Action<byte, byte, bool> OnJoin;  // (入った人, 組, fresh)
    internal static event Action<byte, byte> OnLeave;       // (抜けた人, 組)
    internal static event Action<byte, byte, int> OnHit;    // (攻撃した人, 当たった人, 残りの体力)

    internal static int Started, Joined, Hits;
    internal static int LastDamage; // 今の OnHit で減った体力 (回復なら負)
    internal static bool LastSmash; // 今の OnHit は吹き飛ばす一撃 (溜めた居合・舌の一撃・鍔迫り合いの負け)
    internal static bool Moved;     // 今の OnJoin は組同士の合流で移っただけ
    internal static int LeftHp;     // 今の OnLeave で抜けた人の抜ける前の体力 (0 = 倒れた・死亡の知らせより先に届いても分かる)

    private static Lifespan _hooked;

    static Duel() => Reset();

    // 立ち合いを使う役職が付いた時に呼ぶ (全員の端末で)。試合ごとに 1 回だけ、会議の購読と全快をする
    internal static void Use()
    {
        var match = RoleState.Match;
        if (_hooked == match && !match.IsDead) return;
        _hooked = match;
        Reset();
        UI.DuelHud.Hook();
        DuelHitFx.Hook();
        DuelDodge.Hook();
        DuelTumble.Hook();
        Events<MeetingStartEvent>.Subscribe(_ => OnMeeting(), match, owner: "Duel");
    }

    internal static void Reset()
    {
        DuelTumble.Reset();
        for (int i = 0; i < 256; i++)
        {
            Hp[i] = MaxHp;
            Group[i] = 0;
            FarSince[i] = 0;
        }
        Array.Clear(Count);
        _groups = 0;
    }

    internal static int HpOf(byte id) => Hp[id];
    internal static bool InDuel(byte id) => Group[id] != 0;
    internal static byte GroupOf(byte id) => Group[id];
    internal static bool SameGroup(byte a, byte b) => Group[a] != 0 && Group[a] == Group[b];
    internal static int CountOf(byte g) => g == 0 ? 0 : Count[g];
    internal static byte MemberOf(byte g, int i) => Members[g * MaxMembers + i];
    internal static bool HasRoom(byte id) => Group[id] != 0 && Count[Group[id]] < MaxMembers;
    internal static bool Any => _groups > 0; // どこかに組がある (無ければ比べるだけで帰る口に)

    private enum Kind : byte { Start = 1, Hit = 2, Leave = 3, Join = 4, Smash = 5 }

    // 値 = 種類 | 人 a << 8 | 人 t << 16 | (体力 か 組) << 24
    private static readonly RemoteCall<int> StateCall = new("Duel.State", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(), (_, v) => Apply(v));

    private static void Send(Kind k, byte a, byte t, int last)
    {
        int v = (byte)k | a << 8 | t << 16 | (byte)Math.Clamp(last, 0, 255) << 24;
        StateCall.Send(v);
        Apply(v); // 配る電文は送った本人 (ホスト) の手元では動かない
    }

    private static void Apply(int v)
    {
        var k = (Kind)(v & 0xFF);
        byte a = (byte)(v >> 8), t = (byte)(v >> 16), last = (byte)(v >> 24);
        switch (k)
        {
            case Kind.Start:
                if (last == 0 || last > MaxGroups) return;
                // 体力は引き継ぐ (組の外で先に削られていた分も残す)
                Put(a, last);
                Put(t, last);
                Started++;
                OnJoin?.Invoke(a, last, true);
                OnJoin?.Invoke(t, last, true);
                break;
            case Kind.Join:
                if (last == 0 || last > MaxGroups || Group[a] == last) return;
            {
                // 別の組から移る時 (組同士の合流) は体力を残したまま。抜けた知らせは移った先に入れてから出す (InDuel で移っただけと分かる)
                byte old = Group[a];
                Put(a, last);
                if (Group[a] != last) return;
                Joined++;
                Moved = old != 0;
                try
                {
                    if (Moved)
                    {
                        LeftHp = Hp[a];
                        OnLeave?.Invoke(a, old);
                    }
                    OnJoin?.Invoke(a, last, false);
                }
                finally { Moved = false; }
                break;
            }
            case Kind.Hit:
            case Kind.Smash:
                LastDamage = Hp[t] - last;
                LastSmash = k == Kind.Smash;
                Hp[t] = last;
                Hits++;
                OnHit?.Invoke(a, t, last);
                break;
            case Kind.Leave:
            {
                byte g = Group[a];
                if (g == 0) return;
                Take(a);
                LeftHp = Hp[a];
                Hp[a] = MaxHp;
                OnLeave?.Invoke(a, g);
                break;
            }
        }
    }

    private static void Put(byte id, byte g)
    {
        if (Group[id] == g || Count[g] >= MaxMembers) return;
        if (Group[id] != 0) Take(id);
        if (Count[g] == 0) _groups++;
        Members[g * MaxMembers + Count[g]++] = id;
        Group[id] = g;
        FarSince[id] = 0;
    }

    private static void Take(byte id)
    {
        byte g = Group[id];
        Group[id] = 0;
        int b = g * MaxMembers, n = Count[g];
        for (int i = 0; i < n; i++)
        {
            if (Members[b + i] != id) continue;
            for (int j = i; j < n - 1; j++) Members[b + j] = Members[b + j + 1];
            Count[g]--;
            if (Count[g] == 0) _groups--;
            return;
        }
    }

    // ホスト: 二人を同じ組にする。どちらも組の外なら新しい組・片方が組にいれば空きがあれば加わる・別の組同士は合わせて入りきれば合流。
    // 同じ組になったら true
    internal static bool HostJoin(byte a, byte t)
    {
        if (a == t) return false;
        byte ga = Group[a], gt = Group[t];
        if (ga != 0 && ga == gt) return true;
        if (ga == 0 && gt == 0)
        {
            byte g = FreeGroup();
            if (g == 0) return false;
            Send(Kind.Start, a, t, g);
            return true;
        }
        if (ga == 0 || gt == 0)
        {
            byte g = ga != 0 ? ga : gt, joiner = ga != 0 ? t : a;
            if (Count[g] >= MaxMembers) return false;
            Send(Kind.Join, joiner, joiner, g);
            return true;
        }
        if (Count[ga] + Count[gt] > MaxMembers) return false;
        // 少ない方を多い方へ移す (移している間に名簿が詰まるので先に写す)
        byte into = Count[ga] >= Count[gt] ? ga : gt, from = into == ga ? gt : ga;
        int n = Count[from];
        for (int i = 0; i < n; i++) Moving[i] = Members[from * MaxMembers + i];
        for (int i = 0; i < n; i++) Send(Kind.Join, Moving[i], Moving[i], into);
        return true;
    }

    private static byte FreeGroup()
    {
        for (byte g = 1; g <= MaxGroups; g++)
            if (Count[g] == 0) return g;
        return 0;
    }

    // ホスト: 攻撃が当たった。start なら二人を同じ組にし (HostJoin)、体力を減らす。0 になったら true (倒すのは呼んだ側)。
    // 組に入れなかった人 (組が満員など) は体力だけ減る。start = false は組にしない当たり (この一振りで倒れる相手)。当たりの見た目だけ全員に出る
    // smash = 吹き飛ばす一撃 (受けた人が飛ばされる・倒れた時は飛んだ先に倒れる)
    internal static bool HostDamage(byte attacker, byte target, int damage, bool start = true, bool smash = false)
    {
        if (start) HostJoin(attacker, target);
        FarSince[attacker] = FarSince[target] = 0;
        LastHit[target] = Environment.TickCount64;
        int hp = Math.Max(Hp[target] - damage, 0);
        Send(smash ? Kind.Smash : Kind.Hit, attacker, target, hp);
        return hp == 0;
    }

    // ホスト: この人を組から抜く (倒れた後など)
    internal static void HostLeave(byte id)
    {
        if (Group[id] != 0) Send(Kind.Leave, id, id, Group[id]);
    }

    // ホスト: 4 回/秒、倒れた・抜けた・皆から離れたままの人を組から抜き、1 人になった組を畳む
    internal static void HostTick()
    {
        long now = Environment.TickCount64;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckMs;
        if (!GameData.Instance) return;
        // 組の外で削られた体力は、しばらく当たらなければ全快に戻す
        for (int i = 0; i < 255; i++)
            if (Hp[i] < MaxHp && Group[i] == 0 && now - LastHit[i] >= LeaveMs) Send(Kind.Hit, (byte)i, (byte)i, MaxHp);
        if (_groups == 0) return;
        for (byte g = 1; g <= MaxGroups; g++)
        {
            if (Count[g] == 0) continue;
            int b = g * MaxMembers;
            for (int i = Count[g] - 1; i >= 0; i--)
            {
                byte id = Members[b + i];
                var p = GameData.Instance.GetPlayerById(id);
                if (p == null || p.IsDead || p.Disconnected || !p.Object) { HostLeave(id); continue; }
                var u = p.Object.GetTruePosition();
                bool near = false;
                for (int j = 0; j < Count[g] && !near; j++)
                {
                    if (j == i) continue;
                    var q = GameData.Instance.GetPlayerById(Members[b + j]);
                    if (q == null || !q.Object) continue;
                    var w = q.Object.GetTruePosition();
                    float dx = u.x - w.x, dy = u.y - w.y;
                    near = dx * dx + dy * dy < LeaveDistance * LeaveDistance;
                }
                if (near) { FarSince[id] = 0; continue; }
                if (FarSince[id] == 0) FarSince[id] = now;
                else if (now - FarSince[id] >= LeaveMs) HostLeave(id);
            }
            // 1 人だけ・インポスターだけ (戦う相手がいない) になった組は畳む
            if (Count[g] == 1 || Count[g] > 1 && AllImpostors(g))
                for (int i = Count[g] - 1; i >= 0; i--) HostLeave(Members[b + i]);
        }
    }

    private static bool AllImpostors(byte g)
    {
        for (int i = 0; i < Count[g]; i++)
        {
            var p = GameData.Instance.GetPlayerById(Members[g * MaxMembers + i]);
            if (p == null || !p.Role || !p.Role.IsImpostor) return false;
        }
        return true;
    }

    // 会議が始まったら全部の組を畳む (全員の端末で同じに)
    internal static void OnMeeting()
    {
        for (byte g = 1; g <= MaxGroups; g++)
            while (Count[g] > 0)
            {
                byte id = Members[g * MaxMembers + Count[g] - 1];
                Take(id);
                LeftHp = Hp[id];
                OnLeave?.Invoke(id, g);
            }
        Reset();
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"started={Started} joined={Joined} hits={Hits} groups={_groups}");
        for (byte g = 1; g <= MaxGroups; g++)
        {
            if (Count[g] == 0) continue;
            sb.Append(" [g").Append(g);
            for (int i = 0; i < Count[g]; i++)
            {
                byte id = Members[g * MaxMembers + i];
                sb.Append(' ').Append(id).Append(':').Append(Hp[id]);
            }
            sb.Append(']');
        }
        return sb.ToString();
    }
}
