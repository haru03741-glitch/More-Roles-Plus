using System.Collections.Generic;
using Hazel;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 演出を全員へ 1 本で配り、受け取った端末が FxVisibility の範囲で自分に出すかを決める。
// 送る側は誰に見えるかを気にしない (送り先を絞ると、見えない人に「何か起きた」ことが通信の有無で分かってしまうため)
public static class FxSignal
{
    private readonly struct Msg
    {
        public readonly ushort Fx;
        public readonly FxEvent Ev;

        public Msg(ushort fx, FxEvent ev)
        {
            Fx = fx;
            Ev = ev;
        }
    }

    private static readonly RemoteCall<Msg> Call = new("Fx.Show", Route.Anyone, Write, Read, (sender, m) => Receive(sender, m), Delivery.Queued);

    private static void Write(MessageWriter w, Msg m)
    {
        w.Write(m.Fx);
        w.Write(m.Ev.Actor);
        w.Write(m.Ev.Target);
        w.Write(m.Ev.Arg);
        w.Write(m.Ev.At.x);
        w.Write(m.Ev.At.y);
    }

    private static Msg Read(MessageReader r)
    {
        ushort fx = r.ReadUInt16();
        byte actor = r.ReadByte(), target = r.ReadByte(), arg = r.ReadByte();
        float x = r.ReadSingle(), y = r.ReadSingle();
        return new Msg(fx, new FxEvent(actor, target, new Vector2(x, y), arg));
    }

    // 演出の番号 = 設定の名前の順 (全員が同じ版なら同じ並び。版の違いは設定の指紋で先に弾く)
    private static List<FxVisibility> _list;

    private static List<FxVisibility> List
    {
        get
        {
            if (_list != null) return _list;
            _list = new List<FxVisibility>();
            foreach (var o in Registry.All)
                if (o is FxVisibility v) _list.Add(v);
            return _list;
        }
    }

    // 自分 (actor) が起こした演出を配る。target = 相手 (無ければ 255)・at = 場所・arg = 演出ごとの小さな値
    public static void Show(FxVisibility fx, byte target, Vector2 at, byte arg = 0)
    {
        var me = PlayerControl.LocalPlayer;
        if (!me || fx == null) return;
        int i = List.IndexOf(fx);
        if (i < 0)
        {
            Plugin.Logger.LogWarning($"fx: {fx.Label} is not registered (put it in a static readonly field of a role)");
            return;
        }
        var ev = new FxEvent(me.PlayerId, target, at, arg);
        Dispatch(fx, ev); // 送った人の端末にはその場で
        Call.Send(new Msg((ushort)i, ev));
    }

    private static void Receive(PlayerControl sender, Msg m)
    {
        if (!sender || sender.AmOwner || m.Fx >= List.Count) return;
        // 起こした人は送った人だけ (他人の名前で演出を出させない)
        if (m.Ev.Actor != sender.PlayerId) return;
        Dispatch(List[m.Fx], m.Ev);
    }

    internal static void Dispatch(FxVisibility fx, FxEvent ev)
    {
        if (fx.Handler == null || !fx.LocalCanSee(ev.Actor, ev.Target, ev.At)) return;
        try { fx.Handler(ev); }
        catch (System.Exception e) { Plugin.Logger.LogError($"fx {fx.Key}: {e}"); }
    }

    // 通信妨害の間か。毎フレームの矢印などから聞かれても重くならないよう 250ms ごとに読み直す。
    // 部品は船が替わった時だけ引き直す (ShipStatus.Instance はフリープレイの後も壊れた船を指すので、船の世代と生存で見る)
    private static int _shipGen = -1;
    private static IActivatable _comms;
    private static bool _commsDown;
    private static long _commsAt;

    internal static bool CommsDown()
    {
        long now = System.Environment.TickCount64;
        if (now - _commsAt < 250) return _commsDown;
        _commsAt = now;
        _commsDown = false;
        if (!Terrain.GameClock.ShipAlive) { _comms = null; _shipGen = -1; return false; }
        if (_shipGen != Terrain.GameClock.ShipGen)
        {
            _shipGen = Terrain.GameClock.ShipGen;
            var ship = ShipStatus.Instance;
            var sys = ship ? ship.Systems : null;
            _comms = sys != null && sys.ContainsKey(SystemTypes.Comms) ? sys[SystemTypes.Comms].TryCast<IActivatable>() : null;
        }
        _commsDown = _comms != null && _comms.IsActive;
        return _commsDown;
    }

    // 確認用: 試しの範囲で、部屋の全員について見えるかを並べる
    internal static string Matrix(Audience a, FxHide hide, float radius, byte actor, byte target)
    {
        var me = PlayerControl.LocalPlayer;
        Vector2 at = me ? me.GetTruePosition() : Vector2.zero;
        var sb = new System.Text.StringBuilder($"OK fx [{FxVisibility.Describe(a)}] actor={actor} target={target} r={radius}");
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null) continue;
            var role = RoleState.Of(p.PlayerId);
            sb.Append(' ').Append(p.PlayerId).Append('(').Append(role != null ? role.Team.ToString() : "-").Append(p.Data.IsDead ? ",dead" : "")
                .Append(")=").Append(FxVisibility.Allows(a, hide, radius, p, actor, target, at) ? '1' : '0');
        }
        sb.Append(" registered=").Append(List.Count);
        return sb.ToString();
    }
}
