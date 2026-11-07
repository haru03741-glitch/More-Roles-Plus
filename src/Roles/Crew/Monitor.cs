// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Proxy.cs (the Monitor; role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/ykundesu/SuperNewRoles (GPL-3.0)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Impostor;
using MoreRolesPlus.Rules;

namespace MoreRolesPlus.Roles.Crew;

// モニター (プロキシの相方・マッドメイト): タスクは無く、インポスターが勝てば一緒に勝つ。プロキシとは互いに分かる。
// プロキシがキルすると画面が光る。プロキシに「交換」されるとプロキシになる
public sealed class Monitor : RoleBase, IRepairLimit
{
    public override Team BaseTeam => Team.Madmate;
    public override string Color => "#FF1919";
    public override Text Name => new("Monitor.Name");
    public override string Reading => "もにたー";
    public override Text Blurb => new("Monitor.Blurb");
    public override string Origin => "Uchu, SNR";
    public override bool MadeByOthers => true;
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt FixLights = new("Monitor.FixLights", false);
    public static readonly BoolOpt FixComms = new("Monitor.FixComms", false);
    public static readonly BoolOpt ImpostorVision = new("Monitor.ImpostorVision", false);
    public static readonly BoolOpt Vent = new("Monitor.CanVent", false);
    public static readonly BoolOpt VentMove = new("Monitor.VentMove", false) { When = () => Vent };

    internal byte Partner = 255;

    public bool CanFixLights => FixLights;
    public bool CanFixComms => FixComms;
    public bool CanMoveInVent => VentMove;

    public override bool? CanVent => Vent;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorVision) PortKit.ImpostorVision(ref radius);
    }

    public override void OnAssigned()
    {
        if (IsLocal) PortKit.After(0.5f, () => { if (RoleState.Local == this) SnrKit.AssignOwnTasks(0, 0, 0); });
        // 交換で替わった時は、前の相方をそのまま引き継ぐ
        foreach (var r in RoleState.All)
            if (r is Proxy p && p.Partner == PlayerId) Partner = p.PlayerId;
    }

    public override string NameMark(PlayerControl seen) => seen.PlayerId == Partner ? "<color=#FF1919>◇</color>" : null;

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == Partner && PortKit.Alive(Player)) PortKit.Flash(new UnityEngine.Color(1f, 0.1f, 0.1f, 0.5f));
    }
}
