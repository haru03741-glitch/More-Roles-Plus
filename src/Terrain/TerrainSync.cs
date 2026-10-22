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
    // 全員: この船で適用した結果 (連番 → 結果)。欠番を頼まれた時にもう一度送る。ホストが替わっても新ホストが答えられるよう客も持つ
    private static readonly Dictionary<ushort, ResolvedDamage> History = new();
    private const int MaxHistory = 8192;
    // 客: 船ができる前に届いたホストからの電文。船ができたら届いた順に読み直す (捨てると後から埋まらない)
    private static readonly List<(PlayerControl sender, byte[] data)> Early = new();
    private const int MaxEarly = 256;
    private static readonly Dictionary<byte, (int window, int count)> RequestRate = new();
    private static readonly byte[] Buf = new byte[TerrainWire.BatchHeader + MaxEventsPerRpc * TerrainWire.MaxResolvedBytes];
    private static readonly byte[] BombBuf = new byte[2 + TerrainWire.BombBytes];
    private static readonly byte[] DoorBuf = new byte[TerrainWire.DoorsBytes];
    private static readonly byte[] PlatformBuf = new byte[TerrainWire.PlatformBytes];
    private static readonly byte[] PortalBuf = new byte[TerrainWire.PortalBytes];

    // ホストは次に振る連番、客は次に適用する連番。ホストが替わったら新ホストはここから振り続ける
    private static ushort _nextSeq;
    private static ShipStatus _ship;
    private static int _tick, _lastFlush = -FlushTicks, _stuckSince;
    private const int GapTimeoutTicks = 500;  // 欠番を待つのは 10 秒まで (その間に何度かホストへ頼む)
    private const int ResendAskTicks = 50;    // 欠番を 1 秒待ったらホストへ頼む・頼むのも 1 秒に 1 回まで
    private const int MaxResendBatches = 3;   // 1 回の頼みに返す束の数
    private static int _lastAsk = -ResendAskTicks;
    // 確かめ用 (ブリッジ terrainsync drop/hold): 客で次の束を捨てる数・船ができていないふりをする時刻 (Time.time)
    internal static int TestDropBatches;
    internal static float TestHoldShipUntil;
    internal static int EarlyCount => Early.Count;
    public static int ResendAsked { get; private set; }  // 客: 欠番を頼んだ回数
    public static int ResendServed { get; private set; } // ホスト: 頼まれて送り直した件数
    private const int NoHost = int.MinValue;
    private const int ReportTicks = 50;          // 客が指紋を返すのは 1 秒に 1 通まで
    private const float LagMargin = 1.5f;        // 打撃: ホストに見えている位置の遅れの分
    internal const float MaxBlastDistance = 20f;  // 爆発: 投げた・撃った物が届く所まで
    private const float MaxBlastRadius = 3f;
    internal const float MaxPushReach = 6f;
    internal const float MaxFireRadius = 2f;
    internal const float MaxFloodSize = 7.9f; // 浸水の続く時間 (×10 秒)
    private static int _lastReport = -ReportTicks;
    private static ushort _reportedSeq = ushort.MaxValue;
    private static int _batchHost = NoHost; // 客: 最後に束を受けたホスト
    // 客: 自分の振りが壁に当たる時刻 (Time.time)。それより早く届いた自分の打撃は、そこまで適用を待たせる (後ろの番号も順番待ち)
    // (試合の最初の束で SyncShip が走るので、そこでは消さない。待つのは長くて HitAt 秒)
    private static float _ownHitAt;

    public static int Applied { get; private set; } // テスト用: この試合で適用した件数
    internal static Roles.TerrainBrokenEvent LastBroken; // 確かめ用: 最後に出した壊れた知らせ
    public static int Refused { get; private set; }    // ホスト: 受けなかった依頼の数
    public static int Mismatches { get; private set; } // ホスト: 客の地形が自分と違った回数
    internal static readonly Dictionary<byte, (ushort seq, bool same)> Checks = new(); // ホスト: 客ごとの最後の照合

    // 破壊を依頼する唯一の入口。返り値はログ・テスト用の説明
    public static string Request(in DamageEvent e) => Request(e, out _);

    // ok = 壊れる所があった (客は依頼を送れた時点で true。決めるのはホスト)
    internal static string Request(in DamageEvent e, out bool ok)
    {
        if (!Online()) return ApplyAlone(e, LocalId(), out ok);
        if (AmongUsClient.Instance.AmHost) return HostAccept(e, LocalId(), out ok);
        Requests.Add(e);
        ok = true;
        return "requested";
    }

    // ホスト: 依頼を決めて自分に適用し、配る列に積む
    private static string HostAccept(in DamageEvent e, byte actor) => HostAccept(e, actor, out _);

    private static string HostAccept(in DamageEvent e, byte actor, out bool ok)
    {
        SyncShip();
        long rt = System.Diagnostics.Stopwatch.GetTimestamp();
        ok = TerrainDamage.TryResolve(e, out var r, out string why);
        TerrainDamage.LastResolveMs = (System.Diagnostics.Stopwatch.GetTimestamp() - rt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (!ok) return why;
        r = TerrainWire.RoundTrip(r.WithActor(actor).WithTick(GameClock.Stamp));
        // 大きな瓦礫の止まる所は、ホストが自分で適用した結果から決めて同じ電文に載せる
        string res = ApplyNow(_nextSeq++, r, decide: true, out var landings);
        var sent = r.WithLandings(landings);
        Outbox.Add(sent);
        Remember((ushort)(_nextSeq - 1), sent);
        FireBroken();
        return res;
    }

    // decide = 大きな瓦礫の止まる所を自分で決める (ホスト・一人の時)。客は届いた結果の物を使う
    private static string ApplyNow(ushort seq, in ResolvedDamage r, bool decide, out RubbleLanding[] landings)
    {
        Applied++;
        if (r.Kind == DamageKind.Rewind)
        {
            // 壁は今ここで取り消す (指紋は取り消した件の前の値へ戻り、戻した線と耐久を積む)。水・火などは刻みが来た時 (TerrainRewind)
            landings = Array.Empty<RubbleLanding>();
            TerrainFx.RunDeferred();
            TerrainDigest.Begin();
            try { TerrainRewind.OnApplied(r); }
            finally { TerrainDigest.End(seq); }
            return $"rewind walls={TerrainRewind.Walls}";
        }
        TerrainFx.RunDeferred(); // 前の破壊の見た目を先に出す (出る順を適用の順に保つ)
        WaterSim.BeforeApply(r);
        TerrainUndo.Begin(seq, r);
        TerrainDigest.Begin();
        string res;
        try { res = TerrainDamage.Apply(r, decide, out landings); }
        finally
        {
            try { AfterApply(seq, r); }
            finally { TerrainUndo.End(); } // 漏れの口・減圧の口も件の持ち物にするので、受け手の後で閉じる (受け手が投げても必ず閉じる)
        }
        // 役職への知らせは呼び出し元が連番と配る列を書き終えてから (FireBroken)。受け手がさらに壊しても順番がずれない
        if (!r.Kind.Shapes()) return res;
        Broken.Add(new Roles.TerrainBrokenEvent
        {
            PlayerId = r.Actor,
            Kind = r.Kind,
            Position = r.Position,
            Radius = r.Kind == DamageKind.Explosion ? r.Size : 0f,
            WallsCut = TerrainDamage.LastCut,
        });
        return res;
    }

    // 適用の後 (指紋を閉じ、音と煙・各計算の受け手へ渡す)
    private static void AfterApply(ushort seq, in ResolvedDamage r)
    {
        TerrainDigest.End(seq);
        TerrainDamage.Mark("digest");
        var rc = r;
        TerrainFx.Defer(() => { BreakNoise.Emit(rc); DustCloud.OnApplied(rc); }); // 音と煙は見た目だけ: 次のフレームで
        HammerSwing.OnApplied(r);
        BombFuse.OnApplied(r);
        TerrainDamage.Mark("dust");
        WaterLeak.OnApplied(r);
        TerrainDamage.Mark("leak");
        WaterSim.OnApplied(r);
        TerrainDamage.Mark("water");
        FireSim.OnApplied(r);
        TerrainDamage.Mark("fire");
        PropSim.OnApplied(r);
        TerrainDamage.Mark("props");
        Decompression.OnApplied(r);
        TerrainDamage.Mark("decomp");
    }

    private static readonly List<Roles.TerrainBrokenEvent> Broken = new();
    private static bool _firing;

    // 溜まった「壊れた」知らせを役職へ出す。受け手が壊した分も同じループで後ろに続けて出す
    private static void FireBroken()
    {
        if (_firing) return;
        _firing = true;
        try
        {
            for (int i = 0; i < Broken.Count; i++) LastBroken = Roles.Events<Roles.TerrainBrokenEvent>.Run(Broken[i]);
        }
        finally
        {
            Broken.Clear();
            _firing = false;
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
        if (Early.Count != 0) ReplayEarly();
        if (Pending.Count != 0)
        {
            int stuck = _tick - _stuckSince;
            if (stuck > GapTimeoutTicks) SkipGap();
            else if (stuck > ResendAskTicks && _tick - _lastAsk >= ResendAskTicks) AskResend();
        }
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
        (sender, data) => Receive(sender, data.Array),
        Net.Delivery.Queued); // Tick の中で送るので同じ tick の終わりに出る (遅れ無し)。本編の RPC との前後関係は要らない

    // 受信。sender = 送ってきたプレイヤー
    private static void Receive(PlayerControl sender, byte[] b)
    {
        var client = AmongUsClient.Instance;
        if (!client || !sender) return;
        if (b == null || b.Length < 2) return;
        if (!client.AmHost && FromHostToAll(b[0]) && !ShipReady())
        {
            if (Early.Count < MaxEarly) Early.Add((sender, b));
            else Plugin.Logger.LogWarning("[TerrainSync] too many messages before map loaded, dropped");
            return;
        }
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
                    string bad = Refuse(sender, e) ?? TerrainPermits.TryUse(sender.PlayerId, TerrainPermits.UseOf(e), e.Size, host: true);
                    if (bad != null) { Refused++; Plugin.Logger.LogDebug($"[TerrainSync] refused {sender.PlayerId}: {bad}"); continue; }
                    HostAccept(e, sender.PlayerId);
                }
                break;
            }
            case TerrainWire.OpBatch:
            {
                // 結果はホストからだけ受ける
                if (client.AmHost || sender.OwnerId != client.HostId || b.Length < TerrainWire.BatchHeader) return;
                if (TestDropBatches > 0)
                {
                    TestDropBatches--;
                    Plugin.Logger.LogInfo($"[TerrainSync] test: dropped a batch from #{b[1] | b[2] << 8}");
                    return;
                }
                SyncShip();
                ushort first = TerrainWire.ReadU16(b, ref o);
                int n = b[o++];
                // 船ごとに連番は 0 から始まり、ホストが替わっても新ホストは同じ数え方で続ける。
                // なので最初の束の番号には合わせない (束は入れ替わって届くことがあり、合わせると前の番号を捨ててしまう)。
                // 抜けた番号は順番待ちのまま欠番としてホストへ頼む。
                // 新ホストが自分より遅れている時だけは新ホストの数え方へ合わせ直す (先に適用した分は新ホストに無い)
                if (_batchHost != client.HostId)
                {
                    if (_batchHost != NoHost && (short)(first - _nextSeq) < 0)
                    {
                        Plugin.Logger.LogWarning($"[TerrainSync] new host is behind, resync seq {_nextSeq} -> {first} (pending {Pending.Count} dropped)");
                        _nextSeq = first;
                        Pending.Clear();
                    }
                    _batchHost = client.HostId;
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
                string bad = BombFuse.HostRefuse(sender, pos, radius) ?? TerrainPermits.TryUse(sender.PlayerId, TerrainUse.Bomb, radius, host: true);
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
            case TerrainWire.OpDoors:
            {
                if (client.AmHost || sender.OwnerId != client.HostId || b.Length < TerrainWire.DoorsBytes) return;
                ushort tick = TerrainWire.ReadU16(b, ref o);
                ulong bits = TerrainWire.ReadU32(b, ref o) | (ulong)TerrainWire.ReadU32(b, ref o) << 32;
                GameClock.Observe(tick);
                Decompression.OnDoors(GameClock.Expand(tick), bits);
                break;
            }
            case TerrainWire.OpPlatform:
            {
                if (client.AmHost || sender.OwnerId != client.HostId || b.Length < TerrainWire.PlatformBytes) return;
                ushort tick = TerrainWire.ReadU16(b, ref o);
                byte kind = b[o++], off = b[o++];
                GameClock.Observe(tick);
                WaterSim.OnPlatform(GameClock.Expand(tick), kind, off);
                break;
            }
            case TerrainWire.OpPortal:
            {
                if (client.AmHost || sender.OwnerId != client.HostId || b.Length < TerrainWire.PortalBytes) return;
                ushort tick = TerrainWire.ReadU16(b, ref o);
                var l = new PortalLinks.Link { Owner = b[o++], On = b[o++] != 0 };
                l.Ax = (int)TerrainWire.ReadU32(b, ref o); l.Ay = (int)TerrainWire.ReadU32(b, ref o);
                l.Bx = (int)TerrainWire.ReadU32(b, ref o); l.By = (int)TerrainWire.ReadU32(b, ref o);
                GameClock.Observe(tick);
                l.Tick = GameClock.Expand(tick);
                PortalLinks.OnPortal(l);
                break;
            }
            case TerrainWire.OpResend:
            {
                if (!client.AmHost || b.Length < 3 || !AllowRequest(sender.PlayerId)) return;
                ushort from = TerrainWire.ReadU16(b, ref o);
                Resend(from, sender.OwnerId);
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
        if (e.Kind == DamageKind.Burn) return "world only"; // 焼け落ちはホストの火の計算だけが決める
        if (e.Kind == DamageKind.Crumble) return "world only"; // 時間切れの壁を崩すのはホストだけ
        Vector2 at = sender.GetTruePosition();
        float dx = e.Position.x - at.x, dy = e.Position.y - at.y;
        float max = e.Kind == DamageKind.Blunt ? DamageProfile.Of(e.Kind).Reach + LagMargin : MaxBlastDistance;
        if (dx * dx + dy * dy > max * max) return $"too far {MathF.Sqrt(dx * dx + dy * dy):0.0}";
        if (e.Kind == DamageKind.Explosion && e.Size > MaxBlastRadius) return $"too large {e.Size:0.0}";
        if (e.Kind == DamageKind.Push && e.Size > MaxPushReach) return $"too long {e.Size:0.0}";
        if (e.Kind.IsFire() && e.Size > MaxFireRadius) return $"too large {e.Size:0.0}";
        if (e.Kind == DamageKind.Wall && e.Size > TerrainPermit.MaxWallLength + 0.01f) return $"too long {e.Size:0.0}";
        if (e.Kind == DamageKind.Wall && e.Force > 0.5f && !TerrainPermits.Of(sender.PlayerId).WallPush) return "no push wall";
        if (e.Kind == DamageKind.Flood && e.Size <= 0f) return "world only"; // 止まらない浸水は仕掛け (ホスト) だけ
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
            Remember(_nextSeq, next);
            string res = ApplyNow(_nextSeq++, next, decide: false, out _);
            Plugin.Logger.LogDebug($"[TerrainSync] #{(ushort)(_nextSeq - 1)} {res}");
        }
        _stuckSince = _tick;
        FireBroken();
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

    private static void Remember(ushort seq, in ResolvedDamage r)
    {
        if (History.Count < MaxHistory) History[seq] = r;
    }

    // 客: 欠番 (_nextSeq から) をホストへ頼む
    private static void AskResend()
    {
        _lastAsk = _tick;
        if (!IsGuest()) return;
        ResendAsked++;
        int o = 0;
        Buf[o++] = TerrainWire.OpResend;
        o = TerrainWire.WriteU16(Buf, o, _nextSeq);
        Plugin.Logger.LogInfo($"[TerrainSync] asking host to resend from #{_nextSeq} (pending {Pending.Count})");
        Send(o, AmongUsClient.Instance.HostId);
    }

    // ホスト: from から持っている分を、その人へだけ束にして送り直す (受け手は適用済みの番号を捨てるので重なっても害は無い)
    private static void Resend(ushort from, int target)
    {
        ushort seq = from;
        for (int batch = 0; batch < MaxResendBatches; batch++)
        {
            int n = 0, o = TerrainWire.BatchHeader;
            while (n < MaxEventsPerRpc && (short)(seq - _nextSeq) < 0 && History.TryGetValue(seq, out var r))
            {
                o = TerrainWire.WriteResolved(Buf, o, r);
                n++;
                seq++;
            }
            if (n == 0) break;
            Buf[0] = TerrainWire.OpBatch;
            TerrainWire.WriteU16(Buf, 1, (ushort)(seq - n));
            Buf[3] = (byte)n;
            ResendServed += n;
            Send(o, target);
        }
        Plugin.Logger.LogInfo($"[TerrainSync] resent #{from}..#{(ushort)(seq - 1)} to client {target}");
    }

    // ホストから全員へ配られ、船の上の状態に効く電文か (船ができる前に届いたら取っておく物)
    private static bool FromHostToAll(byte op) =>
        op is TerrainWire.OpBatch or TerrainWire.OpBomb or TerrainWire.OpDoors or TerrainWire.OpPlatform or TerrainWire.OpPortal;

    private static bool ShipReady()
    {
        if (TestHoldShipUntil > 0f)
        {
            if (Time.time < TestHoldShipUntil) return false;
            TestHoldShipUntil = 0f;
        }
        GameClock.Tick();
        return GameClock.ShipAlive;
    }

    // 船ができる前に届いた物を、届いた順に読み直す。船ができないまま試合を抜けたら捨てる
    private static void ReplayEarly()
    {
        if (!Online()) { Early.Clear(); return; }
        if (!ShipReady()) return;
        var list = Early.ToArray();
        Early.Clear();
        Plugin.Logger.LogInfo($"[TerrainSync] replaying {list.Length} messages received before map loaded");
        foreach (var (sender, data) in list)
        {
            try { Receive(sender, data); }
            catch (Exception ex) { Plugin.Logger.LogError($"[TerrainSync] replay: {ex}"); }
        }
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

    // ホスト → 全員: 扉が変わった刻みと開き (変わった時だけ)
    internal static void BroadcastDoors(ushort tick, ulong bits)
    {
        if (!Online() || !AmongUsClient.Instance.AmHost) return;
        DoorBuf[0] = TerrainWire.OpDoors;
        int o = TerrainWire.WriteU16(DoorBuf, 1, tick);
        o = TerrainWire.WriteU32(DoorBuf, o, (uint)bits);
        o = TerrainWire.WriteU32(DoorBuf, o, (uint)(bits >> 32));
        Wire.Send(new ArraySegment<byte>(DoorBuf, 0, o), -1);
    }

    // ホスト → 全員: ポータルの組が開いた・閉じた刻み
    internal static void BroadcastPortal(ushort tick, in PortalLinks.Link l)
    {
        if (!Online() || !AmongUsClient.Instance.AmHost) return;
        PortalBuf[0] = TerrainWire.OpPortal;
        int o = TerrainWire.WriteU16(PortalBuf, 1, tick);
        PortalBuf[o++] = l.Owner;
        PortalBuf[o++] = (byte)(l.On ? 1 : 0);
        o = TerrainWire.WriteU32(PortalBuf, o, (uint)l.Ax);
        o = TerrainWire.WriteU32(PortalBuf, o, (uint)l.Ay);
        o = TerrainWire.WriteU32(PortalBuf, o, (uint)l.Bx);
        o = TerrainWire.WriteU32(PortalBuf, o, (uint)l.By);
        Wire.Send(new ArraySegment<byte>(PortalBuf, 0, o), -1);
    }

    // ホスト → 全員: 動く床が動き始めた刻み (変わった時だけ)
    internal static void BroadcastPlatform(ushort tick, byte kind, byte off)
    {
        if (!Online() || !AmongUsClient.Instance.AmHost) return;
        PlatformBuf[0] = TerrainWire.OpPlatform;
        int o = TerrainWire.WriteU16(PlatformBuf, 1, tick);
        PlatformBuf[o++] = kind;
        PlatformBuf[o++] = off;
        Wire.Send(new ArraySegment<byte>(PlatformBuf, 0, o), -1);
    }

    // ホストと一人の時: その人の破壊として決めて配る (爆弾の導火線が尽きた時など、依頼を受けた後でホストが起こす破壊)
    internal static string RequestAs(in DamageEvent e, byte actor) => RequestAs(e, actor, out _);

    internal static string RequestAs(in DamageEvent e, byte actor, out bool ok)
    {
        if (!Online()) return ApplyAlone(e, actor, out ok);
        if (AmongUsClient.Instance.AmHost) return HostAccept(e, actor, out ok);
        return Request(e, out ok);
    }

    // ホスト・一人: 水・火・減圧・物を back 刻み前の記録へ戻す合図を、壊した結果と同じ列に積む (届く途中の破壊と順番を入れ替えない)
    internal static string HostRewind(int back)
    {
        if (Online() && !AmongUsClient.Instance.AmHost) return "not host";
        SyncShip();
        var r = new ResolvedDamage(DamageKind.Rewind, Vector2.zero, Vector2.zero, Vector2.zero, 0f, 0f, 0, (ushort)Math.Clamp(back, 0, ushort.MaxValue),
            Array.Empty<RubbleLanding>(), ResolvedDamage.NoActor, GameClock.Stamp);
        r = TerrainWire.RoundTrip(r);
        string res = ApplyNow(_nextSeq++, r, decide: true, out _);
        if (!Online()) return res;
        Outbox.Add(r);
        Remember((ushort)(_nextSeq - 1), r);
        return res;
    }

    // 一人の時 (フリープレイ・オフライン): その場で決めて適用する
    private static string ApplyAlone(in DamageEvent e, byte actor, out bool ok)
    {
        long rt = System.Diagnostics.Stopwatch.GetTimestamp();
        SyncShip();
        ok = TerrainDamage.TryResolve(e, out var r, out string why);
        TerrainDamage.LastResolveMs = (System.Diagnostics.Stopwatch.GetTimestamp() - rt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (!ok) return why;
        string res = ApplyNow(_nextSeq++, TerrainWire.RoundTrip(r.WithActor(actor).WithTick(GameClock.Stamp)), decide: true, out _);
        FireBroken();
        return res;
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
        History.Clear();
        Outbox.Clear();
        Requests.Clear();
        RequestRate.Clear();
        ResendAsked = 0;
        ResendServed = 0;
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
