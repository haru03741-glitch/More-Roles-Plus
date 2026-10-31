// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Ghost/Mirage.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// ミラージュ (幽霊役職・インポスター側): 死んだインポスターに付く。誰かが死ぬと画面が水色に光る。
// 「幻影」で、今ある死体のどれか 1 人の死体の写しを自分の居る場所に置く
public sealed class Mirage : AddonBase
{
    public override string Color => "#FF1919";
    public override Text Name => new("Mirage.Name");
    public override string Reading => "みらーじゅ";
    public override Text Blurb => new("Mirage.Blurb");
    public override string Origin => "SNR";
    public override bool Ghost => true;

    public override bool CanAttach(Team team, RoleBase role) => team == Team.Impostor;

    public static readonly BoolOpt LimitUse = new("Mirage.LimitUse", false);
    public static readonly IntOpt LimitCount = new("Mirage.LimitCount", 2, 1, 10) { When = () => LimitUse };

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Mirage.Copy"), "snr_mirage")
        {
            Ghost = true,
            MaxUses = () => LimitUse ? LimitCount : 0,
            OnUse = () =>
            {
                var bodies = Object.FindObjectsOfType<DeadBody>();
                if (bodies == null || bodies.Length == 0) return false;
                var body = bodies[Random.Range(0, bodies.Length)];
                Vector2 at = PlayerControl.LocalPlayer.transform.position;
                SpawnCall.Send((body.ParentId, at));
                Spawn(body.ParentId, at);
                return true;
            },
        });
    }

    // 自分以外の誰かが死んだら光る
    [LocalOnly]
    private void OnDeath(PlayerMurderedEvent e)
    {
        if (e.Target && !e.Target.AmOwner) PortKit.Flash(UnityEngine.Color.cyan);
    }

    private static readonly RemoteCall<(byte id, Vector2 at)> SpawnCall = new("Mirage.Spawn", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.at.x); w.Write(m.at.y); }, r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (_, m) => Spawn(m.id, m.at));

    private static void Spawn(byte id, Vector2 at)
    {
        var p = PortKit.ById(id);
        if (p) SnrKit.SpawnBody(p, at);
    }
}
