using System;
using System.Collections.Generic;
using Hazel;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 地形破壊の同期。ホストが依頼を結果 (ResolvedDamage) に決めて連番を振り、全員が連番の順に適用する。
// 武器・役職からの入口は Request だけ。フリープレイなど一人の時はその場で決めて適用する。
// 通信量は公式鯖の制約を予算として守る: 同じ種類の Reliable を秒十数本出すと切断される実測があるので、
// 0.2 秒に 1 通まで (= 秒 5 本) にまとめ、1 通は 14 件 (中身 480B・封筒込みで 487B) まで。
internal static class TerrainSync
{
    private const int FlushTicks = 10;         // FixedUpdate (50Hz) で数えて 0.2 秒
    private const int MaxEventsPerRpc = 14;     // 瓦礫の止まる所を含めて中身 480B まで
    private const int RequestWindowTicks = 50; // ホストが 1 人から受ける依頼を 1 秒あたり何件まで認めるか
    private const int MaxRequestsPerWindow = 8;

    private static readonly List<ResolvedDamage> Outbox = new(); // ホスト: 未送信の結果 (連番は _nextSeq - Count から)
    private static readonly List<DamageEvent> Requests = new();  // 客: ホストへ未送信の依頼
    private static readonly Dictionary<ushort, ResolvedDamage> Pending = new(); // 客: 順番待ち (前の連番が未着)
    private static readonly Dictionary<byte, (int window, int count)> RequestRate = new();
    private static readonly byte[] Buf = new byte[TerrainWire.BatchHeader + MaxEventsPerRpc * TerrainWire.MaxResolvedBytes];
    private static readonly byte[] BombBuf = new byte[2 + TerrainWire.BombBytes];

    // ホストは次に振る連番、客は次に適用する連番。ホストが替わったら新ホストはここから振り続ける
    private static ushort _nextSeq;
    private static ShipStatus _ship;
    private static int _tick, _lastFlush = -FlushTicks, _stuckSince;
    private const int GapTimeoutTicks = 150; // 欠番を待つのは 3 秒まで
    private const int NoHost = int.MinValue;
    private const int ReportTicks = 50;          // 客が指紋を返すのは 1 秒に 1 通まで
    private const float LagMargin = 1.5f;        // 打撃: ホストに見えている位置の遅れの分
    private const float MaxBlastDistance = 20f;  // 爆発: 投げた・撃った物が届く所まで
    private const float MaxBlastRadius = 3f;
    private static int _lastReport = -ReportTicks;
    private static ushort _reportedSeq = ushort.MaxValue;
    private static int _batchHost = NoHost; // 客: 最後に束を受けたホスト
    // 客: 自分の振りが壁に当たる時刻 (Time.time)。それより早く届いた自分の打撃は、そこまで適用を待たせる (後ろの番号も順番待ち)
    // (試合の最初の束で SyncShip が走るので、そこでは消さない。待つのは長くて HitAt 秒)
    private static float _ownHitAt;

    public static int Applied { get; private set; } // テスト用: この試合で適用した件数
    public static int Refused { get; private set; }    // ホスト: 受けなかった依頼の数
    public static int Mismatches { get; private set; } // ホスト: 客の地形が自分と違った回数
    internal static readonly Dictionary<byte, (ushort seq, bool same)> Checks = new(); // ホスト: 客ごとの最後の照合

    // 破壊を依頼する唯一の入口。返り値はログ・テスト用の説明
    public static string Request(in DamageEvent e)
    {
        if (!Online())
        {
            SyncShip();
            if (!TerrainDamage.TryResolve(e, out var r, out string why)) return why;
            return ApplyNow(_nextSeq++, TerrainWire.RoundTrip(r.WithActor(LocalId()).WithTick(GameClock.Stamp)), decide: true, out _);
        }
        if (AmongUsClient.Instance.AmHost) return HostAccept(e, LocalId());
        Requests.Add(e);
        return "requested";
    }

