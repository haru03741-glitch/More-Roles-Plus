// Based on the RPC design of https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/NebulaRPC.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Hazel;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Net;

// 誰から誰へ送る電文か。受け手はこれを見て、来てはいけない所から来た物を捨てる
internal enum Route
{
    HostToAll, // ホスト → 客。ホスト以外から来た物・自分がホストの時に来た物は捨てる
    ToHost,    // 誰か → ホスト。ホストでない人に来た物は捨てる。ホスト自身が送ると手元で実行する
    Anyone,    // 誰から誰へでも (送り先の確認は受け手が自分でする)
}

// いつ送るか。
//   Now    = Send したその場で 1 通にして出す。本編の RPC (キル等) との前後関係をそのまま保ちたい物はこちら。
//   Queued = 出口の待ち行列に積み、この FixedUpdate の終わりに同じ宛先の物と 1 通にまとめて出す。
//            本数の予算 (Remote.RateLimit) を超える分は次の tick へ持ち越す (捨てない・同じ宛先への順番は保つ)。
//            本編の RPC より後に届くことがあるので、それで困らない物だけ。
internal enum Delivery
{
    Now,
    Queued,
}

// MRP の電文 1 種類。役職や機能のクラスに static readonly で置くだけで、起動時に集めて番号を振る。
//   internal static readonly RemoteCall<byte> KillRequest = new("Kill.Request", Route.ToHost,
//       (w, target) => w.Write(target), r => r.ReadByte(), (sender, target) => ...);
//   KillRequest.Send(target.PlayerId);
// 送っても自分の手元では実行しない (ToHost をホストが送った時だけ例外)。手元に効かせる処理は送る側で呼ぶ。
// 名前は版の中で一意に。名前順に番号が決まるので、名前を変える・足すと版の指紋も変わる。
internal abstract class RemoteCall
{
    public readonly string Name;
    public readonly Route Route;
    public readonly Delivery Delivery;
    public byte Id { get; internal set; }
    public int Sent, Received, Dropped; // 確認用の数

    protected RemoteCall(string name, Route route, Delivery delivery)
    {
        Name = name;
        Route = route;
        Delivery = delivery;
        Remote.Add(this);
    }

    internal abstract void Receive(PlayerControl sender, MessageReader r);
}

internal sealed class RemoteCall<T> : RemoteCall
{
    private readonly Action<MessageWriter, T> _write;
    private readonly Func<MessageReader, T> _read;
    private readonly Action<PlayerControl, T> _body;

    public RemoteCall(string name, Route route, Action<MessageWriter, T> write, Func<MessageReader, T> read, Action<PlayerControl, T> body,
        Delivery delivery = Delivery.Now)
        : base(name, route, delivery)
    {
        _write = write;
        _read = read;
        _body = body;
    }

    // target = 送り先の clientId (-1 = 全員)。ToHost は指定しなくてもホストへ送る
    public void Send(T value, int target = -1)
    {
        var client = AmongUsClient.Instance;
        if (!client || !PlayerControl.LocalPlayer) return;
        if (Route == Route.ToHost)
        {
            if (client.AmHost)
            {
                _body(PlayerControl.LocalPlayer, value);
                return;
            }
            target = client.HostId;
        }
        var w = Remote.Open(this, target);
        try { _write(w, value); }
        catch (Exception e)
        {
            // 書きかけの電文を残すと同じ 1 通の後続ごと壊れるので、その 1 通を捨てる
            Remote.Abort(w);
            Plugin.Logger.LogError($"remote {Name} write: {e}");
            return;
        }
        Remote.Close(w);
    }

    internal override void Receive(PlayerControl sender, MessageReader r) => _body(sender, _read(r));
}

