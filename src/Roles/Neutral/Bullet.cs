// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Bullet.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 弾 (第三陣営): 波動砲ジャッカルに仲間にされた人。キルはできない。「装填」で親の波動砲に自分を込めると、親の波動砲が
// すぐ撃てるようになり、親に張り付いたまま動けなくなる。込めた波動砲 (超波動砲) は賢者のバリアも貫くが、撃つと弾は消える。
// 親が死ぬと、設定でジャッカル (か波動砲ジャッカル) に上がる。最初から配られることはない
public sealed class Bullet : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => Jackal.PackColor;
    public override Text Name => new("Bullet.Name");
    public override string Reading => "だん";
    public override Text Blurb => new("Bullet.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Bullet.Load", "snr_wave") };
    public override bool MadeByOthers => true;
    public override string Faction => WaveCannonJackal.BulletToJackal ? Jackal.Pack : null;
    public override string WinsWithFaction => WaveCannonJackal.BulletToJackal ? null : Jackal.Pack;

    public override bool? CanVent => WaveCannonJackal.Vent;

    // 弾 → 親 (全員の端末で同じように持つ)
    private static readonly Dictionary<byte, byte> Parents = new();

    internal static void SetParent(byte bullet, byte parent)
    {
        if (Parents.Count == 0) RoleState.Match.OnRelease(Parents.Clear);
        Parents[bullet] = parent;
    }

    internal static readonly RemoteCall<(byte bullet, byte parent)> ParentCall = new("Bullet.Parent", Route.HostToAll,
        (w, m) => { w.Write(m.bullet); w.Write(m.parent); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => SetParent(m.bullet, m.parent));

    private PlayerControl Parent => Parents.TryGetValue(PlayerId, out var id) ? PortKit.ById(id) : null;
    private WaveCannonGun ParentGun => RoleState.Of(Parent) is IWaveCannon c ? c.Gun : null;
    private bool Loaded => ParentGun is { } g && g.BulletId == PlayerId;

    // 込めた超波動砲を撃っている間、弾は会議を開けない
    internal static bool LocalNoEmergency => RoleState.Local is Bullet b && b.ParentGun is { ShootingBullet: true };

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Bullet.Load"), "snr_wave")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => WaveCannonJackal.BulletLoadCooldown,
            CanTarget = (_, t) => t.PlayerId == Parent?.PlayerId && ParentGun is { BulletId: < 0, Active: false },
            OnUsed = (_, _) =>
            {
                var gun = ParentGun;
                if (gun == null) return;
                gun.BulletId = PlayerId;
                gun.BulletCharge = WaveCannonJackal.BulletChargeTime;
                // 親の波動砲はすぐ撃てる
                if (Parent && Parent.AmOwner) gun.Ability?.Button?.RewindTo(0f, 0f);
            },
        });
    }

    // 込められている間は親に張り付く
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var parent = Parent;
        if (!Loaded || !PortKit.Alive(Player) || !PortKit.Alive(parent)) return;
        Vector2 at = parent.transform.position;
        Player.transform.position = new Vector3(at.x, at.y, Player.transform.position.z);
        if (Player.NetTransform) Player.NetTransform.SnapTo(at);
        if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (Loaded) ParentGun.BulletId = -1;
    }

    private void OnMurdered(PlayerMurderedEvent e) => Promote();

    private void OnExiled(PlayerExiledEvent e) => Promote();

    private void OnMeetingEnd(MeetingEndEvent e) => Promote();

    // 親が死んだら、設定でジャッカル (か波動砲ジャッカル) に上がる
    private void Promote()
    {
        if (!WaveCannonJackal.BulletToJackal || !PortKit.Alive(Player) || !Parents.ContainsKey(PlayerId) || PortKit.Alive(Parent)) return;
        if (!AmongUsClient.Instance.AmHost) return;
        if (WaveCannonJackal.BulletHasWaveCannon)
        {
            if (WaveCannonJackal.Promoted.Count == 0) RoleState.Match.OnRelease(WaveCannonJackal.Promoted.Clear);
            WaveCannonJackal.Promoted.Add(PlayerId);
            RoleState.Change(Player, RoleState.Proto<WaveCannonJackal>());
        }
        else
        {
            if (Jackal.Promoted.Count == 0) RoleState.Match.OnRelease(Jackal.Promoted.Clear);
            Jackal.Promoted.Add(PlayerId);
            RoleState.Change(Player, RoleState.Proto<Jackal>());
        }
    }

    // 弾の画面: 自分と親に ☆、親の位置が分かる設定なら親に ★、ジャッカルの仲間に ◆
    public override string NameMark(PlayerControl seen)
    {
        string mark = null;
        bool parent = seen.PlayerId == Parent?.PlayerId;
        if (seen.PlayerId == PlayerId || parent) mark = $"<color={Jackal.PackColor}>☆</color>";
        if (parent && WaveCannonJackal.BulletSeesParent) mark += "<color=#FFFF00>★</color>";
        if (seen.PlayerId != PlayerId && Jackal.InPack(seen)) mark += $"<color={Jackal.PackColor}>◆</color>";
        return mark;
    }

    // 親の画面: 弾に ☆、弾の位置が分かる設定なら ●
    public override string MarkSeenBy(PlayerControl viewer)
    {
        if (!viewer || viewer.PlayerId != Parent?.PlayerId) return null;
        return $"<color={Jackal.PackColor}>☆</color>" + (WaveCannonJackal.ParentSeesBullet ? "<color=#00FFFF>●</color>" : "");
    }
}