    // ホスト: 依頼を決めて自分に適用し、配る列に積む
    private static string HostAccept(in DamageEvent e, byte actor)
    {
        SyncShip();
        if (!TerrainDamage.TryResolve(e, out var r, out string why)) return why;
        r = TerrainWire.RoundTrip(r.WithActor(actor).WithTick(GameClock.Stamp));
        // 大きな瓦礫の止まる所は、ホストが自分で適用した結果から決めて同じ電文に載せる
        string res = ApplyNow(_nextSeq++, r, decide: true, out var landings);
        Outbox.Add(r.WithLandings(landings));
        return res;
    }

    // decide = 大きな瓦礫の止まる所を自分で決める (ホスト・一人の時)。客は届いた結果の物を使う
    private static string ApplyNow(ushort seq, in ResolvedDamage r, bool decide, out RubbleLanding[] landings)
    {
        Applied++;
        TerrainDigest.Begin();
        try { return TerrainDamage.Apply(r, decide, out landings); }
        finally
        {
            TerrainDigest.End(seq);
            BreakNoise.Emit(r);
            HammerSwing.OnApplied(r);
            BombFuse.OnApplied(r);
            DustCloud.OnApplied(r);
            WaterLeak.OnApplied(r);
            WaterSim.OnApplied(r);
            PropSim.OnApplied(r);
        }
    }