// 電文をまとめて運ぶ所。本編の RPC 番号は 1 つ (Rpc.Bus) だけ使い、中に [指紋][電文]... を詰める。
// 電文 1 つ = Hazel の入れ子のメッセージ (Tag = 番号・長さ付き) なので、読み損ねても次の電文はずれない。
//
// 通信量の約束 (公式サーバーの実測から):
//   - 1 通は 1199B までが合法で 1250B で即キック → SoftLimit で次の 1 通に分け、HardLimit を超えた物は送らずにエラーに残す。
//   - 同じ種類の Reliable を秒十数本出すと 2 秒で切断 (MRP の電文はサーバーから見ると全部 Bus の 1 種類)
//     → 直近 1 秒の本数を数え、Queued の物は RateLimit を超えたら次の tick へ持ち越す。Now の物は数えるだけで止めない。
internal static class Remote
{
    public const int SoftLimit = 1000;  // 1 通がこれ以上になったら次の電文から新しい 1 通にする
    public const int HardLimit = 1150;  // これを超えた 1 通は送らない (送るとキック)
    public const int RateLimit = 10;    // 直近 1 秒に出す通数の予算 (実測の切断 15/s の下・EK の較正 12/s の下)
    private const int RateWindowMs = 1000;

    private static readonly List<RemoteCall> Pending = new();
    private static RemoteCall[] _byId = Array.Empty<RemoteCall>();

    public static IReadOnlyList<RemoteCall> All => _byId;

    // 計器 (ブリッジ `net`)
    public static int Packets, Deferred, Overflow, Dropped; // 出した通数・予算待ちで持ち越した tick 数・大きすぎて送らなかった通数・送らずに捨てた通数
    public static long Bytes;
    private static readonly long[] SentAt = new long[RateLimit]; // 直近 RateLimit 通の送った時刻 (輪)
    private static int _sentIdx;

    internal static void Add(RemoteCall call) => Pending.Add(call);

