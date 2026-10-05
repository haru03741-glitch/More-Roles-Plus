// Based on the RPC design of https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Modules/NebulaRPC.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    public byte Id { get; internal set; }
    public int Sent, Received, Dropped; // 確認用の数

    protected RemoteCall(string name, Route route)
    {
        Name = name;
        Route = route;
        Remote.Add(this);
    }

    internal abstract void Receive(PlayerControl sender, MessageReader r);
}

internal sealed class RemoteCall<T> : RemoteCall
{
    private readonly Action<MessageWriter, T> _write;
    private readonly Func<MessageReader, T> _read;
    private readonly Action<PlayerControl, T> _body;

    public RemoteCall(string name, Route route, Action<MessageWriter, T> write, Func<MessageReader, T> read, Action<PlayerControl, T> body)
        : base(name, route)
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
        _write(w, value);
        Remote.Close(w);
    }

    internal override void Receive(PlayerControl sender, MessageReader r) => _body(sender, _read(r));
}

// 電文をまとめて運ぶ所。本編の RPC 番号は 1 つ (Rpc.Bus) だけ使い、中に [指紋][電文]... を詰める。
// 電文 1 つ = Hazel の入れ子のメッセージ (Tag = 番号・長さ付き) なので、読み損ねても次の電文はずれない。
internal static class Remote
{
    // まとめ送りの 1 通がこれを超えたら次の電文から新しい 1 通にする (公式サーバーの 1 通の大きさを予算に)
    private const int SoftLimit = 400;

    private static readonly List<RemoteCall> Pending = new();
    private static RemoteCall[] _byId = Array.Empty<RemoteCall>();

    public static IReadOnlyList<RemoteCall> All => _byId;

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
    // 中で出した本編の RPC (キル等) の方が先に届く。
    private static int _batchDepth;
    private static MessageWriter _batch;
    private static int _batchTarget;

    public readonly struct BatchScope : IDisposable
    {
        public void Dispose()
        {
            if (--_batchDepth == 0) Flush();
        }
    }

    public static BatchScope Batch()
    {
        _batchDepth++;
        return default;
    }

    internal static MessageWriter Open(RemoteCall call, int target)
    {
        MessageWriter w;
        if (_batchDepth > 0)
        {
            if (_batch != null && (_batchTarget != target || _batch.Length >= SoftLimit)) Flush();
            if (_batch == null)
            {
                _batch = Start(target);
                _batchTarget = target;
            }
            w = _batch;
        }
        else w = Start(target);
        w.StartMessage(call.Id);
        call.Sent++;
        return w;
    }

    internal static void Close(MessageWriter w)
    {
        w.EndMessage();
        if (_batchDepth == 0) AmongUsClient.Instance.FinishRpcImmediately(w);
    }

    private static void Flush()
    {
        if (_batch == null) return;
        var w = _batch;
        _batch = null;
        if (AmongUsClient.Instance) AmongUsClient.Instance.FinishRpcImmediately(w);
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
}
