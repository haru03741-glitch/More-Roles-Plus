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
// 0.2 秒に 1 通まで (= 秒 5 本) にまとめ、1 通は 14 件 (最大 466B) まで。
internal static class TerrainSync
{
    public const byte RpcId = 213; // MRP の地形同期 (本編の RpcCalls と重ならない高い番号)

    private const int FlushTicks = 10;         // FixedUpdate (50Hz) で数えて 0.2 秒
    private const int MaxEventsPerRpc = 14;     // 瓦礫の止まる所を含めて 1 通 466B まで
    private const int RequestWindowTicks = 50; // ホストが 1 人から受ける依頼を 1 秒あたり何件まで認めるか
    private const int MaxRequestsPerWindow = 8;

    private static readonly List<ResolvedDamage> Outbox = new(); // ホスト: 未送信の結果 (連番は _nextSeq - Count から)
    private static readonly List<DamageEvent> Requests = new();  // 客: ホストへ未送信の依頼
    private static readonly Dictionary<ushort, ResolvedDamage> Pending = new(); // 客: 順番待ち (前の連番が未着)
    private static readonly Dictionary<byte, (int window, int count)> RequestRate = new();
    private static readonly byte[] Buf = new byte[TerrainWire.BatchHeader + MaxEventsPerRpc * TerrainWire.MaxResolvedBytes];

    // ホストは次に振る連番、客は次に適用する連番。ホストが替わったら新ホストはここから振り続ける
    private static ushort _nextSeq;
    private static ShipStatus _ship;
    private static int _tick, _lastFlush = -FlushTicks, _stuckSince;
    private const int GapTimeoutTicks = 150; // 欠番を待つのは 3 秒まで
    private const int NoHost = int.MinValue;
    private static int _batchHost = NoHost; // 客: 最後に束を受けたホスト

    public static int Applied { get; private set; } // テスト用: この試合で適用した件数

    // 破壊を依頼する唯一の入口。返り値はログ・テスト用の説明
    public static string Request(in DamageEvent e)
    {
        if (!Online())
        {
            SyncShip();
            if (!TerrainDamage.TryResolve(e, out var r, out string why)) return why;
            return ApplyNow(TerrainWire.RoundTrip(r), decide: true, out _);
        }
        if (AmongUsClient.Instance.AmHost) return HostAccept(e);
        Requests.Add(e);
        return "requested";
    }

    // ホスト: 依頼を決めて自分に適用し、配る列に積む
    private static string HostAccept(in DamageEvent e)
    {
        SyncShip();
        if (!TerrainDamage.TryResolve(e, out var r, out string why)) return why;
        r = TerrainWire.RoundTrip(r);
        _nextSeq++;
        // 大きな瓦礫の止まる所は、ホストが自分で適用した結果から決めて同じ電文に載せる
        string res = ApplyNow(r, decide: true, out var landings);
        Outbox.Add(r.WithLandings(landings));
        return res;
    }

    // decide = 大きな瓦礫の止まる所を自分で決める (ホスト・一人の時)。客は届いた結果の物を使う
    private static string ApplyNow(in ResolvedDamage r, bool decide, out RubbleLanding[] landings)
    {
        Applied++;
        return TerrainDamage.Apply(r, decide, out landings);
    }

    // 毎 FixedUpdate。積んだ物が無い時は整数 1 つの加算と比較だけで帰る
    public static void Tick()
    {
        _tick++;
        if (Pending.Count != 0 && _tick - _stuckSince > GapTimeoutTicks) SkipGap();
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
            foreach (var e in Requests) HostAccept(e);
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

    private static void Send(int length, int target)
    {
        var client = AmongUsClient.Instance;
        // (long) 必須: net10 (Android) では int が nint のポインタ受けコンストラクタに解決され、壊れた配列で落ちる
        var payload = new Il2CppStructArray<byte>((long)length);
        for (int i = 0; i < length; i++) payload[i] = Buf[i];
        var w = client.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, target);
        w.WriteBytesAndSize(payload);
        client.FinishRpcImmediately(w);
    }

    // 受信 (PlayerControl.HandleRpc から)。sender = 送ってきたプレイヤー
    internal static void Receive(PlayerControl sender, MessageReader reader)
    {
        var client = AmongUsClient.Instance;
        if (!client || !sender) return;
        byte[] b = reader.ReadBytesAndSize();
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
                    HostAccept(e);
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
                    Deliver((ushort)(first + i), r);
                }
                break;
            }
        }
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
        while (Pending.Remove(_nextSeq, out var next))
        {
            _nextSeq++;
            string res = ApplyNow(next, decide: false, out _);
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

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class TerrainRpcPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] byte callId, [HarmonyArgument(1)] MessageReader reader)
    {
        if (callId != TerrainSync.RpcId) return true;
        try { TerrainSync.Receive(__instance, reader); }
        catch (Exception ex) { Plugin.Logger.LogError($"[TerrainSync] receive: {ex}"); }
        return false;
    }
}
