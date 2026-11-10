// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Proxy.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Linq;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Crew;

namespace MoreRolesPlus.Roles.Impostor;

// プロキシ (インポスター): 配られると、別のクルー 1 人がモニター (プロキシの相方のマッドメイト) になる。
// 近くの相方に「交換」で役職を入れ替えられる (プロキシ ⇔ モニター)。設定で、相方が死ぬと後を追う
public sealed class Proxy : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Proxy.Name");
    public override string Reading => "ぷろきし";
    public override Text Blurb => new("Proxy.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Proxy.Swap", "proxy_swap") };

    public static readonly FloatOpt SwapCooldown = new("Proxy.SwapCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly ChoiceOpt FollowDeath = new("Proxy.FollowDeath", 0,
        new Text("Proxy.FollowDeath.None"), new Text("Proxy.FollowDeath.Proxy"), new Text("Proxy.FollowDeath.Monitor"), new Text("Proxy.FollowDeath.Either"));

    // 相方 (全員の端末で同じ。255 = まだいない)
    internal byte Partner = 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Proxy.Swap"), "proxy_swap")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SwapCooldown,
            CanTarget = (_, t) => t.PlayerId == Partner,
            OnUsed = (user, t) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                Swap(user, t);
            },
        });
        // ホスト: 配られた時に相方を作る (交換で替わった時はすぐ相方が決まるので作らない)
        if (AmongUsClient.Instance.AmHost)
        {
            byte me = PlayerId;
            PortKit.After(1f, () => MakePartner(me));
        }
    }

    // ホスト: プロキシ (proxy) と相方のモニター (monitor) の役職を入れ替える。モニター側の「交換」も使う
    internal static void Swap(PlayerControl proxy, PlayerControl monitor)
    {
        byte a = proxy.PlayerId, b = monitor.PlayerId;
        RoleState.Change(proxy, RoleState.Proto<Monitor>());
        RoleState.Change(monitor, RoleState.Proto<Proxy>());
        PairCall.Send((b, a));
        Pair(b, a);
    }

    private static void MakePartner(byte proxy)
    {
        if (RoleState.Of(proxy) is not Proxy p || p.Partner != 255) return;
        var pool = RoleState.All.Where(r => r.PlayerId != proxy && UchuKit.TrueCrew(r.Player) && PortKit.Alive(r.Player) && r is not Monitor).ToList();
        if (pool.Count == 0) return;
        var pick = pool[UnityEngine.Random.Range(0, pool.Count)].Player;
        RoleState.Change(pick, RoleState.Proto<Monitor>());
        PairCall.Send((proxy, pick.PlayerId));
        Pair(proxy, pick.PlayerId);
    }

    private static readonly RemoteCall<(byte proxy, byte monitor)> PairCall = new("Proxy.Pair", Route.HostToAll,
        (w, m) => { w.Write(m.proxy); w.Write(m.monitor); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => Pair(m.proxy, m.monitor));

    private static void Pair(byte proxy, byte monitor)
    {
        if (RoleState.Of(proxy) is Proxy p) p.Partner = monitor;
        if (RoleState.Of(monitor) is Monitor m) m.Partner = proxy;
    }

    // 相方には赤字で互いが分かる
    public override string NameMark(PlayerControl seen) => seen.PlayerId == Partner ? "<color=#FF1919>◇</color>" : null;

    // ホスト: 後追い
    [HostOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Follow(e.Target);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => Follow(e.Player);

    private void Follow(PlayerControl dead)
    {
        if (!dead) return;
        int mode = FollowDeath.Value;
        var partner = PortKit.ById(Partner);
        if (dead.PlayerId == PlayerId && (mode == 1 || mode == 3) && PortKit.Alive(partner)) PortKit.After(0.3f, () => PortKit.HostSuicide(partner));
        else if (dead.PlayerId == Partner && (mode == 2 || mode == 3) && PortKit.Alive(Player)) { var me = Player; PortKit.After(0.3f, () => PortKit.HostSuicide(me)); }
    }
}
