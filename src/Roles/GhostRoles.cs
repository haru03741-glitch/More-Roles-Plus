using System.Linq;

namespace MoreRolesPlus.Roles;

// 幽霊役職 (AddonBase.Ghost) を、死んだ人にホストが付ける。1 人に 1 つまで。
// 出現率を引き、人数の上限 (その試合で付いた数) と付く陣営を見る
internal static class GhostRoles
{
    internal static void Init()
    {
        Events<PlayerMurderedEvent>.Subscribe(e => OnDeath(e.Target), Lifespan.Forever, owner: "GhostRoles");
        Events<PlayerExiledEvent>.Subscribe(e => OnDeath(e.Player), Lifespan.Forever, owner: "GhostRoles");
    }

    private static void OnDeath(PlayerControl dead)
    {
        if (!dead || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        byte id = dead.PlayerId;
        PortKit.After(0.5f, () => Give(id));
    }

    private static void Give(byte id)
    {
        var p = PortKit.ById(id);
        if (!p || p.Data == null || !p.Data.IsDead || p.Data.Disconnected) return;
        var role = RoleState.Of(p);
        if (role == null) return;
        if (RoleState.AddonsOf(id).Any(a => a.Ghost)) return;
        var pool = Options.Registry.Addons.Where(a => a.Ghost && a.Chance > 0).ToList();
        foreach (var ap in pool.OrderBy(_ => UnityEngine.Random.value))
        {
            int have = RoleState.AllAddons.Count(a => a.GetType() == ap.GetType());
            if (have >= ap.Count.Value) continue;
            if (!ap.CanAttach(role.Team, role)) continue;
            if (UnityEngine.Random.Range(0, 100) >= ap.Chance) continue;
            RoleState.AddAddon(p, ap);
            return;
        }
    }
}