    // 起動時に 1 回。static readonly の RemoteCall を全部読み (その型の static の初期化が走る)、名前順に番号を振る
    public static void Init()
    {
        foreach (var t in typeof(Remote).Assembly.GetTypes())
        {
            if (t.IsGenericTypeDefinition) continue;
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(RemoteCall).IsAssignableFrom(f.FieldType)) continue;
                try { f.GetValue(null); }
                catch (Exception e) { Plugin.Logger.LogError($"remote {t.Name}.{f.Name}: {e.InnerException?.Message ?? e.Message}"); }
            }
        }
        var list = Pending.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        for (int i = 1; i < list.Count; i++)
            if (list[i].Name == list[i - 1].Name) Plugin.Logger.LogError($"remote call '{list[i].Name}' is defined twice");
        if (list.Count > 256) Plugin.Logger.LogError($"too many remote calls: {list.Count}");
        _byId = list.Take(256).ToArray();
        for (int i = 0; i < _byId.Length; i++) _byId[i].Id = (byte)i;
        Plugin.Logger.LogInfo($"remote: {_byId.Length} calls");
    }

    // ---- まとめ送り ----
    // using (Remote.Batch()) { A.Send(..); B.Send(..); } で、中の電文を抜ける時に 1 通 (大きければ数通) で送る。
    // 中の電文どうしの順番はそのまま届く。まとめは using を抜ける時に出るので、
    // 中で出した本編の RPC (キル等) の方が先に届く。Queued の電文も中では同じ 1 通に入る。
    private static int _batchDepth;
    private static MessageWriter _batch;
    private static int _batchTarget;

    public readonly struct BatchScope : IDisposable
    {
        public void Dispose()
        {
            if (--_batchDepth == 0) FlushBatch();
        }
    }

    public static BatchScope Batch()
    {
        _batchDepth++;
        return default;
    }

    // ---- 出口の待ち行列 (Queued) ----
    // 宛先ごとに 1 列。Open 中の 1 通に詰め、Tick で閉じて Ready に移し、予算の中で古い物から出す
    private sealed class Lane
    {
        public int Target;
        public MessageWriter Open;
        public readonly List<MessageWriter> Ready = new();
    }

    private static readonly List<Lane> Lanes = new();
    private static bool _closeQueued; // Open〜Close の間: 今の電文が待ち行列の 1 通に入っているか

    private static Lane LaneFor(int target)
    {
        for (int i = 0; i < Lanes.Count; i++)
            if (Lanes[i].Target == target) return Lanes[i];
        var lane = new Lane { Target = target };
        Lanes.Add(lane);
        return lane;
    }

    internal static MessageWriter Open(RemoteCall call, int target)
    {
        MessageWriter w;
        _closeQueued = false;
        if (_batchDepth > 0)
        {
            if (_batch != null && (_batchTarget != target || _batch.Length >= SoftLimit)) FlushBatch();
            if (_batch == null)
            {
                _batch = Start(target);
                _batchTarget = target;
            }
            w = _batch;
        }
        else if (call.Delivery == Delivery.Queued)
        {
            var lane = LaneFor(target);
            if (lane.Open != null && lane.Open.Length >= SoftLimit)
            {
                lane.Ready.Add(lane.Open);
                lane.Open = null;
            }
            lane.Open ??= Start(target);
            w = lane.Open;
            _closeQueued = true;
        }
        else w = Start(target);
        w.StartMessage(call.Id);
        call.Sent++;
        return w;
    }

    internal static void Close(MessageWriter w)
    {
        w.EndMessage();
        if (_batchDepth == 0 && !_closeQueued) Finish(w);
        _closeQueued = false;
    }

    // Open したあと中身を書けなかった時: その 1 通 (まとめ・待ち行列の開いている物も含む) を丸ごと捨てる
    internal static void Abort(MessageWriter w)
    {
        if (ReferenceEquals(w, _batch)) _batch = null;
        for (int i = 0; i < Lanes.Count; i++)
            if (ReferenceEquals(Lanes[i].Open, w)) Lanes[i].Open = null;
        _closeQueued = false;
        Dropped++;
        w.Recycle();
    }

    private static void FlushBatch()
    {
        if (_batch == null) return;
        var w = _batch;
        _batch = null;
        Finish(w);
    }

    // FixedUpdate の終わり (他の Tick が積んだ物を同じ tick で出す)
    public static void Tick()
    {
        // 積んだ物が無い tick は本編の物を読まずに抜ける (毎 tick の経路)
        bool any = false;
        for (int i = 0; i < Lanes.Count && !any; i++) any = Lanes[i].Open != null || Lanes[i].Ready.Count > 0;
        if (!any) return;
        // 送り手の NetId は Start の時に焼いてあるので、本人が消えた (試合を抜けた等) 時は積んだ物ごと捨てる
        if (!AmongUsClient.Instance || !PlayerControl.LocalPlayer)
        {
            DropAll();
            return;
        }
        bool waited = false;
        for (int i = 0; i < Lanes.Count; i++)
        {
            var lane = Lanes[i];
            while (lane.Ready.Count > 0 && WithinBudget())
            {
                var w = lane.Ready[0];
                lane.Ready.RemoveAt(0);
                Finish(w);
            }
            // 予算待ちの間は開いている 1 通を閉じずに詰め続ける (閉じると電文 1 つの細い通が並ぶ)
            if (lane.Open == null) continue;
            if (lane.Ready.Count == 0 && WithinBudget())
            {
                var w = lane.Open;
                lane.Open = null;
                Finish(w);
            }
            else waited = true;
        }
        if (waited) Deferred++;
    }

    // 待ち行列に残っている通数 (計器)
    public static int QueuedPackets()
    {
        int n = 0;
        for (int i = 0; i < Lanes.Count; i++) n += Lanes[i].Ready.Count + (Lanes[i].Open != null ? 1 : 0);
        return n;
    }

    private static void DropAll()
    {
        for (int i = 0; i < Lanes.Count; i++)
        {
            var lane = Lanes[i];
            if (lane.Open != null) { lane.Open.Recycle(); Dropped++; }
            foreach (var w in lane.Ready) { w.Recycle(); Dropped++; }
            lane.Ready.Clear();
            lane.Open = null;
        }
        Lanes.Clear();
    }

    // 直近 1 秒に RateLimit 通出していなければ true
    private static bool WithinBudget() => Environment.TickCount64 - SentAt[_sentIdx] >= RateWindowMs;

    // 直近 1 秒に出した通数 (計器)
    public static int SentLastSecond()
    {
        long now = Environment.TickCount64;
        int n = 0;
        for (int i = 0; i < RateLimit; i++)
            if (SentAt[i] != 0 && now - SentAt[i] < RateWindowMs) n++;
        return n;
    }

    // 1 通を出す所は全部ここ (大きさの上限と本数の計器)
    private static void Finish(MessageWriter w)
    {
        int len = w.Length;
        if (len > HardLimit)
        {
            Overflow++;
            Plugin.Logger.LogError($"remote: packet of {len}B dropped (limit {HardLimit}B)");
            w.Recycle();
            return;
        }
        var client = AmongUsClient.Instance;
        if (!client)
        {
            Dropped++;
            w.Recycle();
            return;
        }
        client.FinishRpcImmediately(w);
        SentAt[_sentIdx] = Environment.TickCount64;
        _sentIdx = (_sentIdx + 1) % RateLimit;
        Packets++;
        Bytes += len;
    }

    private static MessageWriter Start(int target)
    {
        var w = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, Rpc.Bus, SendOption.Reliable, target);
        w.Write(Registry.Fingerprint);
        return w;
    }

    // ---- 受け取り (PlayerControl.HandleRpc から) ----
    internal static void Receive(PlayerControl sender, MessageReader reader)
    {
        uint fp = reader.ReadUInt32();
        if (fp != Registry.Fingerprint)
        {
            Plugin.Logger.LogWarning($"remote from {(sender ? sender.PlayerId : -1)} ignored: fingerprint {fp:X8} != {Registry.Fingerprint:X8}");
            return;
        }
        var client = AmongUsClient.Instance;
        while (reader.BytesRemaining > 0)
        {
            var sub = reader.ReadMessage();
            if (sub.Tag >= _byId.Length)
            {
                Plugin.Logger.LogWarning($"remote: unknown call {sub.Tag}");
                continue;
            }
            var call = _byId[sub.Tag];
            bool ok = call.Route switch
            {
                Route.HostToAll => !client.AmHost && Rpc.FromHost(sender),
                Route.ToHost => client.AmHost,
                _ => true,
            };
            if (!ok)
            {
                call.Dropped++;
                Plugin.Logger.LogWarning($"remote {call.Name} from {(sender ? sender.PlayerId : -1)} dropped (route {call.Route})");
                continue;
            }
            call.Received++;
            try { call.Receive(sender, sub); }
            catch (Exception ex) { Plugin.Logger.LogError($"remote {call.Name}: {ex}"); }
        }
    }

    // ---- 計器 (ブリッジ `net`) ----
    // 確かめ用の電文: `net flood N` で N 個積み、通数が RateLimit/秒で頭打ちになるのを見る
    public static int ProbeReceived;
    private static readonly RemoteCall<byte> Probe = new("Net.Probe", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(), (_, _) => ProbeReceived++, Delivery.Queued);

    public static void Flood(int count)
    {
        for (int i = 0; i < count; i++) Probe.Send((byte)i);
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("packets=").Append(Packets).Append(" bytes=").Append(Bytes)
          .Append(" last1s=").Append(SentLastSecond()).Append('/').Append(RateLimit)
          .Append(" queued=").Append(QueuedPackets()).Append(" deferred=").Append(Deferred).Append(" overflow=").Append(Overflow).Append(" dropped=").Append(Dropped);
        var client = AmongUsClient.Instance;
        var conn = client ? client.connection : null;
        if (conn != null)
        {
            // リンクの健全さ (送信量だけでは切断の前兆が見えない実測がある): 往復時間と、まだ届いた返事の無い Reliable の数
            var udp = conn.TryCast<Hazel.Udp.UdpConnection>();
            if (udp != null) sb.Append(" ping=").Append(udp.AveragePingMs).Append("ms unack=").Append(udp.reliableDataPacketsSent.Count);
        }
        sb.Append('\n');
        foreach (var c in _byId)
        {
            if (c.Sent == 0 && c.Received == 0 && c.Dropped == 0) continue;
            sb.Append(c.Name).Append(' ').Append(c.Delivery == Delivery.Queued ? "queued" : "now")
              .Append(" sent=").Append(c.Sent).Append(" recv=").Append(c.Received);
            if (c.Dropped > 0) sb.Append(" dropped=").Append(c.Dropped);
            sb.Append('\n');
        }
        if (ProbeReceived > 0) sb.Append("probe recv=").Append(ProbeReceived).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }
}