    // 毎 FixedUpdate。積んだ物が無い時は整数 1 つの加算と比較だけで帰る
    public static void Tick()
    {
        _tick++;
        if (_ownHitAt > 0f && Time.time >= _ownHitAt)
        {
            _ownHitAt = 0f;
            if (Pending.Count != 0) Drain();
        }
        if (Pending.Count != 0 && _tick - _stuckSince > GapTimeoutTicks) SkipGap();
        if (TerrainDigest.Any && TerrainDigest.LastSeq != _reportedSeq && _tick - _lastReport >= ReportTicks) Report();
        if (Outbox.Count == 0 && Requests.Count == 0) return;
        if (_tick - _lastFlush < FlushTicks) return;
        _lastFlush = _tick;
        try { Flush(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[TerrainSync] flush: {ex}"); }
    }

    private static void Flush()
    {
        if (!Online()) { Outbox.Clear(); Requests.Clear(); return; }
        var client = AmongUsClient.Instance;
        if (client.AmHost)
        {
            // 客の間にホストになった: 自分の依頼は自分で決める
            foreach (var e in Requests) HostAccept(e, LocalId());
            Requests.Clear();
            if (Outbox.Count == 0) return;

            int n = Math.Min(Outbox.Count, MaxEventsPerRpc);
            ushort first = (ushort)(_nextSeq - Outbox.Count);
            int o = 0;
            Buf[o++] = TerrainWire.OpBatch;
            o = TerrainWire.WriteU16(Buf, o, first);
            Buf[o++] = (byte)n;
            for (int i = 0; i < n; i++) o = TerrainWire.WriteResolved(Buf, o, Outbox[i]);
            Outbox.RemoveRange(0, n);
            Send(o, -1);
        }
        else
        {
            Outbox.Clear();
            int n = Math.Min(Requests.Count, MaxEventsPerRpc);
            int o = 0;
            Buf[o++] = TerrainWire.OpRequest;
            Buf[o++] = (byte)n;
            for (int i = 0; i < n; i++) o = TerrainWire.WriteRequest(Buf, o, Requests[i]);
            Requests.RemoveRange(0, n);
            Send(o, client.HostId);
        }
    }

    // 客: 適用し終えた所の指紋をホストへ返す ([op][連番 u16][指紋 u32] = 7B・1 秒に 1 通まで・新しく適用した時だけ)
    private static void Report()
    {
        _lastReport = _tick;
        if (!Online() || AmongUsClient.Instance.AmHost) return;
        ushort seq = TerrainDigest.LastSeq;
        _reportedSeq = seq;
        int o = 0;
        Buf[o++] = TerrainWire.OpDigest;
        o = TerrainWire.WriteU16(Buf, o, seq);
        o = TerrainWire.WriteU32(Buf, o, TerrainDigest.Running);
        Send(o, AmongUsClient.Instance.HostId);
    }

    private static void Send(int length, int target) => Wire.Send(new ArraySegment<byte>(Buf, 0, length), target);

    // 依頼 (客 → ホスト) と結果 (ホスト → 全員) の両方がこの 1 種類で、先頭の 1 バイトで分ける
    private static readonly Net.RemoteCall<ArraySegment<byte>> Wire = new("Terrain.Wire", Net.Route.Anyone,
        (w, data) =>
        {
            // (long) 必須: net10 (Android) では int が nint のポインタ受けコンストラクタに解決され、壊れた配列で落ちる
            var payload = new Il2CppStructArray<byte>((long)data.Count);
            for (int i = 0; i < data.Count; i++) payload[i] = data.Array[data.Offset + i];
            w.WriteBytesAndSize(payload);
        },
        r => new ArraySegment<byte>(r.ReadBytesAndSize()),
        (sender, data) => Receive(sender, data.Array));

    // 受信。sender = 送ってきたプレイヤー
    private static void Receive(PlayerControl sender, byte[] b)
    {
        var client = AmongUsClient.Instance;
        if (!client || !sender) return;
        if (b == null || b.Length < 2) return;
        int o = 1;
        switch (b[0])
        {
            case TerrainWire.OpRequest:
            {
                if (!client.AmHost) return;
                int n = b[o++];
                for (int i = 0; i < n && o + TerrainWire.MaxRequestBytes <= b.Length; i++)
                {
                    o = TerrainWire.ReadRequest(b, o, out var e);
                    if (!AllowRequest(sender.PlayerId)) continue;
                    string bad = Refuse(sender, e);
                    if (bad != null) { Refused++; Plugin.Logger.LogDebug($"[TerrainSync] refused {sender.PlayerId}: {bad}"); continue; }
                    HostAccept(e, sender.PlayerId);
                }
                break;
            }
            case TerrainWire.OpBatch:
            {
                // 結果はホストからだけ受ける
                if (client.AmHost || sender.OwnerId != client.HostId || b.Length < TerrainWire.BatchHeader) return;
                if (!ShipStatus.Instance) { Plugin.Logger.LogWarning("[TerrainSync] batch before map loaded, dropped"); return; }
                SyncShip();
                ushort first = TerrainWire.ReadU16(b, ref o);
                int n = b[o++];
                // 試合で最初の束・ホストが替わった後の最初の束は、その連番に合わせる (新ホストの数え方に従う)
                if (_batchHost != client.HostId)
                {
                    if (_batchHost != NoHost && first != _nextSeq)
                        Plugin.Logger.LogWarning($"[TerrainSync] host changed, resync seq {_nextSeq} -> {first} (pending {Pending.Count} dropped)");
                    _batchHost = client.HostId;
                    _nextSeq = first;
                    Pending.Clear();
                }
                for (int i = 0; i < n && o < b.Length; i++)
                {
                    o = TerrainWire.ReadResolved(b, o, out var r);
                    if (o < 0) { Plugin.Logger.LogWarning($"[TerrainSync] truncated batch at #{(ushort)(first + i)}"); break; }
                    GameClock.Observe(r.Tick);
                    Deliver((ushort)(first + i), r);
                }
                break;
            }
            case TerrainWire.OpPlace:
            {
                if (!client.AmHost || b.Length < 1 + TerrainWire.BombBytes) return;
                TerrainWire.ReadBomb(b, o, out Vector2 pos, out float radius);
                if (!AllowRequest(sender.PlayerId)) return;
                string bad = BombFuse.HostRefuse(sender, pos, radius);
                if (bad != null) { Refused++; Plugin.Logger.LogDebug($"[TerrainSync] bomb refused {sender.PlayerId}: {bad}"); return; }
                BombFuse.Accept(sender.PlayerId, pos, radius);
                break;
            }
            case TerrainWire.OpBomb:
            {
                if (client.AmHost || sender.OwnerId != client.HostId || b.Length < 2 + TerrainWire.BombBytes) return;
                byte actor = b[o++];
                TerrainWire.ReadBomb(b, o, out Vector2 pos, out float radius);
                BombFuse.Show(actor, pos, radius);
                break;
            }
            case TerrainWire.OpDigest:
            {
                if (!client.AmHost || b.Length < 7) return;
                ushort seq = TerrainWire.ReadU16(b, ref o);
                uint hash = TerrainWire.ReadU32(b, ref o);
                // 自分の指紋が無い番号 (古すぎる・ホスト交代の前) は比べない
                if (!TerrainDigest.TryGet(seq, out uint mine)) return;
                bool same = mine == hash;
                Checks[sender.PlayerId] = (seq, same);
                if (!same)
                {
                    Mismatches++;
                    Plugin.Logger.LogWarning($"[TerrainSync] terrain differs on player {sender.PlayerId} at #{seq} ({hash:x8} vs host {mine:x8})");
                }
                break;
            }
        }
    }

    // ホスト: 依頼を受けない理由 (受けるなら null)。依頼は武器を使った本人の位置の近くでしか起きない
    private static string Refuse(PlayerControl sender, in DamageEvent e)
    {
        var data = sender.Data;
        if (data == null || data.IsDead || data.Disconnected) return "not alive";
        Vector2 at = sender.GetTruePosition();
        float dx = e.Position.x - at.x, dy = e.Position.y - at.y;
        float max = e.Kind == DamageKind.Blunt ? DamageProfile.Of(e.Kind).Reach + LagMargin : MaxBlastDistance;
        if (dx * dx + dy * dy > max * max) return $"too far {MathF.Sqrt(dx * dx + dy * dy):0.0}";
        if (e.Kind == DamageKind.Explosion && e.Size > MaxBlastRadius) return $"too large {e.Size:0.0}";
        return null;
    }

    // 連番の順に適用する。先に着いた後ろの番号は前が揃うまで待たせる。既に適用した番号は捨てる
    internal static void Deliver(ushort seq, in ResolvedDamage r)
    {
        if ((short)(seq - _nextSeq) < 0) return;
        if (Pending.Count == 0) _stuckSince = _tick;
        Pending[seq] = r;
        Drain();
    }

    private static void Drain()
    {
        while (Pending.TryGetValue(_nextSeq, out var next))
        {
            if (_ownHitAt > 0f && next.Kind == DamageKind.Blunt && next.Actor == LocalId() && Time.time < _ownHitAt) break;
            Pending.Remove(_nextSeq);
            string res = ApplyNow(_nextSeq++, next, decide: false, out _);
            Plugin.Logger.LogDebug($"[TerrainSync] #{(ushort)(_nextSeq - 1)} {res}");
        }
        _stuckSince = _tick;
    }

    // 欠番が来ないまま待ち続けない: 一定時間詰まったら、待っている中で一番前の番号まで飛ばす
    private static void SkipGap()
    {
        ushort lowest = 0;
        bool any = false;
        foreach (var k in Pending.Keys)
            if (!any || (short)(k - lowest) < 0) { lowest = k; any = true; }
        if (!any) return;
        Plugin.Logger.LogWarning($"[TerrainSync] gap {_nextSeq}..{(ushort)(lowest - 1)} never arrived, skipped");
        _nextSeq = lowest;
        Drain();
    }

    // 爆弾: 客 → ホストへ置く依頼 / ホスト → 全員へ置かれた知らせ (どちらもすぐ送る・数は少ない)
    internal static void SendPlace(Vector2 pos, float radius)
    {
        if (!IsGuest()) return;
        BombBuf[0] = TerrainWire.OpPlace;
        int o = TerrainWire.WriteBomb(BombBuf, 1, pos, radius);
        Wire.Send(new ArraySegment<byte>(BombBuf, 0, o), AmongUsClient.Instance.HostId);
    }

    internal static void BroadcastBomb(byte actor, Vector2 pos, float radius)
    {
        if (!Online() || !AmongUsClient.Instance.AmHost) return;
        BombBuf[0] = TerrainWire.OpBomb;
        BombBuf[1] = actor;
        int o = TerrainWire.WriteBomb(BombBuf, 2, pos, radius);
        Wire.Send(new ArraySegment<byte>(BombBuf, 0, o), -1);
    }

    // ホストと一人の時: その人の破壊として決めて配る (爆弾の導火線が尽きた時など、依頼を受けた後でホストが起こす破壊)
    internal static string RequestAs(in DamageEvent e, byte actor)
    {
        if (Online() && AmongUsClient.Instance.AmHost) return HostAccept(e, actor);
        return Request(e);
    }

    // 客: 自分の振りが壁に当たる時刻を知らせる (それまで自分の打撃の結果を適用しない)。ホストと一人の時は当たる時に依頼する
    internal static void HoldOwnHitUntil(float time) => _ownHitAt = time;

    internal static bool IsGuest() => Online() && !AmongUsClient.Instance.AmHost;

    private static byte LocalId() => PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : ResolvedDamage.NoActor;

    private static bool AllowRequest(byte playerId)
    {
        int window = _tick / RequestWindowTicks;
        var (w, c) = RequestRate.TryGetValue(playerId, out var v) ? v : (window, 0);
        if (w != window) c = 0;
        if (c >= MaxRequestsPerWindow) return false;
        RequestRate[playerId] = (window, c + 1);
        return true;
    }

    private static bool Online()
    {
        var client = AmongUsClient.Instance;
        return client && client.NetworkMode != NetworkModes.FreePlay && client.AmConnected && PlayerControl.LocalPlayer;
    }

    // マップ (試合) が変わったら連番を 0 から数え直す。全員が同じ ShipStatus の切り替わりで揃う
    private static void SyncShip()
    {
        var ship = ShipStatus.Instance;
        if (_ship == ship) return;
        _ship = ship;
        _nextSeq = 0;
        _batchHost = NoHost;
        Applied = 0;
        Refused = 0;
        Mismatches = 0;
        Checks.Clear();
        _reportedSeq = ushort.MaxValue;
        TerrainDigest.Reset();
        Pending.Clear();
        Outbox.Clear();
        Requests.Clear();
        RequestRate.Clear();
    }

    // テスト用: 結果を電文に書いて読み直し、受け手の順番待ちを通して適用する (一人で同期の経路を確かめる)。
    // reverse = 後ろの連番から届けて、順番待ちが前の到着まで適用を止めることを確かめる
    internal static string Loopback(DamageEvent[] events, bool reverse)
    {
        SyncShip();
        var resolved = new List<ResolvedDamage>();
        foreach (var e in events)
            if (TerrainDamage.TryResolve(e, out var r, out _)) resolved.Add(r);
        if (resolved.Count == 0) return "nothing to deliver";

        int o = 0;
        foreach (var r in resolved) o = TerrainWire.WriteResolved(Buf, o, r);
        var decoded = new List<ResolvedDamage>();
        for (int p = 0; p < o;)
        {
            p = TerrainWire.ReadResolved(Buf, p, out var r);
            if (p < 0) break;
            decoded.Add(r);
        }

        ushort first = _nextSeq;
        int before = Applied;
        var log = new System.Text.StringBuilder($"bytes={o} n={decoded.Count}");
        for (int k = 0; k < decoded.Count; k++)
        {
            int i = reverse ? decoded.Count - 1 - k : k;
            Deliver((ushort)(first + i), decoded[i]);
            log.Append($" deliver#{i}->applied={Applied - before}");
        }
        return log.ToString();
    }
}
