using System;
using UnityEngine;

namespace MoreRolesPlus.Net;

// 自分の位置を飛ばす。本編の TP の RPC (RpcSnapTo) は使わず、MRP の電文で全員に配り、各端末が手元で動かす。
// 公式サーバーは本編の TP の RPC の本数を数えていて、約 6 秒に約 330 本を超えると切断する (実測)。
// 全員が MRP を入れているので、本編の RPC を通さなくても全員の画面で同じ所へ飛ばせる。
//   - 本編の RpcSnapTo と同じく位置の番号 (sequence id) を進めて送る。受け手はそれより古い位置の更新を捨てるので、
//     飛ぶ前の位置へ引き戻されたり、間を滑って見えたりしない。
//   - 電文は動かした tick にすぐ出す (遅れて届くと、本人の普段の位置の更新が先に着いて間を滑って見える)。
//     電文の本数の予算を使い切っている時は最後の行き先だけを持っておき、予算が空いた tick で動かして送る。
internal static class Teleport
{
    private const ushort SidStep = 328; // 道中に残っている位置の更新より確実に新しくなる幅

    private readonly struct Move
    {
        public readonly float X, Y;
        public readonly ushort Sid;
        public Move(float x, float y, ushort sid) { X = x; Y = y; Sid = sid; }
    }

    private static readonly RemoteCall<Move> Moved = new("Teleport.Moved", Route.Anyone,
        (w, m) => { w.Write(m.X); w.Write(m.Y); w.Write(m.Sid); },
        r => new Move(r.ReadSingle(), r.ReadSingle(), r.ReadUInt16()),
        (sender, m) =>
        {
            // 動かせるのは送り手本人だけ
            if (!sender || !float.IsFinite(m.X) || !float.IsFinite(m.Y) || Math.Abs(m.X) > 1000f || Math.Abs(m.Y) > 1000f) return;
            var nt = sender.NetTransform;
            if (nt) nt.SnapTo(new Vector2(m.X, m.Y), m.Sid);
        });

    private static bool _pending;
    private static Vector2 _pendingTo;

    public static int Moves, Deferred; // 計器: 飛んだ回数・予算待ちで後へ回した回数

    // 自分の体の根 (transform.position) を to へ飛ばす
    public static void Self(Vector2 to)
    {
        _pendingTo = to;
        _pending = true;
        if (!TryFlush()) Deferred++;
    }

    // FixedUpdate (Remote.Tick より前)
    public static void Tick()
    {
        if (_pending) TryFlush();
    }

    private static bool TryFlush()
    {
        var lp = PlayerControl.LocalPlayer;
        var client = AmongUsClient.Instance;
        if (!lp || !client) { _pending = false; return true; }
        var nt = lp.NetTransform;
        if (client.NetworkMode == NetworkModes.FreePlay)
        {
            nt.SnapTo(_pendingTo, (ushort)(nt.lastSequenceId + SidStep));
            _pending = false;
            Moves++;
            return true;
        }
        if (!Remote.HasBudget()) return false;
        ushort sid = (ushort)(nt.lastSequenceId + SidStep);
        nt.SnapTo(_pendingTo, sid);
        Moved.Send(new Move(_pendingTo.x, _pendingTo.y, sid));
        _pending = false;
        Moves++;
        return true;
    }
}
