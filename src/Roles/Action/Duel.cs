using System;
using MoreRolesPlus.Net;

namespace MoreRolesPlus.Roles;

// 立ち合い (アクション役職の斬り合い) と、その間だけの体力。
// 攻撃が当たると、攻撃した人と当たった人の 1 対 1 の立ち合いが始まり、当たった人の体力が減る。0 で倒れる (倒すのは呼んだ役職)。
// どちらかが倒れる・離れたまま時間が経つ・会議になると終わり、二人とも全快に戻る。体力と組はホストが持ち、変わるたびに全員へ配る
internal static class Duel
{
    internal const int MaxHp = 100;
    private const byte None = 255;
    private const float LeaveDistance = 7f;   // これより離れて
    private const int LeaveMs = 4000;         // この時間が経ったら終わり
    private const int CheckMs = 250;

    // 全員の端末で持つ写し (ホストは正本)
    private static readonly byte[] Hp = new byte[256];
    private static readonly byte[] Opp = new byte[256];
    private static readonly long[] FarSince = new long[256];
    private static readonly long[] LastHit = new long[256]; // ホスト: 最後に当たった時刻 (立ち合いの外の体力を戻す)
    private static long _nextCheck;

    // 立ち合いの始まり・終わり・体力の変化 (全員の端末で)。HUD が聞く
    internal static event Action<byte, byte> OnStart;      // (攻撃した人, 当たった人)
    internal static event Action<byte, byte> OnEnd;
    internal static event Action<byte, byte, int> OnHit;   // (攻撃した人, 当たった人, 残りの体力)

    internal static int Started, Hits;
    internal static int LastDamage; // 今の OnHit で減った体力 (回復なら負)

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
        Events<MeetingStartEvent>.Subscribe(_ => OnMeeting(), match, owner: "Duel");
    }

    internal static void Reset()
    {
        for (int i = 0; i < 256; i++)
        {
            Hp[i] = MaxHp;
            Opp[i] = None;
            FarSince[i] = 0;
        }
    }

    internal static int HpOf(byte id) => Hp[id];
    internal static byte OpponentOf(byte id) => Opp[id];
    internal static bool InDuel(byte id) => Opp[id] != None;

    private enum Kind : byte { Start = 1, Hit = 2, End = 3 }

    // 値 = 種類 | 攻撃した人 << 8 | 当たった人 << 16 | 体力 << 24
    private static readonly RemoteCall<int> StateCall = new("Duel.State", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(), (_, v) => Apply(v));

    private static void Send(Kind k, byte a, byte t, int hp)
    {
        int v = (byte)k | a << 8 | t << 16 | (byte)Math.Clamp(hp, 0, 255) << 24;
        StateCall.Send(v);
        Apply(v); // 配る電文は送った本人 (ホスト) の手元では動かない
    }

    private static void Apply(int v)
    {
        var k = (Kind)(v & 0xFF);
        byte a = (byte)(v >> 8), t = (byte)(v >> 16);
        int hp = (v >> 24) & 0xFF;
        switch (k)
        {
            case Kind.Start:
                // 体力は引き継ぐ (立ち合いの外で先に削られていた分も残す)
                Opp[a] = t;
                Opp[t] = a;
                Started++;
                OnStart?.Invoke(a, t);
                break;
            case Kind.Hit:
                LastDamage = Hp[t] - hp;
                Hp[t] = (byte)hp;
                Hits++;
                OnHit?.Invoke(a, t, hp);
                break;
            case Kind.End:
                if (Opp[a] == t) Opp[a] = None;
                if (Opp[t] == a) Opp[t] = None;
                Hp[a] = Hp[t] = MaxHp;
                OnEnd?.Invoke(a, t);
                break;
        }
    }

    // ホスト: 攻撃が当たった。どちらも立ち合い中でなければ二人の立ち合いを始め、体力を減らす。0 になったら true (倒すのは呼んだ側)。
    // 立ち合いの相手でない人に当たった時 (一振りで何人も・横から割り込んだ人) は組み直さず体力だけ減らす
    internal static bool HostDamage(byte attacker, byte target, int damage)
    {
        if (Opp[attacker] == None && Opp[target] == None) Send(Kind.Start, attacker, target, MaxHp);
        FarSince[attacker] = FarSince[target] = 0;
        LastHit[target] = Environment.TickCount64;
        int hp = Math.Max(Hp[target] - damage, 0);
        Send(Kind.Hit, attacker, target, hp);
        return hp == 0;
    }

    // ホスト: 立ち合いを終える (倒れた後など)
    internal static void HostEnd(byte a)
    {
        byte t = Opp[a];
        if (t != None) Send(Kind.End, a, t, MaxHp);
    }

    // ホスト: 4 回/秒、倒れた・抜けた・離れたままの立ち合いを終える
    internal static void HostTick()
    {
        long now = Environment.TickCount64;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckMs;
        if (!GameData.Instance) return;
        // 立ち合いの外で削られた体力は、しばらく当たらなければ全快に戻す
        for (int i = 0; i < 255; i++)
            if (Hp[i] < MaxHp && Opp[i] == None && now - LastHit[i] >= LeaveMs) Send(Kind.Hit, (byte)i, (byte)i, MaxHp);
        for (int a = 0; a < 255; a++)
        {
            byte t = Opp[a];
            if (t == None || t < a) continue; // 組は 1 回だけ見る
            var pa = GameData.Instance.GetPlayerById((byte)a);
            var pt = GameData.Instance.GetPlayerById(t);
            if (pa == null || pt == null || pa.IsDead || pt.IsDead || pa.Disconnected || pt.Disconnected || !pa.Object || !pt.Object)
            {
                Send(Kind.End, (byte)a, t, MaxHp);
                continue;
            }
            var ua = pa.Object.GetTruePosition();
            var ut = pt.Object.GetTruePosition();
            float dx = ua.x - ut.x, dy = ua.y - ut.y;
            if (dx * dx + dy * dy < LeaveDistance * LeaveDistance) { FarSince[a] = 0; continue; }
            if (FarSince[a] == 0) FarSince[a] = now;
            else if (now - FarSince[a] >= LeaveMs) Send(Kind.End, (byte)a, t, MaxHp);
        }
    }

    // 会議が始まったら全部の立ち合いを畳む (全員の端末で同じに)
    internal static void OnMeeting()
    {
        for (int a = 0; a < 256; a++)
        {
            byte t = Opp[a];
            if (t == None || t < a) continue;
            Opp[a] = Opp[t] = None;
            OnEnd?.Invoke((byte)a, t);
        }
        Reset();
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"started={Started} hits={Hits}");
        for (int a = 0; a < 256; a++)
            if (Opp[a] != None && Opp[a] > a) sb.Append($" [{a}:{Hp[a]} vs {Opp[a]}:{Hp[Opp[a]]}]");
        return sb.ToString();
    }
}
