using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Roles.Crew;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ドッペルゲンガーの分身にも弾は人と同じく当たる。決まった数が当たると分身が倒れ、本物が分身の所へ引き寄せられて倒れる
public sealed partial class TriggerHappy
{
    private sealed class PuppetTarget
    {
        public byte Owner;
        public float X, Y, Feet;
        public bool Shown;
        public bool Counted; // この tick で当たりを 1 回数えた (人と同じく 1 tick に 1 発まで)
    }
    private readonly List<PuppetTarget> _puppets = new();
    private readonly Stack<PuppetTarget> _puppetPool = new();
    private readonly Dictionary<byte, int> _puppetHits = new();

    // 当たりの候補の分身を読む (CacheTargets と同じ間で)
    private void CachePuppets(bool ghost, Vector2 eye, float reach)
    {
        foreach (var t in _puppets) _puppetPool.Push(t);
        _puppets.Clear();
        if (!DoppelPuppet.Any) return;
        foreach (var m in DoppelPuppet.Each)
        {
            var owner = PortKit.ById(m.Owner);
            if (!PortKit.Alive(owner) || m.Owner == PlayerId) continue;
            if (!FriendlyFire && SameSide(owner)) continue;
            var pos = m.True;
            var t = _puppetPool.Count > 0 ? _puppetPool.Pop() : new PuppetTarget();
            t.Owner = m.Owner; t.X = pos.x; t.Y = pos.y + 0.3f; t.Feet = pos.y;
            t.Shown = ghost || InSight(eye, reach, pos);
            t.Counted = false;
            _puppets.Add(t);
        }
    }

    // 弾が分身に当たったか。true = 弾はそこで止まる (見えている端末だけ血煙を出す)
    private bool TryHitPuppet(Shot s)
    {
        if (_puppets.Count == 0) return false;
        float r = Radius * BulletSize + 0.3f, r2 = r * r;
        for (int k = 0; k < _puppets.Count; k++)
        {
            var t = _puppets[k];
            float dx = t.X - s.X, dy = t.Y - s.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;
            if (!PierceWalls && d2 > 0.0001f)
            {
                float d = FxMath.Sqrt(d2);
                if (PhysicsHelpers.AnyNonTriggersBetween(FxMath.V2(s.X, s.Y), FxMath.V2(dx / d, dy / d), d, Constants.ShipAndObjectsMask)) continue;
            }
            if (s.Real && !t.Counted) { t.Counted = true; HitPuppet(t.Owner); }
            if (!t.Shown) return false;
            GatlingFx.BodyHit(s.X, s.Y, s.Dx, s.Dy, t.Feet, PortKit.ById(t.Owner), flinch: false);
            return true;
        }
        return false;
    }

    private void HitPuppet(byte owner)
    {
        _puppetHits.TryGetValue(owner, out int n);
        if (++n < RequiredHits) { _puppetHits[owner] = n; return; }
        _puppetHits.Remove(owner);
        if (AmongUsClient.Instance.AmHost) HostPuppetKill(Player, owner);
        else PuppetKillCall.Send(owner);
    }

    private static readonly RemoteCall<byte> PuppetKillCall = new("TriggerHappy.PuppetKill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => HostPuppetKill(sender, id));

    // ホスト: 構えている人の申告で、分身が射程の中にいて壁越しでない時だけ受ける
    private static void HostPuppetKill(PlayerControl sender, byte owner)
    {
        if (MeetingHud.Instance || TimeRewind.Playing || RoleState.Of(sender) is not TriggerHappy th || !(th._active || Time.time < th._activeUntil)) return;
        var m = DoppelPuppet.Of(owner);
        var o = PortKit.ById(owner);
        if (m == null || !PortKit.Alive(o)) return;
        if (!FriendlyFire && th.SameSide(o)) return;
        Vector2 a = sender.GetTruePosition(), b = m.True;
        float dx = b.x - a.x, dy = b.y - a.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d > SprayRange + 2f) return;
        if (!PierceWalls && Ricochets == 0 && d > 0.01f && PhysicsHelpers.AnyNonTriggersBetween(a, FxMath.V2(dx / d, dy / d), d, Constants.ShipAndObjectsMask)) return;
        Doppelganger.HostStrikeRanged(sender, owner);
    }
}
