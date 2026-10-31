// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/BlackCat.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 黒猫 (クルーの姿・インポスターの味方): 追放されると、生きている人を 1 人ランダムに道連れにする (設定でインポスターは除く)
public sealed class BlackCat : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("BlackCat.Name");
    public override string Reading => "くろねこ";
    public override Text Blurb => new("BlackCat.Blurb");
    public override string Origin => "SNR";
    public override Team? WinsWithTeam => Team.Impostor;

    public static readonly BoolOpt Vent = new("BlackCat.CanVent", true);
    public static readonly BoolOpt ImpostorSight = new("BlackCat.ImpostorVision", true);
    public static readonly BoolOpt KnowImpostors = new("BlackCat.KnowImpostors", true);
    public static readonly IntOpt NeededTasks = new("BlackCat.NeededTasks", 1, 0, 30) { When = () => KnowImpostors };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        KnowImpostors && PortKit.TasksDone(Player) >= NeededTasks && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    public static readonly BoolOpt SpareImpostors = new("BlackCat.SpareImpostors", true);

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        var pool = new System.Collections.Generic.List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p.PlayerId != PlayerId && PortKit.Alive(p) && !(SpareImpostors && PortKit.IsImpostor(p))) pool.Add(p);
        if (pool.Count > 0) PortKit.HostVanish(Player, pool[UnityEngine.Random.Range(0, pool.Count)]);
    }
}
