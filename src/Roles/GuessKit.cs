// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/NiceGuesser.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// ゲッサー (会議中に役職を当てて撃つ) の共通部品。当たれば相手が、外れれば撃った人が死ぬ
public interface IGuesser { }

internal static class GuessKit
{
    // 能力の「撃つ」で狙う相手が決まった時 (全員の端末で呼ばれる)。撃つ本人の端末で役職を選ぶ画面を開く
    internal static void OnTarget(PlayerControl user, PlayerControl target)
    {
        // ホスト: 能力を使って選んだ相手だけを、この会議の間に 1 回だけ撃てる (能力を使わない申告は受けない)
        if (user && target && AmongUsClient.Instance.AmHost && MeetingHud.Instance)
            Pending[user.PlayerId] = (target.PlayerId, MeetingHud.Instance.Pointer);
        if (!user || !user.AmOwner || !target) return;
        byte id = target.PlayerId;
        RolePicker.Open(r =>
        {
            int idx = Registry.Roles.IndexOf(r);
            if (idx < 0) return;
            if (AmongUsClient.Instance.AmHost) Resolve(PlayerControl.LocalPlayer, id, idx);
            else GuessCall.Send((id, idx));
        });
    }

    private static readonly RemoteCall<(byte target, int role)> GuessCall = new("GuessKit.Guess", Route.ToHost,
        (w, m) => { w.Write(m.target); w.WritePacked(m.role); }, r => (r.ReadByte(), r.ReadPackedInt32()),
        (sender, m) => Resolve(sender, m.target, m.role));

    // ホストの端末で: 当たっていれば相手、外れていれば撃った人を倒す
    private static readonly System.Collections.Generic.Dictionary<byte, (byte target, System.IntPtr meeting)> Pending = new();

    private static void Resolve(PlayerControl user, byte targetId, int roleIdx)
    {
        if (!MeetingHud.Instance || !PortKit.Alive(user) || roleIdx < 0 || roleIdx >= Registry.Roles.Count) return;
        if (!IsGuesser(user)) return;
        if (!Pending.TryGetValue(user.PlayerId, out var pend) || pend.target != targetId || pend.meeting != MeetingHud.Instance.Pointer) return;
        Pending.Remove(user.PlayerId);
        var target = PortKit.ById(targetId);
        if (!PortKit.Alive(target)) return;
        var actual = RoleState.Of(target);
        bool hit = actual != null && actual.GetType() == Registry.Roles[roleIdx].GetType();
        byte deadId = hit ? targetId : user.PlayerId;
        // 先に全員の画面で撃ち抜く演出を始め、照準が縮み切って撃った少し後に倒す (名札の ✕ が銃声より先に出ないように)
        ShotCall.Send(deadId);
        GuessShotFx.Begin(deadId);
        Due.Add((UnityEngine.Time.time + KillDelay, user.PlayerId, deadId, Terrain.GameClock.ShipGen));
    }

    private const float KillDelay = GuessShotFx.HitAt + 0.15f;
    private static readonly System.Collections.Generic.List<(float at, byte user, byte dead, int ship)> Due = new();

    private static readonly RemoteCall<byte> ShotCall = new("GuessKit.Shot", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (_, id) => GuessShotFx.Begin(id));

    // ホスト: 撃った演出の後に倒す。会議が先に閉じていても倒す (撃ったことは取り消さない)。別の試合になっていたら捨てる (番号が使い回される)
    internal static void HostTick()
    {
        if (Due.Count == 0) return;
        float now = UnityEngine.Time.time;
        for (int i = Due.Count - 1; i >= 0; i--)
        {
            var d = Due[i];
            if (now < d.at) continue;
            Due.RemoveAt(i);
            if (d.ship != Terrain.GameClock.ShipGen || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) continue;
            var user = PortKit.ById(d.user);
            var dead = PortKit.ById(d.dead);
            if (!PortKit.Alive(dead)) continue;
            PortKit.HostVanish(user ? user : dead, dead);
            ResultCall.Send((d.user, d.dead));
            Announce(d.user, d.dead);
        }
    }

    private static bool IsGuesser(PlayerControl p)
    {
        if (RoleState.Of(p) is IGuesser) return true;
        foreach (var a in RoleState.AddonsOf(p.PlayerId))
            if (a is IGuesser) return true;
        return false;
    }

    private static readonly RemoteCall<(byte user, byte dead)> ResultCall = new("GuessKit.Result", Route.HostToAll,
        (w, m) => { w.Write(m.user); w.Write(m.dead); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => Announce(m.user, m.dead));

    // 全員の端末で: 誰が撃ち抜かれたかをチャットに出す
    private static void Announce(byte userId, byte deadId)
    {
        var dead = PortKit.ById(deadId);
        if (!dead || dead.Data == null) return;
        PortKit.Notice(new Text(userId == deadId ? "GuessKit.Missed" : "GuessKit.Hit").With("name", dead.Data.PlayerName).ToString());
    }
}
